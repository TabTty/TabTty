using System.Net.Sockets;
using System.Text;

namespace TabTty.Core;

public sealed partial class TerminalSession : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SemaphoreSlim ProcessingSlots = new(4);
    private readonly ISessionWire wire;
    private readonly TelnetProtocol? protocol;
    private readonly TerminalOutput terminal;
    private readonly ConnectionProfile profile;
    private readonly MacroRun? macro;
    private readonly TerminalEncoding transmitter;
    private SessionRecording? recording;
    private readonly TerminalRecordings? projections;
    private readonly SemaphoreSlim state = new(1);
    private readonly SemaphoreSlim recordingChanges = new(1);
    private readonly byte[] receiveBuffer = new byte[8192];
    private readonly ResourceReservation receiveMemory = ApplicationResources.Receive.Reserve(64 * 1024);
    private volatile bool ended;
    private bool terminalReleased;
    private volatile bool disconnected;
    private bool recordingActive;
    private volatile bool pasteActive;
    private int recordingPart = 1;
    private volatile string echoPolicy;
    private readonly object closeGate = new();
    private Task? transportClose;
    private readonly ResourceQuota inputBytes = new(2 * 1024 * 1024, message: "The terminal input budget reached its limit.");
    private readonly ResourceQuota inputUnits = new(256, message: "The terminal input queue reached its limit.");

    internal TerminalSession(ISessionWire wire, ConnectionProfile profile, SessionRecording? recording, int defaultRetainedRows,
        (int Columns, int Rows)? initialGrid = null, MacroRun? macro = null, int initialRecordingPart = 1)
    {
        this.wire = wire;
        this.profile = profile;
        this.macro = macro;
        recordingPart = initialRecordingPart;
        echoPolicy = profile.LocalEcho;
        this.recording = recording;
        recordingActive = recording is not null;
        var grid = initialGrid ?? (80, 24);
        if (profile.Type == "telnet")
            protocol = new TelnetProtocol(profile.AdvertisedType, (ushort)grid.Columns, (ushort)grid.Rows, profile.InitiateBinary);
        RetainedRows = profile.RetainedRows ?? defaultRetainedRows;
        terminal = new TerminalOutput(columns: grid.Columns, retainedRowLimit: RetainedRows, screenRows: grid.Rows,
            receiveEncoding: profile.ReceiveEncoding, receiveSevenBitKana: profile.ReceiveSevenBitKana,
            ambiguousWidth: profile.AmbiguousWidth);
        transmitter = new TerminalEncoding(profile.TransmitEncoding);

        if (recording is not null) projections = new TerminalRecordings(terminal, profile, recording, RetainedRows, grid);
        if (macro is not null)
        {
            terminal.MacroCharacter = macro.Receive;
            terminal.EnteringAlternateScreen = () => macro.Stop("AlternateScreen");
            macro.ObserveSession(this);
        }
        if (recording is not null) ObserveRecording(recording);
    }

    public string DisplayName => profile.DisplayName;
    public int RetainedRows { get; }
    public bool ApplicationKeypad => terminal.ApplicationKeypad;
    public event Action? Bell
    {
        add => terminal.Bell += value;
        remove => terminal.Bell -= value;
    }
    public Guid? HighlightSetId => profile.HighlightSetId;
    public string? RecordingError => recording?.Failure;
    public bool RecordsOutput => profile.RecordsOutput;
    public bool IsSerial => wire is SerialSessionWire;
    public bool CanTransfer => !disconnected && (!ended || macro is not null) && wire is SshSessionWire;

    public Task<SftpClient> OpenFileTransferAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (wire is not SshSessionWire ssh) throw new InvalidOperationException("An SSH session is required.");
        return ssh.OpenTransferAsync(profile.TransmitEncoding, cancellationToken);
    }

    public string FileTransferEndpoint => $"{profile.SshUsername}@{SshDestination.Parse(profile.Host, profile.Port)}";
    public TimeSpan FileTransferWait => TimeSpan.FromSeconds(profile.WaitSeconds);
    public bool IsTelnet => protocol is not null;
    public bool SupportsEchoChange => profile.Type != "ssh";
    public string EchoPolicy => echoPolicy;
    public bool LocalEchoEnabled => LocalEcho;
    public bool CanSetDtr => wire is SerialSessionWire { CanSetDtr: true };
    public bool CanSetRts => wire is SerialSessionWire { CanSetRts: true };
    public string ConnectionDetails => wire is SshSessionWire ssh ? ssh.Details :
        profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host}:{profile.Port}";

    public static async Task<TerminalSession> ConnectAsync(string path, CancellationToken cancellationToken = default,
        SshInteraction? ssh = null, Action<ConnectionProfile>? opened = null, int defaultRetainedRows = 5000,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? viewport = null)
        => await ConnectCoreAsync(path, cancellationToken, ssh, opened, defaultRetainedRows, viewport, null).ConfigureAwait(false);

    internal static Task<TerminalSession> ConnectMacroAsync(string path, MacroRun macro, CancellationToken token,
        SshInteraction? ssh = null, Action<ConnectionProfile>? opened = null, int defaultRetainedRows = 5000,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? viewport = null) =>
        ConnectCoreAsync(path, token, ssh, opened, defaultRetainedRows, viewport, macro);

    private static async Task<TerminalSession> ConnectCoreAsync(string path, CancellationToken cancellationToken,
        SshInteraction? ssh, Action<ConnectionProfile>? opened, int defaultRetainedRows,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? viewport, MacroRun? macro)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultRetainedRows, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultRetainedRows, 100_000);
        using var loadDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loadDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        ConnectionProfile profile;
        try { profile = await ConnectionProfile.LoadAsync(path, loadDeadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (macro is not null && !cancellationToken.IsCancellationRequested &&
            error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or
                System.Text.Json.JsonException or OperationCanceledException)
        { throw new MacroError(MacroErrorCode.ProfileLoad); }
        macro?.ProfileLoaded(profile);
        opened?.Invoke(profile);
        if (profile.Type == "ssh" && ssh is null)
            throw new InvalidOperationException("SSH requires host verification and authentication interaction.");
        SessionRecording? recording = null;
        int recordingPart = 1;
        DateTimeOffset? recordingStart = macro is null ? null : DateTimeOffset.Now;
        if (profile.RecordsOutput)
        {
            while (true)
            {
                try
                {
                    recording = await OpenRecordingAsync(() => SessionRecording.Open(path, profile, macro?.BaseName,
                        recordingPart, macro?.RunId, recordingStart), cancellationToken).ConfigureAwait(false);
                    if (recordingPart > 1 && macro is not null)
                    {
                        macro.RecordingRecovered();
                        try { await macro.WaitForContinuationAsync(cancellationToken).ConfigureAwait(false); }
                        catch
                        {
                            await recording.FinishAsync().ConfigureAwait(false);
                            throw;
                        }
                    }
                    break;
                }
                catch (Exception error) when (macro is not null && !cancellationToken.IsCancellationRequested &&
                    error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or OperationCanceledException)
                {
                    macro.RecordingFailed();
                    macro.RecordingAwaitingRecovery();
                    await macro.WaitForRecordingRetryAsync(cancellationToken).ConfigureAwait(false);
                    recordingPart = checked(recordingPart + 1);
                }
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
        ISessionWire? wire = null;
        TerminalSession? session = null;
        (int Columns, int Rows) grid = (80, 24);
        try
        {
            if (profile.Type != "ssh") grid = await GetGridAsync(deadline.Token).ConfigureAwait(false);
            wire = profile.Type switch
            {
                "serial" => await OpenSerialAsync(profile.Serial, deadline.Token).ConfigureAwait(false),
                "ssh" => await SshSessionWire.OpenAsync(profile, ssh!, GetGridAsync, InitializeSsh,
                    deadline.Token, cancellationToken)
                    .ConfigureAwait(false),
                _ => await OpenTcpAsync(profile, deadline.Token).ConfigureAwait(false),
            };
            if (profile.Type != "ssh") deadline.Token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            session ??= new TerminalSession(wire, profile, recording, defaultRetainedRows, grid, macro, recordingPart);
            if (session.protocol is { } telnet)
                await wire.WriteAsync(telnet.Start(), deadline.Token).ConfigureAwait(false);
            if (profile.Type == "ssh")
            {
                using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                replyDeadline.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
                await session.SendRepliesAsync(replyDeadline.Token).ConfigureAwait(false);
            }
            return session;
        }
        catch (Exception exception)
        {
            if (wire is not null) await wire.CloseAsync().ConfigureAwait(false);
            if (session is not null)
            {
                if (exception is ResourceLimitException)
                {
                    session.projections?.Abort();
                    recording?.MarkIncomplete(exception.Message);
                }
                await session.CloseRecordingsAsync().ConfigureAwait(false);
                session.receiveMemory.Dispose();
            }
            else if (recording is not null) await recording.FinishAsync().ConfigureAwait(false);
            throw;
        }

        TerminalSession InitializeSsh(ISessionWire transport) =>
            session = new TerminalSession(transport, profile, recording, defaultRetainedRows, grid, macro, recordingPart);

        async Task<(int Columns, int Rows)> GetGridAsync(CancellationToken token)
        {
            if (viewport is null) return grid;
            var requested = await viewport(token).ConfigureAwait(false);
            grid = (Math.Clamp(requested.Columns, 2, 1024),
                Math.Clamp(requested.Rows, 1, Math.Min(512, profile.RetainedRows ?? defaultRetainedRows)));
            return grid;
        }
    }

    private static async Task<ISessionWire> OpenTcpAsync(ConnectionProfile profile, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(profile.Host.Trim('[', ']'), profile.Port, token).ConfigureAwait(false);
            return new TcpSessionWire(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<ISessionWire> OpenSerialAsync(SerialSettings serial, CancellationToken token)
    {
        await ApplicationResources.FilePreparations.WaitAsync(token).ConfigureAwait(false);
        var operation = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return SerialSessionWire.Open(serial);
            }
            finally { ApplicationResources.FilePreparations.Release(); }
        });
        try
        {
            return await operation.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = operation.ContinueWith(static task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    // One receive consumer owns the transport read buffer. Sends may run concurrently.
    public async Task<IReadOnlyList<string>?> ReadOutputAsync(CancellationToken cancellationToken = default)
    {
        if (!await ProcessOutputAsync(cancellationToken).ConfigureAwait(false)) return null;
        return await GetOutputAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ProcessOutputAsync(CancellationToken cancellationToken = default)
    {
        while (!ended)
        {
            int count = await wire.ReadAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
            SessionRecording? finishedRecording = null;
            await state.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ended) return false;
                if (count == 0)
                {
                    await ProcessBatchAsync(() =>
                    {
                        byte[] tail = protocol?.CompleteReceive() ?? [];
                        Receive(TerminalReceiveSource.Data, tail);
                        CompleteReceive();
                    }, cancellationToken).ConfigureAwait(false);
                    ended = true;
                    SignalAiActivity();
                    finishedRecording = recording;
                }
                else
                {
                    var (payload, reply) = protocol is null
                        ? (receiveBuffer.AsSpan(0, count).ToArray(), Array.Empty<byte>())
                        : protocol.Receive(receiveBuffer.AsSpan(0, count));
                    if (reply.Length != 0)
                        await wire.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
                    if (payload.Length != 0)
                    {
                        await ProcessPayloadAsync(wire.Source, payload, cancellationToken).ConfigureAwait(false);
                        if (!pasteActive) await SendRepliesAsync(cancellationToken).ConfigureAwait(false);
                        wire.Acknowledge(count);
                    }
                    return true;
                }
            }
            catch (Exception exception) when (exception is ResourceLimitException or NativeTerminalExecutionException)
            {
                FailTerminalOperation(exception);
                throw;
            }
            finally
            {
                state.Release();
            }
            if (finishedRecording is not null) await finishedRecording.FinishAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }

    internal async Task ProcessPayloadAsync(TerminalReceiveSource source, byte[] payload, CancellationToken token)
    {
        for (int offset = 0; offset < payload.Length; offset += 1024)
        {
            byte[] batch = payload.Length <= 1024 ? payload : payload.AsSpan(offset, Math.Min(1024, payload.Length - offset)).ToArray();
            await ProcessBatchAsync(() => Receive(source, batch, token), token).ConfigureAwait(false);
        }
    }

    private static async Task ProcessBatchAsync(Action process, CancellationToken token)
    {
        await ProcessingSlots.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(process, token).ConfigureAwait(false); }
        finally { ProcessingSlots.Release(); }
    }

    private void Receive(TerminalReceiveSource source, byte[] payload, CancellationToken token = default)
    {
        StopFailedRecording();
        if (projections is null) terminal.Receive(source, payload, token);
        else projections.Receive(source, payload, token);
        StopFailedRecording();
        macro?.ReceiveBatch();
        if (payload.Length != 0) SignalAiActivity();
    }

    private void Echo(ReadOnlySpan<byte> payload)
    {
        try
        {
            StopFailedRecording();
            if (projections is null) terminal.EchoUtf8(payload);
            else projections.Echo(payload);
            StopFailedRecording();
        }
        catch (Exception exception) when (exception is ResourceLimitException or NativeTerminalExecutionException)
        {
            FailTerminalOperation(exception);
            throw;
        }
        SignalAiActivity();
    }

    private void FailTerminalOperation(Exception exception)
    {
        ended = true;
        recordingActive = false;
        projections?.Abort();
        recording?.MarkIncomplete(exception.Message);
        Disconnect();
    }

    private void CompleteReceive()
    {
        if (projections is null) terminal.CompleteReceive();
        else projections.CompleteReceive();
        macro?.ReceiveEof();
        recordingActive = false;
    }

    private async Task SendRepliesAsync(CancellationToken token)
    {
        foreach (byte[] response in terminal.TakeReplies())
            await wire.WriteAsync(EncodeInput(response), token).ConfigureAwait(false);
    }

    public async Task SendBreakAsync(CancellationToken cancellationToken = default)
    {
        EnsureManualInput();
        if (wire is SerialSessionWire serial)
        {
            using var unit = inputUnits.Reserve(1);
            EnsureManualInput();
            await serial.SendBreakAsync(cancellationToken).ConfigureAwait(false);
        }
        else
            await SendTelnetCommandAsync(isBreak: true, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAreYouThereAsync(CancellationToken cancellationToken = default) =>
        SendTelnetCommandAsync(isBreak: false, cancellationToken);

    public async Task SendMouseAsync(TerminalMouseInput input, CancellationToken cancellationToken = default)
    {
        using var unit = inputUnits.Reserve(1);
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (pasteActive || IsMacroOwned || HasAiInputOwner) return;
            byte[] report = terminal.EncodeMouse(input);
            if (report.Length != 0)
                await wire.WriteAsync(EncodeInput(report), cancellationToken).ConfigureAwait(false);
        }
        finally { state.Release(); }
    }

    private async Task SendTelnetCommandAsync(bool isBreak, CancellationToken cancellationToken)
    {
        using var unit = inputUnits.Reserve(1);
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            EnsureManualInput();
            if (protocol is null) throw new InvalidOperationException("This command requires Telnet.");
            if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
            await wire.WriteAsync(isBreak ? protocol.Break() : protocol.AreYouThere(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { state.Release(); }
    }

    public void SetSerialLine(bool dtr, bool enabled)
    {
        using var unit = inputUnits.Reserve(1);
        EnsureManualInput();
        if (wire is not SerialSessionWire serial) throw new InvalidOperationException("A COM session is required.");
        serial.SetLine(dtr, enabled);
    }

    private bool LocalEcho => echoPolicy == "on" ||
        (echoPolicy == "followRemote" && protocol is { RemoteEcho: false });

    public async Task SetLocalEchoAsync(string policy, CancellationToken cancellationToken = default)
    {
        if (policy != "off" && (profile.Type == "ssh" ||
            (profile.Type == "telnet" ? policy != "followRemote" : policy != "on")))
            throw new ArgumentException("Unsupported local echo policy.", nameof(policy));
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureConnected(); echoPolicy = policy; }
        finally { state.Release(); }
    }

    private byte[] EncodeInput(byte[] bytes) => protocol?.EncodeInput(bytes) ?? bytes;

    public Task<IReadOnlyList<string>> SendTextAsync(string text, CancellationToken cancellationToken = default,
        bool altPrefix = false)
    {
        if (text.Length > 1024 * 1024 || text.Any(char.IsControl))
            throw new ArgumentException("Terminal text contains a control character or exceeds its size limit.", nameof(text));
        if (transmitter.IsUtf8)
        {
            int size = NativeTerminalCore.CheckUtf8Input(text);
            var memory = inputBytes.Reserve(size + (altPrefix ? size + 1L : 0L));
            try
            {
                byte[] echo = NativeTerminalCore.PrepareUtf8Input(text, size);
                byte[] payload = altPrefix ? [0x1B, .. echo] : echo;
                return SendAsync(() => payload, echo, cancellationToken, memory,
                    payloadAlreadyCharged: true);
            }
            catch { memory.Dispose(); throw; }
        }
        var echoMemory = inputBytes.Reserve(StrictUtf8.GetByteCount(text));
        try { return SendAsync(() =>
        {
            byte[] encoded = EncodeText(text);
            return altPrefix ? [0x1B, .. encoded] : encoded;
        }, StrictUtf8.GetBytes(text), cancellationToken, echoMemory); }
        catch { echoMemory.Dispose(); throw; }
    }

    public Task<IReadOnlyList<string>> SendEnterAsync(CancellationToken cancellationToken = default) =>
        SendAsync(profile.Enter switch { "cr" => [13], "lf" => [10], _ => [13, 10] }, [13, 10], cancellationToken);

    public Task<IReadOnlyList<string>> SendBackspaceAsync(CancellationToken cancellationToken = default) =>
        SendAsync([profile.Backspace == "bs" ? (byte)8 : (byte)127], [8], cancellationToken);

    public Task<IReadOnlyList<string>> SendTabAsync(CancellationToken cancellationToken = default) =>
        SendAsync([9], [9], cancellationToken);

    public Task<IReadOnlyList<string>> SendKeyAsync(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None,
        CancellationToken cancellationToken = default) =>
        SendAsync(() => TerminalKeys.Encode(key, modifiers, terminal.ApplicationCursorKeys, terminal.ApplicationKeypad),
            [], cancellationToken);

    public Task<IReadOnlyList<string>> SendControlAsync(char letter, CancellationToken cancellationToken = default)
    {
        if (letter is < 'A' or > 'Z') throw new ArgumentOutOfRangeException(nameof(letter));
        return SendAsync([(byte)(letter - 'A' + 1)], [], cancellationToken);
    }

    public async Task ValidatePasteAsync(TerminalPaste paste, CancellationToken cancellationToken = default)
    {
        using var unit = inputUnits.Reserve(1);
        using var preflight = inputBytes.Reserve(1024 * 1024);
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { PreflightPasteText(paste.Text); }
        finally { state.Release(); }
    }

    public async Task<bool> SendPasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        CancellationToken stopToken = default, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        using var unit = inputUnits.Reserve(1);
        using var memory = inputBytes.Reserve(PasteReservationSize);
        return await SendReservedPasteAsync(paste, characterDelay, lineDelay, stopToken, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    private const int PasteReservationSize = 1024 * 1024 + 64 * 1024;

    private async Task<bool> SendReservedPasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        CancellationToken stopToken, CancellationToken cancellationToken, IProgress<int>? progress,
        bool preflightComplete = false, AiInputAttempt? ai = null, bool framePaste = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characterDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(characterDelay, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(lineDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lineDelay, 10_000);
        if (paste.CharacterCount == 0) return true;
        using var pacing = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
        bool bracketed;
        using var opening = PasteWriteDeadline(cancellationToken);
        await state.WaitAsync(opening.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (ai is null) EnsureManualInput();
            else CheckAiInput(ai);
            if (pasteActive) throw new InvalidOperationException("A paste is already in progress.");
            if (stopToken.IsCancellationRequested) return false;
            if (!preflightComplete)
            {
                if (ai is null) PreflightPasteText(paste.Text);
                else await ProcessBatchAsync(() => PreflightPasteText(paste.Text), pacing.Token).ConfigureAwait(false);
            }
            if (ai is not null) CheckAiInput(ai);
            bracketed = framePaste && terminal.BracketedPaste;
            pasteActive = true;
            if (bracketed)
            {
                try { await WriteInputAsync("\u001B[200~"u8.ToArray(), opening.Token, ai).ConfigureAwait(false); }
                catch
                {
                    pasteActive = false;
                    throw;
                }
            }
        }
        finally
        {
            state.Release();
        }

        try
        {
            int position = 0;
            int sent = 0;
            int delay = 0;
            while (position < paste.Text.Length && !stopToken.IsCancellationRequested)
            {
                if (delay != 0)
                {
                    try
                    {
                        await Task.Delay(delay, pacing.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                int start = position;
                do
                {
                    bool newline = paste.Text[position] is '\r' or '\n';
                    position += Unicode17.NextGraphemeLength(paste.Text.AsSpan(position));
                    delay = characterDelay + (newline ? lineDelay : 0);
                    sent++;
                }
                while (delay == 0 && position < paste.Text.Length && position - start < 1024);

                using var writing = PasteWriteDeadline(cancellationToken);
                await state.WaitAsync(writing.Token).ConfigureAwait(false);
                try
                {
                    if (stopToken.IsCancellationRequested) break;
                    EnsureConnected();
                    string text = paste.Text[start..position];
                    byte[] payload = EncodeText(text);
                    await WriteInputAsync(EncodeInput(payload), writing.Token, ai).ConfigureAwait(false);
                    if (LocalEcho)
                        Echo(transmitter.IsUtf8 ? payload : StrictUtf8.GetBytes(text));
                }
                finally
                {
                    state.Release();
                }

                progress?.Report(sent);
            }

        }
        catch
        {
            try { await ClosePasteFrameAsync(bracketed, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidOperationException) { }
            throw;
        }
        await ClosePasteFrameAsync(bracketed, cancellationToken).ConfigureAwait(false);
        return !stopToken.IsCancellationRequested;
    }

    private async Task ClosePasteFrameAsync(bool bracketed, CancellationToken cancellationToken)
    {
        using var closing = PasteWriteDeadline(cancellationToken);
        try
        {
            await state.WaitAsync(closing.Token).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                if (bracketed)
                    await wire.WriteAsync("\u001B[201~"u8.ToArray(), closing.Token).ConfigureAwait(false);
                await SendRepliesAsync(closing.Token).ConfigureAwait(false);
            }
            finally { state.Release(); }
        }
        finally { pasteActive = false; }
    }

    private CancellationTokenSource PasteWriteDeadline(CancellationToken lifetime)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
        return deadline;
    }

    public async Task<IReadOnlyList<string>> GetOutputAsync(CancellationToken cancellationToken = default)
    {
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false); }
        finally { state.Release(); }
    }

    private async Task<TerminalSnapshot> CaptureSnapshotAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(terminalReleased, this);
        TerminalSnapshot? snapshot = null;
        await ProcessBatchAsync(() => snapshot = terminal.GetRows(), token).ConfigureAwait(false);
        return snapshot!;
    }

    public async Task<TerminalSnapshot> ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            rows = Math.Clamp(rows, 1, Math.Min(512, RetainedRows));
            await ProcessBatchAsync(() =>
            {
                StopFailedRecording();
                long prior = terminal.AiLayoutEpoch;
                if (projections is null) terminal.Resize(columns, rows);
                else projections.Resize(columns, rows);
                StopFailedRecording();
                if (prior != terminal.AiLayoutEpoch) SignalAiActivity();
            }, cancellationToken).ConfigureAwait(false);
            if (protocol is not null)
            {
                byte[] notification = protocol.SetWindowSize((ushort)columns, (ushort)rows);
                if (notification.Length != 0)
                    await wire.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
            }
            else if (wire is SshSessionWire ssh)
                await ssh.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
            return await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { state.Release(); }
    }

    private void EnsureConnected()
    {
        if (ended && macro is null || disconnected) throw new IOException("The terminal session has ended.");
    }

    private byte[] EncodeText(string text) => transmitter.Encode(text, profile.TransmitSevenBitKana);

    private void PreflightPasteText(string text)
    {
        if (transmitter.IsUtf8) _ = NativeTerminalCore.CheckUtf8Input(text);
        else _ = NativeTerminalCore.CheckLegacyInput(transmitter.NativePage, text, profile.TransmitSevenBitKana);
    }

    private Task<IReadOnlyList<string>> SendAsync(byte[] bytes, byte[] echo, CancellationToken cancellationToken) =>
        SendAsync(() => bytes, echo, cancellationToken);

    private async Task<IReadOnlyList<string>> SendAsync(Func<byte[]> encode, byte[] echo, CancellationToken cancellationToken,
        ResourceReservation? echoMemory = null, bool payloadAlreadyCharged = false)
    {
        using var queuedMemory = echoMemory ?? inputBytes.Reserve(echo.LongLength);
        using var unit = inputUnits.Reserve(1);
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            EnsureManualInput();
            if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
            byte[] payload = [];
            await ProcessBatchAsync(() => payload = encode(), cancellationToken).ConfigureAwait(false);
            using var transmitMemory = inputBytes.Reserve(payload.LongLength *
                (protocol is null ? (payloadAlreadyCharged ? 0 : 1) : 3));
            await wire.WriteAsync(EncodeInput(payload), cancellationToken).ConfigureAwait(false);
            if (LocalEcho)
                await ProcessBatchAsync(() => Echo(echo), CancellationToken.None).ConfigureAwait(false);
            return await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            state.Release();
        }
    }

    public async Task PauseRecordingAsync(CancellationToken cancellationToken = default)
    {
        await recordingChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await state.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StopFailedRecording();
                projections?.Cutoff();
                recordingActive = false;
            }
            finally
            {
                state.Release();
            }

            if (recording is not null)
            {
                await recording.FinishAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (recording.Failure is { } failure) throw new IOException(failure);
            }
        }
        finally
        {
            recordingChanges.Release();
        }
    }

    public async Task ResumeRecordingAsync(CancellationToken cancellationToken = default)
    {
        await recordingChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionRecording? candidate = null;
        try
        {
            var previous = recording ?? throw new InvalidOperationException("Recording is disabled in the profile.");
            if (recordingActive && previous.Failure is null) return;
            await previous.FinishAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            int part = checked(++recordingPart);
            candidate = await OpenRecordingAsync(() => previous.OpenPart(part), cancellationToken).ConfigureAwait(false);
            await state.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ended || disconnected) throw new IOException("The terminal session has ended.");
                projections!.Cutoff();
                projections.Start(candidate);
                recording = candidate;
                recordingActive = true;
                if (macro?.Status.Holds.HasFlag(MacroHold.RecordingFailure) == true) macro.RecordingRecovered();
                ObserveRecording(candidate);
                candidate = null;
            }
            finally
            {
                state.Release();
            }
        }
        finally
        {
            if (candidate is not null) await candidate.FinishAsync().ConfigureAwait(false);
            recordingChanges.Release();
        }
    }

    private void ObserveRecording(SessionRecording part)
    {
        int failureReported = 0;
        part.FailureNotification = ReportFailure;
        if (part.Failure is not null) ReportFailure();

        void ReportFailure()
        {
            if (Interlocked.Exchange(ref failureReported, 1) == 0) _ = ApplyRecordingFailureAsync(part);
        }
    }

    private async Task ApplyRecordingFailureAsync(SessionRecording owner)
    {
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(recording, owner) || terminalReleased) return;
            StopFailedRecording();
            macro?.RecordingFailed();
        }
        finally { state.Release(); }
    }

    private void StopFailedRecording()
    {
        if (!recordingActive || recording?.Failure is null) return;
        recordingActive = false;
        projections?.Abort();
    }

    public void Disconnect()
    {
        disconnected = true;
        SignalAiActivity();
        macro?.Disconnected();
        lock (closeGate) transportClose ??= wire.CloseAsync().AsTask();
    }

    public void AbandonCleanup()
    {
        recording?.Abandon();
        wire.Dispose();
    }

    public void Dispose()
    {
        Disconnect();
        _ = CloseRecordingsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        Task close;
        lock (closeGate) close = transportClose!;
        await Task.WhenAll(CloseRecordingsAsync(), close).ConfigureAwait(false);
    }

    internal async Task ReleaseRetainedOutputAsync()
    {
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            if (terminalReleased) return;
            FinalizeReceive();
            terminalReleased = true;
            terminal.ReleaseNativeState();
            projections?.ReleaseReceiveOnlyState();
            SignalAiActivity();
        }
        finally { state.Release(); }
    }

    // The caller holds state. Finishing the terminal projection does not wait for the file writer.
    private void FinalizeReceive()
    {
        if (ended) return;
        try
        {
            Receive(TerminalReceiveSource.Data, protocol?.CompleteReceive() ?? []);
            CompleteReceive();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
            ResourceLimitException or NativeTerminalExecutionException)
        {
            projections?.Abort();
            recording?.MarkIncomplete(exception.Message);
        }
        finally
        {
            recordingActive = false;
            ended = true;
        }
    }

    private async Task CloseRecordingsAsync()
    {
        await recordingChanges.WaitAsync().ConfigureAwait(false);
        try
        {
            await state.WaitAsync().ConfigureAwait(false);
            try { FinalizeReceive(); }
            finally { state.Release(); }
        }
        finally
        {
            try { if (recording is not null) await recording.FinishAsync().ConfigureAwait(false); }
            finally { recordingChanges.Release(); }
        }
    }

    private static async Task<SessionRecording> OpenRecordingAsync(Func<SessionRecording> open, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        await ApplicationResources.FilePreparations.WaitAsync(token).ConfigureAwait(false);
        var preparation = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return open();
            }
            finally
            {
                ApplicationResources.FilePreparations.Release();
            }
        });
        try
        {
            return await preparation.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = CloseLateRecordingAsync(preparation);
            throw;
        }
    }

    private static async Task CloseLateRecordingAsync(Task<SessionRecording> preparation)
    {
        try
        {
            var recording = await preparation.ConfigureAwait(false);
            await recording.FinishAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or OperationCanceledException)
        {
            // The timed-out connection no longer owns this result.
        }
    }
}
