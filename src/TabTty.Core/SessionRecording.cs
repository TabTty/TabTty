using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Channels;

namespace TabTty.Core;

internal enum RecordingFormat { Raw, Text, Terminal }

internal readonly record struct RecordingNameOptions(bool IncludeDate, bool IncludeTime, bool IncludeSequence)
{
    internal static RecordingNameOptions Default { get; } = new(true, false, true);
}

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
    private readonly ResourceReservation payloadReservation = ApplicationResources.LogPayload.Reserve(0);
    private readonly CancellationTokenSource abandon = new();
    private readonly TaskCompletionSource incomplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider clock;
    private readonly Func<int, SessionRecording> openPart;
    private readonly RecordingDestination? destination;

    internal SessionRecording(RecordingFormat[] formats, TimeProvider clock, Func<RecordingFormat, IRecordingFile> open)
        : this(formats, clock, (_, format) => open(format)) { }

    internal SessionRecording(RecordingFormat[] formats, TimeProvider clock, Func<int, RecordingFormat, IRecordingFile> open)
        : this(1, formats, clock, open) { }

    private SessionRecording(int part, RecordingFormat[] formats, TimeProvider clock,
        Func<int, RecordingFormat, IRecordingFile> open)
        : this(part, formats, clock, format => open(part, format),
            next => new SessionRecording(next, formats, clock, open)) { }

    private SessionRecording(int part, RecordingFormat[] formats, TimeProvider clock,
        Func<RecordingFormat, IRecordingFile> open, Func<int, SessionRecording> openPart,
        RecordingDestination? destination = null, Action? finishPreparation = null)
    {
        this.clock = clock;
        this.openPart = openPart;
        this.destination = destination;
        PartNumber = part;
        var streams = new Dictionary<RecordingFormat, IRecordingFile>();
        try
        {
            foreach (RecordingFormat format in formats) streams.Add(format, open(format));
            finishPreparation?.Invoke();
        }
        catch
        {
            foreach (IRecordingFile stream in streams.Values)
            {
                try { stream.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception error) when (IsFileFailure(error)) { }
            }
            payloadReservation.Dispose();
            throw;
        }

        writer = Task.Run(() => WriteAsync(streams));
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
    public static SessionRecording Open(string profilePath, ConnectionProfile profile, string? macroName = null,
        int part = 1, RecordingDestination? destination = null, TimeProvider? timeProvider = null,
        RecordingNameOptions? nameOptions = null)
    {
        string path = destination?.Verify() ?? OrdinaryWindowsPath.Resolve(profile.LogDirectory,
            Path.GetDirectoryName(Path.GetFullPath(profilePath))!);

        Directory.CreateDirectory(path);
        string label = SanitizeRecordingLabel(macroName ?? profile.DisplayName);
        var formats = new List<RecordingFormat>(3);
        if (profile.RecordRaw) formats.Add(RecordingFormat.Raw);
        if (profile.RecordText) formats.Add(RecordingFormat.Text);
        if (profile.RecordTerminal) formats.Add(RecordingFormat.Terminal);
        RecordingFormat[] selected = formats.ToArray();
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        RecordingNameOptions naming = nameOptions ?? RecordingNameOptions.Default;
        return OpenPartAtDestination(part);

        SessionRecording OpenPartAtDestination(int number)
        {
            destination?.Verify();
            if (selected.Length == 0)
                return new SessionRecording(number, selected, clock,
                    _ => throw new InvalidOperationException("Recording has no selected formats."), OpenPartAtDestination, destination);

            DateTimeOffset now = clock.GetLocalNow();
            string prefix = naming.IncludeDate ? $"{label}_{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : label;
            if (OrdinaryWindowsPath.IsReservedFileName(naming.IncludeSequence ? prefix + "_" : prefix)) prefix = "_" + prefix;
            string stem = naming.IncludeDate && naming.IncludeTime
                ? $"{prefix}_{now.ToString("HH-mm-ss", CultureInfo.InvariantCulture)}" : prefix;
            string fullStem = Path.Combine(path, stem);
            if (!naming.IncludeSequence) return OpenFiles(number, fullStem, overwrite: true);

            string sequencePrefix = prefix + "_";
            BigInteger sequence = ExistingSequences(path, sequencePrefix, "*", naming.IncludeDate)
                .DefaultIfEmpty(BigInteger.MinusOne).Max() + 1;
            for (;; sequence++)
            {
                destination?.Verify();
                string digits = sequence.ToString("D4", CultureInfo.InvariantCulture);
                using FileStream? reservation = TryReserveStem(Path.Combine(path, sequencePrefix + digits));
                // A competing opener may have finished after the directory scan.
                if (reservation is null || ExistingSequences(path, sequencePrefix, $"*{digits}.*", naming.IncludeDate)
                    .Contains(sequence)) continue;
                return OpenFiles(number, fullStem + "_" + digits, overwrite: false);
            }
        }

        SessionRecording OpenFiles(int number, string stem, bool overwrite)
        {
            List<FileStream>? toTruncate = overwrite ? [] : null;
            return new SessionRecording(number, selected, clock, format =>
            {
                destination?.Verify();
                var stream = new FileStream(stem + FileExtension(format),
                    overwrite ? FileMode.OpenOrCreate : FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                toTruncate?.Add(stream);
                return new RecordingFile(stream);
            }, OpenPartAtDestination, destination, overwrite ? TruncateFiles : null);

            void TruncateFiles()
            {
                // Acquire all selected files before replacing any recorded content.
                destination?.Verify();
                foreach (FileStream stream in toTruncate!) stream.SetLength(0);
            }
        }
    }

    private static IEnumerable<BigInteger> ExistingSequences(string directory, string prefix, string pattern, bool dated)
    {
        foreach (string path in Directory.EnumerateFiles(directory, prefix + pattern, RecordingFileSearch))
        {
            ReadOnlySpan<char> name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string extension in FileExtensions)
            {
                if (name.Length < prefix.Length + extension.Length ||
                    !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                ReadOnlySpan<char> digits = name[prefix.Length..^extension.Length];
                if (dated && digits.Length >= 13 && digits[8] == '_' &&
                    TimeOnly.TryParseExact(digits[..8], "HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    digits = digits[9..];
                if (digits.Length >= 4 && (digits.Length == 4 || digits[0] != '0') &&
                    digits.IndexOfAnyExceptInRange('0', '9') < 0 &&
                    BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
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

    private static string SanitizeRecordingLabel(string value)
    {
        const int labelLimit = 64;
        char[] invalid = Path.GetInvalidFileNameChars();
        var name = new StringBuilder();
        foreach (Rune character in value.EnumerateRunes().Take(labelLimit))
            name.Append(character.IsAscii && invalid.Contains((char)character.Value) ? "_" : character.ToString());
        string label = name.ToString().Trim(' ', '.');
        return label.Length == 0 ? "TabTty" : label;
    }

    public SessionRecording OpenPart(int part) => openPart(part);

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

    private void Fail(string message)
    {
        lock (gate)
        {
            failure ??= message;
            stopped = true;
            queue.Writer.TryComplete();
            incomplete.TrySetResult();
        }
    }

    private ITimer WatchProgress(string message, CancellationTokenSource? deadline = null) =>
        clock.CreateTimer(_ =>
        {
            lock (gate)
            {
                if (protectClose) return;
                Fail(message);
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
        try
        {
            while (await queue.Reader.WaitToReadAsync(abandon.Token).ConfigureAwait(false))
            {
                bool flush = await StageBatchAsync(batches).ConfigureAwait(false);
                if (destination is not null && clock.GetElapsedTime(lastDestinationCheck) >= TimeSpan.FromSeconds(1))
                {
                    destination.Verify();
                    lastDestinationCheck = clock.GetTimestamp();
                }
                foreach (var (format, batch) in batches)
                {
                    if (batch.Buffers.Count == 0) continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
                    await using var timeout = WatchProgress("Recording write timed out.", deadline);
                    await ApplicationResources.LogIo.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        await streams[format].WriteAsync(batch.Buffers,
                            batch.FileOffset, deadline.Token).ConfigureAwait(false);
                    }
                    finally { ApplicationResources.LogIo.Release(); }
                    lock (gate)
                    {
                        pendingBytes -= batch.Bytes;
                        pendingItems -= batch.Buffers.Count;
                        batch.FileOffset += batch.Bytes;
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
            Fail(exception.Message);
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
        public long FileOffset { get; set; }
        public bool Dirty { get; set; } = true;
    }

    private sealed class RecordingFile(FileStream stream) : IRecordingFile
    {
        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken token) =>
            RandomAccess.WriteAsync(stream.SafeFileHandle, buffers, offset, token);

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
