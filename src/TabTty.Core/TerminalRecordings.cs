namespace TabTty.Core;

// The session orders recording parts and owns their files. The two terminal
// models stay active across intervals so neither has to reconstruct prior output.
internal sealed class TerminalRecordings
{
    private readonly TerminalOutput terminal;
    private readonly TerminalOutput? receiveOnly;
    private readonly bool raw, recordTerminal;
    private SessionRecording? part;
    private int interval;

    public TerminalRecordings(TerminalOutput terminal, ConnectionProfile profile, SessionRecording initial, int retainedRows,
        (int Columns, int Rows) grid)
    {
        this.terminal = terminal;
        raw = profile.RecordRaw;
        recordTerminal = profile.RecordTerminal;
        if (profile.RecordText)
            receiveOnly = new TerminalOutput(grid.Columns, retainedRows, grid.Rows,
                profile.ReceiveEncoding, profile.ReceiveSevenBitKana, profile.AmbiguousWidth, terminal.StateQuota);
        try { Start(initial); }
        catch
        {
            ReleaseReceiveOnlyState();
            throw;
        }
    }

    public void Start(SessionRecording recording)
    {
        if (part is not null) throw new InvalidOperationException("A recording interval is already active.");
        interval = checked(interval + 1);
        try
        {
            if (recordTerminal)
                terminal.BeginRecording(false, interval, value => Write(RecordingFormat.Terminal, value), MarkRefused);
            receiveOnly?.BeginRecording(true, interval, value => Write(RecordingFormat.Text, value), MarkRefused);
        }
        catch
        {
            EndBoth(abort: true);
            throw;
        }
        part = recording;
    }

    public void Receive(TerminalReceiveSource source, byte[] payload, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (raw) part?.Enqueue(RecordingFormat.Raw, payload);
        if (!terminal.TryReceive(source, payload, token))
        {
            RefuseActiveRecording();
            terminal.Receive(source, payload, token);
        }
        if (receiveOnly is null) return;
        // Once display has committed, the other projection must see the same input.
        if (!receiveOnly.TryReceive(source, payload))
        {
            RefuseActiveRecording();
            receiveOnly.Receive(source, payload);
        }
        receiveOnly.TakeReplies();
    }

    public void Echo(ReadOnlySpan<byte> payload, ResourceReservation? prepaid)
    {
        if (terminal.TryEcho(payload, prepaid)) return;
        RefuseActiveRecording();
        terminal.EchoUtf8(payload);
    }

    public void Resize(int columns, int rows)
    {
        using var displayResize = terminal.PrepareNativeResize(columns, rows);
        using var receiveResize = receiveOnly?.PrepareNativeResize(columns, rows);
        terminal.Resize(columns, rows, displayResize);
        receiveOnly?.Resize(columns, rows, receiveResize);
    }

    public void CompleteReceive(bool keepInterval = false)
    {
        try
        {
            Complete(terminal);
            if (receiveOnly is not null)
            {
                Complete(receiveOnly);
                if (keepInterval)
                {
                    try { receiveOnly.FlushText(); }
                    catch (ResourceLimitException) { RefuseActiveRecording(); }
                }
            }
        }
        finally
        {
            if (keepInterval) part?.FlushReceived();
            else
            {
                try { Cutoff(); }
                finally { ReleaseReceiveOnlyState(); }
            }
        }
    }

    private void Complete(TerminalOutput output)
    {
        if (output.TryCompleteReceive()) return;
        RefuseActiveRecording();
        output.CompleteReceive();
    }

    public void Cutoff()
    {
        if (part is null) return;
        interval = checked(interval + 1);
        try { EndBoth(abort: part.Failure is not null); }
        catch (ResourceLimitException error)
        {
            part.MarkIncomplete(error.Message);
            EndBoth(abort: true);
        }
        finally { part = null; }
    }

    public void Abort()
    {
        if (part is null) return;
        interval = checked(interval + 1);
        try { EndBoth(abort: true); }
        finally { part = null; }
    }

    private void EndBoth(bool abort)
    {
        try { receiveOnly?.EndRecording(interval, abort); }
        finally { terminal.EndRecording(interval, abort); }
    }

    public void ReleaseReceiveOnlyState() => receiveOnly?.ReleaseNativeState();

    private void Write(RecordingFormat format, byte[] value) => part?.Enqueue(format, value);

    private void MarkRefused() => part?.MarkIncomplete(new ResourceLimitException().Message);

    private void RefuseActiveRecording()
    {
        if (part is null) throw new ResourceLimitException();
        MarkRefused();
        Abort();
    }
}
