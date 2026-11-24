namespace TabTty.Core;

internal enum RecordingDisposition { Inactive, Active, Saving, Saved, Incomplete }

internal readonly record struct RecordingStatus(int Part, RecordingDisposition Disposition, string? Error, bool ReleasePending);
internal readonly record struct RecordingOperationResult(RecordingStatus Status, bool Changed);

/// <summary>Owns the recording intervals and outstanding file work for one terminal generation.</summary>
internal sealed class RecordingLifecycle(TerminalOutput terminal, SemaphoreSlim terminalOrder,
    Func<bool> canActivate, Action finalizeReceive, Action<string> failConnection,
    MacroRun? macro, SessionRecording? initial, int initialPart)
{
    private readonly SemaphoreSlim changes = new(1);
    private readonly object gate = new();
    private SessionRecording? part = initial;
    private int number = initialPart;
    private volatile bool active = initial is not null;
    private volatile bool protectedClose;
    private TerminalRecordings? projections;
    private Task<RecordingOperationResult>? pause, close;

    internal string? Failure => Volatile.Read(ref part)?.Failure;
    internal event Action? PartFailed;

    internal RecordingStatus Status
    {
        get
        {
            var current = Volatile.Read(ref part);
            if (current is null) return new(0, RecordingDisposition.Inactive, null, false);
            var pending = !current.ActualRelease.IsCompleted;
            string? failure = current.Failure;
            return new(current.PartNumber, failure is not null ? RecordingDisposition.Incomplete :
                active ? RecordingDisposition.Active : pending ? RecordingDisposition.Saving :
                RecordingDisposition.Saved, failure, pending);
        }
    }

    internal Task ActualRelease => Volatile.Read(ref part)?.ActualRelease ?? Task.CompletedTask;

    internal void Bind(ConnectionProfile profile, int retainedRows, (int Columns, int Rows) grid)
    {
        if (part is not { } current) return;
        projections = new TerminalRecordings(terminal, profile, current, retainedRows, grid);
        ObserveFailure(current);
    }

    internal void Receive(TerminalReceiveSource source, byte[] payload, CancellationToken token)
    {
        StopFailedPart();
        if (projections is null) terminal.Receive(source, payload, token);
        else projections.Receive(source, payload, token);
        StopFailedPart();
    }

    internal void Echo(ReadOnlySpan<byte> payload, ResourceReservation? prepaid)
    {
        StopFailedPart();
        if (projections is null) terminal.EchoUtf8(payload, prepaid);
        else projections.Echo(payload, prepaid);
        StopFailedPart();
    }

    internal void Resize(int columns, int rows)
    {
        StopFailedPart();
        if (projections is null) terminal.Resize(columns, rows);
        else projections.Resize(columns, rows);
        StopFailedPart();
    }

    // These ordered methods run within terminalOrder. File completion is independently owned.
    internal void CompleteReceive(bool keepInterval)
    {
        if (projections is null) terminal.CompleteReceive();
        else projections.CompleteReceive(keepInterval);
        if (!keepInterval) Cutoff();
    }

    internal Task<RecordingOperationResult> CompleteDisconnectAsync()
    {
        lock (gate) return close ??= CompleteDisconnectOwnedAsync();
    }

    internal void Cutoff()
    {
        projections?.Cutoff();
        active = false;
        Volatile.Read(ref part)?.FinishAsync();
    }

    internal void MarkIncomplete(string message)
    {
        active = false;
        projections?.Abort();
        Volatile.Read(ref part)?.MarkIncomplete(message);
    }

    internal void ReleaseReceiveOnlyState() => projections?.ReleaseReceiveOnlyState();

    internal Task<RecordingOperationResult> PauseAsync(CancellationToken observation)
    {
        observation.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (pause is not { IsCompleted: false }) pause = PauseOwnedAsync();
            return pause.WaitAsync(observation);
        }
    }

    private async Task<RecordingOperationResult> PauseOwnedAsync()
    {
        await changes.WaitAsync().ConfigureAwait(false);
        SessionRecording? current;
        bool changed;
        try
        {
            await terminalOrder.WaitAsync().ConfigureAwait(false);
            try
            {
                StopFailedPart();
                changed = active;
                Cutoff();
                current = part;
            }
            finally { terminalOrder.Release(); }
            if (current is null) return new(Status, changed);
            await Task.WhenAny(current.ActualRelease, current.Incomplete).ConfigureAwait(false);
            if (current.Failure is null) await current.ActualRelease.ConfigureAwait(false);
            return new(Status, changed);
        }
        finally { changes.Release(); }
    }

    internal Task<RecordingOperationResult> ResumeAsync(CancellationToken activation) => StartPartAsync(retry: false, activation);
    internal Task<RecordingOperationResult> RetryAsync(CancellationToken activation) => StartPartAsync(retry: true, activation);

    private async Task<RecordingOperationResult> StartPartAsync(bool retry, CancellationToken activation)
    {
        if (!await changes.WaitAsync(0, activation).ConfigureAwait(false))
            throw new IOException("A recording operation is still pending.");
        SessionRecording? candidate = null;
        bool starting = false;
        try
        {
            var previous = part ?? throw new InvalidOperationException("Recording is disabled in the profile.");
            if (previous.Failure is null && (active || retry)) return new(Status, Changed: false);
            if (retry && !previous.ActualRelease.IsCompleted)
                throw new IOException("The previous recording part still owns its files.");
            starting = true;
            await previous.ActualRelease.WaitAsync(activation).ConfigureAwait(false);
            if (previous.StartFailure) throw new RecordingStartException(previous.Failure!);
            int next = checked(++number);
            candidate = await PreparePartAsync(token => previous.OpenPart(next, token), activation).ConfigureAwait(false);
            await terminalOrder.WaitAsync(activation).ConfigureAwait(false);
            try
            {
                lock (gate)
                {
                    activation.ThrowIfCancellationRequested();
                    if (protectedClose || !canActivate()) throw new IOException("The terminal session has ended.");
                    projections!.Cutoff();
                    projections.Start(candidate);
                    Volatile.Write(ref part, candidate);
                    active = true;
                    ObserveFailure(candidate);
                    candidate = null;
                }
                if (macro?.Status.Holds.HasFlag(MacroHold.RecordingFailure) == true) macro.RecordingRecovered();
                return new(Status, Changed: true);
            }
            finally { terminalOrder.Release(); }
        }
        catch (Exception error) when (starting && canActivate() && error is
            (IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
             NotSupportedException or OperationCanceledException or FormatException))
        {
            failConnection(error.Message);
            throw;
        }
        finally
        {
            try { if (candidate is not null) await candidate.FinishAsync().ConfigureAwait(false); }
            finally { changes.Release(); }
        }
    }

    internal void ProtectClose()
    {
        lock (gate)
        {
            protectedClose = true;
            part?.ProtectClose();
        }
    }

    internal bool RequestLoss()
    {
        lock (gate) return protectedClose && part?.Abandon() == true;
    }

    private async Task<RecordingOperationResult> CompleteDisconnectOwnedAsync()
    {
        await changes.WaitAsync().ConfigureAwait(false);
        try
        {
            bool changed = active || !ActualRelease.IsCompleted;
            await terminalOrder.WaitAsync().ConfigureAwait(false);
            try { finalizeReceive(); }
            finally { terminalOrder.Release(); }
            if (part is { } current) await current.FinishAsync().ConfigureAwait(false);
            return new(Status, changed);
        }
        finally { changes.Release(); }
    }

    private void ObserveFailure(SessionRecording owner) => _ = PublishFailureAsync(owner);

    private async Task PublishFailureAsync(SessionRecording owner)
    {
        await Task.WhenAny(owner.Incomplete, owner.ActualRelease).ConfigureAwait(false);
        if (owner.Failure is null) return;
        string? fatal = null;
        await terminalOrder.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(part, owner)) return;
            if (active) StopFailedPart();
            if (owner.StartFailure) fatal = owner.Failure;
            else macro?.RecordingFailed();
        }
        finally { terminalOrder.Release(); }
        if (fatal is not null) failConnection(fatal);
        else PartFailed?.Invoke();
    }

    private void StopFailedPart()
    {
        if (!active || part?.Failure is null) return;
        active = false;
        projections?.Abort();
    }

    internal static async Task<(SessionRecording? Recording, int Part)> PrepareInitialAsync(
        string path, ConnectionProfile profile, MacroRun? macro, CancellationToken token,
        RecordingSettings? settings = null, RecordingOverrides overrides = default)
    {
        int number = 1;
        if (!profile.RecordsOutput) return (null, number);
        string? macroName = macro?.RecordingMacroName;
        var prepared = await PreparePartAsync(activation => SessionRecording.Open(path, profile, macroName,
            settings: settings ?? RecordingSettings.Resolve(path, profile, macroName, overrides: overrides), token: activation), token)
            .ConfigureAwait(false);
        return (prepared, number);
    }

    internal static async Task<SessionRecording> PreparePartAsync(Func<CancellationToken, SessionRecording> open, CancellationToken activation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(activation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        await ApplicationResources.FilePreparations.WaitAsync(token).ConfigureAwait(false);
        var result = new TaskCompletionSource<SessionRecording>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adoption = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var prepared = open(token);
                result.SetResult(prepared);
                if (!await adoption.Task.ConfigureAwait(false))
                    await prepared.FinishAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                result.TrySetException(error);
                _ = result.Task.Exception;
            }
            finally { ApplicationResources.FilePreparations.Release(); }
        });
        try
        {
            var prepared = await result.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            adoption.SetResult(true);
            return prepared;
        }
        catch
        {
            adoption.SetResult(false);
            throw;
        }
    }
}
