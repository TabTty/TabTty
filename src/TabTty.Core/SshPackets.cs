using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TabTty.Core;

internal sealed class SshPackets(Stream stream) : IDisposable
{
    private const int MaxPacket = 256 * 1024;
    private Direction? receive;
    private Direction? transmit;
    private uint receiveSequence, transmitSequence;
    public uint ReceivedSequence => receiveSequence;
    public bool RekeyDue => receive?.RekeyDue == true || transmit?.RekeyDue == true;

    public void SetKeys(bool sending, byte[] key, byte[] nonce, bool strict, string cipher, string? mac, byte[] macKey)
    {
        var next = new Direction(key, nonce, sending, cipher, mac, macKey);
        if (sending)
        {
            transmit?.Dispose();
            transmit = next;
            if (strict) transmitSequence = 0;
        }
        else
        {
            receive?.Dispose();
            receive = next;
            if (strict) receiveSequence = 0;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken token,
        CancellationToken admission = default, Action? committed = null, bool padCredentials = false)
    {
        Direction? state = transmit;
        if (padCredentials && state is null)
            throw new InvalidOperationException("SSH credentials require encrypted transport.");
        int block = padCredentials ? 128 : state is null ? 8 : 16;
        int align = state is null ? 4 : 0;
        int padding = block - (payload.Length + 1 + align) % block;
        if (padding < 4) padding += block;
        int bodySize = 1 + payload.Length + padding;
        int tagSize = state?.TagSize ?? 0;
        int totalSize = checked(4 + bodySize + tagSize);
        if (payload.IsEmpty || totalSize > MaxPacket) throw new InvalidDataException("Invalid SSH packet size.");
        byte[] packet = new byte[totalSize];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)bodySize);
        if (state is null)
        {
            BuildBody(packet.AsSpan(4), payload.Span, padding);
            admission.ThrowIfCancellationRequested();
            committed?.Invoke();
        }
        else
        {
            byte[] body = new byte[bodySize];
            try
            {
                BuildBody(body, payload.Span, padding);
                state.CheckBudget(totalSize);
                admission.ThrowIfCancellationRequested();
                committed?.Invoke();
                if (state.Ctr is { } ctr)
                {
                    ctr.Transform(body, packet.AsSpan(4, bodySize));
                    OpenSsl.Hmac(state.MacHash!, state.MacKey, transmitSequence, packet.AsSpan(0, 4),
                        packet.AsSpan(4, bodySize)).CopyTo(packet.AsSpan(4 + bodySize));
                }
                else
                    OpenSsl.Gcm(true, state.Key, state.Nonce, packet.AsSpan(0, 4), body,
                        packet.AsSpan(4, bodySize), packet.AsSpan(^tagSize));
                state.Advance(totalSize);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        transmitSequence++;
        await stream.WriteAsync(packet, token).ConfigureAwait(false);
    }

    internal async ValueTask SendChannelAsync(ReadOnlyMemory<byte> payload, CancellationToken lifetime,
        CancellationToken admission, Action commitCredit, SshSendProgress progress, int count)
    {
        try
        {
            await SendAsync(payload, lifetime, admission, () =>
            {
                commitCredit();
                progress.Commit(count);
            }).ConfigureAwait(false);
            progress.Submitted();
        }
        catch
        {
            progress.Failed();
            throw;
        }
    }

    public async ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        Direction? state = receive;
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(length);
        int tagSize = state?.TagSize ?? 0;
        if (size < 6 || size > MaxPacket - 4 - tagSize ||
            (state is null ? (size + 4) % 8 != 0 : size % 16 != 0))
            throw new InvalidDataException("Invalid SSH packet framing.");
        int bodySize = (int)size;
        byte[] wireBody = new byte[bodySize + tagSize];
        await stream.ReadExactlyAsync(wireBody, token).ConfigureAwait(false);
        byte[] body = state is null ? wireBody : new byte[bodySize];
        try
        {
            if (state is not null)
            {
                int total = 4 + wireBody.Length;
                state.CheckBudget(total);
                if (state.Ctr is { } ctr)
                {
                    byte[] expected = OpenSsl.Hmac(state.MacHash!, state.MacKey, receiveSequence, length,
                        wireBody.AsSpan(0, bodySize));
                    if (!CryptographicOperations.FixedTimeEquals(expected, wireBody.AsSpan(bodySize)))
                        throw new CryptographicException("SSH packet authentication failed.");
                    ctr.Transform(wireBody.AsSpan(0, bodySize), body);
                }
                else
                    OpenSsl.Gcm(false, state.Key, state.Nonce, length, wireBody.AsSpan(0, bodySize), body, wireBody.AsSpan(^tagSize));
                state.Advance(total);
            }
            receiveSequence++;
            int padding = body[0];
            int payloadSize = bodySize - padding - 1;
            if (padding < 4 || payloadSize < 1) throw new InvalidDataException("Invalid SSH packet padding.");
            return body.AsSpan(1, payloadSize).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    public void Dispose()
    {
        receive?.Dispose();
        transmit?.Dispose();
    }

    private static void BuildBody(Span<byte> body, ReadOnlySpan<byte> payload, int padding)
    {
        body[0] = (byte)padding;
        payload.CopyTo(body[1..]);
        OpenSsl.Fill(body[^padding..]);
    }

    private sealed class Direction : IDisposable
    {
        public byte[] Key { get; }
        public byte[] Nonce { get; }
        public byte[] MacKey { get; }
        public OpenSsl.Ctr? Ctr { get; }
        public string? MacHash { get; }
        public int TagSize { get; }
        public long ProtectedBytes { get; private set; }
        private long packets;
        private long blocks;
        public bool RekeyDue => ProtectedBytes >= 1L << 30 || packets >= (1L << 31) - 512 ||
            blocks >= (1L << 32) - 65536 ||
            (Ctr is null && BinaryPrimitives.ReadUInt64BigEndian(Nonce.AsSpan(4)) >= ulong.MaxValue - 512);
        public Direction(byte[] key, byte[] nonce, bool sending, string cipher, string? mac, byte[] macKey)
        {
            Key = key;
            Nonce = nonce;
            MacKey = macKey;
            if (cipher.EndsWith("-ctr", StringComparison.Ordinal))
            {
                MacHash = mac == "hmac-sha2-512-etm@openssh.com" ? "SHA512" : "SHA256";
                TagSize = MacHash == "SHA512" ? 64 : 32;
                Ctr = new OpenSsl.Ctr(key, nonce, sending);
            }
            else TagSize = 16;
        }
        public void CheckBudget(int bytes)
        {
            if (ProtectedBytes + bytes > 2L << 30 || packets >= 1L << 31 ||
                blocks + (bytes - 4 - TagSize) / 16 > 1L << 32 ||
                (Ctr is null && BinaryPrimitives.ReadUInt64BigEndian(Nonce.AsSpan(4)) == ulong.MaxValue))
                throw new CryptographicException("SSH traffic key usage exceeded its safety limit.");
        }
        public void Advance(int bytes)
        {
            ProtectedBytes += bytes;
            packets++;
            blocks += (bytes - 4 - TagSize) / 16;
            if (Ctr is null)
            {
                Span<byte> invocation = Nonce.AsSpan(4);
                BinaryPrimitives.WriteUInt64BigEndian(invocation, BinaryPrimitives.ReadUInt64BigEndian(invocation) + 1);
            }
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            CryptographicOperations.ZeroMemory(Nonce);
            CryptographicOperations.ZeroMemory(MacKey);
            Ctr?.Dispose();
        }
    }
}
