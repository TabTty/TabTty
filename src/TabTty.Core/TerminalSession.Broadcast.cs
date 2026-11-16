namespace TabTty.Core;

internal enum TerminalInputKind { Text, Enter, Backspace, Tab, Control, Key, Mouse }

internal readonly record struct TerminalInput(TerminalInputKind Kind, string Text = "", bool Alt = false,
    TerminalKey Key = default, TerminalModifiers Modifiers = default, TerminalMouseInput Mouse = default);

public sealed partial class TerminalSession
{
    internal bool AcceptsInput => (!ended || macro is not null) && !disconnected && !IsMacroOwned && !HasAiInputOwner;

    internal sealed class PreparedInput(ResourceReservation unit, ResourceReservation memory,
        Func<CancellationToken, Task<bool>> send, int? mouseGeneration = null) : IDisposable
    {
        public int? MouseGeneration => mouseGeneration;
        public bool Started { get; private set; }

        public Task<bool> SendAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Started) throw new InvalidOperationException("This terminal input has already been submitted.");
            Started = true;
            return send(token);
        }

        public void Dispose()
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
                (EncodedText payload, byte[] echo) prepared;
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
                    prepared = (new EncodedText(report, transmitState), []);
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
                    TerminalInputKind.Text => prepared.payload.Bytes.LongLength + 16,
                    TerminalInputKind.Key or TerminalInputKind.Mouse => 64,
                    _ => prepared.payload.Bytes.LongLength,
                };
                memory.Resize(prepared.echo.LongLength + bound * (protocol is null ? 1 : 3));
                var previousState = transmitState;
                return new PreparedInput(unit, memory, async cancellation =>
                {
                    await state.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        EnsureConnected();
                        EnsureManualInput();
                        if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
                        var (payload, echo) = prepared;
                        if (mouseGeneration is { } generation)
                        {
                            byte[] report = terminal.EncodeBroadcastMouse(input.Mouse with { Generation = generation });
                            if (report.Length == 0) return false;
                            payload = new EncodedText(report, transmitState);
                        }
                        else if (input.Kind == TerminalInputKind.Key || input.Kind == TerminalInputKind.Text && transmitState != previousState)
                        {
                            (payload, echo) = EncodeOperation(input);
                        }
                        else if (input.Kind != TerminalInputKind.Text)
                        {
                            payload = payload with { Next = transmitState };
                        }
                        memory.Resize(echo.LongLength + payload.Bytes.LongLength * (protocol is null ? 1 : 3));
                        await wire.WriteAsync(EncodeInput(payload.Bytes), cancellation).ConfigureAwait(false);
                        transmitState = payload.Next;
                        if (echo.Length > 0 && LocalEcho)
                            await ProcessBatchAsync(() => Echo(echo), CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }
                    finally { state.Release(); }
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

    private (EncodedText Payload, byte[] Echo) EncodeOperation(TerminalInput input)
    {
        if (input.Kind == TerminalInputKind.Text)
        {
            EncodedText payload = EncodeText(input.Text);
            if (input.Alt) payload = payload with { Bytes = [0x1b, .. payload.Bytes] };
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
        return (new EncodedText(result.Bytes, transmitState), result.Echo);
    }

    internal async Task<PreparedInput> PreparePasteAsync(TerminalPaste paste, int characterDelay, int lineDelay,
        CancellationToken token, CancellationToken lifetime, IProgress<int>? progress)
    {
        var unit = inputUnits.Reserve(1);
        ResourceReservation? memory = null;
        try
        {
            memory = inputBytes.Reserve(PasteReservationSize);
            await state.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                EnsureManualInput();
                if (pasteActive) throw new InvalidOperationException("A paste is in progress.");
                _ = EncodeText(paste.Text);
            }
            finally { state.Release(); }
            return new PreparedInput(unit, memory,
                stop => SendReservedPasteAsync(paste, characterDelay, lineDelay, stop, lifetime, progress,
                    preflightComplete: true));
        }
        catch
        {
            memory?.Dispose();
            unit.Dispose();
            throw;
        }
    }
}
