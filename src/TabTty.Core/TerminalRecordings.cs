namespace TabTty.Core;

// The session orders recording parts and owns their files. The two terminal
// models stay active across intervals so neither has to reconstruct prior output.
internal sealed class TerminalRecordings
{
    private readonly TerminalOutput terminal;
    private TerminalOutput? receiveOnly;
    private readonly bool raw, recordTerminal;
    private readonly Action refuse;

    public TerminalRecordings(TerminalOutput terminal, ConnectionProfile profile, int retainedRows,
        (int Columns, int Rows) grid, Action refuse)
    {
        this.terminal = terminal;
        this.refuse = refuse;
        raw = profile.RecordRaw;
        recordTerminal = profile.RecordTerminal;
        if (profile.RecordText)
        {
            receiveOnly = new TerminalOutput(grid.Columns, retainedRows, grid.Rows,
                profile.ReceiveEncoding, profile.ReceiveSevenBitKana, profile.AmbiguousWidth, terminal.StateQuota);
            receiveOnly.ConfigureReceiveOnly();
        }
    }

    public void Start(SessionRecording recording, int interval)
    {
        try
        {
            if (recordTerminal)
                terminal.BeginRecording(false, interval, value => recording.Enqueue(RecordingFormat.Terminal, value), MarkRefused);
            receiveOnly?.BeginRecording(true, interval, value => recording.Enqueue(RecordingFormat.Text, value), MarkRefused);
        }
        catch
        {
            End(interval, abort: true);
            throw;
        }

        void MarkRefused() => recording.MarkIncomplete(new ResourceLimitException().Message);
    }

    public void Receive(TerminalReceiveSource source, byte[] payload, SessionRecording? recording, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (raw) recording?.Enqueue(RecordingFormat.Raw, payload);
        for (int offset = 0; offset < payload.Length; offset += TerminalOutput.ReceiveBatchBytes)
        {
            var batch = payload.AsSpan(offset, Math.Min(TerminalOutput.ReceiveBatchBytes, payload.Length - offset));
            if (!terminal.TryReceive(source, batch, token, receiveOnly))
            {
                refuse();
                terminal.Receive(source, batch, token, receiveOnly);
            }
        }
    }

    public void Echo(ReadOnlySpan<byte> payload, ResourceReservation? prepaid)
    {
        if (terminal.TryEcho(payload, prepaid)) return;
        refuse();
        terminal.EchoUtf8(payload);
    }

    public void Resize(int columns, int rows)
    {
        using var displayResize = terminal.PrepareNativeResize(columns, rows, receiveOnly);
        terminal.Resize(columns, rows, displayResize);
    }

    public void CompleteReceive(bool flushText)
    {
        if (terminal.TryCompleteReceive(receiveOnly, flushText)) return;
        refuse();
        if (!terminal.TryCompleteReceive(receiveOnly)) throw new ResourceLimitException();
    }

    public void End(int interval, bool abort)
    {
        try { receiveOnly?.EndRecording(interval, abort); }
        finally { terminal.EndRecording(interval, abort); }
    }

    public void ReleaseReceiveOnlyState()
    {
        receiveOnly?.ReleaseNativeState();
        receiveOnly = null;
    }

}
