using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace TabTty.Core;

public enum SftpFailure
{
    ChannelRefused, SubsystemRefused, Version, Disconnected, Capacity, Timeout,
    InvalidReply, Missing, Permission, Unsupported, ServerError, UnknownOutcome,
    InvalidName, CannotReplace, SourceChanged, DownloadOrigin, DownloadPermissions,
}

public sealed class SftpException(SftpFailure failure, Exception? inner = null) : IOException(failure.ToString(), inner)
{
    public SftpFailure Failure { get; } = failure;
}

public enum TransferKind { Unknown, File, Directory, Link }
public sealed record TransferAttributes(TransferKind Kind, long? Size, DateTimeOffset? Modified, DateTimeOffset? Accessed = null)
{
    internal (ulong Volume, Guid File)? LocalIdentity { get; init; }

    internal static bool SameOverwriteTarget(TransferAttributes? approved, TransferAttributes? current) =>
        approved?.Kind == current?.Kind && approved?.Size == current?.Size &&
        approved?.Modified == current?.Modified && approved?.LocalIdentity == current?.LocalIdentity;
}
public sealed record SftpEntry(byte[] Name, TransferAttributes Attributes);

public sealed class SftpClient : IDisposable
{
    private const int MaxFrame = 256 * 1024;
    private const int MaxPath = 32 * 1024;
    public const int BlockSize = 32 * 1024;
    public const int MaxEntries = 50_000;
    private readonly SshSessionWire.TransferChannel channel;
    private readonly SemaphoreSlim fileRequests = new(8);
    private readonly SemaphoreSlim controlRequests = new(2);
    private readonly ConcurrentDictionary<uint, PendingRequest> pending = new();
    private readonly TaskCompletionSource<byte[]> version = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] framePrefix = new byte[4];
    private byte[]? frameBody;
    private int prefixLength, bodyLength;
    private PendingRequest? frameRequest;
    private readonly TimeSpan wait;
    private readonly TerminalEncoding codec;
    private int nextId;
    private int stopped;
    public bool CanReplace { get; private set; }
    public bool IsOpen => Volatile.Read(ref stopped) == 0 && channel.IsUsable;

    internal static ResourceReservation ReserveProviderStorage(int bytes) => SshSessionWire.ReserveTransferStorage(bytes);

    internal SftpClient(SshSessionWire.TransferChannel channel, string encoding, TimeSpan wait)
    {
        this.channel = channel;
        this.wait = wait;
        codec = new TerminalEncoding(encoding);
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        using var request = new SshWriter();
        request.Byte(1);
        request.Number(3);
        await WriteFrameAsync(request.Memory, token).ConfigureAwait(false);
        var reply = new SshReader(await version.Task.WaitAsync(token).ConfigureAwait(false));
        reply.Expect(2);
        if (reply.Number() != 3) throw new SftpException(SftpFailure.Version);
        while (reply.Length != 0)
        {
            bool rename = reply.String().SequenceEqual("posix-rename@openssh.com"u8);
            if (reply.String().SequenceEqual("1"u8) && rename) CanReplace = true;
        }
    }

    public string DecodeName(ReadOnlySpan<byte> name)
    {
        try { return codec.DecodeFileName(name); }
        catch (DecoderFallbackException error) { throw new SftpException(SftpFailure.InvalidName, error); }
    }

    public string DisplayPath(byte[] path) => string.Join('/', Split(path).Select(component =>
    {
        try { return DisplayName(component); }
        catch (SftpException error) when (error.Failure == SftpFailure.InvalidName) { return DisplayBytes(component); }
    }));

    public string DisplayName(byte[] name) => PermissionDisplay.Literal(DecodeName(name));

    internal static string DisplayBytes(byte[] bytes) => $"<bytes:{Convert.ToHexString(bytes)}>";

    public byte[] EncodePath(string path)
    {
        using var encoded = new MemoryStream();
        bool first = true;
        foreach (string component in path.Split('/'))
        {
            if (!first) encoded.WriteByte((byte)'/');
            first = false;
            if (component.Length != 0) encoded.Write(EncodeComponent(component, navigation: true));
            if (encoded.Length > MaxPath) throw new SftpException(SftpFailure.InvalidName);
        }
        return encoded.ToArray();
    }

    public byte[] EncodeName(string name) => EncodeComponent(name, navigation: false);

    private byte[] EncodeComponent(string name, bool navigation)
    {
        try
        {
            if (name.Length > MaxPath) throw new SftpException(SftpFailure.InvalidName);
            byte[] bytes = codec.Encode(name);
            if (!ValidComponent(bytes, navigation) || DecodeName(bytes) != name)
                throw new SftpException(SftpFailure.InvalidName);
            return bytes;
        }
        catch (EncoderFallbackException error) { throw new SftpException(SftpFailure.InvalidName, error); }
    }

    private static IEnumerable<byte[]> Split(byte[] path)
    {
        int begin = 0;
        for (int index = 0; index <= path.Length; index++)
        {
            if (index != path.Length && path[index] != (byte)'/') continue;
            yield return path[begin..index];
            begin = index + 1;
        }
    }

    public static byte[] Child(byte[] directory, byte[] name)
    {
        if (!ValidComponent(name, navigation: false)) throw new SftpException(SftpFailure.InvalidName);
        int separator = directory.Length != 0 && directory[^1] != (byte)'/' ? 1 : 0;
        if (directory.Length + name.Length + separator > MaxPath) throw new SftpException(SftpFailure.InvalidName);
        var path = new byte[directory.Length + name.Length + separator];
        directory.CopyTo(path, 0);
        if (separator != 0) path[directory.Length] = (byte)'/';
        name.CopyTo(path, directory.Length + separator);
        return path;
    }

    private static bool ValidComponent(ReadOnlySpan<byte> name, bool navigation) =>
        !name.IsEmpty && name.Length <= MaxPath && name.IndexOfAny((byte)0, (byte)'/') < 0 &&
        (navigation || (!name.SequenceEqual("."u8) && !name.SequenceEqual(".."u8)));

    public async Task<byte[]> ResolveAsync(byte[] path, CancellationToken token)
    {
        SftpEntry[] result = Names(await RequestAsync(16, writer => writer.String(path), 104, token).ConfigureAwait(false));
        if (result.Length != 1 || result[0].Name.Length is 0 or > MaxPath || result[0].Name.Contains((byte)0))
            throw InvalidReply();
        return result[0].Name;
    }

    public async Task<TransferAttributes?> StatAsync(byte[] path, CancellationToken token)
    {
        try
        {
            byte[] response = await RequestAsync(7, writer => writer.String(path), 105, token).ConfigureAwait(false);
            return ReadAttributes(response);
        }
        catch (SftpException error) when (error.Failure == SftpFailure.Missing) { return null; }
    }

    public async Task<SftpEntry[]> ListAsync(byte[] path, CancellationToken token)
    {
        byte[] handle = Handle(await RequestAsync(11, writer => writer.String(path), 102, token).ConfigureAwait(false));
        var entries = new List<SftpEntry>();
        int receivedEntries = 0;
        long bytes = 0;
        try
        {
            while (true)
            {
                byte[]? reply = await RequestAsync(12, writer => writer.String(handle), 104, token, eof: true).ConfigureAwait(false);
                if (reply is null) break;
                SftpEntry[] page = Names(reply);
                if (page.Length == 0) throw InvalidReply();
                receivedEntries += page.Length;
                if (receivedEntries > MaxEntries) throw new SftpException(SftpFailure.Capacity);
                foreach (SftpEntry entry in page)
                {
                    if (entry.Name.AsSpan().SequenceEqual("."u8) || entry.Name.AsSpan().SequenceEqual(".."u8)) continue;
                    if (!ValidComponent(entry.Name, navigation: false)) throw InvalidReply();
                    bytes += entry.Name.Length + 128;
                    if (bytes > 16 * 1024 * 1024)
                        throw new SftpException(SftpFailure.Capacity);
                    entries.Add(entry);
                }
            }
        }
        finally { await TryCloseAsync(handle).ConfigureAwait(false); }
        return entries.ToArray();
    }

    public async Task<byte[]> OpenAsync(byte[] path, bool write, CancellationToken token) =>
        Handle(await RequestAsync(3, request =>
        {
            request.String(path);
            request.Number(write ? 2u | 8u | 32u : 1u);
            // Supply owner read/write permissions at exclusive creation, before any upload bytes.
            request.Number(write ? 4u : 0u); // SSH_FILEXFER_ATTR_PERMISSIONS
            if (write) request.Number((uint)(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        }, 102, token).ConfigureAwait(false));

    public async Task<TransferAttributes> GetAttributesAsync(byte[] handle, CancellationToken token) =>
        ReadAttributes(await RequestAsync(8, writer => writer.String(handle), 105, token).ConfigureAwait(false));

    public async Task<byte[]?> ReadAsync(byte[] handle, long offset, int count, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count is <= 0 or > BlockSize) throw new ArgumentOutOfRangeException(nameof(count));
        byte[]? reply = await RequestAsync(5, writer =>
        {
            writer.String(handle);
            Number64(writer, (ulong)offset);
            writer.Number((uint)count);
        }, 103, token, eof: true, fileData: true).ConfigureAwait(false);
        if (reply is null) return null;
        var reader = new SshReader(reply);
        var data = reader.String();
        if (data.IsEmpty || data.Length > count || reader.Length != 0) throw InvalidReply();
        return data.ToArray();
    }

    public Task WriteAsync(byte[] handle, long offset, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (data.Length is <= 0 or > BlockSize) throw new ArgumentOutOfRangeException(nameof(data));
        return RequestAsync(6, writer =>
        {
            writer.String(handle);
            Number64(writer, (ulong)offset);
            writer.String(data.Span);
        }, 101, token, fileData: true);
    }

    public Task SetTimeAsync(byte[] handle, DateTimeOffset modified, DateTimeOffset? accessed, CancellationToken token) =>
        RequestAsync(10, writer =>
        {
            writer.String(handle);
            writer.Number(8);
            writer.Number(checked((uint)(accessed ?? modified).ToUnixTimeSeconds()));
            writer.Number(checked((uint)modified.ToUnixTimeSeconds()));
        }, 101, token);

    public Task CloseAsync(byte[] handle, CancellationToken token) => RequestAsync(4, writer => writer.String(handle), 101, token);

    public async Task TryCloseAsync(byte[] handle)
    {
        try { await CloseAsync(handle, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { }
    }

    public Task CreateDirectoryAsync(byte[] path, CancellationToken token) => RequestAsync(14, writer =>
    {
        writer.String(path);
        writer.Number(4); // SSH_FILEXFER_ATTR_PERMISSIONS
        writer.Number(0x1c0); // 0700, owner read/write/search
    }, 101, token, mutation: true);

    public Task RemoveAsync(byte[] path, bool directory, CancellationToken token) =>
        RequestAsync(directory ? (byte)15 : (byte)13, writer => writer.String(path), 101, token, mutation: true);

    public Task RenameAsync(byte[] from, byte[] to, bool replace, CancellationToken token)
    {
        if (replace && !CanReplace) throw new SftpException(SftpFailure.CannotReplace);
        return RequestAsync(replace ? (byte)200 : (byte)18, writer =>
        {
            if (replace) writer.Text("posix-rename@openssh.com");
            writer.String(from);
            writer.String(to);
        }, 101, token, mutation: true);
    }

    private async Task<byte[]> RequestAsync(byte type, Action<SshWriter> fields, byte expected,
        CancellationToken token, bool eof = false, bool mutation = false, bool fileData = false)
    {
        SemaphoreSlim slots = fileData ? fileRequests : controlRequests;
        await slots.WaitAsync(token).ConfigureAwait(false);
        bool sent = false;
        using var deadline = new CancellationTokenSource(wait);
        using var sendToken = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
        uint id = unchecked((uint)Interlocked.Increment(ref nextId));
        var requestState = new PendingRequest(deadline, fileData);
        try
        {
            if (!IsOpen) throw new SftpException(SftpFailure.Disconnected);
            if (!pending.TryAdd(id, requestState)) throw InvalidReply();
            using var request = new SshWriter();
            request.Byte(type);
            request.Number(id);
            fields(request);
            await WriteFrameAsync(request.Memory, sendToken.Token).ConfigureAwait(false);
            sent = true;
            deadline.CancelAfter(wait);
            // Drain the reply even if the caller cancels after the request was sent.
            byte[] frame = await requestState.Reply.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var reply = new SshReader(frame);
            byte result = reply.Byte();
            if (reply.Number() != id) throw InvalidReply();
            if (result == 101)
            {
                uint status = reply.Number();
                reply.String();
                reply.String();
                reply.End();
                return status switch
                {
                    0 when expected == 101 => [],
                    1 when eof => null!,
                    2 => throw new SftpException(SftpFailure.Missing),
                    3 => throw new SftpException(SftpFailure.Permission),
                    8 => throw new SftpException(SftpFailure.Unsupported),
                    0 or 1 => throw InvalidReply(),
                    _ => throw new SftpException(SftpFailure.ServerError),
                };
            }
            if (result != expected || expected == 101) throw InvalidReply();
            return frame[5..];
        }
        catch (SshFrameSubmissionUncertainException error)
        {
            Dispose();
            throw new SftpException(mutation ? SftpFailure.UnknownOutcome : SftpFailure.Disconnected, error);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            Dispose();
            throw new SftpException(sent && mutation ? SftpFailure.UnknownOutcome : SftpFailure.Timeout, error);
        }
        catch (InvalidDataException error)
        {
            Dispose();
            throw new SftpException(sent && mutation ? SftpFailure.UnknownOutcome : SftpFailure.InvalidReply, error);
        }
        catch (SftpException error) when (error.Failure == SftpFailure.InvalidReply)
        {
            Dispose();
            if (sent && mutation) throw new SftpException(SftpFailure.UnknownOutcome, error);
            throw;
        }
        catch (SftpException error) when (sent && mutation && error.Failure == SftpFailure.Disconnected)
        {
            throw new SftpException(SftpFailure.UnknownOutcome, error);
        }
        finally
        {
            pending.TryRemove(id, out _);
            slots.Release();
        }
    }

    // The channel's ordered delivery worker is the only caller. A callback
    // adopts its bytes immediately and never waits for another packet or I/O.
    internal ValueTask ConsumeAsync(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        while (!bytes.IsEmpty)
        {
            if (frameBody is null)
            {
                prefixLength += CopyPrefix(ref bytes, framePrefix.AsSpan(prefixLength));
                if (prefixLength < framePrefix.Length) break;
                uint length = BinaryPrimitives.ReadUInt32BigEndian(framePrefix);
                if (length is < 5 or > MaxFrame) throw InvalidReply();
                frameBody = new byte[length];
            }
            if (bodyLength < 5)
            {
                bodyLength += CopyPrefix(ref bytes, frameBody.AsSpan(bodyLength, 5 - bodyLength));
                if (bodyLength < 5) break;
                if (!version.Task.IsCompleted)
                {
                    if (frameBody[0] != 2) throw InvalidReply();
                }
                else
                {
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(frameBody.AsSpan(1));
                    if (!pending.TryGetValue(id, out frameRequest) ||
                        (frameBody[0] == 103 && frameBody.Length > BlockSize + 9) ||
                        (frameRequest.FileData && frameBody[0] != 103 && frameBody.Length > 4096))
                        throw InvalidReply();
                }
            }
            int added = CopyPrefix(ref bytes, frameBody.AsSpan(bodyLength));
            bodyLength += added;
            if (added != 0) frameRequest?.Progress(wait);
            if (bodyLength == frameBody.Length)
            {
                bool delivered = frameRequest is { } request
                    ? request.Reply.TrySetResult(frameBody)
                    : version.TrySetResult(frameBody);
                if (!delivered) throw InvalidReply();
                frameBody = null;
                frameRequest = null;
                prefixLength = bodyLength = 0;
            }
        }
        return ValueTask.CompletedTask;
    }

    internal void CompleteInput(SftpException failure)
    {
        frameBody = null;
        frameRequest = null;
        Dispose();
        if (version.TrySetException(failure)) _ = version.Task.Exception;
        foreach (PendingRequest request in pending.Values) request.Reply.TrySetException(failure);
    }

    private static int CopyPrefix(ref ReadOnlySpan<byte> bytes, Span<byte> destination)
    {
        int count = Math.Min(bytes.Length, destination.Length);
        bytes[..count].CopyTo(destination);
        bytes = bytes[count..];
        return count;
    }

    private Task WriteFrameAsync(ReadOnlyMemory<byte> request, CancellationToken token)
    {
        byte[] frame = new byte[request.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)request.Length);
        request.CopyTo(frame.AsMemory(4));
        return channel.WriteAsync(frame, token);
    }

    private SftpEntry[] Names(byte[] data)
    {
        var reader = new SshReader(data);
        uint count = reader.Number();
        if (count > MaxEntries || count > reader.Length / 12) throw InvalidReply();
        var entries = new SftpEntry[count];
        for (int index = 0; index < entries.Length; index++)
        {
            byte[] name = reader.String().ToArray();
            reader.String();
            entries[index] = new SftpEntry(name, Attributes(ref reader));
        }
        reader.End();
        return entries;
    }

    private byte[] Handle(byte[] data)
    {
        var reader = new SshReader(data);
        byte[] handle = reader.String().ToArray();
        if (handle.Length > 256 || reader.Length != 0) throw InvalidReply();
        return handle;
    }

    private TransferAttributes ReadAttributes(byte[] data)
    {
        var reader = new SshReader(data);
        TransferAttributes result = Attributes(ref reader);
        reader.End();
        return result;
    }

    private TransferAttributes Attributes(ref SshReader reader)
    {
        uint flags = reader.Number();
        if ((flags & ~0x8000000fu) != 0) throw InvalidReply();
        long? size = null;
        if ((flags & 1) != 0)
        {
            ulong length = ((ulong)reader.Number() << 32) | reader.Number();
            if (length > long.MaxValue) throw InvalidReply();
            size = (long)length;
        }
        if ((flags & 2) != 0) { reader.Number(); reader.Number(); }
        TransferKind kind = (flags & 4) == 0 ? TransferKind.Unknown : (reader.Number() & 0xf000) switch
        {
            0x4000 => TransferKind.Directory, 0x8000 => TransferKind.File, 0xa000 => TransferKind.Link,
            _ => TransferKind.Unknown,
        };
        DateTimeOffset? access = null, modified = null;
        if ((flags & 8) != 0)
        {
            access = DateTimeOffset.FromUnixTimeSeconds(reader.Number());
            modified = DateTimeOffset.FromUnixTimeSeconds(reader.Number());
        }
        if ((flags & 0x80000000u) != 0)
        {
            uint count = reader.Number();
            if (count > reader.Length / 8) throw InvalidReply();
            while (count-- != 0) { reader.String(); reader.String(); }
        }
        return new TransferAttributes(kind, size, modified, access);
    }

    private static void Number64(SshWriter writer, ulong value)
    {
        writer.Number((uint)(value >> 32));
        writer.Number((uint)value);
    }

    private SftpException InvalidReply()
    {
        Dispose();
        return new SftpException(SftpFailure.InvalidReply);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        channel.Close();
    }

    private sealed class PendingRequest(CancellationTokenSource deadline, bool fileData)
    {
        public TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FileData => fileData;
        public void Progress(TimeSpan wait)
        {
            try { deadline.CancelAfter(wait); }
            catch (ObjectDisposedException) { }
        }
    }
}
