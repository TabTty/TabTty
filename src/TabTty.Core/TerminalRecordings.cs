using System.Text;

namespace TabTty.Core;

// The session reducer owns the projections and changes their admission interval.
internal sealed class TerminalRecordings
{
    private readonly TerminalOutput terminal;
    private readonly TerminalOutput? receiveOnly;
    private readonly bool raw;
    private readonly bool recordTerminal;
    private readonly StringBuilder text = new();
    private readonly StringBuilder operations = new();
    private SessionRecording? part;
    private int interval;
    private TerminalReceiveSource? alternateCr;
    private readonly RecordedTextPosition position;

    public TerminalRecordings(TerminalOutput terminal, ConnectionProfile profile, SessionRecording initial, int retainedRows,
        (int Columns, int Rows) grid)
    {
        this.terminal = terminal;
        terminal.NativeRecordingCallbacks = true;
        position = new RecordedTextPosition(profile.AmbiguousWidth);
        raw = profile.RecordRaw;
        recordTerminal = profile.RecordTerminal;
        if (profile.RecordTerminal)
        {
            terminal.DisplayOperation = (value, epoch, _) =>
                {
                    if (part is not null && epoch == interval)
                        Append(RecordingFormat.Terminal, operations, value);
                };
        }
        if (profile.RecordText)
        {
            receiveOnly = new TerminalOutput(grid.Columns, retainedRows, grid.Rows,
                profile.ReceiveEncoding, profile.ReceiveSevenBitKana, profile.AmbiguousWidth, terminal.StateQuota)
            {
                NativeRecordingCallbacks = true,
                ImportRecordingPosition = position.Import,
                RecordingColumn = () => position.Column,
                DisplayOperation = ApplyAlternate,
                LeavingAlternateScreen = FlushAlternateCr,
                SourceCharacter = (character, epoch, source) =>
                {
                    if (alternateCr is { } previous &&
                        (character.Value != '\n' || epoch != interval || source != previous))
                        FlushAlternateCr();
                },
            };
        }

        Start(initial);
    }

    public void Start(SessionRecording recording)
    {
        terminal.PrepareRecordingChange();
        receiveOnly?.PrepareRecordingChange();
        part = recording;
        if (recordTerminal) terminal.NativeDisplayRecord = value => EnqueueNative(RecordingFormat.Terminal, value);
        position.Reset();
        AdvanceInterval();
        if (receiveOnly is null) return;
        receiveOnly.ExcludeExistingText();
        receiveOnly.NativeTextRecord = value => EnqueueNative(RecordingFormat.Text, value);
        receiveOnly.TextRecord = value => Append(RecordingFormat.Text, text, value);
    }

    public void Receive(TerminalReceiveSource source, byte[] payload, CancellationToken token = default)
    {
        if (raw) part?.Enqueue(RecordingFormat.Raw, payload);
        terminal.Receive(source, payload, token);
        receiveOnly?.Receive(source, payload);
        receiveOnly?.TakeReplies();
        FlushBuffers();
    }

    public void Echo(ReadOnlySpan<byte> payload)
    {
        terminal.EchoUtf8(payload);
        FlushBuffers();
    }

    public void Resize(int columns, int rows)
    {
        terminal.Resize(columns, rows);
        receiveOnly?.Resize(columns, rows);
        FlushBuffers();
    }

    public void CompleteReceive()
    {
        try
        {
            terminal.CompleteReceive();
            receiveOnly?.CompleteReceive();
        }
        finally
        {
            try { Cutoff(); }
            finally { receiveOnly?.ReleaseNativeState(); }
        }
    }

    public void Cutoff()
    {
        if (part is null) return;
        receiveOnly?.FlushText();
        FlushAlternateCr();
        FlushBuffers();
        if (receiveOnly is not null)
        {
            receiveOnly.TextRecord = null;
            receiveOnly.NativeTextRecord = null;
        }
        terminal.NativeDisplayRecord = null;
        part = null;
        AdvanceInterval();
    }

    public void Abort()
    {
        if (receiveOnly is not null)
        {
            receiveOnly.TextRecord = null;
            receiveOnly.NativeTextRecord = null;
        }
        terminal.NativeDisplayRecord = null;
        part = null;
        alternateCr = null;
        text.Clear();
        operations.Clear();
        receiveOnly?.ReleaseNativeState();
    }

    private void AdvanceInterval()
    {
        interval = checked(interval + 1);
        terminal.RecordingInterval = interval;
        if (receiveOnly is not null) receiveOnly.RecordingInterval = interval;
    }

    private void ApplyAlternate(string operation, int epoch, TerminalReceiveSource source)
    {
        if (!receiveOnly!.IsAlternateScreen || part is null) return;
        if (alternateCr is { } previous)
        {
            alternateCr = null;
            if (operation == "\n" && previous == source && epoch == interval)
            {
                Append(RecordingFormat.Text, text, "\r\n");
                return;
            }
            Append(RecordingFormat.Text, text, "\n");
        }
        if (epoch != interval) return;
        if (operation == "\r") alternateCr = source;
        else if (operation is "\n" or "\t" || operation[0] >= ' ')
            Append(RecordingFormat.Text, text, operation);
    }

    private void FlushAlternateCr()
    {
        if (alternateCr is null) return;
        alternateCr = null;
        Append(RecordingFormat.Text, text, "\n");
    }

    private void EnqueueNative(RecordingFormat format, byte[] value)
    {
        // Native output is already UTF-8; the asynchronous writer owns this array.
        if (part is { Failure: null } recording) recording.Enqueue(format, value);
    }

    private void Append(RecordingFormat format, StringBuilder buffer, string value)
    {
        if (part?.Failure is not null) return;
        if (format == RecordingFormat.Text) position.Append(value);
        buffer.Append(value);
        if (buffer.Length >= 4096) Flush(format, buffer);
    }

    private void FlushBuffers()
    {
        Flush(RecordingFormat.Text, text);
        Flush(RecordingFormat.Terminal, operations);
    }

    private void Flush(RecordingFormat format, StringBuilder buffer)
    {
        if (buffer.Length == 0) return;
        if (part is { } recording)
        {
            if (buffer.Length <= 4096)
            {
                Span<char> characters = stackalloc char[buffer.Length];
                // Copy contiguous UTF-16 so surrogate pairs may cross StringBuilder chunks.
                buffer.CopyTo(0, characters, characters.Length);
                byte[] payload = new byte[Encoding.UTF8.GetByteCount(characters)];
                Encoding.UTF8.GetBytes(characters, payload);
                // The asynchronous writer owns this exact-sized array until it completes.
                recording.Enqueue(format, payload);
            }
            else
                recording.Enqueue(format, Encoding.UTF8.GetBytes(buffer.ToString()));
        }
        buffer.Clear();
    }
}
