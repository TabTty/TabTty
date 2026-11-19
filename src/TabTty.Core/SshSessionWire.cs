using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace TabTty.Core;

public sealed record SshPasswordRequest(SshDestination Destination, string Username,
    CredentialReference? SavedPassword, int Attempt);
public sealed record SshPrivateKeyRequest(SshDestination Destination, string Username, CredentialReference? SavedKey,
    bool CanTryAnotherMethod = false);
public sealed record SshPrompt(string Text, bool Echo);
public sealed record SshChallenge(SshDestination Destination, string Name, string Instruction, SshPrompt[] Prompts);
public sealed record SshSavedCredentialRequest(string Username, CredentialReference? Password, CredentialReference? PrivateKey);
public sealed record SshCredentialAvailability(IReadOnlyDictionary<string, string> Unavailable);

public sealed record SshInteraction(
    Func<SshHostKey, CancellationToken, Task<bool>> ApproveHost,
    Func<SshPasswordRequest, CancellationToken, Task<VaultSecret?>> RequestPassword,
    Action<string>? Message = null,
    Func<SshChallenge, CancellationToken, Task<VaultSecret[]?>>? RequestChallenge = null,
    Func<SshPrivateKeyRequest, CancellationToken, Task<VaultSecret?>>? RequestPrivateKey = null,
    Func<SshDestination, IReadOnlyList<string>>? ApprovedHostFormats = null,
    Func<SshSavedCredentialRequest, CancellationToken, Task<SshCredentialAvailability>>? CheckSavedCredentials = null,
    Action<string>? CredentialNotice = null);

public sealed class SshInteractionCancelledException() : OperationCanceledException("SSH interaction was cancelled.");
internal sealed class SshCredentialAccountException() : IOException("The saved credential belongs to a different SSH account.");
internal sealed class SshCredentialFailureException(string message, Exception? source = null) : IOException(message, source);
internal sealed class SshMethodUnavailableException(string? reason, bool forAttempt = false) : Exception(reason)
{
    internal string? Reason { get; } = reason;
    internal bool ForAttempt { get; } = forAttempt;
}

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
    private readonly SshPacketWriterOrder send = new();
    private readonly SemaphoreSlim receive = new(1);
    private readonly TaskCompletionSource packetKeysCleared = NewSignal();
    private readonly object creditGate = new();
    // Receive credit bounds bytes independently of the peer's packet fragmentation.
    // Record the two receive sources in a bitset rather than queueing a node per packet.
    private byte[] incoming = new byte[ReceiveWindow];
    private System.Collections.BitArray standardErrorBytes = new(ReceiveWindow);
    private readonly Channel<bool> incomingReady = Channel.CreateBounded<bool>(1);
    private int incomingStart, incomingCount;
    private TaskCompletionSource creditChanged = NewSignal();
    private readonly SshChannelWindow channelWindow = new(ReceiveWindow, MaxChunk);
    private uint peerChannel;
    private int columns = 80;
    private int rows = 24;
    private int closed;
    private volatile bool peerClosed;
    private readonly SshChannelClosure channelClosure = new();
    private readonly SshChannelRequests terminalRequests = new();
    private long enqueuedTerminalBytes, deliveredStartupBytes;
    private TaskCompletionSource startupProgress = NewSignal();
    private readonly object shutdownGate = new();
    private Task? shutdown;
    private bool initialExtensionWindow = true;
    private bool finalExtensionsSeen;
    private Task? pump;
    private string? approvedFingerprint;
    private int messages;
    private int messageBytes;

    public string Details => exchange.Details;

    private SshSessionWire(TcpClient client, Stream packetStream, SshDestination destination,
        ConnectionProfile profile, SshInteraction interaction)
    {
        this.client = client;
        this.destination = destination;
        this.interaction = interaction;
        wait = TimeSpan.FromSeconds(profile.WaitSeconds);
        livenessEnabled = profile.SshLivenessEnabled;
        livenessInterval = profile.SshLivenessInterval * 1000L;
        packets = new SshPackets(packetStream);
        exchange = new SshKeyExchange(packets, destination, interaction.ApprovedHostFormats?.Invoke(destination) ?? []);
    }

    public static async Task<ISessionWire> OpenAsync(ConnectionProfile profile, SshInteraction interaction,
        Func<CancellationToken, Task<(int Columns, int Rows)>> getGrid,
        TerminalPayloadConsumer consumeStartup,
        CancellationToken connectToken, CancellationToken sessionToken, Func<Stream, Stream>? wrapPacketStream = null)
    {
        var destination = SshDestination.Parse(profile.Host, profile.Port);
        var client = new TcpClient { NoDelay = true };
        SshSessionWire? wire = null;
        try
        {
            await client.ConnectAsync(destination.NetworkHost, destination.Port, connectToken).ConfigureAwait(false);
            Stream stream = client.GetStream();
            wire = new SshSessionWire(client, wrapPacketStream?.Invoke(stream) ?? stream, destination, profile, interaction);
            await wire.exchange.IdentifyAsync(stream, connectToken).ConfigureAwait(false);
            SshHostKey host = await wire.exchange.ExchangeAsync(connectToken).ConfigureAwait(false);
            await wire.ApproveAsync(host, sessionToken).ConfigureAwait(false);
            await wire.AuthenticateAsync(profile, sessionToken).ConfigureAwait(false);
            using var startup = new SshRemoteDeadline(sessionToken, wire.wait);
            await wire.OpenChannelAsync(profile, startup, getGrid, consumeStartup).ConfigureAwait(false);
            return wire;
        }
        catch
        {
            client.Dispose();
            if (wire is not null)
            {
                wire.Dispose();
                await wire.packetKeysCleared.Task.ConfigureAwait(false);
                await wire.ReleaseReceiveAsync().ConfigureAwait(false);
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
        string username = profile.SshUsername;
        IReadOnlyList<string> preferred = profile.SshMethods;
        if (profile.SshAllowsKeyboardInteractive) preferred = [.. preferred, "keyboardInteractive"];
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        string? unavailableReason = null;
        var saved = new SshSavedCredentialRequest(username,
            preferred.Contains("password") ? profile.SshPassword : null,
            preferred.Contains("publicKey") ? profile.SshPrivateKey : null);
        if ((saved.Password is not null || saved.PrivateKey is not null) && interaction.CheckSavedCredentials is { } check)
        {
            foreach (var (methodName, reason) in (await check(saved, token).ConfigureAwait(false)).Unavailable)
            {
                unavailable.Add(methodName);
                ReportUnavailable(reason);
            }
        }
        if (preferred.All(unavailable.Contains)) throw new SshCredentialFailureException(unavailableReason!);

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
        using (var probe = AuthenticationRequest(username, "none"))
            await SendAuthenticationAsync(probe.Memory, token).ConfigureAwait(false);

        string[] allowed = [];
        var skipped = new HashSet<string>(StringComparer.Ordinal);
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
                        using var request = AuthenticationRequest(username, "publickey");
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
                        skipped.Clear();
                    }
                }
                else throw new InvalidDataException("Unexpected SSH authentication response.");

                while (true)
                {
                    string[] candidates = preferred.Where(candidate => !unavailable.Contains(candidate) &&
                        !skipped.Contains(candidate) && (candidate switch
                        {
                            "password" => !usedPassword && passwords < 3 && allowed.Contains("password"),
                            "keyboardInteractive" => conversations < 3 && interaction.RequestChallenge is not null &&
                                allowed.Contains("keyboard-interactive"),
                            "publicKey" => !usedKey && keyProbes < 8 && (signatureAlgorithms is null ||
                                keyProbes < signatureAlgorithms.Length) && interaction.RequestPrivateKey is not null &&
                                allowed.Contains("publickey"),
                            _ => false,
                        })).ToArray();
                    if (candidates.Length == 0)
                    {
                        if (unavailableReason is not null) throw new SshCredentialFailureException(unavailableReason);
                        throw new IOException(needsChange ? "The SSH server requires a password change." :
                            "SSH authentication failed or requires an unavailable method.");
                    }
                    method = candidates[0];

                    try
                    {
                        if (method == "publicKey")
                        {
                            if (privateKey is null)
                            {
                                using VaultSecret material = await interaction.RequestPrivateKey!(
                                    new SshPrivateKeyRequest(destination, username, profile.SshPrivateKey, candidates.Length > 1), token)
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
                                continue;
                            }
                            userSignature = signatureAlgorithms[keyProbes++];
                            signedKey = false;
                            using var probe = AuthenticationRequest(username, "publickey");
                            probe.Byte(0);
                            probe.Text(userSignature);
                            probe.String(privateKey.PublicBlob);
                            await SendAuthenticationAsync(probe.Memory, token).ConfigureAwait(false);
                        }
                        else if (method == "password")
                        {
                            var request = new SshPasswordRequest(destination, username, profile.SshPassword, ++passwords);
                            using VaultSecret secret = await interaction.RequestPassword(request, token).ConfigureAwait(false)
                                ?? throw new SshInteractionCancelledException();
                            if (secret.Bytes.Length > 16 * 1024) throw new InvalidDataException("The SSH password exceeds its size limit.");
                            _ = VaultContainer.StrictUtf8.GetCharCount(secret.Bytes.Span);
                            using var password = AuthenticationRequest(username, "password", secret.Bytes.Length);
                            password.Byte(0);
                            password.String(secret.Bytes.Span);
                            await SendAuthenticationAsync(password.Memory, token, padCredentials: true).ConfigureAwait(false);
                        }
                        else
                        {
                            conversations++;
                            rounds = 0;
                            using var interactive = AuthenticationRequest(username, "keyboard-interactive");
                            interactive.String([]);
                            interactive.String([]);
                            await SendAuthenticationAsync(interactive.Memory, token).ConfigureAwait(false);
                        }
                        break;
                    }
                    catch (SshMethodUnavailableException exception)
                    {
                        (exception.ForAttempt ? unavailable : skipped).Add(method);
                        if (exception.Reason is { } reason) ReportUnavailable(reason);
                    }
                }
            }
        }
        finally { privateKey?.Dispose(); }

        void ReportUnavailable(string reason)
        {
            if (reason == unavailableReason) return;
            unavailableReason = reason;
            interaction.CredentialNotice?.Invoke(reason);
        }
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
            await SendAuthenticationAsync(response.Memory, token, padCredentials: true).ConfigureAwait(false);
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
        Func<CancellationToken, Task<(int Columns, int Rows)>> getGrid, TerminalPayloadConsumer consumeStartup)
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
        channelWindow.Open(reply.Number(), reply.Number());
        reply.End();
        var grid = await getGrid(token).ConfigureAwait(false);
        (columns, rows) = grid;
        using var stopDelivery = new CancellationTokenSource();
        Task delivery = DeliverStartupAsync(consumeStartup, stopDelivery.Token);
        pump = Task.Run(ReceiveAsync);
        try
        {
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
                await RequestStartupAsync(pty.Memory, delivery, token).ConfigureAwait(false);
            }
            using (var shell = ChannelMessage(98))
            {
                shell.Text("shell");
                shell.Byte(1);
                await RequestStartupAsync(shell.Memory, delivery, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            // No further transport input is admitted. Already buffered input
            // still belongs to this startup generation and must settle there.
            Dispose();
            await pump.ConfigureAwait(false);
            long admitted;
            lock (creditGate) admitted = enqueuedTerminalBytes;
            if (!delivery.IsFaulted)
                await WaitForStartupDeliveryAsync(admitted, delivery, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            stopDelivery.Cancel();
            await delivery.ConfigureAwait(false);
        }
    }

    private async Task RequestStartupAsync(ReadOnlyMemory<byte> payload, Task delivery, CancellationToken token)
    {
        var reply = await terminalRequests.RequestAsync(
            commit => SendTransferPacketAsync(payload, token, committed: commit), token).ConfigureAwait(false);
        await WaitForStartupDeliveryAsync(reply.ReceiveFence, delivery, token).ConfigureAwait(false);
        if (!reply.Success) throw new IOException("The SSH server refused terminal startup.");
    }

    private async Task WaitForStartupDeliveryAsync(long fence, Task delivery, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (creditGate)
            {
                if (deliveredStartupBytes >= fence) return;
                changed = startupProgress.Task;
            }
            if (await Task.WhenAny(changed, delivery).WaitAsync(token).ConfigureAwait(false) == delivery)
            {
                await delivery.ConfigureAwait(false);
                lock (creditGate) if (deliveredStartupBytes >= fence) return;
                throw new IOException("The SSH channel closed before completing startup delivery.");
            }
        }
    }

    private async Task DeliverStartupAsync(TerminalPayloadConsumer consume, CancellationToken token)
    {
        var buffer = new byte[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = await DeliverPayloadAsync(buffer, consume, token).ConfigureAwait(false);
                if (count == 0) return;
                lock (creditGate)
                {
                    deliveredStartupBytes += count;
                    startupProgress.TrySetResult();
                    startupProgress = NewSignal();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            terminalRequests.End(error);
            Dispose();
            throw;
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

    private async ValueTask SendAuthenticationAsync(ReadOnlyMemory<byte> packet, CancellationToken token,
        bool padCredentials = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait);
        await packets.SendAsync(packet, deadline.Token, padCredentials: padCredentials).ConfigureAwait(false);
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
        if (length < body.Length) safe += " [窶ｦ]";
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

    internal Task PeerClosed => channelClosure.ProtocolEnded;

    public async ValueTask<int> DeliverPayloadAsync(Memory<byte> buffer, TerminalPayloadConsumer consume, CancellationToken token)
    {
        await receive.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (count, source) = await ReadTerminalPayloadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return 0;
            await consume(source, buffer[..count].ToArray(), CancellationToken.None).ConfigureAwait(false);
            Acknowledge(count);
            return count;
        }
        finally { receive.Release(); }
    }

    private async ValueTask<(int Count, TerminalReceiveSource Source)> ReadTerminalPayloadAsync(
        Memory<byte> buffer, CancellationToken token)
    {
        while (true)
        {
            lock (creditGate)
            {
                if (incomingCount != 0) return ReadBuffered(buffer.Span);
                if (channelClosure.ReceiveEnded) return default;
            }
            if (!await incomingReady.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                // Closing may race with the final buffer notification.
                lock (creditGate) return incomingCount == 0 ? default : ReadBuffered(buffer.Span);
            }
            incomingReady.Reader.TryRead(out _);
        }
    }

    private (int Count, TerminalReceiveSource Source) ReadBuffered(Span<byte> buffer)
    {
        bool standardError = standardErrorBytes[incomingStart];
        var source = standardError ? TerminalReceiveSource.StandardError : TerminalReceiveSource.Data;
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
        return (count, source);
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
            enqueuedTerminalBytes += bytes.Length;
        }
        incomingReady.Writer.TryWrite(true);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        _ = await WriteChannelAsync(buffer, cancellationToken, new(buffer.Length)).ConfigureAwait(false);

    internal ValueTask<int> WriteChannelAsync(ReadOnlyMemory<byte> buffer, CancellationToken token, SshSendProgress progress) =>
        WriteObservedAsync(buffer, token, default, default, progress, null);

    internal ValueTask<int> WritePreparedPasteAsync(ReadOnlyMemory<byte> data, ReadOnlyMemory<int> encodedEnds, CancellationToken lifetime,
        CancellationToken stopInput, Action<int>? confirmed = null) =>
        WriteObservedAsync(data, lifetime, stopInput, encodedEnds, new(data.Length), confirmed);

    private async ValueTask<int> WriteObservedAsync(ReadOnlyMemory<byte> buffer, CancellationToken lifetime,
        CancellationToken stopInput, ReadOnlyMemory<int> encodedEnds, SshSendProgress progress, Action<int>? confirmed)
    {
        try { return await WriteChannelBytesAsync(buffer, lifetime, stopInput, encodedEnds, progress, confirmed).ConfigureAwait(false); }
        finally { progress.Settle(); }
    }

    private async ValueTask<int> WriteChannelBytesAsync(ReadOnlyMemory<byte> buffer, CancellationToken lifetime,
        CancellationToken stopInput, ReadOnlyMemory<int> encodedEnds, SshSendProgress progress, Action<int>? confirmed)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, stop.Token);
        using var beforeUnit = stopInput.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(linked.Token, stopInput) : null;
        int submitted = 0;
        int confirmedBytes = 0;
        while (submitted < buffer.Length)
        {
            linked.Token.ThrowIfCancellationRequested();
            bool boundary = beforeUnit is null || IsCompleteBoundary(submitted);
            CancellationToken admission = boundary && beforeUnit is not null ? beforeUnit.Token : linked.Token;
            int requested = buffer.Length - submitted;
            if (beforeUnit is not null && !boundary)
            {
                requested = 1;
                while (!IsCompleteBoundary(submitted + requested)) requested++;
            }

            try
            {
                await send.WaitAsync(admission, payload: true).ConfigureAwait(false);
                Task? waitForCredit = null;
                try
                {
                    int count;
                    lock (creditGate)
                    {
                        if (peerClosed || channelClosure.CloseRequested) throw new IOException("The SSH channel is closed.");
                        count = exchange.IsDue ? 0 : channelWindow.ReserveSend(requested);
                        if (count == 0) waitForCredit = exchange.IsDue ? rekeyCompleted.Task : creditChanged.Task;
                    }
                    if (count != 0)
                    {
                        using var data = ChannelMessage(94, count + 32);
                        data.String(buffer.Span.Slice(submitted, count));
                        try
                        {
                            await packets.SendChannelAsync(data.Memory, stop.Token, admission, () =>
                            {
                                lock (creditGate) channelWindow.CommitSend(count);
                            }, progress, count).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (progress.State.Uncertain is null && !stop.IsCancellationRequested)
                        {
                            lock (creditGate)
                            {
                                channelWindow.CancelSend(count);
                                SignalCredit();
                            }
                            throw;
                        }
                        submitted += count;
                        if (confirmed is not null)
                        {
                            int boundaryIndex = encodedEnds.Span.BinarySearch(submitted);
                            if (boundaryIndex < 0) boundaryIndex = ~boundaryIndex - 1;
                            if (boundaryIndex >= 0 && encodedEnds.Span[boundaryIndex] > confirmedBytes)
                            {
                                confirmedBytes = encodedEnds.Span[boundaryIndex];
                                confirmed(confirmedBytes);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (admission.IsCancellationRequested && !stop.IsCancellationRequested &&
                    progress.State.Uncertain is null)
                {
                    throw;
                }
                catch
                {
                    Dispose();
                    throw;
                }
                finally { send.Release(); }
                if (waitForCredit is not null) await waitForCredit.WaitAsync(admission).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (boundary && stopInput.IsCancellationRequested && !linked.IsCancellationRequested &&
                progress.State.Uncertain is null)
            {
                return submitted;
            }
        }
        return submitted;

        bool IsCompleteBoundary(int offset) => encodedEnds.IsEmpty ? Utf8Cutoff(buffer.Span, offset) :
            offset == 0 || encodedEnds.Span.BinarySearch(offset) >= 0;
    }

    private static bool Utf8Cutoff(ReadOnlySpan<byte> encoded, int index) =>
        index == 0 || index == encoded.Length ||
        ((encoded[index] & 0xc0) != 0x80 && (encoded[index - 1] != 13 || encoded[index] != 10));

    private void Acknowledge(int count)
    {
        if (stop.IsCancellationRequested || peerClosed) return;
        int adjust = CollectCredit(count);
        if (adjust != 0) _ = ReturnCreditAsync(adjust);
    }

    private int CollectCredit(int count)
    {
        lock (creditGate) return channelWindow.Consume(count);
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
        while (count != 0)
        {
            using var adjust = ChannelMessage(93);
            adjust.Number((uint)count);
            int grant = count;
            await SendControlAsync(adjust.Memory, token, () =>
            {
                lock (creditGate) channelWindow.CommitGrant(grant);
            }).ConfigureAwait(false);
            lock (creditGate) count = channelWindow.CompleteGrant();
        }
    }

    private async Task ReceiveAsync()
    {
        Task<byte[]>? read = null;
        try
        {
            lastLiveness = Environment.TickCount64;
            read = packets.ReadAsync(stop.Token).AsTask();
            Task timer = Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            while (!peerClosed && !stop.IsCancellationRequested)
            {
                Task ready = await Task.WhenAny(read, timer).ConfigureAwait(false);
                if (ready == timer)
                {
                    timer = Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                    if (!exchange.IsDue)
                    {
                        await CheckLivenessAsync().ConfigureAwait(false);
                        continue;
                    }
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
            terminalRequests.End(new IOException("The SSH terminal channel ended."));
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
            channelClosure.EndProtocol();
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
        catch (Exception error)
        {
            // Stop queued channel writes before handing the writer over after a failed trust decision or exchange.
            incomingReady.Writer.TryComplete(stop.IsCancellationRequested ? null : error);
            Dispose();
            throw;
        }
        finally { send.Release(); }
    }

    private async ValueTask ProcessAsync(byte[] packet, CancellationToken token, bool duringExchange = false)
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
                    channelWindow.AddSend(amount);
                    SignalCredit();
                }
                break;
            case 94:
            case 95:
                uint dataType = type == 95 ? reader.Number() : 0;
                byte[] bytes = reader.String().ToArray();
                reader.End();
                if (channelClosure.ReceiveEnded || bytes.Length > MaxChunk) throw new InvalidDataException("Unexpected SSH channel data.");
                lock (creditGate) channelWindow.AcceptReceive(bytes.Length);
                if (bytes.Length != 0)
                {
                    bool terminalPayload = type == 94 || dataType == 1;
                    if (terminalPayload) EnqueueTerminalPayload(bytes, type == 95);
                    else Acknowledge(bytes.Length);
                }
                break;
            case 96:
                reader.End();
                channelClosure.ReceiveEof();
                incomingReady.Writer.TryWrite(true);
                break;
            case 97:
                reader.End();
                using (var close = ChannelMessage(97))
                    await RespondCloseAsync(close.Memory, token, duringExchange).ConfigureAwait(false);
                lock (creditGate)
                {
                    peerClosed = true;
                    SignalCredit();
                }
                channelClosure.EndProtocol();
                break;
            case 98:
                ReadOnlySpan<byte> request = reader.String();
                bool wantsReply = reader.Boolean();
                bool exitStatus = request.SequenceEqual("exit-status"u8);
                bool exitSignal = request.SequenceEqual("exit-signal"u8);
                if (exitStatus)
                {
                    reader.Number();
                    reader.End();
                }
                else if (exitSignal)
                {
                    reader.String();
                    reader.Boolean();
                    reader.String();
                    reader.String();
                    reader.End();
                }
                if (wantsReply)
                {
                    using var response = ChannelMessage(exitStatus || exitSignal ? (byte)99 : (byte)100);
                    await RespondAsync(response.Memory, token, duringExchange).ConfigureAwait(false);
                }
                break;
            case 99 or 100:
                reader.End();
                lock (creditGate) terminalRequests.Complete(type == 99, enqueuedTerminalBytes);
                break;
            default:
                throw new InvalidDataException($"Unexpected SSH channel message ({type}).");
        }
    }

    private async Task RespondCloseAsync(ReadOnlyMemory<byte> close, CancellationToken token, bool defer)
    {
        if (defer)
        {
            if (channelClosure.ScheduleClosePacket())
                await RespondAsync(close, token, defer: true).ConfigureAwait(false);
            return;
        }
        await send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref closed) != 0 || !channelClosure.ScheduleClosePacket()) return;
            await packets.SendAsync(close, token).ConfigureAwait(false);
        }
        finally { send.Release(); }
    }

    public async ValueTask CloseAsync()
    {
        try { await DisconnectForDeliveryAsync().ConfigureAwait(false); }
        finally { await ReleaseReceiveAsync().ConfigureAwait(false); }
    }

    internal Task DisconnectForDeliveryAsync()
    {
        if (channelClosure.RequestClose())
            lock (creditGate) SignalCredit();
        lock (shutdownGate) return shutdown ??= CloseChannelAsync();
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
                await channelClosure.ProtocolEnded.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
            or ObjectDisposedException or CryptographicException or InvalidDataException) { }
        finally { Dispose(); }
        await packetKeysCleared.Task.ConfigureAwait(false);
    }

    internal async Task ReleaseReceiveAsync()
    {
        if (!channelClosure.BeginRelease())
        {
            await channelClosure.ActualRelease.ConfigureAwait(false);
            return;
        }
        await receive.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (creditGate)
            {
                incomingCount = incomingStart = 0;
                incoming = [];
                standardErrorBytes = new System.Collections.BitArray(0);
            }
            incomingReady.Reader.TryRead(out _);
            receiveMemory.Dispose();
        }
        finally
        {
            receive.Release();
            channelClosure.EndRelease();
        }
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

    private async Task SendControlAsync(ReadOnlyMemory<byte> packet, CancellationToken token, Action? committed = null)
    {
        await send.WaitAsync(token).ConfigureAwait(false);
        bool issued = false;
        try
        {
            await packets.SendAsync(packet, stop.Token, token, () =>
            {
                issued = true;
                committed?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!issued && !stop.IsCancellationRequested) { throw; }
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
