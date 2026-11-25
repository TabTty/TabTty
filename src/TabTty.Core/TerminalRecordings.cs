namespace TabTty.Core;

// The session orders recording parts and owns their files. The two terminal
// models stay active across intervals so neither has to reconstruct prior output.
internal sealed class TerminalRecordings
{
    private readonly TerminalOutput terminal;
    private readonly TerminalOutput? receiveOnly;
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
            receiveOnly = new TerminalOutput(grid.Columns, retainedRows, grid.Rows,
                profile.ReceiveEncoding, profile.ReceiveSevenBitKana, profile.AmbiguousWidth, terminal.StateQuota);
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
        if (!terminal.TryReceive(source, payload, token))
        {
            refuse();
            terminal.Receive(source, payload, token);
        }
        if (receiveOnly is null) return;
        // Once display has committed, the other projection must see the same input.
        if (!receiveOnly.TryReceive(source, payload))
        {
            refuse();
            receiveOnly.Receive(source, payload);
        }
        receiveOnly.TakeReplies();
    }

    public void Echo(ReadOnlySpan<byte> payload, ResourceReservation? prepaid)
    {
        if (terminal.TryEcho(payload, prepaid)) return;
        refuse();
        terminal.EchoUtf8(payload);
    }

    public void Resize(int columns, int rows)
    {
        using var displayResize = terminal.PrepareNativeResize(columns, rows);
        using var receiveResize = receiveOnly?.PrepareNativeResize(columns, rows);
        terminal.Resize(columns, rows, displayResize);
        receiveOnly?.Resize(columns, rows, receiveResize);
    }

    public void CompleteReceive(bool flushText)
    {
        Complete(terminal);
        if (receiveOnly is null) return;
        Complete(receiveOnly);
        if (flushText)
        {
            try { receiveOnly.FlushText(); }
            catch (ResourceLimitException) { refuse(); }
        }
    }

    private void Complete(TerminalOutput output)
    {
        if (output.TryCompleteReceive()) return;
        refuse();
        output.CompleteReceive();
    }

    public void End(int interval, bool abort)
    {
        try { receiveOnly?.EndRecording(interval, abort); }
        finally { terminal.EndRecording(interval, abort); }
    }

    public void ReleaseReceiveOnlyState() => receiveOnly?.ReleaseNativeState();

}
