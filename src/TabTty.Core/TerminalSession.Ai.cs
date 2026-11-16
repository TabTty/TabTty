namespace TabTty.Core;

internal sealed class AiInputRejectedException(string code) : InvalidOperationException(code)
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
            attempt.Cancellation.ThrowIfCancellationRequested();
            attempt.MarkAdmitted();
            return write();
        }
    }

    public void Dispose()
    {
        Revoke();
        if (cancellation!.IsCompleted) revoked.Dispose();
        else _ = cancellation.ContinueWith(_ => revoked.Dispose(), TaskScheduler.Default);
    }
}

internal sealed class AiInputAttempt(AiInputLease lease, long layoutEpoch, CancellationToken cancellation)
{
    private int admitted;
    internal AiInputLease Lease { get; } = lease;
    internal long LayoutEpoch { get; } = layoutEpoch;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal bool Admitted => Volatile.Read(ref admitted) != 0;
    internal int CompletedWrites { get; private set; }
    internal void MarkAdmitted() => Volatile.Write(ref admitted, 1);
    internal void CompleteWrite() => CompletedWrites++;
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

    private async ValueTask WriteInputAsync(byte[] bytes, CancellationToken deadline, AiInputAttempt? ai)
    {
        if (ai is null)
        {
            await wire.WriteAsync(bytes, deadline).ConfigureAwait(false);
            return;
        }
        CheckAiInput(ai);
        await ai.Lease.StartWrite(ai, () => wire.WriteAsync(bytes, deadline)).ConfigureAwait(false);
        ai.CompleteWrite();
    }

    internal async Task<bool> SendAiPasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        bool framePaste, AiInputAttempt attempt, CancellationToken lifetime)
    {
        using var unit = inputUnits.Reserve(1);
        using var memory = inputBytes.Reserve(PasteReservationSize);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(attempt.Lease.Token, attempt.Cancellation);
        return await SendReservedPasteAsync(paste, characterDelay, lineDelay, stop.Token, lifetime, null,
            ai: attempt, framePaste: framePaste).ConfigureAwait(false);
    }

    internal async Task<(int Row, int Column)?> SendAiInputAsync(TerminalInput input, AiMouseAction? mouse,
        AiInputAttempt attempt, CancellationToken lifetime)
    {
        using var unit = inputUnits.Reserve(1);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(attempt.Lease.Token, attempt.Cancellation, lifetime);
        await state.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            CheckAiInput(attempt);
            if (pasteActive) throw new AiInputRejectedException("busy");
            TerminalMouseInput report = default;
            byte[] payload;
            byte[] echo;
            if (mouse is { } mouseAction)
            {
                report = GetAiMouseReport(attempt.Lease, mouseAction);
                byte[] bytes = terminal.EncodeMouse(report);
                if (bytes.Length == 0) throw new AiInputRejectedException("unsupported_action");
                payload = bytes;
                echo = [];
            }
            else (payload, echo) = EncodeOperation(input);
            using var memory = inputBytes.Reserve(echo.LongLength + payload.LongLength * (protocol is null ? 1 : 3));
            using var deadline = PasteWriteDeadline(lifetime);
            await WriteInputAsync(EncodeInput(payload), deadline.Token, attempt).ConfigureAwait(false);
            if (echo.Length != 0 && LocalEcho)
                await ProcessBatchAsync(() => Echo(echo), CancellationToken.None).ConfigureAwait(false);
            if (mouse is not { } action) return null;
            int limit = terminal.MouseMode.SgrCoordinates ? int.MaxValue : 223;
            var effective = (Math.Min(action.Row, limit), Math.Min(action.Column, limit));
            report = report with { Row = effective.Item1 - 1, Column = effective.Item2 - 1 };
            var presses = attempt.Lease.Presses;
            if (action.Action == "press" && terminal.MouseMode.Tracking != 9) presses.Add(report.Button, report);
            if (action.Action == "release") presses.Remove(report.Button);
            if (action.Action == "move")
                foreach (int button in presses.Keys.ToArray())
                    presses[button] = presses[button] with { Row = report.Row, Column = report.Column };
            return effective;
        }
        finally { state.Release(); }
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
            await state.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (disconnected || ended) return;
                foreach (TerminalMouseInput press in lease.Presses.Values)
                {
                    byte[] release = terminal.EncodeMouse(press with { Release = true, Motion = false });
                    if (release.Length != 0)
                        await wire.WriteAsync(EncodeInput(release), deadline.Token).ConfigureAwait(false);
                }
            }
            finally { state.Release(); }
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
            await ProcessBatchAsync(() => result = observe(terminal, GetAiTerminalState()), token).ConfigureAwait(false);
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
        var window = await ObserveAiAsync((model, _) => TerminalHistory.CaptureSearch(model, cursor, query, token, started), token)
            .ConfigureAwait(false);
        var scanned = await Task.Run(() => TerminalHistory.Scan(window, query, caseSensitive, limit, token, outputBudget), token)
            .ConfigureAwait(false);
        return await ObserveAiAsync((model, info) => TerminalHistory.CompleteSearch(model, info, window, scanned,
            query, caseSensitive, limit, context, token), token).ConfigureAwait(false);
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

    private AiTerminalState GetAiTerminalState()
    {
        var hash = new HashCode();
        hash.Add(terminal.AiCursorRow);
        hash.Add(terminal.AiCursorColumn);
        hash.Add(terminal.AiCursorVisible);
        hash.Add(terminal.AiCursorShape);
        hash.Add(terminal.ApplicationKeypad);
        hash.Add(terminal.ApplicationCursorKeys);
        hash.Add(terminal.BracketedPaste);
        hash.Add(terminal.MouseMode);
        hash.Add(terminal.AiLayoutEpoch);
        for (int row = 0; row < terminal.AiRows; row++)
        {
            if (terminal.AiScreenRow(row) is not { } line) continue;
            hash.Add(line.Id);
            hash.Add(line.DisplayRevision);
        }
        return new AiTerminalState(terminal.AiLayoutEpoch, hash.ToHashCode(), terminal.AiColumns, terminal.AiRows,
            terminal.AiCursorRow, terminal.AiCursorColumn, terminal.AiCursorVisible, terminal.AiCursorShape,
            terminal.IsAlternateScreen, terminal.ApplicationCursorKeys, terminal.ApplicationKeypad,
            terminal.BracketedPaste, terminal.MouseMode);
    }
}
