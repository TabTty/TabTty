namespace TabTty.Core;

internal enum TerminalInputKind { Text, Enter, Backspace, Tab, Control, Key, Mouse }

internal readonly record struct TerminalInput(TerminalInputKind Kind, string Text = "", bool Alt = false,
    TerminalKey Key = default, TerminalModifiers Modifiers = default, TerminalMouseInput Mouse = default);

public sealed partial class TerminalSession
{
    internal bool AcceptsInput => SendDirectionAvailable && !IsMacroOwned && !HasAiInputOwner;

    private sealed class PasteAdmission(Task<bool> commit)
    {
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> Commit { get; } = commit;
        internal Func<bool>? CheckCommit { get; set; }
    }

    internal sealed class PreparedInput(ResourceReservation unit, IDisposable memory,
        Func<CancellationToken, Task<bool>> send, int? mouseGeneration = null, Task<bool>? pasteReady = null,
        Func<bool>? canCommit = null) : IDisposable
    {
        private readonly object gate = new();
        private bool sending, disposeRequested;
        public int? MouseGeneration => mouseGeneration;
        public bool Started { get; private set; }
        internal Task<bool>? PasteReady => pasteReady;
        internal bool CanCommitPaste => canCommit?.Invoke() == true;

        public Task<bool> SendAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposeRequested, this);
                if (Started) throw new InvalidOperationException("This terminal input has already been submitted.");
                Started = sending = true;
            }
            return SendOwnedAsync(token);
        }

        private async Task<bool> SendOwnedAsync(CancellationToken token)
        {
            try { return await send(token).ConfigureAwait(false); }
            finally
            {
                lock (gate)
                {
                    sending = false;
                    if (disposeRequested) Release();
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposeRequested) return;
                disposeRequested = true;
                if (!sending) Release();
            }
        }

        private void Release()
        {
            memory.Dispose();
            unit.Dispose();
        }
    }

    internal async Task<PreparedInput?> PrepareInputAsync(TerminalInput input, int? gestureGeneration,
        CancellationToken token)
    {
        var unit = inputUnits.Reserve(1);
        ResourceReservation? memory = null;
        try
        {
            memory = inputBytes.Reserve(0);
            await state.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                EnsureManualInput();
                if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
                int? mouseGeneration = null;
                (byte[] payload, byte[] echo) prepared;
                if (input.Kind == TerminalInputKind.Mouse)
                {
                    mouseGeneration = gestureGeneration ?? terminal.MouseMode.Generation;
                    byte[] report = terminal.EncodeBroadcastMouse(input.Mouse with { Generation = mouseGeneration.Value });
                    if (report.Length == 0)
                    {
                        memory.Dispose();
                        unit.Dispose();
                        return null;
                    }
                    prepared = (report, []);
                }
                else
                {
                    if (input.Kind == TerminalInputKind.Text)
                    {
                        if (input.Text.Length > 1024 * 1024 || input.Text.Any(char.IsControl))
                            throw new ArgumentException("Terminal text contains a control character or exceeds its size limit.");
                        memory.Resize(StrictUtf8.GetByteCount(input.Text));
                    }
                    prepared = EncodeOperation(input);
                }
                long bound = input.Kind switch
                {
                    TerminalInputKind.Text => prepared.payload.LongLength,
                    TerminalInputKind.Key or TerminalInputKind.Mouse => 64,
                    _ => prepared.payload.LongLength,
                };
                memory.Resize(prepared.echo.LongLength + bound * (!IsTelnet ? 1 : 3));
                return new PreparedInput(unit, memory, async cancellation =>
                {
                    await manualInputOrder.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        var (payload, echo) = prepared;
                        bool localEcho;
                        ValueTask<TransportSendResult> submission;
                        await state.WaitAsync(cancellation).ConfigureAwait(false);
                        try
                        {
                            EnsureConnected();
                            EnsureManualInput();
                            if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
                            if (mouseGeneration is { } generation)
                            {
                                byte[] report = terminal.EncodeBroadcastMouse(input.Mouse with { Generation = generation });
                                if (report.Length == 0) return false;
                                payload = report;
                            }
                            else if (input.Kind == TerminalInputKind.Key)
                            {
                                (payload, echo) = EncodeOperation(input);
                            }
                            memory.Resize(echo.LongLength + payload.LongLength * (!IsTelnet ? 1 : 3));
                            localEcho = echo.Length > 0 && LocalEcho;
                            submission = transport.SubmitPreparedAsync(EncodeInput(payload), cancellation);
                        }
                        finally { state.Release(); }
                        var result = await submission.ConfigureAwait(false);
                        if (localEcho)
                        {
                            await state.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (!terminalReleased)
                                    await ProcessBatchAsync(() => Echo(echo.AsSpan(0, ConfirmedInputEchoLength(result, payload, echo,
                                        input.Kind == TerminalInputKind.Text ? input.Text : null, input.Alt))),
                                        CancellationToken.None).ConfigureAwait(false);
                            }
                            finally { state.Release(); }
                        }
                        if (result.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                        return true;
                    }
                    finally { manualInputOrder.Release(); }
                }, mouseGeneration);
            }
            finally { state.Release(); }
        }
        catch
        {
            memory?.Dispose();
            unit.Dispose();
            throw;
        }
    }

    private (byte[] Payload, byte[] Echo) EncodeOperation(TerminalInput input)
    {
        if (input.Kind == TerminalInputKind.Text)
        {
            byte[] payload = EncodeText(input.Text);
            if (input.Alt) payload = [0x1b, .. payload];
            return (payload, StrictUtf8.GetBytes(input.Text));
        }
        (byte[] Bytes, byte[] Echo) result = input.Kind switch
        {
            TerminalInputKind.Enter => (profile.Enter switch { "cr" => [13], "lf" => [10], _ => [13, 10] }, [13, 10]),
            TerminalInputKind.Backspace => ([profile.Backspace == "bs" ? (byte)8 : (byte)127], [8]),
            TerminalInputKind.Tab => ([9], [9]),
            TerminalInputKind.Control => ([(byte)(input.Text[0] - 'A' + 1)], []),
            TerminalInputKind.Key when input.Key == TerminalKey.KeypadEnter && !terminal.ApplicationKeypad =>
                (profile.Enter switch { "cr" => [13], "lf" => [10], _ => [13, 10] }, [13, 10]),
            TerminalInputKind.Key => (TerminalKeys.Encode(input.Key, input.Modifiers, terminal.ApplicationCursorKeys,
                terminal.ApplicationKeypad), []),
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
        return result;
    }

    internal async Task<PreparedInput> PreparePasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        CancellationToken token, CancellationToken lifetime, IProgress<int>? progress, Task<bool> commit)
    {
        var unit = inputUnits.Reserve(1);
        IDisposable? memory = null;
        try
        {
            memory = ReservePasteWork(paste);
            await state.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                EnsureManualInput();
                if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
            }
            finally { state.Release(); }
            await Task.Run(() => PreflightPasteText(paste, characterDelay, lineDelay, token), token).ConfigureAwait(false);
            var admission = new PasteAdmission(commit);
            return new PreparedInput(unit, memory, async stop =>
            {
                try
                {
                    return await SendReservedPasteAsync(paste, characterDelay, lineDelay, stop, lifetime, progress,
                        preflightComplete: true, admission: admission).ConfigureAwait(false);
                }
                finally { admission.Ready.TrySetResult(false); }
            }, pasteReady: admission.Ready.Task, canCommit: () => admission.CheckCommit?.Invoke() == true);
        }
        catch
        {
            memory?.Dispose();
            unit.Dispose();
            throw;
        }
    }
}
