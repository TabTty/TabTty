using System.Security.Cryptography;
using System.Text;

namespace TabTty.Core;

public sealed record SshHostKey(SshDestination Destination, string Format, ReadOnlyMemory<byte> PublicBlob,
    string Fingerprint);

internal sealed class SshKeyExchange(SshPackets packets, SshDestination destination, IReadOnlyList<string> approvedFormats)
{
    private static readonly byte[] ClientVersion = "SSH-2.0-TabTty"u8.ToArray();
    private static readonly string[] Exchanges = ["mlkem768x25519-sha256", "curve25519-sha256", "curve25519-sha256@libssh.org",
        "ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521",
        "diffie-hellman-group16-sha512", "diffie-hellman-group14-sha256"];
    private static readonly string[] Ciphers = ["aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "aes256-ctr"];
    private static readonly string[] Macs = ["hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com"];
    private byte[]? serverVersion;
    private byte[]? sessionId;
    private bool strict;
    private long lastExchange;
    private readonly string[] hostSignatures = SshPublicKey.Signatures
        .OrderByDescending(name => approvedFormats.Contains(name.StartsWith("rsa-sha2-", StringComparison.Ordinal) ?
            "ssh-rsa" : name)).ToArray();
    public ReadOnlyMemory<byte> SessionId => sessionId ?? throw new InvalidOperationException("SSH keys are not established.");
    public string Details { get; private set; } = "";

    public bool IsDue => packets.RekeyDue || Environment.TickCount64 - lastExchange >= 3600_000;

    public async Task IdentifyAsync(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync("SSH-2.0-TabTty\r\n"u8.ToArray(), token).ConfigureAwait(false);
        byte[] one = new byte[1];
        byte[] line = new byte[256];
        int length = 0;
        int lines = 0;
        for (int bytes = 0; bytes < 64 * 1024 && lines < 256; bytes++)
        {
            await stream.ReadExactlyAsync(one, token).ConfigureAwait(false);
            if (length < line.Length) line[length] = one[0];
            length++;
            if (one[0] != '\n') continue;
            if (line.AsSpan(0, 4).SequenceEqual("SSH-"u8))
            {
                if (length is < 10 or > 255)
                    throw new InvalidDataException("Invalid SSH identification length.");
                int end = length - (line[length - 2] == '\r' ? 2 : 1);
                serverVersion = line.AsSpan(0, end).ToArray();
                if (serverVersion.Any(value => value is < 32 or > 126) ||
                    !(serverVersion.AsSpan().StartsWith("SSH-2.0-"u8) ||
                      serverVersion.AsSpan().StartsWith("SSH-1.99-"u8)))
                    throw new InvalidDataException("Unsupported SSH identification.");
                return;
            }
            Array.Clear(line, 0, Math.Min(length, 4));
            length = 0;
            lines++;
        }
        throw new InvalidDataException("The SSH server did not send its identification.");
    }

    public async Task<SshHostKey> ExchangeAsync(CancellationToken token, byte[]? serverInit = null,
        Func<byte[], CancellationToken, ValueTask>? processInFlight = null, Task<byte[]>? pendingRead = null)
    {
        bool initial = sessionId is null;
        using var clientInit = BuildInit(initial);
        await packets.SendAsync(clientInit.Memory, token).ConfigureAwait(false);
        serverInit ??= await ReadForExchangeAsync(20, initial, token, processInFlight, pendingRead).ConfigureAwait(false);
        Proposal proposal = ReadProposal(serverInit);
        if (initial)
        {
            strict = proposal.Exchange.Contains("kex-strict-s") ||
                proposal.Exchange.Contains("kex-strict-s-v00@openssh.com");
            if (strict && packets.ReceivedSequence != 1)
                throw new InvalidDataException("Strict SSH key exchange did not start with KEXINIT.");
        }

        string exchange = Select(Exchanges, proposal.Exchange, "key exchange");
        string host = Select(hostSignatures, proposal.Host, "host signature");
        string outgoing = Select(Ciphers, proposal.Outgoing, "outgoing cipher");
        string incoming = Select(Ciphers, proposal.Incoming, "incoming cipher");
        string? outgoingMac = outgoing.EndsWith("-ctr", StringComparison.Ordinal)
            ? Select(Macs, proposal.MacOut, "outgoing MAC") : null;
        string? incomingMac = incoming.EndsWith("-ctr", StringComparison.Ordinal)
            ? Select(Macs, proposal.MacIn, "incoming MAC") : null;
        _ = Select(["none"], proposal.CompressionOut, "outgoing compression");
        _ = Select(["none"], proposal.CompressionIn, "incoming compression");
        if (proposal.Guess && (proposal.Exchange[0] != exchange || proposal.Host[0] != host))
            _ = await packets.ReadAsync(token).ConfigureAwait(false);

        using var ephemeral = new SshAgreement(exchange);
        byte[] clientPublic = ephemeral.PublicKey;
        using (var request = new SshWriter())
        {
            request.Byte(30);
            request.String(clientPublic);
            await packets.SendAsync(request.Memory, token).ConfigureAwait(false);
        }

        byte[] reply = await ReadForExchangeAsync(31, initial, token).ConfigureAwait(false);
        var reader = new SshReader(reply);
        reader.Expect(31);
        byte[] hostBlob = reader.String().ToArray();
        byte[] serverPublic = reader.String().ToArray();
        byte[] signature = reader.String().ToArray();
        reader.End();

        byte[] shared = ephemeral.SharedSecret(serverPublic);
        byte[] exchangeHash = [];
        byte[]? transmitKey = null, receiveKey = null, transmitNonce = null, receiveNonce = null;
        byte[] transmitMacKey = [], receiveMacKey = [];
        try
        {
            using (var transcript = new SshWriter(128 + clientInit.Span.Length + serverInit.Length +
                hostBlob.Length + serverVersion!.Length + signature.Length))
            {
                transcript.String(ClientVersion);
                transcript.String(serverVersion);
                transcript.String(clientInit.Span);
                transcript.String(serverInit);
                transcript.String(hostBlob);
                transcript.String(clientPublic);
                transcript.String(serverPublic);
                transcript.Raw(shared);
                exchangeHash = OpenSsl.Hash(ephemeral.Hash, transcript.Span, ephemeral.HashSize);
            }
            _ = SshPublicKey.Verify(host, hostBlob, signature, exchangeHash);
            sessionId ??= exchangeHash.ToArray();
            transmitNonce = Derive(ephemeral.Hash, ephemeral.HashSize, shared, exchangeHash, (byte)'A', outgoingMac is null ? 12 : 16);
            receiveNonce = Derive(ephemeral.Hash, ephemeral.HashSize, shared, exchangeHash, (byte)'B', incomingMac is null ? 12 : 16);
            transmitKey = Derive(ephemeral.Hash, ephemeral.HashSize, shared, exchangeHash, (byte)'C',
                outgoing.StartsWith("aes128-", StringComparison.Ordinal) ? 16 : 32);
            receiveKey = Derive(ephemeral.Hash, ephemeral.HashSize, shared, exchangeHash, (byte)'D',
                incoming.StartsWith("aes128-", StringComparison.Ordinal) ? 16 : 32);
            if (outgoingMac is not null)
                transmitMacKey = Derive(ephemeral.Hash, ephemeral.HashSize, shared, exchangeHash, (byte)'E',
                    outgoingMac == Macs[0] ? 32 : 64);
            if (incomingMac is not null)
                receiveMacKey = Derive(ephemeral.Hash, ephemeral.HashSize, shared, exchangeHash, (byte)'F',
                    incomingMac == Macs[0] ? 32 : 64);
            CryptographicOperations.ZeroMemory(shared);
            await packets.SendAsync(new byte[] { 21 }, token).ConfigureAwait(false);
            packets.SetKeys(sending: true, transmitKey, transmitNonce, strict, outgoing, outgoingMac, transmitMacKey);
            transmitKey = transmitNonce = null;
            transmitMacKey = [];
            byte[] newKeys = await ReadForExchangeAsync(21, initial, token).ConfigureAwait(false);
            if (newKeys.Length != 1) throw new InvalidDataException("Invalid SSH NEWKEYS.");
            packets.SetKeys(sending: false, receiveKey, receiveNonce, strict, incoming, incomingMac, receiveMacKey);
            receiveKey = receiveNonce = null;
            receiveMacKey = [];
        }
        finally
        {
            foreach (byte[]? secret in new[] { shared, exchangeHash, transmitKey, receiveKey, transmitNonce, receiveNonce,
                transmitMacKey, receiveMacKey })
                if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }

        lastExchange = Environment.TickCount64;
        Details = $"{exchange}\n{host}\nTX {outgoing} {outgoingMac}\nRX {incoming} {incomingMac}\nStrict KEX {strict}";
        Span<byte> hash = stackalloc byte[32];
        OpenSsl.Sha256(hostBlob, hash);
        string format = host.StartsWith("rsa-sha2-", StringComparison.Ordinal) ? "ssh-rsa" : host;
        return new SshHostKey(destination, format, hostBlob, "SHA256:" + Convert.ToBase64String(hash).TrimEnd('='));
    }

    private async ValueTask<byte[]> ReadForExchangeAsync(byte expected, bool initial, CancellationToken token,
        Func<byte[], CancellationToken, ValueTask>? processInFlight = null, Task<byte[]>? pendingRead = null)
    {
        for (int messages = 0; messages < 128; messages++)
        {
            byte[] packet = pendingRead is null ? await packets.ReadAsync(token).ConfigureAwait(false) :
                await pendingRead.WaitAsync(token).ConfigureAwait(false);
            pendingRead = null;
            if (packet[0] == expected) return packet;
            if ((!strict || !initial) && packet[0] is 2 or 4) continue;
            if (processInFlight is not null && packet[0] is >= 80 and <= 100)
            {
                await processInFlight(packet, token).ConfigureAwait(false);
                continue;
            }
            throw new InvalidDataException($"Unexpected SSH message during key exchange ({packet[0]}).");
        }
        throw new InvalidDataException("Too many SSH messages during key exchange.");
    }

    private byte[] Derive(string hashName, int hashSize, ReadOnlySpan<byte> shared, ReadOnlySpan<byte> exchangeHash,
        byte discriminator, int length)
    {
        using var prefix = new SshWriter(shared.Length + exchangeHash.Length);
        prefix.Raw(shared);
        prefix.Raw(exchangeHash);
        using var material = new SshWriter(prefix.Span.Length + 1 + sessionId!.Length + length + hashSize);
        byte[] key = new byte[length];
        try
        {
            int offset = 0;
            while (offset < length)
            {
                material.Raw(prefix.Span);
                if (offset == 0)
                {
                    material.Byte(discriminator);
                    material.Raw(sessionId);
                }
                else material.Raw(key.AsSpan(0, offset));
                byte[] hash = OpenSsl.Hash(hashName, material.Span, hashSize);
                try
                {
                    int count = Math.Min(hashSize, length - offset);
                    hash.AsSpan(0, count).CopyTo(key.AsSpan(offset));
                    offset += count;
                }
                finally { CryptographicOperations.ZeroMemory(hash); }
                material.Clear();
            }
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private SshWriter BuildInit(bool initial)
    {
        var writer = new SshWriter(512);
        try
        {
            writer.Byte(20);
            Span<byte> cookie = stackalloc byte[16];
            OpenSsl.Fill(cookie);
            writer.Raw(cookie);
            string exchanges = string.Join(',', Exchanges);
            writer.Text(initial ? exchanges + ",ext-info-c,kex-strict-c,kex-strict-c-v00@openssh.com" : exchanges);
            writer.Text(string.Join(',', hostSignatures));
            writer.Text(string.Join(',', Ciphers));
            writer.Text(string.Join(',', Ciphers));
            writer.Text(string.Join(',', Macs));
            writer.Text(string.Join(',', Macs));
            writer.Text("none");
            writer.Text("none");
            writer.String([]);
            writer.String([]);
            writer.Byte(0);
            writer.Number(0);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static Proposal ReadProposal(byte[] packet)
    {
        var reader = new SshReader(packet);
        reader.Expect(20);
        reader.Take(16);
        string[] exchange = ReadNames(ref reader), host = ReadNames(ref reader),
            outgoing = ReadNames(ref reader), incoming = ReadNames(ref reader);
        string[] macOut = ReadNames(ref reader), macIn = ReadNames(ref reader);
        string[] compressionOut = ReadNames(ref reader), compressionIn = ReadNames(ref reader);
        ReadNames(ref reader);
        ReadNames(ref reader);
        bool guess = reader.Boolean();
        if (reader.Number() != 0) throw new InvalidDataException("Invalid SSH KEXINIT.");
        reader.End();
        return new Proposal(exchange, host, outgoing, incoming, macOut, macIn, compressionOut, compressionIn, guess);
    }

    internal static string[] ReadNames(ref SshReader reader)
    {
        var bytes = reader.String();
        foreach (byte value in bytes)
            if (value is < 33 or > 126) throw new InvalidDataException("Invalid SSH algorithm list.");
        if (bytes.IsEmpty) return [];
        string[] names = Encoding.ASCII.GetString(bytes).Split(',');
        if (names.Any(static name => name.Length is 0 or > 64))
            throw new InvalidDataException("Invalid SSH algorithm name.");
        return names;
    }

    private static string Select(string[] preferred, string[] peer, string category) =>
        preferred.FirstOrDefault(candidate => peer.Contains(candidate, StringComparer.Ordinal))
        ?? throw new InvalidDataException($"No compatible SSH {category} algorithm.");

    private sealed record Proposal(string[] Exchange, string[] Host, string[] Outgoing, string[] Incoming, string[] MacOut, string[] MacIn,
        string[] CompressionOut, string[] CompressionIn, bool Guess);
}
