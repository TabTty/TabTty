using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace TabTty.Core;

public sealed record SshPasswordRequest(SshDestination Destination, string Username,
    CredentialReference? SavedPassword, int Attempt);
public sealed record SshPrivateKeyRequest(SshDestination Destination, string Username, CredentialReference? SavedKey);
public sealed record SshPrompt(string Text, bool Echo);
public sealed record SshChallenge(SshDestination Destination, string Name, string Instruction, SshPrompt[] Prompts);

public sealed record SshInteraction(
    Func<SshHostKey, CancellationToken, Task<bool>> ApproveHost,
    Func<SshPasswordRequest, CancellationToken, Task<VaultSecret?>> RequestPassword,
    Action<string>? Message = null,
    Func<SshChallenge, CancellationToken, Task<VaultSecret[]?>>? RequestChallenge = null,
    Func<SshPrivateKeyRequest, CancellationToken, Task<VaultSecret?>>? RequestPrivateKey = null,
    Func<SshDestination, IReadOnlyList<string>>? ApprovedHostFormats = null);

public sealed class SshInteractionCancelledException() : OperationCanceledException("SSH interaction was cancelled.");

internal sealed partial class SshSessionWire : ISessionWire
{
    private const int ReceiveWindow = 1024 * 1024;
    private const int MaxChunk = 32768;
    private readonly TcpClient client;
    private readonly ResourceReservation receiveMemory = ApplicationResources.Receive.Reserve(2 * 1024 * 1024 - 64 * 1024);
    private readonly SshPackets packets;
    private readonly SshKeyExchange exchange;
    private readonly SshInteraction interaction;
    private readonly SshDestination destination;
    private readonly TimeSpan wait;
    private readonly bool livenessEnabled;
    private readonly long livenessInterval;
    private readonly Queue<byte[]> deferredControls = new();
    private TaskCompletionSource rekeyCompleted = NewSignal();
    private int deferredBytes;
    private int pendingKeepalives;
    private int unansweredWindows;
    private long lastLiveness;
    private string[]? serverSignatureAlgorithms;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim send = new(1);
    private readonly SemaphoreSlim receive = new(1);
    private readonly TaskCompletionSource packetKeysCleared = NewSignal();
    private readonly object creditGate = new();
    // Receive credit bounds bytes independently of the peer's packet fragmentation.
    // Record the two receive sources in a bitset rather than queueing a node per packet.
    private readonly byte[] incoming = new byte[ReceiveWindow];
    private readonly System.Collections.BitArray standardErrorBytes = new(ReceiveWindow);
    private readonly Channel<bool> incomingReady = Channel.CreateBounded<bool>(1);
    private int incomingStart, incomingCount;
    private TaskCompletionSource creditChanged = NewSignal();
    private int receiveCredit = ReceiveWindow;
    private int returnedCredit;
    private uint peerChannel;
    private uint peerWindow;
    private uint peerPacket;
    private int columns = 80;
    private int rows = 24;
    private int closed;
    private bool peerClosed;
    private bool peerEof;
    private bool sentClose;
    private readonly TaskCompletionSource peerCloseReceived = NewSignal();
    private readonly object shutdownGate = new();
    private Task? shutdown;
    private bool initialExtensionWindow = true;
    private bool finalExtensionsSeen;
    private Task? pump;
    private string? approvedFingerprint;
    private int messages;
    private int messageBytes;

    public TerminalReceiveSource Source { get; private set; }
    public string Details => exchange.Details;

    private SshSessionWire(TcpClient client, SshDestination destination, ConnectionProfile profile, SshInteraction interaction)
    {
        this.client = client;
        this.destination = destination;
        this.interaction = interaction;
        wait = TimeSpan.FromSeconds(profile.WaitSeconds);
        livenessEnabled = profile.SshLivenessEnabled;
        livenessInterval = profile.SshLivenessInterval * 1000L;
        packets = new SshPackets(client.GetStream());
        exchange = new SshKeyExchange(packets, destination, interaction.ApprovedHostFormats?.Invoke(destination) ?? []);
    }

    public static async Task<ISessionWire> OpenAsync(ConnectionProfile profile, SshInteraction interaction,
        Func<CancellationToken, Task<(int Columns, int Rows)>> getGrid,
        Func<ISessionWire, TerminalSession> initialize,
        CancellationToken connectToken, CancellationToken sessionToken)
    {
        var destination = SshDestination.Parse(profile.Host, profile.Port);
        var client = new TcpClient { NoDelay = true };
        SshSessionWire? wire = null;
        try
        {
            await client.ConnectAsync(destination.NetworkHost, destination.Port, connectToken).ConfigureAwait(false);
            wire = new SshSessionWire(client, destination, profile, interaction);
            await wire.exchange.IdentifyAsync(client.GetStream(), connectToken).ConfigureAwait(false);
            SshHostKey host = await wire.exchange.ExchangeAsync(connectToken).ConfigureAwait(false);
            await wire.ApproveAsync(host, sessionToken).ConfigureAwait(false);
            await wire.AuthenticateAsync(profile, sessionToken).ConfigureAwait(false);
            using var startup = new SshRemoteDeadline(sessionToken, wire.wait);
            await wire.OpenChannelAsync(profile, startup, getGrid, initialize).ConfigureAwait(false);
            wire.pump = Task.Run(wire.ReceiveAsync);
            return wire;
        }
        catch
        {
            client.Dispose();
            if (wire is not null)
            {
                wire.Dispose();
                await wire.packetKeysCleared.Task.ConfigureAwait(false);
                wire.receiveMemory.Dispose();
                wire.stop.Dispose();
            }
            throw;
        }
    }

    private async Task ApproveAsync(SshHostKey host, CancellationToken token)
    {
        if (approvedFingerprint == host.Fingerprint) return;
        if (!await interaction.ApproveHost(host, token).ConfigureAwait(false))
            throw new SshInteractionCancelledException();
        token.ThrowIfCancellationRequested();
        approvedFingerprint = host.Fingerprint;
    }

    private async Task AuthenticateAsync(ConnectionProfile profile, CancellationToken token)
    {
        using var serviceDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        serviceDeadline.CancelAfter(wait);
        using (var service = new SshWriter())
        {
            service.Byte(5);
            service.Text("ssh-userauth");
            await SendAuthenticationAsync(service.Memory, serviceDeadline.Token).ConfigureAwait(false);
        }
        var accepted = new SshReader(await ReadProtocolAsync(serviceDeadline.Token, authenticate: false).ConfigureAwait(false));
        accepted.Expect(6);
        if (!accepted.String().SequenceEqual("ssh-userauth"u8)) throw new InvalidDataException("Unexpected SSH service.");
        accepted.End();
        using (var probe = AuthenticationRequest(profile.SshUsername, "none"))
            await SendAuthenticationAsync(probe.Memory, token).ConfigureAwait(false);

        string[] allowed = [];
        IReadOnlyList<string> preferred = profile.SshMethods;
        string method = "none";
        int passwords = 0, conversations = 0, rounds = 0, stages = 0;
        bool usedPassword = false, needsChange = false;
        bool signedKey = false, usedKey = false;
        int keyProbes = 0;
        string[]? signatureAlgorithms = null;
        string? userSignature = null;
        SshPrivateKey? privateKey = null;
        try
        {
            while (true)
            {
                byte[] response = await ReadProtocolAsync(token, authenticate: true).ConfigureAwait(false);
                var result = new SshReader(response);
                byte type = result.Byte();
                if (type == 52)
                {
                    result.End();
                    return;
                }
                if (type == 60)
                {
                    if (method == "publicKey")
                    {
                        if (privateKey is null || signedKey ||
                            !result.String().SequenceEqual(Encoding.ASCII.GetBytes(userSignature!)) ||
                            !result.String().SequenceEqual(privateKey.PublicBlob))
                            throw new InvalidDataException("Unexpected SSH public-key approval.");
                        result.End();
                        using var request = AuthenticationRequest(profile.SshUsername, "publickey");
                        request.Byte(1);
                        request.Text(userSignature!);
                        request.String(privateKey.PublicBlob);
                        using var proof = new SshWriter(request.Span.Length + 64);
                        proof.String(exchange.SessionId.Span);
                        proof.Raw(request.Span);
                        byte[] signature = privateKey.Sign(userSignature!, proof.Span);
                        using var signatureBlob = new SshWriter();
                        signatureBlob.Text(userSignature!);
                        signatureBlob.String(signature);
                        request.String(signatureBlob.Span);
                        signedKey = true;
                        await SendAuthenticationAsync(request.Memory, token).ConfigureAwait(false);
                        continue;
                    }
                    if (method == "keyboardInteractive")
                    {
                        if (++rounds > 32) throw new IOException("SSH keyboard-interactive exceeded its round limit.");
                        await RespondToChallengeAsync(ParseChallenge(ref result), token).ConfigureAwait(false);
                        continue;
                    }
                    if (method != "password") throw new InvalidDataException("Unexpected SSH authentication challenge.");
                    Message(result.String());
                    result.String();
                    result.End();
                    needsChange = true;
                    passwords = 3;
                }
                else if (type == 51)
                {
                    allowed = SshKeyExchange.ReadNames(ref result);
                    bool partial = result.Boolean();
                    result.End();
                    if (partial)
                    {
                        if (method == "none" || ++stages >= 8)
                            throw new InvalidDataException("Invalid SSH partial authentication progression.");
                        usedPassword |= method == "password";
                        if (method == "publicKey")
                        {
                            usedKey = true;
                            privateKey?.Dispose();
                            privateKey = null;
                        }
                        passwords = conversations = 0;
                    }
                }
                else throw new InvalidDataException("Unexpected SSH authentication response.");

                method = preferred.FirstOrDefault(candidate => candidate switch
                {
                    "password" => !usedPassword && passwords < 3 && allowed.Contains("password"),
                    "keyboardInteractive" => conversations < 3 && interaction.RequestChallenge is not null &&
                        allowed.Contains("keyboard-interactive"),
                    "publicKey" => !usedKey && keyProbes < 8 && (signatureAlgorithms is null ||
                        keyProbes < signatureAlgorithms.Length) &&
                        interaction.RequestPrivateKey is not null && allowed.Contains("publickey"),
                    _ => false,
                }) ?? throw new IOException(needsChange ? "The SSH server requires a password change." :
                    "SSH authentication failed or requires an unavailable method.");

                if (method == "publicKey")
                {
                    if (privateKey is null)
                    {
                        using VaultSecret material = await interaction.RequestPrivateKey!(
                            new SshPrivateKeyRequest(destination, profile.SshUsername, profile.SshPrivateKey), token)
                            .ConfigureAwait(false) ?? throw new SshInteractionCancelledException();
                        privateKey = SshPrivateKey.FromPkcs8(material.Bytes.Span);
                        signatureAlgorithms = privateKey.Algorithm == "ssh-rsa"
                            ? new[] { "rsa-sha2-512", "rsa-sha2-256" }
                                .Where(name => serverSignatureAlgorithms is null || serverSignatureAlgorithms.Contains(name)).ToArray()
                            : serverSignatureAlgorithms is null || serverSignatureAlgorithms.Contains(privateKey.Algorithm)
                                ? [privateKey.Algorithm] : [];
                    }
                    if (signatureAlgorithms!.Length == 0)
                    {
                        allowed = allowed.Where(name => name != "publickey").ToArray();
                        using var none = AuthenticationRequest(profile.SshUsername, "none");
                        await SendAuthenticationAsync(none.Memory, token).ConfigureAwait(false);
                        continue;
                    }
                    userSignature = signatureAlgorithms[keyProbes++];
                    signedKey = false;
                    using var probe = AuthenticationRequest(profile.SshUsername, "publickey");
                    probe.Byte(0);
                    probe.Text(userSignature);
                    probe.String(privateKey.PublicBlob);
                    await SendAuthenticationAsync(probe.Memory, token).ConfigureAwait(false);
                }
                else if (method == "password")
                {
                    var request = new SshPasswordRequest(destination, profile.SshUsername, profile.SshPassword, ++passwords);
                    using VaultSecret secret = await interaction.RequestPassword(request, token).ConfigureAwait(false)
                        ?? throw new SshInteractionCancelledException();
                    if (secret.Bytes.Length > 16 * 1024) throw new InvalidDataException("The SSH password exceeds its size limit.");
                    _ = VaultContainer.StrictUtf8.GetCharCount(secret.Bytes.Span);
                    using var password = AuthenticationRequest(profile.SshUsername, "password", secret.Bytes.Length);
                    password.Byte(0);
                    password.String(secret.Bytes.Span);
                    await SendAuthenticationAsync(password.Memory, token).ConfigureAwait(false);
                }
                else
                {
                    conversations++;
                    rounds = 0;
                    using var interactive = AuthenticationRequest(profile.SshUsername, "keyboard-interactive");
                    interactive.String([]);
                    interactive.String([]);
                    await SendAuthenticationAsync(interactive.Memory, token).ConfigureAwait(false);
                }
            }
        }
        finally { privateKey?.Dispose(); }
    }

    private SshChallenge ParseChallenge(ref SshReader reader)
    {
        int total = 0;
        string name = ReadPrompt(ref reader, ref total);
        string instruction = ReadPrompt(ref reader, ref total);
        _ = ReadPrompt(ref reader, ref total);
        uint count = reader.Number();
        if (count > 32) throw new InvalidDataException("Too many SSH keyboard-interactive prompts.");
        var prompts = new SshPrompt[count];
        for (int i = 0; i < prompts.Length; i++)
            prompts[i] = new SshPrompt(ReadPrompt(ref reader, ref total), reader.Boolean());
        reader.End();
        return new SshChallenge(destination, name, instruction, prompts);
    }

    private static string ReadPrompt(ref SshReader reader, ref int total)
    {
        var bytes = reader.String();
        if (bytes.Length > 8192 || (total += bytes.Length) > 65536)
            throw new InvalidDataException("SSH keyboard-interactive prompt exceeds its size limit.");
        return InertText(VaultContainer.StrictUtf8.GetString(bytes));
    }

    private async Task RespondToChallengeAsync(SshChallenge challenge, CancellationToken token)
    {
        VaultSecret[] answers = await interaction.RequestChallenge!(challenge, token).ConfigureAwait(false)
            ?? throw new SshInteractionCancelledException();
        try
        {
            if (answers.Length != challenge.Prompts.Length)
                throw new InvalidDataException("Incorrect SSH keyboard-interactive response count.");
            int length = 0;
            foreach (VaultSecret answer in answers)
            {
                if (answer.Bytes.Length > 8192 || (length += answer.Bytes.Length) > 65536)
                    throw new InvalidDataException("SSH keyboard-interactive response exceeds its size limit.");
                _ = VaultContainer.StrictUtf8.GetCharCount(answer.Bytes.Span);
            }
            using var response = new SshWriter(length + 5 + answers.Length * 4);
            response.Byte(61);
            response.Number((uint)answers.Length);
            foreach (VaultSecret answer in answers) response.String(answer.Bytes.Span);
            await SendAuthenticationAsync(response.Memory, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (VaultSecret answer in answers) answer.Dispose();
        }
    }

    private static SshWriter AuthenticationRequest(string username, string method, int extra = 0)
    {
        var request = new SshWriter(Encoding.UTF8.GetByteCount(username) + extra + 128);
        request.Byte(50);
        request.Text(username);
        request.Text("ssh-connection");
        request.Text(method);
        return request;
    }

    private async Task OpenChannelAsync(ConnectionProfile profile, SshRemoteDeadline deadline,
        Func<CancellationToken, Task<(int Columns, int Rows)>> getGrid, Func<ISessionWire, TerminalSession> initialize)
    {
        CancellationToken token = deadline.Token;
        using (var channel = new SshWriter())
        {
            channel.Byte(90);
            channel.Text("session");
            channel.Number(0);
            channel.Number(ReceiveWindow);
            channel.Number(MaxChunk);
            await packets.SendAsync(channel.Memory, token).ConfigureAwait(false);
        }
        var reply = new SshReader(await ReadProtocolAsync(token, authenticate: false, deadline).ConfigureAwait(false));
        reply.Expect(91);
        RequireChannel(ref reply);
        peerChannel = reply.Number();
        peerWindow = reply.Number();
        peerPacket = reply.Number();
        if (peerPacket == 0) throw new InvalidDataException("Invalid SSH peer packet limit.");
        reply.End();
        var grid = await getGrid(token).ConfigureAwait(false);
        (columns, rows) = grid;
        TerminalSession session = initialize(this);
        using (var pty = ChannelMessage(98))
        {
            pty.Text("pty-req");
            pty.Byte(1);
            pty.Text(profile.AdvertisedType);
            pty.Number((uint)grid.Columns);
            pty.Number((uint)grid.Rows);
            pty.Number(0);
            pty.Number(0);
            pty.String(profile.TransmitEncoding == "utf-8" ? new byte[] { 42, 0, 0, 0, 1, 0 } : new byte[] { 0 });
            await packets.SendAsync(pty.Memory, token).ConfigureAwait(false);
        }
        await RequireChannelSuccessAsync(deadline, session).ConfigureAwait(false);
        using (var shell = ChannelMessage(98))
        {
            shell.Text("shell");
            shell.Byte(1);
            await packets.SendAsync(shell.Memory, token).ConfigureAwait(false);
        }
        await RequireChannelSuccessAsync(deadline, session).ConfigureAwait(false);
    }

    private async Task RequireChannelSuccessAsync(SshRemoteDeadline deadline, TerminalSession session)
    {
        CancellationToken token = deadline.Token;
        while (true)
        {
            byte[] packet = await ReadProtocolAsync(token, authenticate: false, deadline).ConfigureAwait(false);
            if (packet[0] == 99)
            {
                var reply = new SshReader(packet);
                reply.Expect(99);
                RequireChannel(ref reply);
                reply.End();
                return;
            }
            if (packet[0] is not (93 or 94 or 95))
                throw new IOException("The SSH server refused terminal startup.");
            await ProcessAsync(packet, token, startup: session).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadProtocolAsync(CancellationToken token, bool authenticate, SshRemoteDeadline? stage = null)
    {
        for (int messages = 0; messages < 128; messages++)
        {
            byte[] packet = await ReadRemoteAsync(token).ConfigureAwait(false);
            bool initialExtension = initialExtensionWindow;
            initialExtensionWindow = false;
            if (packet[0] == 7)
            {
                if (!initialExtension && (!authenticate || finalExtensionsSeen))
                    throw new InvalidDataException("SSH extensions arrived outside their negotiated position.");
                ReadExtensions(packet);
                if (initialExtension) continue;
                finalExtensionsSeen = true;
                packet = await ReadRemoteAsync(token).ConfigureAwait(false);
                if (packet[0] != 52) throw new InvalidDataException("Final SSH extensions must precede authentication success.");
                return packet;
            }
            if (packet[0] == 20)
            {
                using var rekeyDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                rekeyDeadline.CancelAfter(wait);
                SshHostKey host = await exchange.ExchangeAsync(rekeyDeadline.Token, packet).ConfigureAwait(false);
                stage?.Pause();
                try { await ApproveAsync(host, stage?.Parent ?? token).ConfigureAwait(false); }
                finally { stage?.Resume(); }
                continue;
            }
            if (HandleTransport(packet, authenticate)) continue;
            if (packet[0] == 80)
            {
                var reader = new SshReader(packet);
                reader.Byte();
                reader.String();
                if (reader.Boolean())
                    await packets.SendAsync(new byte[] { 82 }, token).ConfigureAwait(false);
                continue;
            }
            return packet;
        }
        throw new InvalidDataException("Too many SSH control messages while awaiting a protocol response.");
    }

    private async ValueTask<byte[]> ReadRemoteAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait);
        return await packets.ReadAsync(deadline.Token).ConfigureAwait(false);
    }

    private async ValueTask SendAuthenticationAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait);
        await packets.SendAsync(packet, deadline.Token).ConfigureAwait(false);
    }

    private bool HandleTransport(byte[] packet, bool authenticate)
    {
        var reader = new SshReader(packet);
        switch (reader.Byte())
        {
            case 1:
                reader.Number();
                Message(reader.String());
                reader.String();
                reader.End();
                throw new IOException("The SSH server disconnected.");
            case 2:
                reader.String();
                reader.End();
                return true;
            case 4:
                reader.Boolean();
                Message(reader.String());
                reader.String();
                reader.End();
                return true;
            case 7:
                throw new InvalidDataException("SSH extensions arrived outside their negotiated position.");
            case 53 when authenticate:
                Message(reader.String());
                reader.String();
                reader.End();
                return true;
            default:
                return false;
        }
    }

    private void ReadExtensions(byte[] packet)
    {
        var reader = new SshReader(packet);
        reader.Expect(7);
        uint count = reader.Number();
        if (count > reader.Length / 8) throw new InvalidDataException("Invalid SSH extensions.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
        {
            var name = reader.String();
            var value = reader.String();
            if (name.IsEmpty || name.ContainsAnyExceptInRange((byte)33, (byte)126) ||
                !seen.Add(Encoding.ASCII.GetString(name))) throw new InvalidDataException("Invalid SSH extension name.");
            if (name.SequenceEqual("server-sig-algs"u8))
            {
                using var list = new SshWriter(value.Length + 4);
                list.String(value);
                var names = new SshReader(list.Span);
                serverSignatureAlgorithms = SshKeyExchange.ReadNames(ref names);
            }
        }
        reader.End();
    }

    private void Message(ReadOnlySpan<byte> body)
    {
        if (messages >= 64 || messageBytes >= 256 * 1024 || interaction.Message is null) return;
        int length = Math.Min(body.Length, Math.Min(16 * 1024, 256 * 1024 - messageBytes));
        string safe = InertText(Encoding.UTF8.GetString(body[..length]));
        if (length < body.Length) safe += " […]";
        messages++;
        messageBytes += length;
        interaction.Message(safe);
    }

    private static string InertText(string text)
    {
        var safe = new StringBuilder();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value is not ('\r' or '\n' or '\t') ||
                Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format)
                safe.Append($"\\u{{{rune.Value:X}}}");
            else
                safe.Append(rune);
        }
        return safe.ToString();
    }

    internal Task PeerClosed => peerCloseReceived.Task;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await receive.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                lock (creditGate)
                {
                    if (incomingCount != 0) return ReadBuffered(buffer.Span);
                    if (peerEof) return 0;
                }
                if (!await incomingReady.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Closing may race with the final buffer notification.
                    lock (creditGate) return incomingCount == 0 ? 0 : ReadBuffered(buffer.Span);
                }
                incomingReady.Reader.TryRead(out _);
            }
        }
        finally { receive.Release(); }
    }

    private int ReadBuffered(Span<byte> buffer)
    {
        bool standardError = standardErrorBytes[incomingStart];
        Source = standardError ? TerminalReceiveSource.StandardError : TerminalReceiveSource.Data;
        int limit = Math.Min(buffer.Length, incomingCount);
        int count = 0;
        int offset = incomingStart;
        while (count < limit && standardErrorBytes[offset] == standardError)
        {
            buffer[count++] = incoming[offset];
            offset = (offset + 1) & (ReceiveWindow - 1);
        }
        incomingStart = offset;
        incomingCount -= count;
        return count;
    }

    private void EnqueueTerminalPayload(ReadOnlySpan<byte> bytes, bool standardError)
    {
        lock (creditGate)
        {
            if (bytes.Length > incoming.Length - incomingCount)
                throw new InvalidDataException("SSH receive window exceeded.");
            int start = (incomingStart + incomingCount) & (ReceiveWindow - 1);
            int first = Math.Min(bytes.Length, incoming.Length - start);
            bytes[..first].CopyTo(incoming.AsSpan(start));
            bytes[first..].CopyTo(incoming);
            for (int i = 0; i < bytes.Length; i++)
                standardErrorBytes[(start + i) & (ReceiveWindow - 1)] = standardError;
            incomingCount += bytes.Length;
        }
        incomingReady.Writer.TryWrite(true);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        while (!buffer.IsEmpty)
        {
            linked.Token.ThrowIfCancellationRequested();
            await send.WaitAsync(linked.Token).ConfigureAwait(false);
            Task? waitForCredit = null;
            try
            {
                int count;
                lock (creditGate)
                {
                    if (peerClosed) throw new IOException("The SSH channel is closed.");
                    count = exchange.IsDue ? 0 : (int)Math.Min((uint)Math.Min(MaxChunk, buffer.Length), Math.Min(peerWindow, peerPacket));
                    if (count == 0) waitForCredit = exchange.IsDue ? rekeyCompleted.Task : creditChanged.Task;
                    else peerWindow -= (uint)count;
                }
                if (count != 0)
                {
                    using var data = ChannelMessage(94, count + 32);
                    data.String(buffer.Span[..count]);
                    await packets.SendAsync(data.Memory, linked.Token).ConfigureAwait(false);
                    buffer = buffer[count..];
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally { send.Release(); }
            if (waitForCredit is not null) await waitForCredit.WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }

    public void Acknowledge(int count)
    {
        int adjust = CollectCredit(count);
        if (adjust != 0) _ = ReturnCreditAsync(adjust);
    }

    private int CollectCredit(int count)
    {
        int adjust = 0;
        lock (creditGate)
        {
            returnedCredit += count;
            if (returnedCredit >= ReceiveWindow / 2 || receiveCredit <= MaxChunk)
            {
                adjust = returnedCredit;
                returnedCredit = 0;
                receiveCredit += adjust;
            }
        }
        return adjust;
    }

    public async Task ResizeAsync(int width, int height, CancellationToken token)
    {
        if (width == columns && height == rows) return;
        using var change = ChannelMessage(98);
        change.Text("window-change");
        change.Byte(0);
        change.Number((uint)width);
        change.Number((uint)height);
        change.Number(0);
        change.Number(0);
        await SendControlAsync(change.Memory, token).ConfigureAwait(false);
        columns = width;
        rows = height;
    }

    private async Task ReturnCreditAsync(int count)
    {
        try
        {
            await SendCreditAsync(count, stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or CryptographicException
            or OperationCanceledException or ObjectDisposedException)
        {
            if (!stop.IsCancellationRequested)
            {
                incomingReady.Writer.TryComplete(exception);
                Dispose();
            }
        }
    }

    private async Task SendCreditAsync(int count, CancellationToken token)
    {
        using var adjust = ChannelMessage(93);
        adjust.Number((uint)count);
        await SendControlAsync(adjust.Memory, token).ConfigureAwait(false);
    }

    private async Task ReceiveAsync()
    {
        Task<byte[]>? read = null;
        try
        {
            lastLiveness = Environment.TickCount64;
            read = packets.ReadAsync(stop.Token).AsTask();
            while (!peerClosed && !stop.IsCancellationRequested)
            {
                Task timer = Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                Task ready = await Task.WhenAny(read, timer).ConfigureAwait(false);
                if (ready == timer && !exchange.IsDue)
                {
                    await CheckLivenessAsync().ConfigureAwait(false);
                    continue;
                }
                if (ready == timer)
                {
                    await RekeyAsync(null, read).ConfigureAwait(false);
                }
                else
                {
                    byte[] packet = await read.ConfigureAwait(false);
                    if (packet[0] == 20) await RekeyAsync(packet, null).ConfigureAwait(false);
                    else await ProcessAsync(packet, stop.Token).ConfigureAwait(false);
                    if (!peerClosed && exchange.IsDue) await RekeyAsync(null, null).ConfigureAwait(false);
                }
                lastLiveness = Environment.TickCount64;
                unansweredWindows = 0;
                if (!peerClosed) read = packets.ReadAsync(stop.Token).AsTask();
            }
            incomingReady.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            incomingReady.Writer.TryComplete(stop.IsCancellationRequested ? null : exception);
            Dispose();
        }
        finally
        {
            // A timed-out exchange may still own its original stream read.
            EndTransferChannel();
            // End that read before the packet keys can be released.
            if (read is { IsCompleted: false })
            {
                stop.Cancel();
                client.Dispose();
                try { await read.ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or SocketException or
                    OperationCanceledException or ObjectDisposedException or CryptographicException) { }
            }
            peerCloseReceived.TrySetResult();
        }
    }

    private async Task RekeyAsync(byte[]? init, Task<byte[]>? pendingRead)
    {
        await send.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(wait);
            SshHostKey host = await exchange.ExchangeAsync(deadline.Token, init,
                (packet, token) => ProcessAsync(packet, token, duringExchange: true), pendingRead).ConfigureAwait(false);
            await ApproveAsync(host, stop.Token).ConfigureAwait(false);
            while (deferredControls.TryDequeue(out byte[]? response))
            {
                deferredBytes -= response.Length;
                await packets.SendAsync(response, stop.Token).ConfigureAwait(false);
            }
            lock (creditGate)
            {
                rekeyCompleted.TrySetResult();
                rekeyCompleted = NewSignal();
            }
        }
        finally { send.Release(); }
    }

    private async ValueTask ProcessAsync(byte[] packet, CancellationToken token, bool duringExchange = false,
        TerminalSession? startup = null)
    {
        if (HandleTransport(packet, authenticate: false)) return;
        var reader = new SshReader(packet);
        byte type = reader.Byte();
        if (type == 80)
        {
            reader.String();
            if (reader.Boolean())
                await RespondAsync(new byte[] { 82 }, token, duringExchange).ConfigureAwait(false);
            return;
        }
        if (type is 81 or 82)
        {
            reader.End();
            if (pendingKeepalives == 0) throw new InvalidDataException("Unexpected SSH global request reply.");
            pendingKeepalives--;
            return;
        }
        if (type == 90)
        {
            reader.String();
            uint other = reader.Number();
            reader.Number();
            reader.Number();
            using var rejection = new SshWriter();
            rejection.Byte(92);
            rejection.Number(other);
            rejection.Number(1);
            rejection.String([]);
            rejection.String([]);
            await RespondAsync(rejection.Memory, token, duringExchange).ConfigureAwait(false);
            return;
        }
        uint channel = reader.Number();
        if (channel != 0)
        {
            await ProcessTransferAsync(channel, type, packet.AsMemory(5), token, duringExchange).ConfigureAwait(false);
            return;
        }
        switch (type)
        {
            case 93:
                uint amount = reader.Number();
                reader.End();
                lock (creditGate)
                {
                    if (amount > uint.MaxValue - peerWindow) throw new InvalidDataException("SSH channel window overflow.");
                    peerWindow += amount;
                    SignalCredit();
                }
                break;
            case 94:
            case 95:
                uint dataType = type == 95 ? reader.Number() : 0;
                byte[] bytes = reader.String().ToArray();
                reader.End();
                if (peerEof || bytes.Length > MaxChunk) throw new InvalidDataException("Unexpected SSH channel data.");
                lock (creditGate)
                {
                    if (bytes.Length > receiveCredit) throw new InvalidDataException("SSH receive window exceeded.");
                    receiveCredit -= bytes.Length;
                }
                if (bytes.Length != 0)
                {
                    var source = type == 94 ? TerminalReceiveSource.Data : TerminalReceiveSource.StandardError;
                    bool terminalPayload = type == 94 || dataType == 1;
                    if (startup is not null)
                    {
                        if (terminalPayload) await startup.ProcessPayloadAsync(source, bytes, token).ConfigureAwait(false);
                        int adjust = CollectCredit(bytes.Length);
                        if (adjust != 0) await SendCreditAsync(adjust, token).ConfigureAwait(false);
                    }
                    else if (terminalPayload) EnqueueTerminalPayload(bytes, type == 95);
                    else Acknowledge(bytes.Length);
                }
                break;
            case 96:
                reader.End();
                lock (creditGate) peerEof = true;
                incomingReady.Writer.TryWrite(true);
                break;
            case 97:
                reader.End();
                if (!sentClose)
                {
                    using var close = ChannelMessage(97);
                    await RespondCloseAsync(close.Memory, token, duringExchange).ConfigureAwait(false);
                }
                lock (creditGate)
                {
                    peerClosed = true;
                    SignalCredit();
                }
                peerCloseReceived.TrySetResult();
                break;
            case 98:
                reader.String();
                if (reader.Boolean())
                {
                    using var failure = ChannelMessage(100);
                    await RespondAsync(failure.Memory, token, duringExchange).ConfigureAwait(false);
                }
                break;
            default:
                throw new InvalidDataException($"Unexpected SSH channel message ({type}).");
        }
    }

    private async Task RespondCloseAsync(ReadOnlyMemory<byte> close, CancellationToken token, bool defer)
    {
        if (defer)
        {
            if (!sentClose)
            {
                sentClose = true;
                await RespondAsync(close, token, defer: true).ConfigureAwait(false);
            }
            return;
        }
        await send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (sentClose || Volatile.Read(ref closed) != 0) return;
            sentClose = true;
            await packets.SendAsync(close, token).ConfigureAwait(false);
        }
        finally { send.Release(); }
    }

    public ValueTask CloseAsync()
    {
        lock (shutdownGate) return new ValueTask(shutdown ??= CloseChannelAsync());
    }

    private async Task CloseChannelAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (Volatile.Read(ref closed) == 0)
            {
                using var close = ChannelMessage(97);
                await RespondCloseAsync(close.Memory, deadline.Token, defer: false).ConfigureAwait(false);
                await peerCloseReceived.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
            or ObjectDisposedException or CryptographicException or InvalidDataException) { }
        finally { Dispose(); }
        await packetKeysCleared.Task.ConfigureAwait(false);
        await receive.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (creditGate) incomingCount = 0;
            incomingReady.Reader.TryRead(out _);
            receiveMemory.Dispose();
        }
        finally { receive.Release(); }
    }

    private async Task CheckLivenessAsync()
    {
        if (!livenessEnabled || peerClosed || Environment.TickCount64 - lastLiveness < livenessInterval) return;
        lastLiveness = Environment.TickCount64;
        if (pendingKeepalives > 0 && ++unansweredWindows >= 3)
            throw new IOException("The SSH server did not respond to liveness checks.");
        if (pendingKeepalives >= 3) return;
        using var request = new SshWriter();
        request.Byte(80);
        request.Text("keepalive@openssh.com");
        request.Byte(1);
        pendingKeepalives++;
        await SendControlAsync(request.Memory, stop.Token).ConfigureAwait(false);
    }

    private Task RespondAsync(ReadOnlyMemory<byte> response, CancellationToken token, bool defer)
    {
        if (!defer) return SendControlAsync(response, token);
        if (deferredControls.Count == 128 || response.Length > 256 * 1024 - deferredBytes)
            throw new IOException("The SSH deferred control queue reached its limit.");
        byte[] saved = response.ToArray();
        deferredBytes += saved.Length;
        deferredControls.Enqueue(saved);
        return Task.CompletedTask;
    }

    private SshWriter ChannelMessage(byte type, int capacity = 256)
    {
        var packet = new SshWriter(capacity);
        packet.Byte(type);
        packet.Number(peerChannel);
        return packet;
    }

    private async Task SendControlAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        await send.WaitAsync(token).ConfigureAwait(false);
        try { await packets.SendAsync(packet, token).ConfigureAwait(false); }
        catch
        {
            Dispose();
            throw;
        }
        finally { send.Release(); }
    }

    private static void RequireChannel(ref SshReader reader)
    {
        if (reader.Number() != 0) throw new InvalidDataException("Invalid SSH channel identifier.");
    }

    private void SignalCredit()
    {
        creditChanged.TrySetResult();
        creditChanged = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel();
        client.Dispose();
        _ = ClearAsync();
    }

    private async Task ClearAsync()
    {
        try
        {
            if (pump is not null) await pump.ConfigureAwait(false);
            await send.WaitAsync().ConfigureAwait(false);
            try { packets.Dispose(); }
            finally { send.Release(); }
            packetKeysCleared.TrySetResult();
        }
        catch (Exception exception) { packetKeysCleared.TrySetException(exception); }
    }

}
