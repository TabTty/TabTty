namespace TabTty.Core;

internal sealed class AiInputRejectedException(string code) : TransportAdmissionRejectedException(code)
{
    internal string Code { get; } = code;
}

internal sealed class AiInputLease : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource revoked = new();
    internal Dictionary<int, TerminalMouseInput> Presses { get; } = [];
    internal CancellationToken Token { get; }
    private Task? cancellation;
    internal bool IsRevoked { get; private set; }

    internal AiInputLease() => Token = revoked.Token;

    internal void Revoke()
    {
        lock (gate)
        {
            if (IsRevoked) return;
            IsRevoked = true;
            cancellation = revoked.CancelAsync();
        }
    }

    internal ValueTask StartWrite(AiInputAttempt attempt, Func<ValueTask> write)
    {
        lock (gate)
        {
            if (IsRevoked) throw new AiInputRejectedException("control_required");
            attempt.MarkAdmitted();
            return write();
        }
    }

    internal ValueTask<T> StartWrite<T>(AiInputAttempt attempt, Func<ValueTask<T>> write)
    {
        lock (gate)
        {
            if (IsRevoked) throw new AiInputRejectedException("control_required");
            attempt.MarkAdmitted();
            return write();
        }
    }

    internal void AdmitWrite(AiInputAttempt attempt)
    {
        lock (gate)
        {
            if (IsRevoked) throw new AiInputRejectedException("control_required");
            try { attempt.MarkAdmitted(); }
            catch (OperationCanceledException)
            {
                throw new AiInputRejectedException("control_required");
            }
        }
    }

    public void Dispose()
    {
        Revoke();
        if (cancellation!.IsCompleted) revoked.Dispose();
        else _ = cancellation.ContinueWith(_ => revoked.Dispose(), TaskScheduler.Default);
    }
}

internal sealed class AiInputAttempt(AiInputLease lease, long layoutEpoch, CancellationToken cancellation,
    Func<bool>? observationExpired = null)
{
    private int admitted;
    internal AiInputLease Lease { get; } = lease;
    internal long LayoutEpoch { get; } = layoutEpoch;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal bool Admitted => Volatile.Read(ref admitted) != 0;
    internal bool IsStopped() => Lease.Token.IsCancellationRequested || Cancellation.IsCancellationRequested;
    internal int CompletedWrites { get; private set; }
    internal TransportSubmission? Submission { get; private set; }
    internal long DefinitePayloadBytes { get; private set; }
    internal void MarkAdmitted()
    {
        Cancellation.ThrowIfCancellationRequested();
        if (!Admitted && observationExpired?.Invoke() == true)
            throw new AiInputRejectedException("stale_observation");
        Volatile.Write(ref admitted, 1);
    }
    internal void CompleteWrite(int payloadBytes = 0)
    {
        CompletedWrites++;
        DefinitePayloadBytes += payloadBytes;
    }
    internal void RecordOrdinarySubmission(TransportSendResult result)
    {
        Submission = result.Status;
        if (result.Error is null || result.DefiniteBytes != 0) CompleteWrite(result.DefiniteBytes);
    }
    internal void RecordPasteFailure(PasteWriteException failure)
    {
        if (CompletedWrites == 0) Submission = failure.Submission;
    }
}

internal readonly record struct AiTerminalState(long LayoutEpoch, int Revision, int Columns, int Rows,
    int CursorRow, int CursorColumn, bool CursorVisible, TerminalCursorShape CursorStyle, bool IsAlternate,
    bool ApplicationCursor, bool ApplicationKeypad, bool BracketedPaste, TerminalMouseMode Mouse);

internal readonly record struct AiMouseAction(string Action, int Button, int Row, int Column, TerminalModifiers Modifiers);

public sealed partial class TerminalSession
{
    private AiInputLease? aiInputOwner;
    private readonly object aiActivityGate = new();
    private TaskCompletionSource? aiActivity;
    internal bool HasAiInputOwner => Volatile.Read(ref aiInputOwner) is not null;
    internal bool AiConnectionEnded => disconnected || ended;
    internal string AiReceiveEncoding => profile.ReceiveEncoding;
    internal string AiTransmitEncoding => profile.TransmitEncoding;

    internal AiInputLease? TryGrantAiInput()
    {
        if (!AcceptsInput || AiConnectionEnded || pasteActive || inputUnits.Used != 0) return null;
        var lease = new AiInputLease();
        if (Interlocked.CompareExchange(ref aiInputOwner, lease, null) is null)
        {
            if (inputUnits.Used == 0 && !AiConnectionEnded && !IsMacroOwned) return lease;
            Interlocked.CompareExchange(ref aiInputOwner, null, lease);
        }
        lease.Dispose();
        return null;
    }

    private void EnsureManualInput()
    {
        if (IsMacroOwned) throw new InvalidOperationException("A macro owns terminal input.");
        if (HasAiInputOwner) throw new InvalidOperationException("AI owns terminal input.");
    }

    private void CheckAiInput(AiInputAttempt attempt)
    {
        attempt.Cancellation.ThrowIfCancellationRequested();
        if (!ReferenceEquals(aiInputOwner, attempt.Lease) || attempt.Lease.Token.IsCancellationRequested ||
            IsMacroOwned || disconnected || ended)
            throw new AiInputRejectedException("control_required");
        if (!attempt.Admitted && attempt.LayoutEpoch != terminal.AiLayoutEpoch)
            throw new AiInputRejectedException("stale_observation");
    }

    private void AdmitAiWrite(AiInputAttempt attempt)
    {
        CheckAiInput(attempt);
        attempt.Lease.AdmitWrite(attempt);
    }

    internal async Task<bool> SendAiPasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        bool framePaste, AiInputAttempt attempt, CancellationToken lifetime)
    {
        using var unit = inputUnits.Reserve(1);
        using var memory = ReservePasteWork(paste);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(attempt.Lease.Token, attempt.Cancellation);
        try
        {
            return await SendReservedPasteAsync(paste, characterDelay, lineDelay, stop.Token, lifetime, null,
                ai: attempt, framePaste: framePaste).ConfigureAwait(false);
        }
        catch (PasteWriteException failure)
        {
            attempt.RecordPasteFailure(failure);
            throw;
        }
    }

    internal async Task<(int Row, int Column)?> SendAiInputAsync(TerminalInput input, AiMouseAction? mouse,
        AiInputAttempt attempt, CancellationToken lifetime)
    {
        using var unit = inputUnits.Reserve(1);
        using var memory = inputBytes.Reserve(0);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(attempt.Lease.Token, attempt.Cancellation, lifetime);
        TerminalMouseInput report = default;
        TerminalMouseMode mode = default;
        byte[] echo, payload;
        bool localEcho;
        InputCutoff? cutoff = null;
        ValueTask<TransportSendResult> submission;
        await state.WaitAsync(pending.Token).ConfigureAwait(false);
        using var deadline = PasteWriteDeadline(lifetime);
        try
        {
            CheckAiInput(attempt);
            if (pasteActive) throw new AiInputRejectedException("busy");
            if (mouse is { } mouseAction)
            {
                mode = terminal.MouseMode;
                report = GetAiMouseReport(attempt.Lease, mouseAction);
                var plan = terminal.PlanMouse(report, attempt.Lease.Presses.TryGetValue(report.Button, out var previous) ? previous : null);
                if (plan.Payload.Length == 0) throw new AiInputRejectedException("unsupported_action");
                report = plan.Effective;
                payload = plan.Payload;
                echo = [];
            }
            else if (input.Kind == TerminalInputKind.Text)
            {
                int length = transmitter.GetByteCount(input.Text, profile.TransmitSevenBitKana);
                int alt = input.Alt ? 1 : 0;
                int closure = transmitter.CanClosePrefix ? NativeTerminalCore.MaxTransmitClosureBytes : 0;
                memory.Resize(StrictUtf8.GetByteCount(input.Text) + (length + alt + closure) * (IsTelnet ? 3L : 1L) + alt * length);
                (payload, echo) = EncodeOperation(input, length);
                cutoff = CreateAiTextCutoff(input.Text, payload, alt, attempt, pending.Token);
            }
            else (payload, echo) = EncodeOperation(input);
            if (cutoff is null) memory.Resize(echo.LongLength + payload.LongLength * (IsTelnet ? 3L : 1L));
            localEcho = echo.Length != 0 && LocalEcho;
            submission = mouse.HasValue
                ? attempt.Lease.StartWrite(attempt, () => SubmitInputAsync(payload, deadline.Token))
                : transport.SubmitPayloadAsync(payload, deadline.Token, () => AdmitAiWrite(attempt), cutoff);
        }
        finally { state.Release(); }
        var result = await submission.ConfigureAwait(false);
        int echoLength = ConfirmedInputEchoLength(result, payload, echo,
            input.Kind == TerminalInputKind.Text ? input.Text : null, input.Alt);
        attempt.RecordOrdinarySubmission(result);
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            if (localEcho) await EchoSubmittedInputAsync(echo, echoLength).ConfigureAwait(false);
            if (result.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            if (mouse is not { } action) return null;
            var effective = (report.Row + 1, report.Column + 1);
            if (terminalReleased || terminal.MouseMode.Generation != mode.Generation) return effective;
            var presses = attempt.Lease.Presses;
            if (action.Action == "press" && mode.Tracking != 9) presses.Add(report.Button, report);
            if (action.Action == "release") presses.Remove(report.Button);
            if (action.Action == "move")
                foreach (int button in presses.Keys.ToArray())
                    presses[button] = presses[button] with { Row = report.Row, Column = report.Column };
            return effective;
        }
        finally { state.Release(); }
    }

    private InputCutoff CreateAiTextCutoff(string text, byte[] payload, int alt, AiInputAttempt attempt, CancellationToken pending)
    {
        bool sevenBitKana = profile.TransmitSevenBitKana;
        return new(pending, attempt.IsStopped,
            offset =>
            {
                if (offset == 0 || offset == payload.Length) return offset;
                // An Alt prefix belongs to the first encoded text unit.
                return alt + NativeTerminalCore.TransmitCutoff(transmitter.NativePage, text, payload.AsSpan(alt),
                    Math.Max(1, offset - alt), sevenBitKana);
            }, transmitter.CanClosePrefix ? count =>
            {
                var confirmed = payload.AsSpan(alt, count - alt);
                int length = transmitter.ConfirmedSourcePrefix(text, payload.AsSpan(alt), count - alt, sevenBitKana);
                return transmitter.ClosePrefix(text.AsSpan(0, length), confirmed, sevenBitKana);
            } : null);
    }

    private TerminalMouseInput GetAiMouseReport(AiInputLease lease, AiMouseAction action)
    {
        TerminalMouseMode mode = terminal.MouseMode;
        foreach (int button in lease.Presses.Keys.ToArray())
            if (lease.Presses[button].Generation != mode.Generation) lease.Presses.Remove(button);
        if (action.Row < 1 || action.Row > terminal.AiRows || action.Column < 1 || action.Column > terminal.AiColumns ||
            action.Action == "release" && !lease.Presses.ContainsKey(action.Button) ||
            action.Action == "press" && lease.Presses.ContainsKey(action.Button))
            throw new AiInputRejectedException("unsupported_action");
        int selected = action.Action == "move" ? lease.Presses.Keys.LastOrDefault(3) : action.Button;
        return new TerminalMouseInput(selected, action.Column - 1, action.Row - 1, action.Action == "release",
            action.Action == "move", action.Modifiers, mode.Generation);
    }

    internal async Task ReleaseAiInputAsync(AiInputLease lease)
    {
        try
        {
            if (lease.Presses.Count == 0) return;
            using var deadline = PasteWriteDeadline(CancellationToken.None);
            Task submission;
            await state.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (disconnected || ended) return;
                var releases = new List<byte[]>();
                foreach (TerminalMouseInput press in lease.Presses.Values)
                {
                    byte[] release = terminal.PlanMouse(press with { Release = true, Motion = false }, press).Payload;
                    if (release.Length != 0) releases.Add(EncodeInput(release));
                }
                submission = transport.WriteBatchAsync(releases, deadline.Token);
            }
            finally { state.Release(); }
            await submission.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref aiInputOwner, null, lease);
            lease.Dispose();
        }
    }

    internal async Task<T> ObserveAiAsync<T>(Func<TerminalOutput, AiTerminalState, T> observe, CancellationToken token)
    {
        await state.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(terminalReleased, this);
            T result = default!;
            await ProcessBatchAsync(() => result = observe(terminal, terminal.ReadAiScreenState()), token).ConfigureAwait(false);
            return result;
        }
        finally { state.Release(); }
    }

    internal Task<(AiTerminalState State, Task Change)> ObserveAiChangeAsync(CancellationToken token) =>
        ObserveAiAsync((_, value) =>
        {
            lock (aiActivityGate)
                return (value, (Task)(aiActivity ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        }, token);

    internal Task<TerminalHistory.HistoryPage> ReadRetainedHistoryAsync(TerminalHistory.Span? range,
        TerminalHistory.Point? position, int rows, int bytes, int outputBudget, CancellationToken token) =>
        ObserveAiAsync((model, info) => TerminalHistory.Read(model, info, range, position, rows, bytes, outputBudget, token), token);

    internal async Task<TerminalHistory.SearchPage> SearchRetainedHistoryAsync(TerminalHistory.SearchCursor? cursor,
        string query, bool caseSensitive, int limit, int context, long started, int outputBudget, CancellationToken token)
    {
        using var window = await ObserveAiAsync((model, _) => model.CaptureHistorySearch(cursor, query, context, started, token), token)
            .ConfigureAwait(false);
        await Task.Run(() => window.Scan(query, caseSensitive, limit, started, outputBudget, token), token)
            .ConfigureAwait(false);
        return await ObserveAiAsync((model, info) => model.CompleteHistorySearch(info, window,
            query, caseSensitive, limit, context, started, token), token).ConfigureAwait(false);
    }

    internal bool TryPublishHistory(TerminalHistory.Span[] dependencies, Action publish,
        CancellationToken token, out string? error)
    {
        error = null;
        if (!state.Wait(0, token)) return false;
        try
        {
            if (terminalReleased)
            {
                error = "session_changed";
                return true;
            }
            error = TerminalHistory.Validate(terminal, dependencies, token);
            if (error is null) publish();
            return true;
        }
        finally { state.Release(); }
    }

    private void SignalAiActivity()
    {
        TaskCompletionSource? activity;
        lock (aiActivityGate)
        {
            activity = aiActivity;
            aiActivity = null;
        }
        activity?.TrySetResult();
    }

}
