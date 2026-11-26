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
        Func<CancellationToken, Task<bool>> send, Task<bool>? pasteReady = null,
        Func<bool>? canCommit = null) : IDisposable
    {
        private readonly object gate = new();
        private bool sending, disposeRequested;
        internal TerminalMouseInput? MouseReport { get; private set; }
        internal void SetMouseReport(TerminalMouseInput report) => MouseReport = report;
        internal long DefinitePayloadBytes { get; private set; }
        internal void AddDefinitePayloadBytes(int bytes) => DefinitePayloadBytes += bytes;
        internal TransportSubmission? Submission { get; private set; }
        internal void RecordOrdinarySubmission(TransportSendResult result)
        {
            Submission = result.Status;
            AddDefinitePayloadBytes(result.DefiniteBytes);
        }
        internal void RecordPasteFailure(PasteWriteException failure)
        {
            if (!PasteFrameSubmitted && DefinitePayloadBytes == 0) Submission = failure.Submission;
        }
        internal bool PasteFrameSubmitted { get; private set; }
        internal void MarkPasteFrameSubmitted() => PasteFrameSubmitted = true;
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
        CancellationToken token, TerminalMouseInput? previous = null)
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
                    byte[] report = terminal.PlanMouse(input.Mouse with { Generation = mouseGeneration.Value }, previous).Payload;
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
                PreparedInput? request = null;
                request = new PreparedInput(unit, memory, async cancellation =>
                {
                    var result = await SubmitOrderedManualInputAsync(() =>
                    {
                        var (payload, echo) = prepared;
                        if (mouseGeneration is { } generation)
                        {
                            var report = terminal.PlanMouse(input.Mouse with { Generation = generation }, previous);
                            payload = report.Payload;
                            if (payload.Length == 0)
                                return ValueTask.FromResult<(byte[], byte[])?>(null);
                            request!.SetMouseReport(report.Effective);
                        }
                        else if (input.Kind == TerminalInputKind.Key)
                        {
                            (payload, echo) = EncodeOperation(input);
                        }
                        memory.Resize(echo.LongLength + payload.LongLength * (!IsTelnet ? 1 : 3));
                        return ValueTask.FromResult<(byte[], byte[])?>(new(payload, echo));
                    }, cancellation, text: input.Kind == TerminalInputKind.Text ? input.Text : null, altPrefix: input.Alt,
                        admit: mouseGeneration is not null ? null : () =>
                        {
                            if (!AcceptsInput || pasteActive)
                                throw new TransportAdmissionRejectedException("The terminal is no longer eligible for broadcast input.");
                        }, recordSubmission: request!.RecordOrdinarySubmission).ConfigureAwait(false);
                    return result;
                });
                return request;
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

    private (byte[] Payload, byte[] Echo) EncodeOperation(TerminalInput input, int? encodedLength = null)
    {
        if (input.Kind == TerminalInputKind.Text)
        {
            byte[] payload = encodedLength is { } length ? transmitter.Encode(input.Text, profile.TransmitSevenBitKana, length) : EncodeText(input.Text);
            if (input.Alt) payload = [0x1b, .. payload];
            return (payload, StrictUtf8.GetBytes(input.Text));
        }
        var kind = input.Kind == TerminalInputKind.Key && input.Key == TerminalKey.KeypadEnter && !terminal.ApplicationKeypad
            ? TerminalInputKind.Enter : input.Kind;
        return kind switch
        {
            TerminalInputKind.Enter => (profile.Enter switch { "cr" => [13], "lf" => [10], _ => [13, 10] }, [13, 10]),
            TerminalInputKind.Backspace => ([profile.Backspace == "bs" ? (byte)8 : (byte)127], [8]),
            TerminalInputKind.Tab => ([9], [9]),
            TerminalInputKind.Control when input.Text is [>= 'A' and <= 'Z'] => ([(byte)(input.Text[0] - 'A' + 1)], []),
            TerminalInputKind.Key => (TerminalKeys.Encode(input.Key, input.Modifiers, terminal.ApplicationCursorKeys,
                terminal.ApplicationKeypad), []),
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
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
            PreparedInput? request = null;
            request = new PreparedInput(unit, memory, async stop =>
            {
                try
                {
                    return await SendReservedPasteAsync(paste, characterDelay, lineDelay, stop, lifetime, progress,
                        preflightComplete: true, admission: admission,
                        recordPayloadProgress: request!.AddDefinitePayloadBytes,
                        recordFrameSubmission: request!.MarkPasteFrameSubmitted).ConfigureAwait(false);
                }
                catch (PasteWriteException failure)
                {
                    request!.RecordPasteFailure(failure);
                    throw;
                }
                finally { admission.Ready.TrySetResult(false); }
            }, pasteReady: admission.Ready.Task, canCommit: () => admission.CheckCommit?.Invoke() == true);
            return request;
        }
        catch
        {
            memory?.Dispose();
            unit.Dispose();
            throw;
        }
    }
}
