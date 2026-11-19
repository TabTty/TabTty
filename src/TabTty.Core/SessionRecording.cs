using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace TabTty.Core;

internal enum RecordingFormat { Raw, Text, Terminal }

internal sealed class SessionRecording
{
    private const int ByteLimit = 8 * 1024 * 1024;
    private const int ItemLimit = 4096;
    private const int BatchBytes = 64 * 1024;
    private const int BatchItems = 512;
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly object gate = new();
    private readonly Task writer;
    private readonly RecordingFormat[] formats;
    private int pendingBytes;
    private int pendingItems;
    private bool stopped, protectClose, receiveFlushQueued, settled;
    private readonly record struct Work(RecordingFormat Format, byte[]? Payload, bool Flush = false);
    private string? failure;
    private readonly ResourceReservation payloadReservation = ApplicationResources.LogPayload.Reserve(0);
    private readonly CancellationTokenSource abandon = new();
    private readonly TaskCompletionSource incomplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider clock;
    private readonly Func<int, RecordingFormat, IRecordingFile> openFile;
    private readonly RecordingDestination? destination;

    internal SessionRecording(RecordingFormat[] formats, TimeProvider clock, Func<RecordingFormat, IRecordingFile> open)
        : this(formats, clock, (_, format) => open(format)) { }

    internal SessionRecording(RecordingFormat[] formats, TimeProvider clock, Func<int, RecordingFormat, IRecordingFile> open)
        : this(1, formats, clock, open) { }

    private SessionRecording(int part, RecordingFormat[] formats, TimeProvider clock,
        Func<int, RecordingFormat, IRecordingFile> open, RecordingDestination? destination = null)
    {
        this.formats = formats;
        this.clock = clock;
        openFile = open;
        this.destination = destination;
        PartNumber = part;
        var streams = new Dictionary<RecordingFormat, IRecordingFile>();
        try
        {
            foreach (RecordingFormat format in formats) streams.Add(format, openFile(part, format));
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

    private static IRecordingFile OpenFile(string stem, int part, RecordingFormat format)
    {
        string extension = format switch
        {
            RecordingFormat.Raw => ".raw.tttylog",
            RecordingFormat.Text => ".log",
            _ => ".term.tttylog",
        };
        string file = $"{stem}-p{part.ToString("D3", CultureInfo.InvariantCulture)}{extension}";
        return new RecordingFile(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public int PartNumber { get; }
    public Task Incomplete => incomplete.Task;
    public Task ActualRelease => writer;

    public void ProtectClose()
    {
        lock (gate) protectClose = true;
    }

    public string? Failure => Volatile.Read(ref failure);
    public static SessionRecording Open(string profilePath, ConnectionProfile profile, string? nameOverride = null,
        int part = 1, string? runSuffix = null, DateTimeOffset? capturedStart = null,
        RecordingDestination? destination = null)
    {
        string path = destination?.Verify() ?? OrdinaryWindowsPath.Resolve(profile.LogDirectory,
            Path.GetDirectoryName(Path.GetFullPath(profilePath))!);

        Directory.CreateDirectory(path);
        char[] invalid = Path.GetInvalidFileNameChars();
        var name = new StringBuilder();
        foreach (Rune character in (nameOverride ?? profile.DisplayName).EnumerateRunes().Take(64))
            name.Append(character.IsAscii && invalid.Contains((char)character.Value) ? "_" : character.ToString());
        string label = name.ToString().Trim(' ', '.');
        if (label.Length == 0) label = "TabTty";
        var now = capturedStart ?? DateTimeOffset.Now;
        string timestamp = now.ToString("yyyyMMdd-HHmmss.fffzzz", CultureInfo.InvariantCulture).Replace(":", "");
        var formats = new List<RecordingFormat>(3);
        if (profile.RecordRaw) formats.Add(RecordingFormat.Raw);
        if (profile.RecordText) formats.Add(RecordingFormat.Text);
        if (profile.RecordTerminal) formats.Add(RecordingFormat.Terminal);
        string stem = $"{label}-{timestamp}-{runSuffix ?? Guid.NewGuid().ToString("N")}";
        if (OrdinaryWindowsPath.IsReservedFileName(stem)) stem = "_" + stem;
        string fullStem = Path.Combine(path, stem);
        return new SessionRecording(part, formats.ToArray(), TimeProvider.System, (number, format) =>
        {
            destination?.Verify();
            return OpenFile(fullStem, number, format);
        }, destination);
    }

    public SessionRecording OpenPart(int part) => new(part, formats, clock, openFile, destination);

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
