using System.Text;
using System.Runtime.InteropServices;
using System.Buffers;

namespace TabTty.Core;

public sealed class TerminalOutput
{
    // Native decoding/editing owns the common UTF-8 path. Unsupported operations
    // transfer its complete state once to the managed implementation below.
    private NativeTerminalCore? nativeCore;
    internal bool UsesNativeCore => nativeCore is not null;
    internal bool NativeRecordingCallbacks { get; set; }
    internal delegate void RecordingPositionImport(int column, bool known, ReadOnlySpan<uint> cluster);
    internal RecordingPositionImport? ImportRecordingPosition { get; set; }
    internal Action<TerminalReceiveSource?>? ImportAlternateCr { get; set; }
    internal Action<byte[]>? NativeTextRecord { get; set; }
    internal Action<byte[]>? NativeDisplayRecord { get; set; }
    private uint NativeRecordingFlags => (NativeDisplayRecord is not null ? 1U : 0U) |
        (NativeTextRecord is not null ? 2U : 0U);
    internal void ReleaseNativeState()
    {
        nativeCore?.Dispose();
        nativeCore = null;
        data.Legacy?.Dispose();
        standardError.Legacy?.Dispose();
    }
    internal void BeginRecording(bool text)
    {
        if (nativeCore is not null) nativeCore.BeginRecording(text ? 2U : 1U, RecordingInterval);
        else if (text) ExcludeExistingText();
    }
    internal void EndRecording(bool text, bool abort)
    {
        if (nativeCore is not null)
            nativeCore.EndRecording(text ? 2U : 1U, RecordingInterval, abort, abort ? null : NativeTextRecord);
        else if (text && !abort) FlushText();
    }
    private bool HasManagedOnlyObservers => MacroCharacter is not null ||
        (!NativeRecordingCallbacks && (DisplayOperation is not null || TextRecord is not null ||
            SourceCharacter is not null || RecordingColumn is not null));
    private bool TryNativeFeed(uint source, ReadOnlySpan<byte> payload, CancellationToken token = default)
    {
        if (nativeCore is null) return false;
        if (HasManagedOnlyObservers || receiveCompleted) { EnsureManaged(); return false; }
        if (nativeCore.TryFeed(source, payload, NativeRecordingFlags, NativeTextRecord, NativeDisplayRecord,
            token, EnqueueReply, replies.Count, Bell))
            return true;
        token.ThrowIfCancellationRequested();
        EnsureManaged();
        return false;
    }

    private void EnsureManaged()
    {
        if (nativeCore is not { } native) return;
        native.Export(ImportNativeState);
        aiLayoutEpoch = native.LayoutEpoch;
        nativeCore = null;
        native.Dispose();
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.ManagedHandoffs);
    }

    private unsafe void ImportNativeState(NativeTerminalCore.ExportState state)
    {
        if (state.Editing is null || state.SourceCount != 3 || state.RowCount is 0 ||
            state.RowCount > retainedRowLimit + 512 || state.Reserved0 < state.RowCount || state.Reserved0 > state.RowCount + 512 ||
            state.Snapshot.ScreenStart < 0 || state.Snapshot.ScreenStart >= state.RowCount)
            throw new InvalidDataException("The native terminal exported an invalid state.");
        var imported = new List<TerminalRow>(checked((int)state.Reserved0));
        bool committed = false;
        try
        {
            var nextHistory = new LinkedList<TerminalRow>();
            var nextScreen = new List<TerminalRow>(checked((int)state.RowCount - state.Snapshot.ScreenStart));
            for (int index = 0; index < state.Reserved0; index++)
            {
                NativeTerminalCore.ExportRow row = state.Rows[index];
                NativeTerminalCore.RowView view = row.View;
                if (view.CellCount > 1024 || view.CellTextLength > 1024 * 1024 || row.TabCount > 1024 ||
                    row.FormatCount > 1025 * 256 || row.RecordedCount > 1024 ||
                    row.SubmittedLength != uint.MaxValue && row.SubmittedLength > 1024 * 512)
                    throw new InvalidDataException("The native terminal exported invalid row bounds.");
                string? submitted = row.SubmittedLength == uint.MaxValue ? null :
                    new string(row.Submitted, 0, checked((int)row.SubmittedLength));
                var managed = TerminalRow.ImportNative(view.Id, view.LogicalId, view.LogicalOffset,
                    view.DisplayRevision, row.ContentRevision, row.SubmittedRevision, view.SoftWrap != 0,
                    new ReadOnlySpan<NativeTerminalCore.CellView>(view.Cells, checked((int)view.CellCount)),
                    new ReadOnlySpan<char>(view.CellText, checked((int)view.CellTextLength)),
                    new ReadOnlySpan<NativeTerminalCore.TabView>(row.Tabs, checked((int)row.TabCount)),
                    submitted, StateQuota);
                imported.Add(managed);
                managed.ExcludeFromRecording = row.Excluded != 0;
                foreach (var format in new ReadOnlySpan<NativeTerminalCore.FormatView>(row.Formats, checked((int)row.FormatCount)))
                {
                    if (format.Column > 1024 || format.Scalar != 0xFEFF)
                        throw new InvalidDataException("The native terminal exported invalid format text.");
                    managed.AddFormat(checked((int)format.Column), new Rune(0xFEFF));
                }
                if (row.RecordedCount != 0)
                {
                    var recorded = new List<(int Start, int End)>((int)row.RecordedCount);
                    foreach (var span in new ReadOnlySpan<NativeTerminalCore.TabView>(row.Recorded, (int)row.RecordedCount))
                        recorded.Add((checked((int)span.Start), checked((int)span.End)));
                    managed.RecordedColumns = recorded;
                }
                managed.LogicalClusterOffset = row.LogicalClusterOffset;
                if (index < state.Snapshot.ScreenStart) nextHistory.AddLast(managed);
                else if (index < state.RowCount) nextScreen.Add(managed);
            }
            var editing = state.Editing;
            var nextParked = ReadPage(editing->Parked);
            var nextAlternate = ReadPage(editing->Alternate);
            if (nextParked is { } normal && nextHistory.Count + normal.Rows.Count > retainedRowLimit)
                throw new InvalidDataException("The parked screen exceeds its retained-row limit.");
            SourceState nextData = ImportSource(state.Sources[0]);
            SourceState nextError = ImportSource(state.Sources[1]);
            SourceState nextEcho = ImportSource(state.Sources[2]);
            List<Rune> nextCluster = ImportScalars(state.Cluster, state.ClusterCount);
            TerminalRow? nextClusterRow = state.ClusterRow == 0 ? null :
                nextScreen.FirstOrDefault(row => row.Id == state.ClusterRow) ??
                    throw new InvalidDataException("The native terminal exported an invalid cluster row.");
            if (state.CursorRow >= state.Snapshot.ScreenRows || state.ClusterCount > 256 || state.RecordedCount > 256)
                throw new InvalidDataException("The native terminal exported invalid cursor or cluster bounds.");
            ImportRecordingPosition?.Invoke(checked((int)state.RecordedColumn), state.RecordedKnown != 0,
                new ReadOnlySpan<uint>(state.RecordedCluster, checked((int)state.RecordedCount)));
            ImportAlternateCr?.Invoke(state.Reserved1 switch
            {
                1 => TerminalReceiveSource.Data, 2 => TerminalReceiveSource.StandardError, _ => null,
            });

            foreach (TerminalRow row in screen) row.ReleaseOwnedState();
            foreach (TerminalRow row in scrollback) row.ReleaseOwnedState();
            ClearHistoryIndex();
            scrollback = nextHistory;
            screen = nextScreen;
            parkedScreen = nextParked;
            alternatePage = nextAlternate;
            data = nextData;
            standardError = nextError;
            localEcho = nextEcho;
            cluster = nextCluster;
            clusterRow = nextClusterRow;
            clusterColumn = checked((int)state.ClusterColumn);
            clusterWidth = checked((int)state.ClusterWidth);
            cursorRow = checked((int)state.CursorRow);
            column = state.Snapshot.CursorColumn;
            columns = state.Snapshot.Columns;
            screenRows = state.Snapshot.ScreenRows;
            marginTop = checked((int)editing->MarginTop);
            marginBottom = checked((int)editing->MarginBottom);
            savedPosition = editing->PositionRow < 0 ? null : (editing->PositionRow, checked((int)editing->PositionColumn));
            savedCursor = ReadCursor(editing->SavedRow, editing->SavedColumn, editing->SavedForeground,
                editing->SavedBackground, editing->SavedStyle, editing->SavedFlags);
            var restore = editing->NormalReturn;
            normalReturnState = restore.Active == 0 ? null :
                ReadCursor(restore.Row, restore.Column, restore.Foreground, restore.Background, restore.Style, restore.Flags);
            tabStops.Clear();
            for (int stop = 0; stop < 1024; stop++)
                if ((editing->TabStops[stop / 64] & (1UL << (stop % 64))) != 0) tabStops.Add(stop);
            autoWrap = (state.ModeFlags & 1) != 0;
            lineFeedReturns = (state.ModeFlags & 2) != 0;
            wrapPending = (state.ModeFlags & 4) != 0;
            receiveCompleted = (state.ModeFlags & 8) != 0;
            BracketedPaste = (state.ModeFlags & 16) != 0;
            ApplicationCursorKeys = (state.ModeFlags & 32) != 0;
            ApplicationKeypad = (state.ModeFlags & 64) != 0;
            originMode = (state.ModeFlags & 128) != 0;
            insertMode = (state.ModeFlags & 256) != 0;
            style = new(NativeTerminalCore.Color(state.StyleForeground), NativeTerminalCore.Color(state.StyleBackground),
                (TerminalStyleFlags)state.StyleFlags);
            clusterStyle = new(NativeTerminalCore.Color(state.ClusterForeground), NativeTerminalCore.Color(state.ClusterBackground),
                (TerminalStyleFlags)state.ClusterFlags);
            cursorVisible = (state.Snapshot.Flags & 1) != 0;
            cursorBlink = (state.Snapshot.Flags & 2) != 0;
            cursorShape = (TerminalCursorShape)state.Snapshot.CursorShape;
            mouse = NativeTerminalCore.DecodeMouseMode(state.Snapshot.MouseTracking, state.Snapshot.MouseGeneration);
            committed = true;
        }
        finally
        {
            if (!committed) foreach (TerminalRow row in imported) row.ReleaseOwnedState();
        }

        (List<TerminalRow> Rows, int CursorRow, int Column, bool WrapPending, int Width, int Height)?
            ReadPage(NativeTerminalCore.PageView page)
        {
            if (page.Count == 0) return null;
            if (page.First < state.RowCount || page.First > state.Reserved0 ||
                page.Count > state.Reserved0 - page.First || page.Rows is 0 or > 512 ||
                page.Columns is < 2 or > 1024 || page.Row >= page.Rows || page.Column >= page.Columns)
                throw new InvalidDataException("The native terminal exported an invalid parked page.");
            return (imported.GetRange((int)page.First, (int)page.Count), (int)page.Row, (int)page.Column,
                page.Wrap != 0, (int)page.Columns, (int)page.Rows);
        }

        static CursorState ReadCursor(uint row, uint column, uint foreground, uint background, uint style, uint flags) =>
            new(checked((int)row), checked((int)column),
                new(NativeTerminalCore.Color(foreground), NativeTerminalCore.Color(background), (TerminalStyleFlags)style),
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0 ? '0' : 'B',
                (flags & 16) != 0 ? '0' : 'B', (flags & 32) != 0);
    }

    private static unsafe SourceState ImportSource(NativeTerminalCore.SourceView source)
    {
        if (source.PendingCount > 4 || source.ParameterCount > 4096 || source.LastGraphicCount > 256 || source.Reserved > 1024 * 1024)
            throw new InvalidDataException("The native terminal exported invalid decoder state.");
        var state = new SourceState
        {
            PendingCount = checked((int)source.PendingCount), PendingInterval = checked((int)source.PendingInterval),
            EscapeInterval = checked((int)source.EscapeInterval), PreviousInterval = checked((int)source.PreviousInterval),
            IsAtStart = (source.Flags & 1) == 0, PreviousCr = (source.Flags & 2) != 0,
            LastWasGraphic = (source.Flags & 4) != 0,
            Osc = (source.Flags & 8) != 0, StringBytes = checked((int)source.Reserved),
            G0 = (source.Flags & 16) != 0 ? '0' : 'B',
            G1 = (source.Flags & 32) != 0 ? '0' : 'B',
            UseG1 = (source.Flags & 64) != 0,
            Escape = source.Escape switch
            {
                0 => EscapeState.Ground, 1 => EscapeState.Escape, 2 => EscapeState.Csi,
                3 => EscapeState.ControlString, 4 => EscapeState.ControlStringEscape,
                5 => EscapeState.EscapeIntermediate,
                _ => throw new InvalidDataException("The native terminal exported an invalid parser state."),
            },
        };
        new ReadOnlySpan<byte>(source.Pending, state.PendingCount).CopyTo(state.Pending);
        state.Parameters.Append(Encoding.ASCII.GetString(new ReadOnlySpan<byte>(source.Parameters, checked((int)source.ParameterCount))));
        state.LastGraphicRunes.AddRange(ImportScalars(source.LastGraphic, source.LastGraphicCount));
        if (state.LastGraphicRunes.Count != 0)
            state.LastGraphic = TerminalText.Cluster(CollectionsMarshal.AsSpan(state.LastGraphicRunes));
        return state;
    }

    private static unsafe List<Rune> ImportScalars(uint* source, uint count)
    {
        if (count > 256) throw new InvalidDataException("The native terminal exported an oversized cluster.");
        var result = new List<Rune>(checked((int)count));
        foreach (uint scalar in new ReadOnlySpan<uint>(source, checked((int)count))) result.Add(new Rune(checked((int)scalar)));
        return result;
    }

    private SourceState data = new();
    private SourceState standardError = new();
    private SourceState localEcho = new() { IsAtStart = false };
    private LinkedList<TerminalRow> scrollback = new();
    private RetainedHistoryRows? historyRows;

    internal RetainedHistoryRows GetHistoryRows(bool allowBuild = true, bool trackClusters = false)
    {
        EnsureManaged();
        if (historyRows is null && !allowBuild) throw new TerminalHistory.HistoryException("busy");
        historyRows ??= new(retainedRowLimit, scrollback);
        historyRows.Prepare(parkedScreen?.Rows ?? screen, allowBuild, trackClusters);
        return historyRows;
    }

    private void ClearHistoryIndex()
    {
        historyRows?.Dispose();
        historyRows = null;
    }
    private readonly Stack<TerminalRow> spareRows = new(2);
    private List<TerminalRow> screen;
    internal ResourceQuota StateQuota { get; }
    private readonly ResourceReservation parserMemory;
    private (List<TerminalRow> Rows, int CursorRow, int Column, bool WrapPending, int Width, int Height)? parkedScreen;
    private (List<TerminalRow> Rows, int CursorRow, int Column, bool WrapPending, int Width, int Height)? alternatePage;
    private CursorState? normalReturnState;
    private int columns;
    private readonly int retainedRowLimit;
    private int screenRows;
    private readonly int ambiguousWidth;
    private int cursorRow;
    private int column;
    private bool wrapPending;
    private bool receiveCompleted;
    private List<Rune> cluster = [];
    private TerminalRow? clusterRow;
    private int clusterColumn;
    private int clusterWidth;
    private bool autoWrap = true;
    private bool lineFeedReturns;
    private bool insertMode;
    private bool originMode;
    private int marginTop;
    private int marginBottom;
    private readonly HashSet<int> tabStops = Enumerable.Range(1, 127).Select(index => index * 8).ToHashSet();
    private (int Row, int Column)? savedPosition;
    private CursorState savedCursor;
    private readonly Queue<byte[]> replies = new();
    private int replyBytes;
    private bool applicationKeypad;
    internal bool ApplicationKeypad { get => nativeCore?.ApplicationKeypad ?? applicationKeypad; private set => applicationKeypad = value; }
    private TerminalStyle style;
    private TerminalStyle clusterStyle;
    private TerminalStyle EraseStyle => style with { Flags = style.Flags & TerminalStyleFlags.Inverse };
    private bool cursorVisible = true;
    private bool cursorBlink = true;
    private TerminalCursorShape cursorShape;
    private TerminalMouseMode mouse;
    private bool applicationCursorKeys;
    internal bool ApplicationCursorKeys { get => nativeCore?.ApplicationCursorKeys ?? applicationCursorKeys; private set => applicationCursorKeys = value; }
    private bool bracketedPaste;
    internal bool BracketedPaste { get => nativeCore?.BracketedPaste ?? bracketedPaste; private set => bracketedPaste = value; }
    private int recordingInterval;
    internal int RecordingInterval
    {
        get => recordingInterval;
        set => recordingInterval = value;
    }
    internal Action<string, int, TerminalReceiveSource>? DisplayOperation { get; set; }
    internal Action<string>? TextRecord { get; set; }
    internal Func<int?>? RecordingColumn { get; set; }
    internal Action? LeavingAlternateScreen { get; set; }
    internal Action<Rune, int, TerminalReceiveSource>? SourceCharacter { get; set; }
    internal Action<TerminalReceiveSource, Rune, bool>? MacroCharacter { get; set; }
    internal Action? EnteringAlternateScreen { get; set; }
    internal bool IsAlternateScreen => nativeCore?.IsAlternateScreen ?? parkedScreen is not null;
    private long aiLayoutEpoch;
    internal long AiLayoutEpoch { get => nativeCore?.LayoutEpoch ?? aiLayoutEpoch; private set => aiLayoutEpoch = value; }
    internal int AiColumns => columns;
    internal int AiRows => screenRows;
    internal IEnumerable<TerminalRow> AiNormalRows { get { EnsureManaged(); return scrollback.Concat(parkedScreen?.Rows ?? screen); } }

    internal AiTerminalState ReadAiScreenState()
    {
        if (nativeCore is { } native) return native.ReadScreenState();
        var hash = new HashCode();
        hash.Add(cursorRow);
        hash.Add(column);
        hash.Add(cursorVisible);
        hash.Add(cursorShape);
        hash.Add(ApplicationKeypad);
        hash.Add(ApplicationCursorKeys);
        hash.Add(BracketedPaste);
        hash.Add(MouseMode);
        hash.Add(AiLayoutEpoch);
        foreach (var line in screen)
        {
            hash.Add(line.Id);
            hash.Add(line.DisplayRevision);
        }
        return new AiTerminalState(AiLayoutEpoch, hash.ToHashCode(), columns, screenRows, cursorRow, column,
            cursorVisible, cursorShape, IsAlternateScreen, ApplicationCursorKeys, ApplicationKeypad, BracketedPaste, MouseMode);
    }

    internal (long Id, long Revision) ReadAiScreenRow(int row, int first, int last, Action<TerminalCell> visit)
    {
        if (nativeCore is { } native) return native.ReadScreenRow(row, first, last, visit);
        if (row >= screen.Count) return default;
        var line = screen[row];
        foreach (var cell in line.GetAiCells(first, last, columns).Span) visit(cell);
        return (line.Id, line.DisplayRevision);
    }
    internal void NormalizeAiHistoryIdentity(bool includeClusterOffsets = false) =>
        NormalizeAnchors(AiNormalRows, trackClusters: includeClusterOffsets);
    public event Action? Bell;

    internal byte[][] TakeReplies()
    {
        byte[][] result = replies.ToArray();
        replies.Clear();
        replyBytes = 0;
        return result;
    }

    public TerminalOutput(int columns = 80, int retainedRowLimit = 5000, int screenRows = 24,
        string receiveEncoding = "utf-8", bool receiveSevenBitKana = false, int ambiguousWidth = 1)
        : this(columns, retainedRowLimit, screenRows, receiveEncoding, receiveSevenBitKana, ambiguousWidth,
            new ResourceQuota(2L * 1024 * 1024 * 1024, ApplicationResources.TerminalState)) { }

    internal TerminalOutput(int columns, int retainedRowLimit, int screenRows, string receiveEncoding,
        bool receiveSevenBitKana, int ambiguousWidth, ResourceQuota stateQuota)
    {
        StateQuota = stateQuota;
        parserMemory = stateQuota.Reserve(128 * 1024);
        screen = [new(budget: StateQuota)];
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedRowLimit, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retainedRowLimit, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(screenRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(screenRows, Math.Min(512, retainedRowLimit));
        this.columns = columns;
        this.retainedRowLimit = retainedRowLimit;
        this.screenRows = screenRows;
        marginBottom = screenRows - 1;
        savedCursor = new CursorState(0, 0, default, false, true, false);
        ArgumentOutOfRangeException.ThrowIfLessThan(ambiguousWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ambiguousWidth, 2);
        this.ambiguousWidth = ambiguousWidth;
        if (receiveEncoding != "utf-8")
        {
            var codec = new TerminalEncoding(receiveEncoding);
            data.Legacy = new LegacyReceiver(codec, receiveSevenBitKana);
            standardError.Legacy = new LegacyReceiver(codec, receiveSevenBitKana);
        }
        if (NativeTerminalCore.Enabled)
        {
            if (receiveEncoding == "utf-8")
            {
                try { nativeCore = new NativeTerminalCore(columns, screenRows, retainedRowLimit, ambiguousWidth, StateQuota); }
                catch (ResourceLimitException)
                {
                    // Native initial capacities can exceed the managed engine's empty
                    // state. Preserve the managed admission contract under a tight quota.
                    Interlocked.Increment(ref NativeTerminalCoreDiagnostics.ManagedFallbackModels);
                }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    throw new InvalidOperationException("The native terminal engine is missing or incompatible with this application.", error);
                }
            }
            else Interlocked.Increment(ref NativeTerminalCoreDiagnostics.ManagedFallbackModels);
        }
    }

    public void Receive(TerminalReceiveSource source, ReadOnlySpan<byte> payload) => Receive(source, payload, default);

    internal void Receive(TerminalReceiveSource source, ReadOnlySpan<byte> payload, CancellationToken token)
    {
        if (receiveCompleted)
        {
            throw new InvalidOperationException("Terminal reception has ended.");
        }

        if (source is not (TerminalReceiveSource.Data or TerminalReceiveSource.StandardError))
            throw new ArgumentOutOfRangeException(nameof(source));
        token.ThrowIfCancellationRequested();
        if (TryNativeFeed(source == TerminalReceiveSource.Data ? 0U : 1U, payload, token)) return;

        SourceState state = source switch
        {
            TerminalReceiveSource.Data => data,
            TerminalReceiveSource.StandardError => standardError,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        Decode(state, payload, flush: false);
    }

    public TerminalSnapshot GetRows()
    {
        if (nativeCore is not null)
        {
            if (nativeCore.TrySnapshot(out TerminalSnapshot? snapshot)) return snapshot!;
            EnsureManaged();
        }
        NormalizeAnchors();
        return new(
        (IsAlternateScreen || scrollback.Count == 0) && screen.Count == 1 && screen[0].IsEmpty
            ? []
            : VisibleRows().Select(static row => row.GetSnapshot()).ToArray(), IsAlternateScreen,
        (IsAlternateScreen ? 0 : scrollback.Count) + cursorRow, column, columns, screenRows,
        IsAlternateScreen ? 0 : scrollback.Count, cursorVisible, cursorShape, cursorBlink, mouse, StateQuota);
    }

    private void NormalizeAnchors() => NormalizeAnchors(VisibleRows());

    private static void NormalizeAnchors(IEnumerable<TerminalRow> rows, bool trackClusters = false)
    {
        TerminalRow? previous = null;
        foreach (TerminalRow row in rows)
        {
            if (previous is not null)
            {
                row.LogicalId = previous.SoftWrapToNext ? previous.LogicalId : row.Id;
                row.LogicalOffset = previous.SoftWrapToNext ? previous.LogicalOffset + previous.GetText().Length : 0;
                if (trackClusters) row.LogicalClusterOffset = previous.SoftWrapToNext
                    ? previous.LogicalClusterOffset + previous.TextClusterCount() : 0;
            }
            previous = row;
        }
    }

    internal byte[] EncodeMouse(TerminalMouseInput input) => TerminalMouse.Encode(input, MouseMode, columns, screenRows);
    internal TerminalMouseMode MouseMode => nativeCore?.MouseMode ?? mouse;
    internal byte[] EncodeBroadcastMouse(TerminalMouseInput input)
    {
        TerminalMouseMode mouse = MouseMode;
        int width = mouse.SgrCoordinates ? columns : Math.Min(columns, 223);
        int height = mouse.SgrCoordinates ? screenRows : Math.Min(screenRows, 223);
        if (!input.Release && (input.Column < 0 || input.Column >= width || input.Row < 0 || input.Row >= height))
            return [];
        return TerminalMouse.Encode(input, mouse, width, height);
    }

    private IEnumerable<TerminalRow> VisibleRows() => IsAlternateScreen ? screen : scrollback.Concat(screen);

    private TerminalRow NewRow() => spareRows.TryPop(out TerminalRow? spare) ? spare : new(budget: StateQuota);

    private void ExcludeExistingText()
    {
        foreach (TerminalRow row in scrollback.Concat(parkedScreen?.Rows ?? screen))
            row.ExcludeFromRecording = !row.IsEmpty;
    }

    internal void FlushText()
    {
        if (nativeCore is not null && HasManagedOnlyObservers) EnsureManaged();
        if (nativeCore is not null)
        {
            if (NativeTextRecord is null) return;
            if (nativeCore.TryFlushText(NativeRecordingFlags, NativeTextRecord, NativeDisplayRecord))
            {
                return;
            }
            EnsureManaged();
        }
        if (TextRecord is null || IsAlternateScreen) return;
        foreach (TerminalRow row in VisibleRows()) SubmitText(row);
    }

    private bool SubmitText(TerminalRow row, bool boundary = false)
    {
        if (row.ExcludeFromRecording) return false;
        string revision = row.GetText(preserveTabs: true, serializedColumn: null);
        if (revision == row.SubmittedText) return false;
        int? serializedColumn = RecordingColumn?.Invoke();
        string pending = row.RecordedColumns is null && !row.HasTabs ? revision :
            row.GetText(preserveTabs: true, pendingOnly: row.RecordedColumns is not null, serializedColumn: serializedColumn);
        if (pending.Length != 0) TextRecord!(pending);
        if (revision.Length != 0 || boundary) row.SubmittedText = revision;
        row.RecordedColumns = null;
        return true;
    }

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
        height = Math.Min(height, Math.Min(512, retainedRowLimit));
        return columns == width && screenRows == height ? null : nativeCore?.PrepareResize(width, height);
    }

    internal void Resize(int width, int height, NativeTerminalCore.PreparedResize? plan)
    {
        height = Math.Min(height, Math.Min(512, retainedRowLimit));
        if (columns == width && screenRows == height) return;
        if (nativeCore is not null)
        {
            if (plan is null) throw new InvalidOperationException("The native terminal resize was not prepared.");
            plan.Apply(NativeTextRecord);
            columns = width;
            screenRows = height;
            marginTop = 0;
            marginBottom = height - 1;
            return;
        }
        AiLayoutEpoch++;
        if (IsAlternateScreen)
        {
            columns = width;
            screenRows = height;
            marginTop = 0;
            marginBottom = height - 1;
            if (screen.Count > height)
            {
                for (int index = height; index < screen.Count; index++) screen[index].ReleaseOwnedState();
                screen.RemoveRange(height, screen.Count - height);
            }
            foreach (TerminalRow row in screen) row.Clear(width);
            cursorRow = Math.Min(cursorRow, height - 1);
            column = Math.Min(column, width - 1);
            wrapPending = false;
            if (clusterRow is not null && (!screen.Contains(clusterRow) || clusterColumn + clusterWidth > width))
                ForgetCluster();
            return;
        }

        EnsureCursorRowExists();
        int oldCursor = scrollback.Count + cursorRow;
        if (columns == width)
        {
            var rows = VisibleRows().ToArray();
            UseRows(rows, oldCursor, height);
            screenRows = height;
            marginTop = 0;
            marginBottom = height - 1;
            return;
        }

        NormalizeAnchors(AiNormalRows, trackClusters: true);
        TerminalRow[] prior = VisibleRows().ToArray();
        var retained = new Queue<TerminalRow>();
        int originalColumn = wrapPending ? columns : column;
        int resizedCursorRow = 0;
        int resizedCursorColumn = 0;
        int retired = 0;
        int placed = 0;
        TerminalRow? relocatedCluster = null;
        int relocatedColumn = 0;
        for (int first = 0; first < prior.Length;)
        {
            int last = first;
            bool excluded = prior[first].ExcludeFromRecording;
            while (last + 1 < prior.Length && prior[last].SoftWrapToNext)
                excluded |= prior[++last].ExcludeFromRecording;
            long logicalId = prior[first].LogicalId;
            long logicalOffset = prior[first].LogicalOffset;
            long clusterOffset = prior[first].LogicalClusterOffset;
            var target = new TerminalRow(prior[first].Id, StateQuota)
                { ExcludeFromRecording = excluded, LogicalId = logicalId, LogicalOffset = logicalOffset,
                    LogicalClusterOffset = clusterOffset };
            var recorded = new List<(int Start, int End)>();
            int next = 0;
            bool containsUnrecorded = false;
            bool boundarySubmitted = prior[last].SubmittedText == "";
            for (int index = first; index <= last; index++)
            {
                TerminalRow source = prior[index];
                string text = source.GetText(preserveTabs: true, serializedColumn: null);
                bool unchanged = source.SubmittedText == text;
                ReadOnlySpan<TerminalCell> elements = source.GetCells().Span;
                var formats = source.Formats.ToDictionary();
                var tabs = source.Tabs.ToDictionary();
                var relocatedTabs = new List<(int Start, int End)>();
                int sourceEnd = 0;
                bool mappedCursor = false;
                foreach (TerminalCell cell in elements)
                {
                    if (next + cell.Width > width) Wrap();
                    if (index == oldCursor && !mappedCursor && originalColumn < cell.Column + cell.Width)
                    {
                        resizedCursorRow = placed;
                        resizedCursorColumn = next + Math.Max(0, originalColumn - cell.Column);
                        mappedCursor = true;
                    }
                    if (formats.Remove(cell.Column, out string? format))
                    {
                        long revision = Math.Max(target.FormatRevision, source.FormatRevision);
                        foreach (Rune rune in format.EnumerateRunes()) target.AddFormat(next, rune);
                        target.PreserveFormatRevision(revision);
                    }
                    if (tabs.TryGetValue(cell.Column, out int end) && next + end - cell.Column <= width)
                        relocatedTabs.Add((next, next + end - cell.Column));
                    if (cell.IsErased) target.Erase(next, next + cell.Width, cell.Style);
                    else target.Write(next, cell.Text, cell.Width, cell.Style);
                    target.PreserveRevision(next, source.RevisionAt(cell.Column));
                    if (unchanged || source.IsColumnRecorded(cell.Column)) AddRecorded(next, next + cell.Width);
                    else containsUnrecorded = true;
                    if (ReferenceEquals(source, clusterRow) && cell.Column == clusterColumn)
                    {
                        relocatedCluster = target;
                        relocatedColumn = next;
                    }
                    next += cell.Width;
                    sourceEnd = cell.Column + cell.Width;
                }
                foreach (var (_, format) in formats.OrderBy(pair => pair.Key))
                {
                    long revision = Math.Max(target.FormatRevision, source.FormatRevision);
                    foreach (Rune rune in format.EnumerateRunes()) target.AddFormat(next, rune);
                    target.PreserveFormatRevision(revision);
                }
                if (tabs.TryGetValue(sourceEnd, out int trailing) && next + trailing - sourceEnd < width)
                    relocatedTabs.Add((next, next + trailing - sourceEnd));
                foreach (var (start, end) in relocatedTabs) target.AddReflowTab(start, end);
                if (index == oldCursor && !mappedCursor)
                {
                    resizedCursorRow = placed;
                    resizedCursorColumn = Math.Min(width, next + Math.Max(0, originalColumn - sourceEnd));
                }

                void Wrap()
                {
                    foreach (var (start, end) in relocatedTabs) target.AddReflowTab(start, end);
                    relocatedTabs.Clear();
                    FinishRow(soft: true, emptyBoundary: false);
                    target = new TerminalRow(budget: StateQuota)
                        { ExcludeFromRecording = excluded, LogicalId = logicalId, LogicalOffset = logicalOffset,
                            LogicalClusterOffset = clusterOffset };
                    recorded = [];
                    containsUnrecorded = false;
                    next = 0;
                }
            }
            FinishRow(soft: false, emptyBoundary: boundarySubmitted);
            first = last + 1;

            void AddRecorded(int start, int end)
            {
                if (recorded.Count > 0 && recorded[^1].End == start)
                    recorded[^1] = (recorded[^1].Start, end);
                else recorded.Add((start, end));
            }

            void FinishRow(bool soft, bool emptyBoundary)
            {
                target.SoftWrapToNext = soft;
                string text = target.GetText();
                logicalOffset += text.Length;
                clusterOffset += TerminalRow.CountClusters(text);
                if ((!containsUnrecorded && recorded.Count != 0) || (target.IsEmpty && emptyBoundary))
                    target.SubmittedText = target.GetText(preserveTabs: true, serializedColumn: null);
                else if (recorded.Count != 0)
                    target.RecordedColumns = recorded;
                retained.Enqueue(target);
                placed++;
                if (retained.Count > retainedRowLimit)
                {
                    TerminalRow expired = retained.Dequeue();
                    if (ReferenceEquals(expired, relocatedCluster)) relocatedCluster = null;
                    try { if (TextRecord is not null) SubmitText(expired); }
                    finally { expired.ReleaseOwnedState(); }
                    retired++;
                }
            }
        }
        columns = width;
        screenRows = height;
        marginTop = 0;
        marginBottom = height - 1;
        UseRows(retained.ToArray(), Math.Max(0, resizedCursorRow - retired), height);
        foreach (TerminalRow replaced in prior) replaced.ReleaseOwnedState();
        wrapPending = autoWrap && resizedCursorColumn == width;
        column = Math.Min(width - 1, resizedCursorColumn);
        clusterRow = relocatedCluster;
        clusterColumn = relocatedColumn;
    }

    private void UseRows(TerminalRow[] rows, int retainedCursor, int height)
    {
        ClearHistoryIndex();
        int first = Math.Max(0, rows.Length - height);
        scrollback.Clear();
        for (int index = 0; index < first; index++) scrollback.AddLast(rows[index]);
        screen = rows[first..].ToList();
        cursorRow = Math.Clamp(retainedCursor - first, 0, screen.Count - 1);
    }

    private void RecordLineFeed(bool crlf, bool eligible)
    {
        if (TextRecord is null || IsAlternateScreen) return;
        EnsureCursorRowExists();
        if (!eligible || screen[cursorRow].ExcludeFromRecording) return;
        int start = cursorRow;
        while (start > 0 && screen[start - 1].SoftWrapToNext)
        {
            start--;
            if (screen[start].ExcludeFromRecording) return;
        }
        LinkedListNode<TerminalRow>? firstWrapped = null;
        if (start == 0)
        {
            for (var node = scrollback.Last; node is not null && node.Value.SoftWrapToNext; node = node.Previous)
            {
                if (node.Value.ExcludeFromRecording) return;
                firstWrapped = node;
            }
        }
        bool changed = false;
        for (var node = firstWrapped; node is not null; node = node.Next)
            changed |= SubmitText(node.Value, boundary: true);
        for (int i = start; i <= cursorRow; i++)
            changed |= SubmitText(screen[i], boundary: true);
        if (changed) TextRecord(crlf ? "\r\n" : "\n");
    }

    internal void EchoUtf8(ReadOnlySpan<byte> payload)
    {
        if (TryNativeFeed(2, payload)) return;
        Decode(localEcho, payload, flush: false);
    }

    public void CompleteReceive()
    {
        if (receiveCompleted)
        {
            return;
        }

        if (nativeCore is not null && HasManagedOnlyObservers) EnsureManaged();
        if (nativeCore is not null)
        {
            if (nativeCore.TryComplete(NativeRecordingFlags, NativeTextRecord, NativeDisplayRecord))
            {
                receiveCompleted = true;
                return;
            }
            EnsureManaged();
        }
        Decode(data, [], flush: true);
        Decode(standardError, [], flush: true);
        receiveCompleted = true;
    }

    private void AdvanceRow(bool soft = false)
    {
        EnsureCursorRowExists();
        TerminalRow previous = screen[cursorRow];
        previous.SoftWrapToNext = soft;
        if (cursorRow == marginBottom)
        {
            ScrollRegion(1, down: false);
            FollowLogicalLine(previous);
            if (soft && previous.ExcludeFromRecording) screen[cursorRow].ExcludeFromRecording = true;
            RetireRows();
            return;
        }
        if (cursorRow < screenRows - 1)
        {
            cursorRow++;
            EnsureCursorRowExists();
            FollowLogicalLine(previous);
            if (soft && previous.ExcludeFromRecording) screen[cursorRow].ExcludeFromRecording = true;
            return;
        }

    }

    private void FollowLogicalLine(TerminalRow previous)
    {
        if (!previous.SoftWrapToNext) return;
        screen[cursorRow].LogicalId = previous.LogicalId;
        screen[cursorRow].LogicalOffset = previous.LogicalOffset + previous.GetText().Length;
        screen[cursorRow].LogicalClusterOffset = previous.LogicalClusterOffset + previous.TextClusterCount();
    }

    private void ScrollRegion(int count, bool down)
    {
        ForgetCluster();
        while (screen.Count <= marginBottom) screen.Add(NewRow());
        count = Math.Min(count, marginBottom - marginTop + 1);
        for (int index = 0; index < count; index++)
        {
            int remove = down ? marginBottom : marginTop;
            if (!down && marginTop == 0 && marginBottom == screenRows - 1 && !IsAlternateScreen)
            {
                try { historyRows?.Append(screen[remove]); }
                catch (ResourceLimitException) { ClearHistoryIndex(); }
                scrollback.AddLast(screen[remove]);
            }
            else screen[remove].ReleaseOwnedState();
            screen.RemoveAt(remove);
            TerminalRow blank = NewRow();
            blank.Erase(0, columns, EraseStyle);
            screen.Insert(down ? marginTop : marginBottom, blank);
        }
        RetireRows();
    }

    private void EnsureCursorRowExists()
    {
        while (screen.Count <= cursorRow)
        {
            screen.Add(NewRow());
            RetireRows();
        }
    }

    private void RetireRows()
    {
        if (IsAlternateScreen) return;
        while (scrollback.Count + screen.Count > retainedRowLimit)
        {
            TerminalRow row = scrollback.First!.Value;
            historyRows?.Remove(scrollback.First!);
            scrollback.RemoveFirst();
            try { if (TextRecord is not null) SubmitText(row); }
            finally
            {
                if (spareRows.Count < 2) { row.Recycle(); spareRows.Push(row); }
                else row.ReleaseOwnedState();
            }
        }
    }

    private void Decode(SourceState state, ReadOnlySpan<byte> payload, bool flush)
    {
        if (state.Legacy is { } legacy)
        {
            legacy.Decode(payload, flush, RecordingInterval, (character, interval) => ApplyRune(state, character, interval),
                () => state.Escape != EscapeState.Ground);
            return;
        }

        foreach (byte value in payload)
        {
            if (state.PendingCount == 0 && value < 0x80)
            {
                Emit(new Rune(value), RecordingInterval);
                continue;
            }

            if (state.PendingCount == 0) state.PendingInterval = RecordingInterval;
            state.Pending[state.PendingCount++] = value;
            while (state.PendingCount != 0)
            {
                OperationStatus status = Rune.DecodeFromUtf8(state.Pending.AsSpan(0, state.PendingCount),
                    out Rune decoded, out int consumed);
                if (status == OperationStatus.NeedMoreData) break;
                Emit(decoded, state.PendingInterval);
                state.PendingCount -= consumed;
                state.Pending.AsSpan(consumed, state.PendingCount).CopyTo(state.Pending);
                state.PendingInterval = RecordingInterval;
            }
        }

        if (flush && state.PendingCount != 0)
        {
            Emit(Rune.ReplacementChar, state.PendingInterval);
            state.PendingCount = 0;
        }

        void Emit(Rune character, int interval)
        {
            bool atStart = state.IsAtStart;
            state.IsAtStart = false;
            if (!atStart || character.Value != 0xFEFF) ApplyRune(state, character, interval);
        }
    }

    private void ApplyRune(SourceState state, Rune character, int interval, bool generated = false)
    {
        if (!generated && state != localEcho)
            MacroCharacter?.Invoke(ReceiveSource(state), character,
                state.Escape == EscapeState.Ground ||
                state.Escape is not (EscapeState.ControlString or EscapeState.ControlStringEscape) && character.Value is '\r' or '\n' or '\t');
        if (Rune.IsControl(character)) state.LastWasGraphic = false;
        SourceCharacter?.Invoke(character, interval, ReceiveSource(state));
        bool crlf = state.PreviousCr && state.PreviousInterval == interval;
        state.PreviousCr = character.Value == '\r';
        state.PreviousInterval = interval;
        if (ConsumeControlString(state, character))
        {
            return;
        }

        switch (character.Value)
        {
            case 0x0E:
            case 0x0F:
                state.UseG1 = character.Value == 0x0E;
                RecordOperation(state, character.ToString(), interval);
                return;
            case '\a':
                Bell?.Invoke();
                return;
            case 0x1B:
                state.Escape = EscapeState.Escape;
                state.EscapeInterval = interval;
                state.Parameters.Clear();
                return;
            case 0x18:
            case 0x1A:
                state.Escape = EscapeState.Ground;
                return;
            case '\r':
                RecordOperation(state, "\r", interval);
                ForgetCluster();
                column = 0;
                wrapPending = false;
                return;
            case '\b':
                RecordOperation(state, "\b", interval);
                ForgetCluster();
                column = Math.Max(0, column - 1);
                wrapPending = false;
                return;
            case '\t':
                RecordOperation(state, "\t", interval);
                ForgetCluster();
                EnsureCursorRowExists();
                if (interval != RecordingInterval) screen[cursorRow].ExcludeFromRecording = true;
                int tab = tabStops.Where(stop => stop > column && stop < columns).DefaultIfEmpty(columns - 1).Min();
                screen[cursorRow].AddTab(column, tab);
                column = tab;
                wrapPending = false;
                return;
            case '\n':
            case '\v':
            case '\f':
                RecordOperation(state, character.ToString(), interval);
                if (character.Value == '\n') RecordLineFeed(crlf, interval == RecordingInterval);
                MoveDown(lineFeedReturns);
                return;
        }

        if (Rune.IsControl(character) || ConsumeEscape(state, character))
        {
            return;
        }

        if ((state.UseG1 ? state.G1 : state.G0) == '0' && character.Value is >= 0x5F and <= 0x7E)
            character = new Rune(" ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"[character.Value - 0x5F]);
        if (!state.LastWasGraphic || !Unicode17.Joins(CollectionsMarshal.AsSpan(state.LastGraphicRunes), character))
            state.LastGraphicRunes.Clear();
        if (state.LastGraphicRunes.Count == 256)
            throw new InvalidOperationException("Terminal source cluster exceeds its size limit.");
        state.LastGraphicRunes.Add(character);
        string scalarText = TerminalText.Scalar(character);
        state.LastGraphic = state.LastGraphicRunes.Count == 1 ? scalarText :
            TerminalText.Cluster(CollectionsMarshal.AsSpan(state.LastGraphicRunes));
        state.LastWasGraphic = true;

        RecordOperation(state, scalarText, interval);
        if (character.Value == 0xFEFF)
        {
            ForgetCluster();
            EnsureCursorRowExists();
            if (interval != RecordingInterval) screen[cursorRow].ExcludeFromRecording = true;
            screen[cursorRow].AddFormat(column + (wrapPending ? 1 : 0), character);
            return;
        }

        if (clusterRow is not null && Unicode17.Joins(CollectionsMarshal.AsSpan(cluster), character))
        {
            if (interval != RecordingInterval) clusterRow.ExcludeFromRecording = true;
            ExtendCluster(character);
            return;
        }

        cluster.Clear();
        cluster.Add(character);
        int width = Unicode17.GraphemeWidth(CollectionsMarshal.AsSpan(cluster), ambiguousWidth);
        if (wrapPending || (autoWrap && column + width > columns))
        {
            AdvanceRow(soft: true);
            column = 0;
            wrapPending = false;
        }
        else if (column + width > columns)
        {
            column = columns - width;
        }

        EnsureCursorRowExists();
        clusterRow = screen[cursorRow];
        if (interval != RecordingInterval) clusterRow.ExcludeFromRecording = true;
        clusterColumn = column;
        clusterWidth = width;
        clusterStyle = style;
        if (insertMode) clusterRow.Insert(clusterColumn, width, columns, EraseStyle);
        clusterRow.Write(clusterColumn, scalarText, width, clusterStyle);
        SetCursorAfterCluster();
    }

    private void ExtendCluster(Rune character)
    {
        if (cluster.Count == 256)
            throw new InvalidOperationException("Terminal text cluster exceeds 256 Unicode scalars.");
        cluster.Add(character);
        int width = Unicode17.GraphemeWidth(CollectionsMarshal.AsSpan(cluster), ambiguousWidth);
        clusterRow!.ClearCells(clusterColumn, clusterColumn + clusterWidth);
        if (clusterColumn + width > columns)
        {
            if (autoWrap)
            {
                AdvanceRow(soft: true);
                clusterRow = screen[cursorRow];
                clusterColumn = 0;
            }
            else
            {
                clusterColumn = columns - width;
            }
        }

        clusterWidth = width;
        clusterRow.Write(clusterColumn, TerminalText.Cluster(CollectionsMarshal.AsSpan(cluster)), width, clusterStyle);
        SetCursorAfterCluster();
    }

    private void SetCursorAfterCluster()
    {
        int next = clusterColumn + clusterWidth;
        wrapPending = autoWrap && next == columns;
        column = Math.Min(columns - 1, next);
    }

    private void ForgetCluster() => clusterRow = null;

    private void RecordOperation(SourceState state, string operation, int interval) =>
        DisplayOperation?.Invoke(operation, interval, ReceiveSource(state));

    private TerminalReceiveSource ReceiveSource(SourceState state) =>
        state == standardError ? TerminalReceiveSource.StandardError : TerminalReceiveSource.Data;

    private void MoveDown(bool returnToLeft)
    {
        ForgetCluster();
        AdvanceRow();
        if (returnToLeft) column = 0;
        wrapPending = false;
    }

    private bool ConsumeEscape(SourceState state, Rune character)
    {
        int value = character.Value;
        switch (state.Escape)
        {
            case EscapeState.Ground:
                return false;
            case EscapeState.Escape:
                if (value is 'D' or 'E' or 'M')
                {
                    RecordOperation(state, $"\u001b{(char)value}", state.EscapeInterval);
                    state.Escape = EscapeState.Ground;
                    if (value == 'M')
                    {
                        ForgetCluster();
                        if (cursorRow == marginTop) ScrollRegion(1, down: true);
                        else cursorRow = Math.Max(0, cursorRow - 1);
                        wrapPending = false;
                    }
                    else MoveDown(value == 'E');
                    return true;
                }
                if (value is '7' or '8' or 'H')
                {
                    if (value == '7') SaveCursor(state);
                    else if (value == '8') RestoreCursor(state);
                    else tabStops.Add(column);
                    state.Escape = EscapeState.Ground;
                    RecordOperation(state, $"\u001b{(char)value}", state.EscapeInterval);
                    return true;
                }
                if (value is '=' or '>' or 'c')
                {
                    state.Escape = EscapeState.Ground;
                    if (value == 'c') Reset(hard: true);
                    else ApplicationKeypad = value == '=';
                    RecordOperation(state, $"\u001b{(char)value}", state.EscapeInterval);
                    return true;
                }

                if (value is ']' or 'P' or 'X' or '^' or '_')
                {
                    state.Escape = EscapeState.ControlString;
                    state.Osc = value == ']';
                    state.StringBytes = 0;
                }
                else
                {
                    state.Escape = value switch
                    {
                        '[' => EscapeState.Csi,
                        >= 0x20 and <= 0x2F => EscapeState.EscapeIntermediate,
                        _ => EscapeState.Ground,
                    };
                    if (state.Escape == EscapeState.EscapeIntermediate) state.Parameters.Append((char)value);
                }

                return true;
            case EscapeState.EscapeIntermediate:
                if (value is not (>= 0x20 and <= 0x2F))
                {
                    state.Escape = EscapeState.Ground;
                    if (state.Parameters.Length == 1 && state.Parameters[0] is '(' or ')' && value is '0' or 'B')
                    {
                        if (state.Parameters[0] == '(') state.G0 = (char)value;
                        else state.G1 = (char)value;
                        RecordOperation(state, $"\u001b{state.Parameters}{(char)value}", state.EscapeInterval);
                    }
                }
                else if (state.Parameters.Length < 4096) state.Parameters.Append((char)value);
                else throw new InvalidOperationException("Terminal escape sequence exceeds its size limit.");

                return true;
            case EscapeState.Csi:
                if (value is >= 0x40 and <= 0x7E)
                {
                    string? parameters = ApplyCsi(state, state.Parameters, value);
                    if (parameters is not null)
                        RecordOperation(state, $"\u001b[{parameters}{(char)value}", state.EscapeInterval);
                    state.Escape = EscapeState.Ground;
                }
                else if (value is >= 0x20 and <= 0x3F)
                {
                    if (state.Parameters.Length == 4096)
                    {
                        throw new InvalidOperationException("Terminal escape sequence exceeds its size limit.");
                    }

                    state.Parameters.Append((char)value);
                }
                else
                {
                    state.Escape = EscapeState.Ground;
                }

                return true;
            default:
                throw new InvalidOperationException("Invalid terminal parser state.");
        }
    }

    private static bool ConsumeControlString(SourceState state, Rune character)
    {
        int value = character.Value;
        if (state.Escape == EscapeState.ControlStringEscape)
        {
            state.Escape = EscapeState.Escape;
            state.Parameters.Clear();
            return false;
        }

        if (state.Escape != EscapeState.ControlString)
        {
            return false;
        }

        if (value is 0x18 or 0x1A || (value == '\a' && state.Osc))
        {
            state.Escape = EscapeState.Ground;
        }
        else if (value == 0x1B)
        {
            state.Escape = EscapeState.ControlStringEscape;
        }
        else
        {
            state.StringBytes += character.Utf8SequenceLength;
            if (state.StringBytes > 1024 * 1024)
            {
                throw new InvalidOperationException("Terminal control string exceeds its size limit.");
            }
        }

        return true;
    }

    private string? ApplyCsi(SourceState source, StringBuilder parameters, int final)
    {
        string spelling = parameters.ToString();
        int mainParameters = 1, subparameters = 1;
        foreach (char character in spelling)
        {
            if (character == ';') { mainParameters++; subparameters = 1; }
            if (character == ':') subparameters++;
            if (mainParameters > 32 || subparameters > 16)
                throw new InvalidOperationException("Terminal parameters exceed their size limit.");
        }
        if (final == 'q' && spelling.EndsWith(' ') &&
            int.TryParse(spelling.AsSpan(0, spelling.Length - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int cursorStyle) && cursorStyle is >= 0 and <= 6)
        {
            cursorShape = cursorStyle switch
            {
                3 or 4 => TerminalCursorShape.Underline,
                5 or 6 => TerminalCursorShape.Bar,
                _ => TerminalCursorShape.Block,
            };
            cursorBlink = cursorStyle is 0 or 1 or 3 or 5;
            return spelling;
        }
        if (final == 'p' && spelling == "!")
        {
            Reset(hard: false);
            return spelling;
        }
        if (final == 'n' && spelling is "5" or "6" or "?6")
        {
            int row = cursorRow - (originMode ? marginTop : 0) + 1;
            Reply(spelling == "5" ? "\u001b[0n" : $"\u001b[{(spelling[0] == '?' ? "?" : "")}{row};{column + 1}R");
            return null;
        }
        if (final == 'c' && spelling is "" or "0")
        {
            Reply("\u001b[?1;2c");
            return null;
        }
        if (final == 'm')
        {
            string rendition = parameters.ToString();
            return TerminalRendition.Apply(rendition, ref style) ? rendition : null;
        }
        if (final is 'h' or 'l')
        {
            return SetModes(source, parameters, final == 'h');
        }

        Span<int> values = stackalloc int[2];
        values.Clear();
        int index = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            char character = parameters[i];
            if (character is >= '0' and <= '9')
            {
                values[index] = Math.Min(1024, values[index] * 10 + character - '0');
            }
            else if (character == ';' && index == 0)
            {
                index++;
            }
            else
            {
                return null;
            }
        }

        int count = Math.Max(1, values[0]);
        if (index > 0 && final is not ('H' or 'f' or 'r'))
        {
            return null;
        }

        switch (final)
        {
            case 'H':
            case 'f':
                cursorRow = Math.Clamp(Math.Max(1, values[0]) - 1 + (originMode ? marginTop : 0),
                    originMode ? marginTop : 0, originMode ? marginBottom : screenRows - 1);
                column = Math.Clamp(values[1] - 1, 0, columns - 1);
                break;
            case 'A':
                cursorRow = Math.Max(originMode ? marginTop : 0, cursorRow - count);
                break;
            case 'B':
                cursorRow = Math.Min(originMode ? marginBottom : screenRows - 1, cursorRow + count);
                break;
            case 'C':
                column = Math.Min(columns - 1, column + count);
                break;
            case 'D':
                column = Math.Max(0, column - count);
                break;
            case 'E':
                cursorRow = Math.Min(originMode ? marginBottom : screenRows - 1, cursorRow + count);
                column = 0;
                break;
            case 'F':
                cursorRow = Math.Max(originMode ? marginTop : 0, cursorRow - count);
                column = 0;
                break;
            case 'G':
            case '`':
                column = Math.Min(columns - 1, count - 1);
                break;
            case 'd':
                cursorRow = Math.Clamp(count - 1 + (originMode ? marginTop : 0),
                    originMode ? marginTop : 0, originMode ? marginBottom : screenRows - 1);
                break;
            case '@':
                EnsureCursorRowExists();
                screen[cursorRow].Insert(column, Math.Min(count, columns - column), columns, EraseStyle);
                break;
            case 'P':
                EnsureCursorRowExists();
                screen[cursorRow].Delete(column, Math.Min(count, columns - column), columns, EraseStyle);
                break;
            case 'X':
                EnsureCursorRowExists();
                screen[cursorRow].Erase(column, column + Math.Min(count, columns - column), EraseStyle);
                break;
            case 'L':
            case 'M':
                if (cursorRow >= marginTop && cursorRow <= marginBottom)
                {
                    int top = marginTop;
                    marginTop = cursorRow;
                    ScrollRegion(count, down: final == 'L');
                    marginTop = top;
                }
                break;
            case 'S':
            case 'T':
                ScrollRegion(count, down: final == 'T');
                break;
            case 'r':
                int topMargin = Math.Max(1, values[0]) - 1;
                int bottomMargin = values[1] == 0 ? screenRows - 1 : values[1] - 1;
                if (topMargin >= bottomMargin || bottomMargin >= screenRows) return null;
                marginTop = topMargin;
                marginBottom = bottomMargin;
                cursorRow = originMode ? marginTop : 0;
                column = 0;
                break;
            case 's' when parameters.Length == 0:
                savedPosition = (cursorRow, column);
                break;
            case 'u' when parameters.Length == 0:
                if (savedPosition is { } position)
                {
                    cursorRow = Math.Clamp(position.Row, 0, screenRows - 1);
                    column = Math.Clamp(position.Column, 0, columns - 1);
                }
                break;
            case 'I':
                for (int step = 0; step < Math.Min(count, columns); step++)
                    column = tabStops.Where(stop => stop > column && stop < columns).DefaultIfEmpty(columns - 1).Min();
                break;
            case 'Z':
                for (int step = 0; step < Math.Min(count, columns); step++)
                    column = tabStops.Where(stop => stop < column).DefaultIfEmpty(0).Max();
                break;
            case 'g' when values[0] is 0 or 3:
                if (values[0] == 3) tabStops.Clear();
                else tabStops.Remove(column);
                break;
            case 'b':
                string repeated = source.LastGraphic;
                source.Escape = EscapeState.Ground;
                for (int repeat = 0; repeat < count; repeat++)
                    foreach (Rune rune in repeated.EnumerateRunes())
                        ApplyRune(source, rune, source.EscapeInterval, generated: true);
                return null;
            case 'J' when values[0] is 0 or 1:
                EnsureCursorRowExists();
                for (int row = 0; row < screen.Count; row++)
                {
                    if (row == cursorRow)
                        screen[row].Erase(values[0] == 0 ? column : 0, values[0] == 0 ? columns : column + 1, EraseStyle);
                    else if ((values[0] == 0 && row > cursorRow) || (values[0] == 1 && row < cursorRow))
                        screen[row].Erase(0, columns, EraseStyle);
                }
                break;
            case 'K' when values[0] <= 2:
                ForgetCluster();
                EraseLine(values[0]);
                return parameters.ToString();
            case 'J' when values[0] == 2:
                ForgetCluster();
                while (screen.Count < screenRows) screen.Add(new TerminalRow(budget: StateQuota));
                foreach (TerminalRow row in screen)
                {
                    row.Erase(0, columns, EraseStyle);
                }

                return parameters.ToString();
            case 'J' when values[0] == 3:
                if (!IsAlternateScreen)
                {
                    foreach (TerminalRow row in scrollback) row.ReleaseOwnedState();
                    ClearHistoryIndex();
                    scrollback.Clear();
                }
                return parameters.ToString();
            default:
                return null;
        }

        ForgetCluster();
        wrapPending = false;
        return parameters.ToString();
    }

    private string? SetModes(SourceState source, StringBuilder parameters, bool enabled)
    {
        string modes = parameters.ToString();
        bool dec = modes.StartsWith('?');
        string[] entries = (dec ? modes[1..] : modes).Split(';');
        if (entries.Length > 32 || entries.Any(entry => entry.Any(character => !char.IsAsciiDigit(character))))
            return null;

        var applied = new List<string>();
        foreach (string entry in entries)
        {
            if (!dec)
            {
                if (entry == "4")
                {
                    insertMode = enabled;
                    applied.Add(entry);
                }
                if (entry == "20")
                {
                    lineFeedReturns = enabled;
                    applied.Add(entry);
                }
                continue;
            }

            switch (entry)
            {
                case "1":
                    ApplicationCursorKeys = enabled;
                    break;
                case "7":
                    autoWrap = enabled;
                    if (!enabled) wrapPending = false;
                    break;
                case "6":
                    originMode = enabled;
                    cursorRow = enabled ? marginTop : 0;
                    column = 0;
                    wrapPending = false;
                    ForgetCluster();
                    break;
                case "25":
                    cursorVisible = enabled;
                    break;
                case "2004":
                    BracketedPaste = enabled;
                    break;
                case "9":
                case "1000":
                case "1002":
                case "1003":
                    mouse = mouse with
                    {
                        Tracking = enabled ? int.Parse(entry, System.Globalization.CultureInfo.InvariantCulture) : 0,
                        Generation = unchecked(mouse.Generation + 1),
                    };
                    break;
                case "1006":
                    mouse = mouse with { SgrCoordinates = enabled, Generation = unchecked(mouse.Generation + 1) };
                    break;
                case "47":
                case "1047":
                case "1049":
                    SetAlternateScreen(enabled, int.Parse(entry, System.Globalization.CultureInfo.InvariantCulture), source);
                    break;
                case "1048":
                    if (enabled) SaveCursor(source);
                    else RestoreCursor(source);
                    break;
                default:
                    continue;
            }
            applied.Add(entry);
        }
        return applied.Count == 0 ? null : (dec ? "?" : "") + string.Join(';', applied);
    }

    private void SetAlternateScreen(bool enabled, int mode = 47, SourceState? source = null)
    {
        if (enabled && source != localEcho) EnteringAlternateScreen?.Invoke();
        if (enabled == IsAlternateScreen) return;
        AiLayoutEpoch++;
        ForgetCluster();
        if (enabled)
        {
            FlushText();
            int width = columns, height = screenRows;
            if (mode == 1049)
            {
                normalReturnState = CaptureCursor(source!);
                if (alternatePage is { } discard)
                    foreach (TerminalRow row in discard.Rows) row.ReleaseOwnedState();
                alternatePage = null;
            }
            parkedScreen = (screen, cursorRow, column, wrapPending, columns, screenRows);
            if (alternatePage is { } previous)
            {
                (screen, cursorRow, column, wrapPending, columns, screenRows) = previous;
                Resize(width, height);
            }
            else
            {
                screen = [new(budget: StateQuota)];
                cursorRow = column = 0;
                wrapPending = false;
            }
        }
        else
        {
            LeavingAlternateScreen?.Invoke();
            int width = columns, height = screenRows;
            if (mode == 1047) foreach (TerminalRow row in screen) row.ReleaseOwnedState();
            alternatePage = mode == 1047 ? null : (screen, cursorRow, column, wrapPending, columns, screenRows);
            (screen, cursorRow, column, wrapPending, columns, screenRows) = parkedScreen!.Value;
            parkedScreen = null;
            Resize(width, height);
            if (mode == 1049 && normalReturnState is { } restore)
            {
                style = restore.Style;
                originMode = restore.Origin;
                autoWrap = restore.AutoWrap;
                if (source is not null) RestoreCharset(source, restore);
                normalReturnState = null;
            }
        }
    }

    private void EraseLine(int mode)
    {
        EnsureCursorRowExists();

        TerminalRow row = screen[cursorRow];
        switch (mode)
        {
            case 0:
                row.Erase(column, columns, EraseStyle);
                break;
            case 1:
                row.Erase(0, column + 1, EraseStyle);
                break;
            case 2:
                row.Erase(0, columns, EraseStyle);
                break;
        }
    }

    private CursorState CaptureCursor(SourceState source) => new(cursorRow, column, style, originMode, autoWrap,
        wrapPending, source.G0, source.G1, source.UseG1);

    private void SaveCursor(SourceState source) => savedCursor = CaptureCursor(source);

    private static void RestoreCharset(SourceState source, CursorState cursor)
    {
        source.G0 = cursor.G0;
        source.G1 = cursor.G1;
        source.UseG1 = cursor.UseG1;
    }

    private void Reply(string message) => EnqueueReply(Encoding.ASCII.GetBytes(message));

    private void EnqueueReply(byte[] reply)
    {
        if (replies.Count == 128 || reply.Length > 65536 - replyBytes)
            throw new InvalidOperationException("Terminal reply queue exceeds its size limit.");
        replies.Enqueue(reply);
        replyBytes += reply.Length;
    }

    private void Reset(bool hard)
    {
        AiLayoutEpoch++;
        ForgetCluster();
        style = default;
        insertMode = originMode = lineFeedReturns = ApplicationCursorKeys = ApplicationKeypad = false;
        autoWrap = true;
        wrapPending = false;
        marginTop = 0;
        marginBottom = screenRows - 1;
        savedCursor = new CursorState(0, 0, default, false, true, false);
        savedPosition = null;
        cursorVisible = true;
        if (!hard) return;
        if (parkedScreen is { } normal)
        {
            LeavingAlternateScreen?.Invoke();
            foreach (TerminalRow row in normal.Rows) row.ReleaseOwnedState();
        }
        if (alternatePage is { } previous)
            foreach (TerminalRow row in previous.Rows) row.ReleaseOwnedState();
        foreach (TerminalRow row in screen) row.ReleaseOwnedState();
        foreach (TerminalRow row in scrollback) row.ReleaseOwnedState();
        parkedScreen = null;
        alternatePage = null;
        normalReturnState = null;
        screen = [new(budget: StateQuota)];
        ClearHistoryIndex();
        scrollback.Clear();
        cursorRow = column = 0;
        cursorVisible = true;
        cursorShape = TerminalCursorShape.Block;
        cursorBlink = true;
        BracketedPaste = false;
        mouse = new TerminalMouseMode(0, false, unchecked(mouse.Generation + 1));
        tabStops.Clear();
        for (int stop = 8; stop < 1024; stop += 8) tabStops.Add(stop);
        foreach (SourceState source in new[] { data, standardError, localEcho })
        {
            source.G0 = source.G1 = 'B';
            source.UseG1 = false;
        }
    }

    private void RestoreCursor(SourceState source)
    {
        ForgetCluster();
        cursorRow = Math.Clamp(savedCursor.Row, 0, screenRows - 1);
        column = Math.Clamp(savedCursor.Column, 0, columns - 1);
        style = savedCursor.Style;
        originMode = savedCursor.Origin;
        autoWrap = savedCursor.AutoWrap;
        wrapPending = savedCursor.WrapPending && column == columns - 1;
        RestoreCharset(source, savedCursor);
    }

    private readonly record struct CursorState(int Row, int Column, TerminalStyle Style, bool Origin,
        bool AutoWrap, bool WrapPending, char G0 = 'B', char G1 = 'B', bool UseG1 = false);

    private enum EscapeState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        ControlString,
        ControlStringEscape,
    }

    private sealed class SourceState
    {
        public byte[] Pending { get; } = new byte[4];
        public int PendingCount { get; set; }
        public int PendingInterval { get; set; }
        public int EscapeInterval { get; set; }
        public int PreviousInterval { get; set; }
        public bool PreviousCr { get; set; }
        public LegacyReceiver? Legacy { get; set; }
        public bool IsAtStart { get; set; } = true;
        public EscapeState Escape { get; set; }
        public StringBuilder Parameters { get; } = new();
        public bool Osc { get; set; }
        public int StringBytes { get; set; }
        public char G0 { get; set; } = 'B';
        public char G1 { get; set; } = 'B';
        public bool UseG1 { get; set; }
        public string LastGraphic { get; set; } = "";
        public bool LastWasGraphic { get; set; }
        public List<Rune> LastGraphicRunes { get; } = [];
    }
}

public enum TerminalReceiveSource
{
    Data,
    StandardError,
}
