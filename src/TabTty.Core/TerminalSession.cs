using System.Net.Sockets;
using System.Text;

namespace TabTty.Core;

public sealed partial class TerminalSession : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SemaphoreSlim ProcessingSlots = new(4);
    // A newly visible viewport can start its snapshot while receive workers are saturated.
    private static readonly SemaphoreSlim ViewportSlot = new(1);
    private readonly SessionTransport transport;
    private readonly TerminalOutput terminal;
    private readonly ConnectionProfile profile;
    private readonly MacroRun? macro;
    private readonly TerminalEncoding transmitter;
    private readonly RecordingLifecycle recordings;
    private readonly SemaphoreSlim state = new(1);
    private readonly SemaphoreSlim manualInputOrder = new(1);
    private volatile bool ended;
    private bool terminalReleased;
    private volatile bool disconnected;
    private volatile bool pasteActive;
    private volatile string echoPolicy;
    private volatile bool remoteEcho;
    private readonly object closeGate = new();
    private Task? transportClose;
    private string? recordingStartFailure;
    private readonly ResourceQuota inputBytes = new(2 * 1024 * 1024, message: "The terminal input budget reached its limit.");
    private readonly ResourceQuota inputUnits = new(256, message: "The terminal input queue reached its limit.");

    internal TerminalSession(ISessionWire? wire, ConnectionProfile profile, SessionRecording? recording, int defaultRetainedRows,
        (int Columns, int Rows)? initialGrid = null, MacroRun? macro = null, int initialRecordingPart = 1,
        ResourceQuota? terminalState = null)
    {
        this.profile = profile;
        this.macro = macro;
        echoPolicy = profile.LocalEcho;
        var grid = initialGrid ?? (80, 24);
        RetainedRows = profile.RetainedRows ?? defaultRetainedRows;
        terminal = new TerminalOutput(grid.Columns, RetainedRows, grid.Rows, profile.ReceiveEncoding,
            profile.ReceiveSevenBitKana, profile.AmbiguousWidth, terminalState);
        transmitter = new TerminalEncoding(profile.TransmitEncoding);

        recordings = new RecordingLifecycle(terminal, state, () => !ended && !disconnected, FinalizeReceive,
            RecordingStartFailed, macro, recording, initialRecordingPart);
        try
        {
            transport = new SessionTransport(wire, profile, grid, ConsumeTransportPayloadAsync, ApplyRemoteEchoAsync,
                ConsumeTransportEofAsync);
            recordings.Bind(profile, RetainedRows, grid);
        }
        catch
        {
            recordings.ReleaseReceiveOnlyState();
            terminal.ReleaseNativeState();
            throw;
        }
        if (macro is not null)
        {
            terminal.ReceiveObserver = macro.ObserveReceive;
            macro.ObserveSession(this);
        }
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
    public string? RecordingError => recordings.Failure;
    internal event Action? RecordingPartFailed
    {
        add => recordings.PartFailed += value;
        remove => recordings.PartFailed -= value;
    }
    internal string? RecordingStartFailure => Volatile.Read(ref recordingStartFailure);
    internal RecordingStatus RecordingStatus => recordings.Status;
    internal Task RecordingActualRelease => recordings.ActualRelease;
    internal TransportStatus TransportStatus => transport.Status;
    internal Task TransportActualRelease => transport.ActualRelease;
    internal event Action? InputSendQueued;
    public bool RecordsOutput => profile.RecordsOutput;
    internal bool IsSsh => transport.IsSsh;
    internal CredentialReference? ConfiguredSshPassword => IsSsh ? profile.SshPassword : null;
    public bool IsSerial => transport.IsSerial;
    public bool CanTransfer => !disconnected && (!ended || HasExternalMacro) && transport.IsSsh;

    public Task<SftpClient> OpenFileTransferAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return transport.OpenTransferAsync(profile.TransmitEncoding, cancellationToken);
    }

    public string FileTransferEndpoint => $"{profile.SshUsername}@{SshDestination.Parse(profile.Host, profile.Port)}";
    public TimeSpan FileTransferWait => TimeSpan.FromSeconds(profile.WaitSeconds);
    public bool IsTelnet => transport.IsTelnet;
    public bool SupportsEchoChange => profile.Type != "ssh";
    public string EchoPolicy => echoPolicy;
    public bool LocalEchoEnabled => LocalEcho;
    public bool CanSetDtr => transport.CanSetDtr;
    public bool CanSetRts => transport.CanSetRts;
    public string ConnectionDetails => transport.SshDetails ??
        (profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host}:{profile.Port}");

    public static async Task<TerminalSession> ConnectAsync(string path, CancellationToken cancellationToken = default,
        SshInteraction? ssh = null, Action<ConnectionProfile>? opened = null, int defaultRetainedRows = 5000,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? viewport = null)
        => await ConnectCoreAsync(path, cancellationToken, ssh, opened, defaultRetainedRows, viewport, null).ConfigureAwait(false);

    internal static Task<TerminalSession> ConnectReviewedAsync(string path, ConnectionProfile profile, MacroRun? macro,
        CancellationToken token, SshInteraction? ssh = null, Action<ConnectionProfile>? opened = null, int defaultRetainedRows = 5000,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? viewport = null, RecordingSettings? recording = null) =>
        ConnectCoreAsync(path, token, ssh, opened, defaultRetainedRows, viewport, macro, profile, recording);

    private static async Task<TerminalSession> ConnectCoreAsync(string path, CancellationToken cancellationToken,
        SshInteraction? ssh, Action<ConnectionProfile>? opened, int defaultRetainedRows,
        Func<CancellationToken, Task<(int Columns, int Rows)>>? viewport, MacroRun? macro,
        ConnectionProfile? reviewed = null, RecordingSettings? recordingSettings = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultRetainedRows, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultRetainedRows, 100_000);
        using var loadDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loadDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        ConnectionProfile profile;
        try { profile = reviewed ?? await ConnectionProfile.LoadAsync(path, loadDeadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (macro is not null && !cancellationToken.IsCancellationRequested &&
            error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or
                System.Text.Json.JsonException or OperationCanceledException)
        { throw new MacroError(MacroErrorCode.ProfileLoad); }
        if (profile.HasPostConnectionMacro && macro is null)
            throw new InvalidOperationException("This connection cannot run the configured post-connection macro.");
        opened?.Invoke(profile);
        if (profile.Type == "ssh" && ssh is null)
            throw new InvalidOperationException("SSH requires host verification and authentication interaction.");
        SessionRecording? recording;
        int recordingPart;
        try
        {
            (recording, recordingPart) = await RecordingLifecycle.PrepareInitialAsync(path, profile, macro, cancellationToken,
                recordingSettings).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is
            (IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException or
             OperationCanceledException or FormatException))
        { throw new RecordingStartException(error.Message, error); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
        TerminalSession? session = null;
        (int Columns, int Rows) grid = (80, 24);
        try
        {
            if (profile.Type != "ssh") grid = await GetGridAsync(deadline.Token).ConfigureAwait(false);
            session = new TerminalSession(null, profile, recording, defaultRetainedRows, grid, macro, recordingPart);
            await session.transport.StartAsync(deadline.Token, ssh, GetSshGridAsync, session.ProcessPayloadAsync, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.Type == "ssh")
            {
                using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                replyDeadline.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
                var pty = grid;
                var latest = await GetSshGridAsync(replyDeadline.Token).ConfigureAwait(false);
                if (latest != pty)
                {
                    var geometry = session.transport.CommitGeometry(latest.Columns, latest.Rows);
                    var result = await session.transport.ApplyGeometryAsync(geometry, replyDeadline.Token).ConfigureAwait(false);
                    if (result.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                }
                await session.SendRepliesAsync(replyDeadline.Token).ConfigureAwait(false);
            }
            if (session.RecordingStartFailure is { } failure) throw new RecordingStartException(failure);
            return session;
        }
        catch (Exception exception)
        {
            if (session is not null)
            {
                if (exception is ResourceLimitException) session.recordings.MarkIncomplete(exception.Message);
                try
                {
                    if (macro is not null) await session.ReleaseMacroObservationsAsync(macro).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                finally { await session.ReleaseRetainedOutputAsync().ConfigureAwait(false); }
            }
            else if (recording is not null) await recording.FinishAsync().ConfigureAwait(false);
            if (session?.RecordingStartFailure is { } failure && exception is not RecordingStartException)
                throw new RecordingStartException(failure, exception);
            throw;
        }

        async Task<(int Columns, int Rows)> GetSshGridAsync(CancellationToken token)
        {
            var prior = grid;
            var next = await GetGridAsync(token).ConfigureAwait(false);
            if (next != prior)
            {
                var owner = session!;
                await owner.state.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await ProcessBatchAsync(() => owner.recordings.Resize(next.Columns, next.Rows), token).ConfigureAwait(false);
                }
                finally { owner.state.Release(); }
            }
            return next;
        }

        async Task<(int Columns, int Rows)> GetGridAsync(CancellationToken token)
        {
            if (viewport is null) return grid;
            var requested = await viewport(token).ConfigureAwait(false);
            grid = (Math.Clamp(requested.Columns, 2, 1024),
                Math.Clamp(requested.Rows, 1, Math.Min(512, profile.RetainedRows ?? defaultRetainedRows)));
            return grid;
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
        if (ended) return false;
        try
        {
            var outcome = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (outcome == ReceiveOutcome.Ended) return false;
            if (outcome == ReceiveOutcome.Completed && recordings.Status.Disposition == RecordingDisposition.Saving)
                await recordings.ActualRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is ResourceLimitException or NativeTerminalExecutionException)
        {
            await state.WaitAsync().ConfigureAwait(false);
            try { FailTerminalOperation(exception); }
            finally { state.Release(); }
            throw;
        }
    }

    private async Task ConsumeTransportPayloadAsync(TerminalReceiveSource source, byte[] payload, CancellationToken token)
    {
        await state.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (ended) return;
            await ProcessPayloadAsync(source, payload, token).ConfigureAwait(false);
        }
        finally { state.Release(); }
        await SendRepliesAfterConsumptionAsync(token).ConfigureAwait(false);
    }

    private async Task ApplyRemoteEchoAsync(bool enabled, CancellationToken token)
    {
        await state.WaitAsync(token).ConfigureAwait(false);
        try { if (!ended) remoteEcho = enabled; }
        finally { state.Release(); }
    }

    private async Task ConsumeTransportEofAsync(byte[] tail, CancellationToken token)
    {
        await state.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (ended) return;
            await ProcessBatchAsync(() =>
            {
                Receive(TerminalReceiveSource.Data, tail);
                recordings.CompleteReceive(keepInterval: transport.CanSendAfterEof && !disconnected);
                terminal.ReceiveObserver?.Invoke(new(TerminalReceiveEventKind.Eof));
            }, token).ConfigureAwait(false);
            ended = true;
            SignalAiActivity();
        }
        finally { state.Release(); }
    }

    private async Task SendRepliesAfterConsumptionAsync(CancellationToken token)
    {
        await state.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (pasteActive || disconnected) return;
            byte[][] replies = terminal.TakeReplies();
            if (replies.Length == 0) return;
            var units = inputUnits.Reserve(replies.Length);
            ResourceReservation memory;
            try { memory = inputBytes.Reserve(replies.Sum(reply => reply.LongLength) * (IsTelnet ? 3 : 1)); }
            catch { units.Dispose(); throw; }
            _ = DeliverRepliesAsync(replies, memory, units);
        }
        finally { state.Release(); }
        // The receive consumer completes at bounded reply admission.
    }

    private async Task DeliverRepliesAsync(byte[][] replies, ResourceReservation memory,
        ResourceReservation units)
    {
        using (memory)
        using (units)
        using (var deadline = PasteWriteDeadline(CancellationToken.None))
        {
            try
            {
                await transport.WriteBatchAsync(replies.Select(EncodeInput), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception problem) when (problem is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!disconnected) Disconnect();
            }
        }
    }

    internal async Task ProcessPayloadAsync(TerminalReceiveSource source, byte[] payload, CancellationToken token)
    {
        const int batchBytes = TerminalOutput.ReceiveBatchBytes;
        for (int offset = 0; offset < payload.Length; offset += batchBytes)
        {
            byte[] batch = payload.Length <= batchBytes ? payload : payload.AsSpan(offset, Math.Min(batchBytes, payload.Length - offset)).ToArray();
            await ProcessBatchAsync(() => Receive(source, batch, token), token).ConfigureAwait(false);
        }
    }

    private static async Task ProcessBatchAsync(Action process, CancellationToken token, SemaphoreSlim? slots = null)
    {
        slots ??= ProcessingSlots;
        await slots.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(process, token).ConfigureAwait(false); }
        finally { slots.Release(); }
    }

    private void Receive(TerminalReceiveSource source, byte[] payload, CancellationToken token = default)
    {
        recordings.Receive(source, payload, token);
        terminal.ReceiveObserver?.Invoke(new(TerminalReceiveEventKind.Batch));
        if (payload.Length != 0) SignalAiActivity();
    }

    private void Echo(ReadOnlySpan<byte> payload, ResourceReservation? prepaid = null)
    {
        try
        {
            recordings.Echo(payload, prepaid);
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
        recordings.MarkIncomplete(exception.Message);
        Disconnect();
    }

    internal async Task WaitForTerminalPeerCloseAsync(CancellationToken token)
    {
        if (!transport.IsSsh)
        {
            if (HasExternalMacro)
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            else if (macro is not null)
                await macro.Completion.WaitAsync(token).ConfigureAwait(false);
            return;
        }
        await transport.PeerClosed.WaitAsync(token).ConfigureAwait(false);
        if (!HasExternalMacro && transport.PeerCloseFailure is { } failure)
            throw new IOException(failure.Message, failure);
    }

    private async Task SendRepliesAsync(CancellationToken token)
    {
        byte[][] replies = terminal.TakeReplies();
        if (replies.Length != 0)
            await transport.WriteBatchAsync(replies.Select(EncodeInput), token).ConfigureAwait(false);
    }

    public async Task SendBreakAsync(CancellationToken cancellationToken = default)
    {
        EnsureManualInput();
        if (transport.IsSerial)
        {
            using var unit = inputUnits.Reserve(1);
            EnsureManualInput();
            await transport.SendSerialBreakAsync(cancellationToken).ConfigureAwait(false);
        }
        else
            await SendTelnetCommandAsync(isBreak: true, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAreYouThereAsync(CancellationToken cancellationToken = default) =>
        SendTelnetCommandAsync(isBreak: false, cancellationToken);

    private readonly TerminalMouseInput?[] manualMouseButtons = new TerminalMouseInput?[3];

    public async Task SendMouseAsync(TerminalMouseInput input, CancellationToken cancellationToken = default)
    {
        using var unit = inputUnits.Reserve(1);
        await manualInputOrder.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TerminalMouseReport report;
            ValueTask<TransportSendResult> submission;
            bool heldButton = input.Button is >= 0 and < 3;
            await state.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                if (pasteActive || IsMacroOwned || HasAiInputOwner) return;
                var previous = heldButton ? manualMouseButtons[input.Button] : null;
                if (previous?.Generation != terminal.MouseMode.Generation) previous = null;
                if (heldButton && (input.Release || input.Motion) && previous is null) return;
                report = terminal.PlanMouse(input, previous);
                if (heldButton && (input.Release || !input.Motion)) manualMouseButtons[input.Button] = null;
                if (report.Payload.Length == 0) return;
                submission = SubmitInputAsync(report.Payload, cancellationToken);
            }
            finally { state.Release(); }
            var result = await submission.ConfigureAwait(false);
            if (result.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            if (heldButton && !input.Release) manualMouseButtons[input.Button] = report.Effective;
        }
        finally { manualInputOrder.Release(); }
    }

    private async Task SendTelnetCommandAsync(bool isBreak, CancellationToken cancellationToken)
    {
        using var unit = inputUnits.Reserve(1);
        ValueTask notification;
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            EnsureManualInput();
            if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
            notification = transport.TelnetCommandAsync(isBreak, cancellationToken);
        }
        finally { state.Release(); }
        await notification.ConfigureAwait(false);
    }

    public void SetSerialLine(bool dtr, bool enabled)
    {
        using var unit = inputUnits.Reserve(1);
        EnsureManualInput();
        transport.SetSerialLine(dtr, enabled);
    }

    private bool LocalEcho => echoPolicy == "on" ||
        (echoPolicy == "followRemote" && IsTelnet && !remoteEcho);

    public async Task SetLocalEchoAsync(string policy, CancellationToken cancellationToken = default)
    {
        if (policy != "off" && (profile.Type == "ssh" ||
            (profile.Type == "telnet" ? policy != "followRemote" : policy != "on")))
            throw new ArgumentException("Unsupported local echo policy.", nameof(policy));
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureConnected(); echoPolicy = policy; }
        finally { state.Release(); }
    }

    private byte[] EncodeInput(byte[] bytes) => transport.EncodeInput(bytes);

    private Task SendTextCoreAsync(string text, CancellationToken cancellationToken, bool altPrefix)
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
                    payloadAlreadyCharged: true, text: text, altPrefix: altPrefix);
            }
            catch { memory.Dispose(); throw; }
        }
        var echoMemory = inputBytes.Reserve(StrictUtf8.GetByteCount(text));
        try { return SendAsync(() =>
        {
            byte[] encoded = EncodeText(text);
            return altPrefix ? [0x1B, .. encoded] : encoded;
        }, StrictUtf8.GetBytes(text), cancellationToken, echoMemory, text: text, altPrefix: altPrefix); }
        catch { echoMemory.Dispose(); throw; }
    }

    internal Task DeliverInputAsync(TerminalInput input, CancellationToken token = default) =>
        input.Kind == TerminalInputKind.Text
            ? SendTextCoreAsync(input.Text, token, input.Alt)
            : SendAsync(() => EncodeOperation(input), token);

    public async Task ValidatePasteAsync(TerminalPaste paste, CancellationToken cancellationToken = default)
    {
        using var unit = inputUnits.Reserve(1);
        using var preflight = ReservePasteWork(paste);
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureConnected(); }
        finally { state.Release(); }
        await Task.Run(() => PreflightPasteText(paste, 0, 0, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SendPasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        CancellationToken stopToken = default, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        using var unit = inputUnits.Reserve(1);
        using var memory = ReservePasteWork(paste);
        return await SendReservedPasteAsync(paste, characterDelay, lineDelay, stopToken, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    private const int PasteReservationSize = 64 * 1024;

    private IDisposable ReservePasteWork(TerminalPaste paste)
    {
        var session = inputBytes.Reserve(PasteReservationSize);
        IDisposable? source = null;
        try
        {
            source = paste.HoldSource();
            return new PasteWork(session, ApplicationResources.TerminalState.Reserve(PasteReservationSize), source);
        }
        catch
        {
            source?.Dispose();
            session.Dispose();
            throw;
        }
    }

    private sealed class PasteWork(ResourceReservation session, ResourceReservation application, IDisposable source) : IDisposable
    {
        public void Dispose()
        {
            application.Dispose();
            session.Dispose();
            source.Dispose();
        }
    }

    private async Task<bool> SendReservedPasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        CancellationToken stopToken, CancellationToken cancellationToken, IProgress<int>? progress,
        bool preflightComplete = false, AiInputAttempt? ai = null, bool framePaste = true, PasteAdmission? admission = null,
        Action<int>? recordPayloadProgress = null, Action? recordFrameSubmission = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characterDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(characterDelay, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(lineDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lineDelay, 10_000);
        if (paste.CharacterCount == 0)
        {
            if (admission is null) return true;
            admission.CheckCommit = () => AcceptsInput && !cancellationToken.IsCancellationRequested;
            admission.Ready.TrySetResult(true);
            return await admission.Commit.WaitAsync(stopToken).ConfigureAwait(false);
        }
        using var pacing = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
        if (!preflightComplete)
        {
            try
            {
                await Task.Run(() => PreflightPasteText(paste, characterDelay, lineDelay, pacing.Token),
                    pacing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested) { return false; }
        }
        bool bracketed;
        ValueTask<bool> startFrame = ValueTask.FromResult(true);
        ResourceReservation? echoAllowance = null;
        using var opening = admission is null ? PasteWriteDeadline(cancellationToken) :
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await state.WaitAsync(admission is null ? opening.Token : pacing.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (ai is null) EnsureManualInput();
            else CheckAiInput(ai);
            if (pasteActive) throw new InvalidOperationException("A paste is already in progress.");
            if (stopToken.IsCancellationRequested) return false;
            bracketed = framePaste && terminal.BracketedPaste;
            bool localEcho = LocalEcho;
            if (admission is not null)
            {
                if (localEcho)
                {
                    var first = paste.GetChunks(characterDelay, lineDelay).First();
                    var source = paste.Text.AsSpan(first.Offset, first.Length);
                    byte[] echoBytes = NativeTerminalCore.PrepareUtf8Input(source, NativeTerminalCore.CheckUtf8Input(source));
                    echoAllowance = terminal.ReserveEcho(echoBytes);
                }
                admission.CheckCommit = () => AcceptsInput && !cancellationToken.IsCancellationRequested &&
                    bracketed == (framePaste && terminal.BracketedPaste) && localEcho == LocalEcho;
            }
            pasteActive = true;
            if (bracketed && admission is null) startFrame = TryOpenPasteFrameAsync(opening.Token, stopToken, ai);
        }
        finally { state.Release(); }
        using var reservedEcho = echoAllowance;

        try
        {
            if (admission is not null)
            {
                admission.Ready.TrySetResult(true);
                if (!await admission.Commit.WaitAsync(pacing.Token).ConfigureAwait(false))
                {
                    await ClosePasteFrameAsync(bracketed: false, cancellationToken).ConfigureAwait(false);
                    return false;
                }
                opening.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
                if (bracketed) startFrame = TryOpenPasteFrameAsync(opening.Token, stopToken, ai);
            }
            if (!await startFrame.ConfigureAwait(false))
            {
                await ClosePasteFrameAsync(bracketed: false, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (bracketed) recordFrameSubmission?.Invoke();
        }
        catch
        {
            pasteActive = false;
            throw;
        }

        int submittedSourceLength = 0;
        try
        {
            int sent = 0;
            int delay = 0;
            foreach (var chunk in paste.GetChunks(characterDelay, lineDelay))
            {
                if (stopToken.IsCancellationRequested) break;
                if (delay != 0)
                {
                    try { await Task.Delay(delay, pacing.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested) { break; }
                }

                using var writing = PasteWriteDeadline(cancellationToken);
                ValueTask<int> submission;
                byte[] payload;
                TerminalEncoding.PasteBoundaries boundaries;
                bool echo;
                await state.WaitAsync(writing.Token).ConfigureAwait(false);
                try
                {
                    if (stopToken.IsCancellationRequested) break;
                    EnsureConnected();
                    var text = paste.Text.AsSpan(chunk.Offset, chunk.Length);
                    payload = transmitter.Encode(text, profile.TransmitSevenBitKana);
                    boundaries = transmitter.MapPasteBoundaries(text, payload, profile.TransmitSevenBitKana);
                    echo = LocalEcho;
                    Func<int, byte[]>? closeCodec = transmitter.CanClosePrefix ? bytes =>
                        transmitter.ClosePrefix(paste.Text.AsSpan(chunk.Offset, boundaries.SourcePrefix(bytes)),
                            payload.AsSpan(0, bytes), profile.TransmitSevenBitKana) : null;

                    if (ai is null)
                        submission = transport.WriteBoundedPayloadAsync(payload, boundaries.Bytes, writing.Token, stopToken, closeCodec);
                    else
                    {
                        CheckAiInput(ai);
                        submission = transport.WriteBoundedPayloadAsync(payload, boundaries.Bytes, writing.Token, stopToken, closeCodec,
                            onFirstSubmission: () => AdmitAiWrite(ai), onQueued: InputSendQueued, stopRequested: ai.IsStopped);
                    }
                }
                finally { state.Release(); }
                int submitted;
                try { submitted = await submission.ConfigureAwait(false); }
                catch (PasteWriteException failure)
                {
                        recordPayloadProgress?.Invoke(failure.DefiniteBytes);
                        await ApplySubmittedPasteAsync(paste, chunk, payload, boundaries, failure.DefiniteBytes, echo, ai,
                        reservedEcho).ConfigureAwait(false);
                    throw;
                }
                recordPayloadProgress?.Invoke(submitted);
                await ApplySubmittedPasteAsync(paste, chunk, payload, boundaries, submitted, echo, ai,
                    reservedEcho).ConfigureAwait(false);
                if (submitted != payload.Length) break;

                submittedSourceLength = chunk.Offset + chunk.Length;
                sent += chunk.CompletedGraphemes;
                delay = chunk.Delay;
                progress?.Report(sent);
            }
        }
        catch (PasteWriteException failure) when (failure.Submission != TransportSubmission.NotIssued)
        {
            Disconnect();
            pasteActive = false;
            throw;
        }
        catch
        {
            try { await ClosePasteFrameAsync(bracketed, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidOperationException) { }
            throw;
        }
        await ClosePasteFrameAsync(bracketed, cancellationToken).ConfigureAwait(false);
        return submittedSourceLength == paste.Text.Length;
    }

    private async ValueTask<bool> TryOpenPasteFrameAsync(CancellationToken deadline, CancellationToken stop, AiInputAttempt? ai)
    {
        byte[] opener = "\u001B[200~"u8.ToArray();
        int submitted = await transport.WriteBoundedPayloadAsync(opener, [opener.Length], deadline, stop,
            onFirstSubmission: ai is null ? null : () => AdmitAiWrite(ai), onQueued: InputSendQueued,
            stopRequested: ai is null ? null : ai.IsStopped).ConfigureAwait(false);
        if (submitted == 0) return false;
        ai?.CompleteWrite();
        return true;
    }

    private async Task ApplySubmittedPasteAsync(TerminalPaste paste, TerminalPaste.Chunk chunk, byte[] payload,
        TerminalEncoding.PasteBoundaries boundaries, int submitted, bool echo, AiInputAttempt? ai,
        ResourceReservation? reservedEcho)
    {
        if (submitted == 0) return;
        ai?.CompleteWrite(submitted);
        if (!echo) return;
        await state.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (terminalReleased) return;
            if (transmitter.IsUtf8) Echo(payload.AsSpan(0, boundaries.CompletePrefix(submitted)), reservedEcho);
            else
            {
                int sourceCount = boundaries.SourcePrefix(submitted);
                var echoedText = paste.Text.AsSpan(chunk.Offset, sourceCount);
                byte[] utf8 = new byte[StrictUtf8.GetByteCount(echoedText)];
                StrictUtf8.GetBytes(echoedText, utf8);
                Echo(utf8, reservedEcho);
            }
        }
        finally { state.Release(); }
    }

    private async Task ClosePasteFrameAsync(bool bracketed, CancellationToken cancellationToken)
    {
        using var closing = PasteWriteDeadline(cancellationToken);
        try
        {
            if (bracketed)
                await transport.WriteAsync("\u001B[201~"u8.ToArray(), closing.Token).ConfigureAwait(false);
            // Keep paste ownership until all admitted replies have reached the ordered writer.
            await state.WaitAsync(closing.Token).ConfigureAwait(false);
            Task replies;
            try
            {
                EnsureConnected();
                replies = SendRepliesAsync(closing.Token);
                pasteActive = false;
            }
            finally { state.Release(); }
            await replies.ConfigureAwait(false);
        }
        finally { pasteActive = false; }
    }

    private CancellationTokenSource PasteWriteDeadline(CancellationToken lifetime)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.WaitSeconds));
        return deadline;
    }

    public Task<IReadOnlyList<string>> GetOutputAsync(CancellationToken cancellationToken = default) =>
        GetOutputAsync(cancellationToken, ProcessingSlots);

    internal Task<IReadOnlyList<string>> GetViewportOutputAsync() => GetOutputAsync(default, ViewportSlot);

    private async Task<IReadOnlyList<string>> GetOutputAsync(CancellationToken cancellationToken, SemaphoreSlim slots)
    {
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CaptureSnapshotAsync(cancellationToken, slots).ConfigureAwait(false); }
        finally { state.Release(); }
    }

    private async Task<TerminalSnapshot> CaptureSnapshotAsync(CancellationToken token, SemaphoreSlim? slots = null)
    {
        ObjectDisposedException.ThrowIf(terminalReleased, this);
        TerminalSnapshot? snapshot = null;
        await ProcessBatchAsync(() => snapshot = terminal.GetRows(), token, slots).ConfigureAwait(false);
        return snapshot!;
    }

    public async Task<TerminalSnapshot> ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        SessionTransport.GeometryUpdate geometry;
        await state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            rows = Math.Clamp(rows, 1, Math.Min(512, RetainedRows));
            await ProcessBatchAsync(() =>
            {
                long prior = terminal.AiLayoutEpoch;
                recordings.Resize(columns, rows);
                if (prior != terminal.AiLayoutEpoch) SignalAiActivity();
            }, cancellationToken).ConfigureAwait(false);
            geometry = transport.CommitGeometry(columns, rows);
        }
        finally { state.Release(); }
        var notification = await transport.ApplyGeometryAsync(geometry, cancellationToken).ConfigureAwait(false);
        if (notification.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return (TerminalSnapshot)await GetOutputAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool SendDirectionAvailable => !disconnected &&
        (!ended || HasExternalMacro || transport.CanSendAfterEof);

    private void EnsureConnected()
    {
        if (!SendDirectionAvailable) throw new IOException("The terminal session has ended.");
    }

    private byte[] EncodeText(string text) => transmitter.Encode(text, profile.TransmitSevenBitKana);

    private void PreflightPasteText(TerminalPaste paste, int characterDelay, int lineDelay, CancellationToken token)
    {
        foreach (var chunk in paste.GetChunks(characterDelay, lineDelay))
        {
            token.ThrowIfCancellationRequested();
            var text = paste.Text.AsSpan(chunk.Offset, chunk.Length);
            transmitter.PreflightPasteChunk(text, profile.TransmitSevenBitKana);
        }
        token.ThrowIfCancellationRequested();
    }

    private Task SendAsync(Func<byte[]> encode, byte[] echo, CancellationToken cancellationToken,
        ResourceReservation? echoMemory = null, bool payloadAlreadyCharged = false, string? text = null,
        bool altPrefix = false) =>
        SendAsync(() => (encode(), echo), cancellationToken, echoMemory ?? inputBytes.Reserve(echo.LongLength),
            payloadAlreadyCharged, text, altPrefix);

    private async Task SendAsync(Func<(byte[] Payload, byte[] Echo)> encode,
        CancellationToken cancellationToken, ResourceReservation? queued = null,
        bool payloadAlreadyCharged = false, string? text = null, bool altPrefix = false)
    {
        using var queuedMemory = queued ?? inputBytes.Reserve(0);
        using var unit = inputUnits.Reserve(1);
        using var transmitMemory = inputBytes.Reserve(0);
        await SubmitOrderedManualInputAsync(async () =>
        {
            (byte[] Payload, byte[] Echo) input = ([], []);
            await ProcessBatchAsync(() => input = encode(), cancellationToken).ConfigureAwait(false);
            if (queued is null) queuedMemory.Resize(input.Echo.LongLength);
            transmitMemory.Resize(input.Payload.LongLength * (IsTelnet ? 3 : payloadAlreadyCharged ? 0 : 1));
            return input;
        }, cancellationToken, text, altPrefix).ConfigureAwait(false);
    }

    private async Task<bool> SubmitOrderedManualInputAsync(
        Func<ValueTask<(byte[] Payload, byte[] Echo)?>> prepare, CancellationToken cancellationToken,
        string? text = null, bool altPrefix = false, Action? admit = null, Action<TransportSendResult>? recordSubmission = null)
    {
        await manualInputOrder.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValueTask<TransportSendResult> submission;
            byte[] payload, echo;
            bool localEcho;
            await state.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                EnsureManualInput();
                if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
                if (await prepare().ConfigureAwait(false) is not { } input) return false;
                (payload, echo) = input;
                localEcho = echo.Length != 0 && LocalEcho;
                submission = SubmitInputAsync(payload, cancellationToken, admit);
            }
            finally { state.Release(); }
            var result = await submission.ConfigureAwait(false);
            recordSubmission?.Invoke(result);
            await state.WaitAsync().ConfigureAwait(false);
            try
            {
                if (localEcho)
                    await EchoSubmittedInputAsync(echo, ConfirmedInputEchoLength(result, payload, echo, text, altPrefix))
                        .ConfigureAwait(false);
                if (result.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                return true;
            }
            finally { state.Release(); }
        }
        finally { manualInputOrder.Release(); }
    }

    private ValueTask EchoSubmittedInputAsync(byte[] echo, int length) =>
        terminalReleased || length == 0 ? ValueTask.CompletedTask :
            new ValueTask(ProcessBatchAsync(() => Echo(echo.AsSpan(0, length)), CancellationToken.None));

    private ValueTask<TransportSendResult> SubmitInputAsync(byte[] payload, CancellationToken token,
        Action? admit = null) => transport.SubmitPayloadAsync(payload, token, onFirstSubmission: admit);

    private int ConfirmedInputEchoLength(TransportSendResult result, byte[] payload, byte[] echo, string? text, bool altPrefix)
    {
        if (result.Error is null || echo.Length == 0) return echo.Length;
        if (text is null || result.DefiniteBytes == 0 || text.Any(char.IsControl)) return 0;
        int prefix = altPrefix ? 1 : 0;
        return transmitter.ConfirmedEchoLength(text, payload.AsSpan(prefix), result.DefiniteBytes - prefix,
            profile.TransmitSevenBitKana);
    }

    public async Task PauseRecordingAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await recordings.PauseAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.Status.Error is { } failure) throw new IOException(failure);
    }
    public Task ResumeRecordingAsync(CancellationToken cancellationToken = default) => recordings.ResumeAsync(cancellationToken);
    internal Task RetryRecordingAsync(CancellationToken cancellationToken = default) => recordings.RetryAsync(cancellationToken);

    internal Task<RecordingOperationResult> ChangeRecordingAsync(bool resume, CancellationToken token) =>
        !resume ? recordings.PauseAsync(token) : recordings.Failure is null ? recordings.ResumeAsync(token) : recordings.RetryAsync(token);

    private void RecordingStartFailed(string message)
    {
        if (Interlocked.CompareExchange(ref recordingStartFailure, message, null) is null) Disconnect();
    }

    public void Disconnect()
    {
        disconnected = true;
        SignalAiActivity();
        macro?.Disconnected();
        lock (closeGate) transportClose ??= transport.DisconnectAsync();
    }

    public bool RecordingReleasePending => recordings.Status.ReleasePending;
    public void ProtectRecordingClose() => recordings.ProtectClose();
    internal bool AbandonCleanup() => recordings.RequestLoss();

    public void Dispose() => _ = DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        Task close;
        lock (closeGate) close = transportClose!;
        await transport.DeliverySettled.ConfigureAwait(false);
        await Task.WhenAll(recordings.CompleteDisconnectAsync(), close).ConfigureAwait(false);
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
            recordings.ReleaseReceiveOnlyState();
            SignalAiActivity();
        }
        finally { state.Release(); }
    }

    internal async Task ReleaseMacroObservationsAsync(MacroRun owner)
    {
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            if (terminal.ReceiveObserver == owner.ObserveReceive) terminal.ReceiveObserver = null;
        }
        finally { state.Release(); }
    }

    // The caller holds state. Finishing the terminal projection does not wait for the file writer.
    private void FinalizeReceive()
    {
        if (ended)
        {
            recordings.Cutoff();
            return;
        }
        try
        {
            Receive(TerminalReceiveSource.Data, transport.CompleteReceive());
            recordings.CompleteReceive(keepInterval: false);
            terminal.ReceiveObserver?.Invoke(new(TerminalReceiveEventKind.Eof));
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
            ResourceLimitException or NativeTerminalExecutionException)
        {
            recordings.MarkIncomplete(exception.Message);
        }
        finally
        {
            ended = true;
        }
    }
}
