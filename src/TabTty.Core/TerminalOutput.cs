using System.Text;

namespace TabTty.Core;

public sealed class TerminalOutput
{
    private readonly NativeTerminalCore native;
    private readonly int retainedRowLimit;
    private int columns, rows;
    private readonly Queue<byte[]> replies = new();
    private uint recordingFlags;
    private Action<byte[]>? textRecord, terminalRecord;
    private bool released;

    internal ResourceQuota StateQuota { get; }
    internal bool UsesNativeCore => !released;
    internal bool ApplicationKeypad => native.ApplicationKeypad;
    internal bool ApplicationCursorKeys => native.ApplicationCursorKeys;
    internal bool BracketedPaste => native.BracketedPaste;
    internal TerminalMouseMode MouseMode => native.MouseMode;
    internal long AiLayoutEpoch => native.LayoutEpoch;
    internal int AiColumns => columns;
    internal int AiRows => rows;
    public event Action? Bell;

    internal Action<TerminalReceiveSource, Rune, bool>? MacroCharacter
    {
        set => native.ReceiveObserver = value;
    }

    internal Action? EnteringAlternateScreen
    {
        set => native.AlternateObserver = value;
    }

    public TerminalOutput(int columns = 80, int retainedRowLimit = 5000, int screenRows = 24,
        string receiveEncoding = "utf-8", bool receiveSevenBitKana = false, int ambiguousWidth = 1)
        : this(columns, retainedRowLimit, screenRows, receiveEncoding, receiveSevenBitKana, ambiguousWidth,
            new ResourceQuota(2L * 1024 * 1024 * 1024, ApplicationResources.TerminalState)) { }

    internal TerminalOutput(int columns, int retainedRowLimit, int screenRows, string receiveEncoding,
        bool receiveSevenBitKana, int ambiguousWidth, ResourceQuota stateQuota)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedRowLimit, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retainedRowLimit, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(screenRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(screenRows, Math.Min(512, retainedRowLimit));
        ArgumentOutOfRangeException.ThrowIfLessThan(ambiguousWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ambiguousWidth, 2);
        this.columns = columns;
        rows = screenRows;
        this.retainedRowLimit = retainedRowLimit;
        StateQuota = stateQuota;
        var codec = new TerminalEncoding(receiveEncoding);
        try
        {
            native = new NativeTerminalCore(columns, rows, retainedRowLimit, ambiguousWidth, stateQuota,
                codec.NativePage, receiveSevenBitKana);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("The native terminal engine is missing or incompatible with this application.", error);
        }
    }

    public void Receive(TerminalReceiveSource source, ReadOnlySpan<byte> payload) => Receive(source, payload, default);

    internal void Receive(TerminalReceiveSource source, ReadOnlySpan<byte> payload, CancellationToken token)
    {
        if (!TryReceive(source, payload, token)) throw new ResourceLimitException();
    }

    internal bool TryReceive(TerminalReceiveSource source, ReadOnlySpan<byte> payload, CancellationToken token = default)
    {
        if (source is not (TerminalReceiveSource.Data or TerminalReceiveSource.StandardError))
            throw new ArgumentOutOfRangeException(nameof(source));
        token.ThrowIfCancellationRequested();
        return native.TryFeed((uint)source, payload, recordingFlags, textRecord, terminalRecord,
            token, replies.Enqueue, replies.Count, Bell);
    }

    internal void EchoUtf8(ReadOnlySpan<byte> payload)
    {
        if (!TryEcho(payload)) throw new ResourceLimitException();
    }

    internal bool TryEcho(ReadOnlySpan<byte> payload) =>
        native.TryFeed(2, payload, recordingFlags, textRecord, terminalRecord, replySink: replies.Enqueue, pendingReplies: replies.Count, bell: Bell);

    public void CompleteReceive()
    {
        if (!TryCompleteReceive()) throw new ResourceLimitException();
    }

    internal bool TryCompleteReceive() => native.TryComplete(recordingFlags, textRecord, terminalRecord);

    public TerminalSnapshot GetRows() =>
        native.TrySnapshot(out var snapshot) ? snapshot! : throw new ResourceLimitException();

    internal AiTerminalState ReadAiScreenState() => native.ReadScreenState();
    internal (long Id, long Revision) ReadAiScreenRow(int row, int first, int last, Action<TerminalCell> visit) =>
        native.ReadScreenRow(row, first, last, visit);
    internal IHistoryRows GetHistoryRows(bool allowBuild = true) => native.ReadHistory(allowBuild);

    public void Resize(int width, int height)
    {
        using var plan = PrepareNativeResize(width, height);
        Resize(width, height, plan);
    }

    internal NativeTerminalCore.PreparedResize? PrepareNativeResize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int bounded = Math.Min(height, Math.Min(512, retainedRowLimit));
        return columns == width && rows == bounded ? null : native.PrepareResize(width, bounded);
    }

    internal void Resize(int width, int height, NativeTerminalCore.PreparedResize? plan)
    {
        if (plan is null) return;
        plan.Apply(textRecord);
        columns = width;
        rows = Math.Min(height, Math.Min(512, retainedRowLimit));
    }

    internal byte[] EncodeMouse(TerminalMouseInput input) => TerminalMouse.Encode(input, MouseMode, columns, rows);

    internal byte[] EncodeBroadcastMouse(TerminalMouseInput input)
    {
        TerminalMouseMode mouse = MouseMode;
        int width = mouse.SgrCoordinates ? columns : Math.Min(columns, 223);
        int height = mouse.SgrCoordinates ? rows : Math.Min(rows, 223);
        if (!input.Release && (input.Column < 0 || input.Column >= width || input.Row < 0 || input.Row >= height))
            return [];
        return TerminalMouse.Encode(input, mouse, width, height);
    }

    internal byte[][] TakeReplies()
    {
        byte[][] result = replies.ToArray();
        replies.Clear();
        return result;
    }

    internal void BeginRecording(bool text, int interval, Action<byte[]> publish, Action? rejected = null)
    {
        uint flags = text ? 2U : 1U;
        native.BeginRecording(flags, interval);
        recordingFlags = flags;
        native.RecordingRefused = rejected;
        if (text) textRecord = publish;
        else terminalRecord = publish;
    }

    internal void EndRecording(int interval, bool abort)
    {
        if (recordingFlags == 0) return;
        native.EndRecording(recordingFlags, interval, abort, abort ? null : textRecord);
        recordingFlags = 0;
        native.RecordingRefused = null;
        textRecord = terminalRecord = null;
    }

    internal void FlushText()
    {
        if (textRecord is not null && !native.TryFlushText(recordingFlags, textRecord, terminalRecord))
            throw new ResourceLimitException();
    }

    internal void ReleaseNativeState()
    {
        released = true;
        native.Dispose();
        replies.Clear();
        native.ReceiveObserver = null;
        native.AlternateObserver = null;
        native.RecordingRefused = null;
        textRecord = terminalRecord = null;
    }
}

public enum TerminalReceiveSource
{
    Data,
    StandardError,
}
