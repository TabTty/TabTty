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
    private readonly Channel<(RecordingFormat Format, byte[] Payload)> queue = Channel.CreateUnbounded<(RecordingFormat, byte[])>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly object gate = new();
    private readonly Task writer;
    private readonly string stem;
    private readonly RecordingFormat[] formats;
    private int pendingBytes;
    private int pendingItems;
    private bool stopped;
    private string? failure;
    private readonly ResourceReservation payloadReservation = ApplicationResources.LogPayload.Reserve(0);
    private readonly CancellationTokenSource abandon = new();
    private Action? failureNotification;

    private SessionRecording(string stem, int part, RecordingFormat[] formats)
    {
        this.stem = stem;
        this.formats = formats;
        var streams = new Dictionary<RecordingFormat, FileStream>();
        try
        {
            foreach (RecordingFormat format in formats)
            {
                string extension = format switch
                {
                    RecordingFormat.Raw => ".raw.tttylog",
                    RecordingFormat.Text => ".log",
                    _ => ".term.tttylog",
                };
                string file = $"{stem}-p{part.ToString("D3", CultureInfo.InvariantCulture)}{extension}";
                streams.Add(format, new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }
        }
        catch
        {
            foreach (FileStream stream in streams.Values)
            {
                stream.Dispose();
            }
            throw;
        }

        writer = Task.Run(() => WriteAsync(streams));
    }

    public string? Failure => Volatile.Read(ref failure);
    internal Action? FailureNotification
    {
        get => Volatile.Read(ref failureNotification);
        set => Volatile.Write(ref failureNotification, value);
    }

    public static SessionRecording Open(string profilePath, ConnectionProfile profile, string? nameOverride = null,
        int part = 1, string? runSuffix = null, DateTimeOffset? capturedStart = null)
    {
        string configured = profile.LogDirectory;
        string path = Path.GetFullPath(configured, Path.GetDirectoryName(Path.GetFullPath(profilePath))!);
        if ((Path.IsPathRooted(configured) && !Path.IsPathFullyQualified(configured)) ||
            configured.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            configured.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            (configured.Length > 2 && configured[2..].Contains(':')) ||
            (configured.Contains(':') && !(configured.Length > 2 && char.IsAsciiLetter(configured[0]) && configured[1] == ':')))
        {
            throw new InvalidDataException("Invalid recording directory.");
        }

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
        return new SessionRecording(Path.Combine(path, $"{label}-{timestamp}-{runSuffix ?? Guid.NewGuid().ToString("N")}"), part,
            formats.ToArray());
    }

    public SessionRecording OpenPart(int part) => new(stem, part, formats);

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
            queue.Writer.TryWrite((format, payload));
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

    public void Abandon()
    {
        Fail("Recording cleanup was cancelled. Capture may be incomplete.");
        abandon.Cancel();
    }

    private void Fail(string message)
    {
        lock (gate)
        {
            failure ??= message;
            stopped = true;
            queue.Writer.TryComplete();
        }
        FailureNotification?.Invoke();
    }

    private async Task WriteAsync(Dictionary<RecordingFormat, FileStream> streams)
    {
        var batches = streams.Keys.ToDictionary(format => format, _ => new WriteBatch());
        try
        {
            while (await queue.Reader.WaitToReadAsync(abandon.Token).ConfigureAwait(false))
            {
                await StageBatchAsync(batches).ConfigureAwait(false);
                foreach (var (format, batch) in batches)
                {
                    if (batch.Buffers.Count == 0) continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using var timeout = deadline.Token.Register(() => Fail("Recording write timed out."));
                    await ApplicationResources.LogIo.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        await RandomAccess.WriteAsync(streams[format].SafeFileHandle, batch.Buffers,
                            batch.FileOffset, deadline.Token).ConfigureAwait(false);
                    }
                    finally { ApplicationResources.LogIo.Release(); }
                    lock (gate)
                    {
                        pendingBytes -= batch.Bytes;
                        pendingItems -= batch.Buffers.Count;
                        batch.FileOffset += batch.Bytes;
                        batch.Bytes = 0;
                        batch.Buffers.Clear();
                        payloadReservation.Resize(pendingBytes);
                    }
                }
            }

            foreach (FileStream stream in streams.Values)
            {
                abandon.Token.ThrowIfCancellationRequested();
                using var flushDeadline = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
                flushDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var flushTimeout = flushDeadline.Token.Register(() => Fail("Recording flush timed out."));
                await ApplicationResources.LogIo.WaitAsync(flushDeadline.Token).ConfigureAwait(false);
                try { RandomAccess.FlushToDisk(stream.SafeFileHandle); }
                finally { ApplicationResources.LogIo.Release(); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or OperationCanceledException or NotSupportedException)
        {
            Fail(exception.Message);
            while (queue.Reader.TryRead(out _)) { }
        }
        finally
        {
            foreach (FileStream stream in streams.Values)
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (IOException exception) { Fail(exception.Message); }
            }
            payloadReservation.Dispose();
        }
    }

    private async Task StageBatchAsync(Dictionary<RecordingFormat, WriteBatch> batches)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
        window.CancelAfter(TimeSpan.FromSeconds(1));
        int bytes = 0, items = 0;
        try
        {
            do
            {
                while (!window.IsCancellationRequested && queue.Reader.TryRead(out var next))
                {
                    WriteBatch batch = batches[next.Format];
                    batch.Buffers.Add(next.Payload);
                    batch.Bytes += next.Payload.Length;
                    bytes += next.Payload.Length;
                    if (++items >= BatchItems || bytes >= BatchBytes) return;
                }
            } while (await queue.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!abandon.IsCancellationRequested) { }
    }

    private sealed class WriteBatch
    {
        public List<ReadOnlyMemory<byte>> Buffers { get; } = [];
        public int Bytes { get; set; }
        public long FileOffset { get; set; }
    }
}
