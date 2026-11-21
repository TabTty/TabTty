using System.Globalization;
using System.Numerics;
using System.Threading.Channels;

namespace TabTty.Core;

internal enum RecordingFormat { Raw, Text, Terminal }

internal sealed class SessionRecording
{
    private const int ByteLimit = 8 * 1024 * 1024;
    private const int ItemLimit = 4096;
    private const int BatchBytes = 64 * 1024;
    private const int BatchItems = 512;
    private static readonly string[] FileExtensions = Enum.GetValues<RecordingFormat>().Select(FileExtension).ToArray();
    private static readonly EnumerationOptions RecordingFileSearch = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = false,
    };
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly object gate = new();
    private readonly Task writer;
    private int pendingBytes;
    private int pendingItems;
    private bool stopped, protectClose, receiveFlushQueued, settled;
    private readonly record struct Work(RecordingFormat Format, byte[]? Payload, bool Flush = false);
    private string? failure;
    private bool startFailure;
    private readonly ResourceReservation payloadReservation = ApplicationResources.LogPayload.Reserve(0);
    private readonly CancellationTokenSource abandon = new();
    private readonly TaskCompletionSource incomplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider clock;
    private readonly Func<int, CancellationToken, Dictionary<RecordingFormat, IRecordingFile>> preparePart;
    private readonly RecordingDestination? destination;

    internal SessionRecording(RecordingFormat[] formats, TimeProvider clock, Func<RecordingFormat, IRecordingFile> open)
        : this(formats, clock, (_, format) => open(format)) { }

    internal SessionRecording(RecordingFormat[] formats, TimeProvider clock, Func<int, RecordingFormat, IRecordingFile> open)
        : this(1, formats, clock, open) { }

    private SessionRecording(int part, RecordingFormat[] formats, TimeProvider clock,
        Func<int, RecordingFormat, IRecordingFile> open)
        : this(part, clock, (number, _) => AcquireFiles(formats, format => open(number, format)), default) { }

    private SessionRecording(int part, TimeProvider clock,
        Func<int, CancellationToken, Dictionary<RecordingFormat, IRecordingFile>> preparePart,
        CancellationToken activation, RecordingDestination? destination = null)
    {
        this.clock = clock;
        this.preparePart = preparePart;
        this.destination = destination;
        PartNumber = part;
        Dictionary<RecordingFormat, IRecordingFile> streams;
        try { streams = preparePart(part, activation); }
        catch
        {
            payloadReservation.Dispose();
            abandon.Dispose();
            throw;
        }
        writer = Task.Run(() => WriteAsync(streams));
    }

    private static Dictionary<RecordingFormat, IRecordingFile> AcquireFiles(
        RecordingFormat[] formats, Func<RecordingFormat, IRecordingFile> open, Action? finishPreparation = null)
    {
        var streams = new Dictionary<RecordingFormat, IRecordingFile>();
        try
        {
            foreach (RecordingFormat format in formats) streams.Add(format, open(format));
            finishPreparation?.Invoke();
            return streams;
        }
        catch
        {
            foreach (IRecordingFile stream in streams.Values)
            {
                try { stream.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception error) when (IsFileFailure(error)) { }
            }
            throw;
        }
    }

    private static string FileExtension(RecordingFormat format) => format switch
    {
        RecordingFormat.Raw => ".raw.tttylog",
        RecordingFormat.Text => ".log",
        _ => ".term.tttylog",
    };

    public int PartNumber { get; }
    public Task Incomplete => incomplete.Task;
    public Task ActualRelease => writer;

    public void ProtectClose()
    {
        lock (gate) protectClose = true;
    }

    public string? Failure => Volatile.Read(ref failure);
    internal bool StartFailure => Volatile.Read(ref startFailure);
    public static SessionRecording Open(string profilePath, ConnectionProfile profile, string? macroName = null,
        int part = 1, RecordingDestination? destination = null, TimeProvider? timeProvider = null,
        RecordingSettings? settings = null, CancellationToken token = default,
        Func<string, string, IEnumerable<string>>? enumerate = null)
    {
        var recording = settings ?? RecordingSettings.Resolve(profilePath, profile, macroName, reviewed: destination);
        destination = recording.Destination;
        string path = destination.Verify();

        var formats = new List<RecordingFormat>(3);
        if (profile.RecordRaw) formats.Add(RecordingFormat.Raw);
        if (profile.RecordText) formats.Add(RecordingFormat.Text);
        if (profile.RecordTerminal) formats.Add(RecordingFormat.Terminal);
        RecordingFormat[] selected = formats.ToArray();
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        return new SessionRecording(part, clock, PreparePartAtDestination, token, destination);

        Dictionary<RecordingFormat, IRecordingFile> PreparePartAtDestination(int number, CancellationToken activation)
        {
            activation.ThrowIfCancellationRequested();
            destination?.Verify();
            if (selected.Length == 0)
                return [];

            DateTime localTime = DateTime.SpecifyKind(clock.GetLocalNow().DateTime, DateTimeKind.Local);
            var expanded = recording.Template.Expand(profile.DisplayName, macroName, localTime);
            Directory.CreateDirectory(path);
            if (expanded.Width == 0)
                return OpenFiles(Path.Combine(path, expanded.FileBase(0)), recording.Collision, activation);

            BigInteger sequence = BigInteger.Max(expanded.Start,
                ExistingSequences(path, expanded, "*", activation, enumerate).DefaultIfEmpty(expanded.Start - 1).Max() + 1);
            for (;; sequence++)
            {
                activation.ThrowIfCancellationRequested();
                destination?.Verify();
                string digits = expanded.Digits(sequence);
                string stem = Path.Combine(path, expanded.FileBase(sequence));
                activation.ThrowIfCancellationRequested();
                using FileStream? reservation = TryReserveStem(stem);
                // A competing opener may have finished after the directory scan.
                if (reservation is null || ExistingSequences(path, expanded, digits, activation, enumerate, exact: true).Contains(sequence)) continue;
                activation.ThrowIfCancellationRequested();
                return OpenFiles(stem, LogCollisionPolicy.Error, activation);
            }
        }

        Dictionary<RecordingFormat, IRecordingFile> OpenFiles(
            string stem, LogCollisionPolicy collision, CancellationToken activation)
        {
            var mode = collision switch
            {
                LogCollisionPolicy.Error => FileMode.CreateNew,
                LogCollisionPolicy.Overwrite => FileMode.OpenOrCreate,
                LogCollisionPolicy.Append => FileMode.Append,
                _ => throw new InvalidDataException("Invalid log collision policy.")
            };
            List<FileStream>? toTruncate = collision == LogCollisionPolicy.Overwrite ? [] : null;
            return AcquireFiles(selected, format =>
            {
                activation.ThrowIfCancellationRequested();
                destination?.Verify();
                activation.ThrowIfCancellationRequested();
                var stream = new FileStream(stem + FileExtension(format), mode, FileAccess.Write, FileShare.Read,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                toTruncate?.Add(stream);
                return new RecordingFile(stream);
            }, toTruncate is not null ? TruncateFiles : null);

            void TruncateFiles()
            {
                // Acquire all selected files before replacing any recorded content.
                destination?.Verify();
                foreach (FileStream stream in toTruncate!)
                {
                    activation.ThrowIfCancellationRequested();
                    stream.SetLength(0);
                }
            }
        }
    }

    private static IEnumerable<BigInteger> ExistingSequences(string directory, ExpandedRecordingName expanded,
        string digitsPattern, CancellationToken activation, Func<string, string, IEnumerable<string>>? enumerate,
        bool exact = false)
    {
        string prefix = expanded.Prefix;
        string suffix = expanded.Suffix;
        activation.ThrowIfCancellationRequested();
        string pattern = prefix + digitsPattern + suffix + ".*";
        using var entries = (enumerate?.Invoke(directory, pattern) ??
            Directory.EnumerateFiles(directory, pattern, RecordingFileSearch)).GetEnumerator();
        while (true)
        {
            activation.ThrowIfCancellationRequested();
            bool found = entries.MoveNext();
            activation.ThrowIfCancellationRequested();
            if (!found) yield break;
            ReadOnlySpan<char> name = Path.GetFileName(entries.Current);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string extension in FileExtensions)
            {
                int ending = suffix.Length + extension.Length;
                if (name.Length < prefix.Length + ending || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                    !name[^ending..^extension.Length].Equals(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                ReadOnlySpan<char> digits = name[prefix.Length..^ending];
                if (digits.Length >= expanded.Width &&
                    (!exact || digits.SequenceEqual(digitsPattern)) &&
                    digits.IndexOfAnyExceptInRange('0', '9') < 0 &&
                    BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) &&
                    digits.SequenceEqual(expanded.Digits(sequence)))
                    yield return sequence;
                break;
            }
        }
    }

    private static FileStream? TryReserveStem(string stem)
    {
        // Reserve the stem on the destination filesystem, including for disjoint formats
        // and when different machines or path aliases refer to the same directory.
        try
        {
            return new FileStream(stem + ".reserve", FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 80 or 183 ||
            !OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 17)
        {
            return null;
        }
    }

    public SessionRecording OpenPart(int part, CancellationToken token = default) =>
        new(part, clock, preparePart, token, destination);

    public void Enqueue(RecordingFormat format, byte[] payload)
    {
        if (payload.Length == 0) return;
        lock (gate)
        {
            if (stopped) return;
            if (payload.Length > ByteLimit - pendingBytes || pendingItems == ItemLimit)
            {
                Fail("The recording queue reached its limit.");
                return;
            }

            pendingBytes += payload.Length;
            try { payloadReservation.Resize(pendingBytes); }
            catch (ResourceLimitException)
            {
                pendingBytes -= payload.Length;
                Fail("The application recording budget reached its limit.");
                return;
            }
            pendingItems++;
            queue.Writer.TryWrite(new(format, payload));
        }
    }

    public void FlushReceived()
    {
        lock (gate)
        {
            if (stopped || receiveFlushQueued) return;
            receiveFlushQueued = true;
            queue.Writer.TryWrite(new(default, null, Flush: true));
        }
    }

    public Task FinishAsync()
    {
        lock (gate)
        {
            stopped = true;
            queue.Writer.TryComplete();
        }

        return writer;
    }

    public void MarkIncomplete(string message) => Fail(message);

    public bool Abandon()
    {
        lock (gate)
        {
            if (settled) return false;
            Fail("Recording cleanup was cancelled. Capture may be incomplete.");
        }
        _ = abandon.CancelAsync();
        return true;
    }

    private void Fail(string message, bool duringStart = false)
    {
        lock (gate)
        {
            if (failure is null)
            {
                Volatile.Write(ref startFailure, duringStart);
                Volatile.Write(ref failure, message);
            }
            stopped = true;
            queue.Writer.TryComplete();
            incomplete.TrySetResult();
        }
    }

    private ITimer WatchProgress(string message, CancellationTokenSource? deadline = null, bool duringStart = false) =>
        clock.CreateTimer(_ =>
        {
            lock (gate)
            {
                if (protectClose) return;
                Fail(message, duringStart);
            }
            try { deadline?.Cancel(); }
            catch (ObjectDisposedException) { }
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

    private static bool IsFileFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException;

    private async Task WriteAsync(Dictionary<RecordingFormat, IRecordingFile> streams)
    {
        var batches = streams.Keys.ToDictionary(format => format, _ => new WriteBatch());
        long lastDestinationCheck = clock.GetTimestamp();
        bool duringStart = false;
        try
        {
            while (await queue.Reader.WaitToReadAsync(abandon.Token).ConfigureAwait(false))
            {
                bool flush = await StageBatchAsync(batches).ConfigureAwait(false);
                if (destination is not null && clock.GetElapsedTime(lastDestinationCheck) >= TimeSpan.FromSeconds(1))
                {
                    duringStart = batches.Values.Any(batch => batch.WrittenBytes == 0 && batch.Buffers.Count != 0);
                    destination.Verify();
                    lastDestinationCheck = clock.GetTimestamp();
                    duringStart = false;
                }
                foreach (var (format, batch) in batches)
                {
                    if (batch.Buffers.Count == 0) continue;
                    duringStart = batch.WrittenBytes == 0;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
                    await using var timeout = WatchProgress("Recording write timed out.", deadline, duringStart);
                    await ApplicationResources.LogIo.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        await streams[format].WriteAsync(batch.Buffers,
                            batch.WrittenBytes, deadline.Token).ConfigureAwait(false);
                    }
                    finally { ApplicationResources.LogIo.Release(); }
                    duringStart = false;
                    lock (gate)
                    {
                        pendingBytes -= batch.Bytes;
                        pendingItems -= batch.Buffers.Count;
                        batch.WrittenBytes += batch.Bytes;
                        batch.Dirty = true;
                        batch.Bytes = 0;
                        batch.Buffers.Clear();
                        payloadReservation.Resize(pendingBytes);
                    }
                }
                if (flush) await FlushAsync(streams, batches).ConfigureAwait(false);
            }

            await FlushAsync(streams, batches).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Fail(exception.Message, duringStart);
            while (queue.Reader.TryRead(out _)) { }
        }
        finally
        {
            foreach (IRecordingFile stream in streams.Values)
            {
                await using var timeout = WatchProgress("Recording file close timed out.");
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (IsFileFailure(exception)) { Fail(exception.Message); }
            }
            payloadReservation.Dispose();
            lock (gate) settled = true;
        }
    }

    private async Task FlushAsync(Dictionary<RecordingFormat, IRecordingFile> streams,
        Dictionary<RecordingFormat, WriteBatch> batches)
    {
        foreach (var (format, stream) in streams)
        {
            var batch = batches[format];
            if (!batch.Dirty) continue;
            abandon.Token.ThrowIfCancellationRequested();
            using var flushDeadline = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
            await using var flushTimeout = WatchProgress("Recording flush timed out.", flushDeadline);
            await ApplicationResources.LogIo.WaitAsync(flushDeadline.Token).ConfigureAwait(false);
            try
            {
                await stream.FlushAsync(flushDeadline.Token).ConfigureAwait(false);
                batch.Dirty = false;
            }
            finally { ApplicationResources.LogIo.Release(); }
        }
    }

    private async Task<bool> StageBatchAsync(Dictionary<RecordingFormat, WriteBatch> batches)
    {
        using var interval = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token, interval.Token);
        int bytes = 0, items = 0;
        try
        {
            do
            {
                while (!window.IsCancellationRequested && queue.Reader.TryRead(out var next))
                {
                    if (next.Flush) return true;
                    byte[] payload = next.Payload!;
                    WriteBatch batch = batches[next.Format];
                    batch.Buffers.Add(payload);
                    batch.Bytes += payload.Length;
                    bytes += payload.Length;
                    if (++items >= BatchItems || bytes >= BatchBytes) return false;
                }
            } while (await queue.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!abandon.IsCancellationRequested) { }
        return false;
    }

    private sealed class WriteBatch
    {
        public List<ReadOnlyMemory<byte>> Buffers { get; } = [];
        public int Bytes { get; set; }
        public long WrittenBytes { get; set; }
        public bool Dirty { get; set; } = true;
    }

    private sealed class RecordingFile(FileStream stream) : IRecordingFile
    {
        // Append opens at EOF with exclusive write access. Each writer tracks progress from its own part's start.
        private readonly long partStart = stream.Position;
        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken token) =>
            RandomAccess.WriteAsync(stream.SafeFileHandle, buffers, partStart + offset, token);

        public ValueTask FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RandomAccess.FlushToDisk(stream.SafeFileHandle);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

internal interface IRecordingFile : IAsyncDisposable
{
    ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken token);
    ValueTask FlushAsync(CancellationToken token);
}
