using System.Text;

namespace TabTty.Core;

internal sealed class TerminalRow
{
    private static long nextId;
    private static long nextContentRevision;
    private readonly List<Cell> cells = [];
    private SortedDictionary<int, string>? formats;
    private SortedDictionary<int, int>? tabs;
    private string? cachedText;
    private TerminalTextFingerprint? cachedFingerprint;
    private int? cachedClusters;
    private long? cachedContentRevision;
    private long formatRevision;
    private TerminalCell[]? cachedCells;
    private int cachedCellCount;
    private readonly ResourceReservation? memory;
    private readonly ResourceQuota? budget;
    private ResourceReservation? cachedTextMemory, cachedCellsMemory;
    private bool sharedText, sharedCells;
    private string? submittedText;
    private long submittedTextRevision;
    private List<(int Start, int End)>? recordedColumns;
    public long Id { get; private set; }
    public long LogicalId { get; set; }
    public long LogicalOffset { get; set; }
    internal long LogicalClusterOffset { get; set; }
    internal long DisplayRevision { get; private set; }
    internal long HistoryRevision { get; private set; }
    public bool IsEmpty => cells.Count == 0 && (formats is null || formats.Count == 0) &&
        (tabs is null || tabs.Count == 0);
    public bool SoftWrapToNext { get; set; }
    public string? SubmittedText
    {
        get => submittedText;
        set
        {
            memory?.Add(TextSize(value) - TextSize(submittedText));
            submittedText = value;
            submittedTextRevision = HistoryRevision;
        }
    }
    public bool ExcludeFromRecording { get; set; }
    public List<(int Start, int End)>? RecordedColumns
    {
        get => recordedColumns;
        set
        {
            memory?.Add((value?.Capacity ?? 0) * 16L - (recordedColumns?.Capacity ?? 0) * 16L);
            recordedColumns = value;
        }
    }
    public IEnumerable<KeyValuePair<int, string>> Formats => formats ?? Enumerable.Empty<KeyValuePair<int, string>>();
    public IEnumerable<KeyValuePair<int, int>> Tabs => tabs ?? Enumerable.Empty<KeyValuePair<int, int>>();

    public TerminalRow(long? retainedId = null, ResourceQuota? budget = null)
    {
        Id = retainedId ?? Interlocked.Increment(ref nextId);
        LogicalId = Id;
        this.budget = budget;
        memory = budget?.Reserve(256 + sizeof(long));
    }

    private static long TextSize(string? value) => string.IsNullOrEmpty(value) || TerminalText.IsShared(value) ? 0 : 24 + value.Length * 2L;

    internal static TerminalRow ImportNative(long id, long logicalId, long logicalOffset,
        long displayRevision, long contentRevision, long submittedRevision, bool softWrap,
        ReadOnlySpan<NativeTerminalCore.CellView> source, ReadOnlySpan<char> text,
        ReadOnlySpan<NativeTerminalCore.TabView> sourceTabs, string? submitted, ResourceQuota quota)
    {
        var row = new TerminalRow(id, quota);
        try
        {
            row.EnsureCapacity(source.Length);
            foreach (NativeTerminalCore.CellView cell in source)
            {
                string? value = null;
                if (cell.Length != ushort.MaxValue)
                {
                    ReadOnlySpan<char> characters = text.Slice(checked((int)cell.Offset), cell.Length);
                    value = characters.Length == 1 && characters[0] < 128
                        ? TerminalText.Scalar(new Rune(characters[0])) : new string(characters);
                }
                row.memory?.Add(TextSize(value));
                var style = new TerminalStyle(NativeTerminalCore.Color(cell.Foreground),
                    NativeTerminalCore.Color(cell.Background), (TerminalStyleFlags)cell.Flags);
                // AI observations always hand off first, so no content revision issued by
                // the native engine has escaped. Give imported content fresh proof tokens.
                row.cells.Add(new Cell(value, cell.Width, style,
                    cell.Width < 0 ? 0 : Interlocked.Increment(ref nextContentRevision)));
            }
            foreach (NativeTerminalCore.TabView tab in sourceTabs)
                row.AddReflowTab(checked((int)tab.Start), checked((int)tab.End));
            row.LogicalId = logicalId;
            row.LogicalOffset = logicalOffset;
            row.DisplayRevision = displayRevision;
            row.HistoryRevision = contentRevision;
            row.SoftWrapToNext = softWrap;
            row.SubmittedText = submitted;
            row.submittedTextRevision = submittedRevision;
            return row;
        }
        catch
        {
            row.ReleaseOwnedState();
            throw;
        }
    }

    private void EnsureCapacity(int count)
    {
        if (count <= cells.Capacity) return;
        int capacity = Math.Max(count, Math.Max(4, cells.Capacity * 2));
        long extra = (capacity - cells.Capacity) * 64L;
        memory?.Add(extra);
        try { cells.EnsureCapacity(capacity); }
        catch { memory?.Add(-extra); throw; }
    }

    private void Invalidate()
    {
        HistoryRevision++;
        cachedContentRevision = null;
        cachedFingerprint = null;
        cachedClusters = null;
        if (!sharedText) cachedTextMemory?.Dispose();
        cachedText = null;
        cachedTextMemory = null;
        sharedText = false;
        InvalidateCells();
    }

    private void InvalidateCells()
    {
        DisplayRevision++;
        if (!sharedCells) cachedCellsMemory?.Dispose();
        cachedCells = null;
        cachedCellsMemory = null;
        sharedCells = false;
    }

    public TerminalSnapshot.Row GetSnapshot()
    {
        string text = GetText();
        ReadOnlyMemory<TerminalCell> snapshotCells = GetCells();
        sharedText = sharedCells = true;
        return new TerminalSnapshot.Row(Id, LogicalId, LogicalOffset, text, SoftWrapToNext, snapshotCells,
            cachedTextMemory, cachedCellsMemory, DisplayRevision);
    }

    public void ReleaseOwnedState()
    {
        memory?.Dispose();
        if (!sharedText) cachedTextMemory?.Dispose();
        if (!sharedCells) cachedCellsMemory?.Dispose();
    }

    public void Recycle()
    {
        Invalidate();
        cells.Clear();
        tabs = null;
        formats = null;
        formatRevision = 0;
        submittedText = null;
        recordedColumns = null;
        SoftWrapToNext = ExcludeFromRecording = false;
        Id = LogicalId = Interlocked.Increment(ref nextId);
        LogicalOffset = 0;
        LogicalClusterOffset = 0;
        memory?.Resize(256 + sizeof(long) + cells.Capacity * 64L);
    }

    public bool HasTabs => tabs is { Count: > 0 };

    public bool IsColumnRecorded(int column)
    {
        if (recordedColumns is null) return false;
        foreach (var range in recordedColumns)
            if (column >= range.Start && column < range.End) return true;
        return false;
    }

    public void AddTab(int start, int end)
    {
        if (end != (start / 8 + 1) * 8) return;
        for (int column = start; column < Math.Min(end, cells.Count); column++)
            if (cells[column].Width != 0) return;
        tabs ??= new();
        if (!tabs.ContainsKey(start)) memory?.Add(64);
        tabs[start] = end;
    }

    public void AddReflowTab(int start, int end)
    {
        if (end != (start / 8 + 1) * 8) return;
        tabs ??= new();
        if (!tabs.ContainsKey(start)) memory?.Add(64);
        tabs[start] = end;
    }

    public void Insert(int start, int count, int columns, TerminalStyle erase = default)
    {
        EnsureCapacity(Math.Max(start, cells.Count) + count);
        ClearCells(start, start);
        if (start < cells.Count && cells[start].Width == -1) ClearCells(start, start + 1);
        while (cells.Count < start) cells.Add(default);
        cells.InsertRange(start, Enumerable.Repeat(new Cell(null, 0, erase), count));
        if (cells.Count > columns)
        {
            if (cells[columns].Width == -1)
            {
                memory?.Add(-TextSize(cells[columns - 1].Text));
                cells[columns - 1] = default;
            }
            memory?.Add(-cells.Skip(columns).Sum(cell => TextSize(cell.Text)));
            cells.RemoveRange(columns, cells.Count - columns);
        }
        ShiftMetadata(start, count, columns);
    }

    public void Delete(int start, int count, int columns, TerminalStyle erase = default)
    {
        if (erase != default) EnsureCapacity(columns);
        ClearCells(start, Math.Min(columns, start + count));
        if (start < cells.Count) cells.RemoveRange(start, Math.Min(count, cells.Count - start));
        if (erase != default)
        {
            while (cells.Count < columns - count) cells.Add(default);
            while (cells.Count < columns) cells.Add(new Cell(null, 0, erase));
        }
        ShiftMetadata(start, -count, columns);
    }

    private void ShiftMetadata(int start, int delta, int columns)
    {
        Invalidate();
        long revision = Interlocked.Increment(ref nextContentRevision);
        for (int i = start; i < cells.Count; i++) cells[i] = cells[i] with { Revision = revision };
        RecordedColumns = null;
        memory?.Add(-(tabs?.Count ?? 0) * 64L);
        tabs = null;
        if (formats is null) return;
        var updated = new SortedDictionary<int, string>();
        foreach (var (column, text) in formats)
        {
            if (delta < 0 && column >= start && column < start - delta)
            {
                memory?.Add(-64 - TextSize(text));
                continue;
            }
            int destination = column < start ? column : column + delta;
            if (destination < columns) updated[destination] = text;
            else memory?.Add(-64 - TextSize(text));
        }
        formats = updated;
    }

    public void Write(int column, string text, int width, TerminalStyle style)
    {
        if (column < cells.Count && cells[column].Width == width && cells[column].Text == text)
        {
            if (cells[column].Style == style) return;
            InvalidateCells();
            cells[column] = cells[column] with { Style = style };
            if (width == 2) cells[column + 1] = cells[column + 1] with { Style = style };
            return;
        }
        EnsureCapacity(column + width);
        memory?.Add(TextSize(text));
        ClearCells(column, column + width);
        while (cells.Count < column + width) cells.Add(default);
        cells[column] = new Cell(text, width, style, Interlocked.Increment(ref nextContentRevision));
        if (width == 2) cells[column + 1] = new Cell(null, -1, style);
    }

    public void Clear(int start = 0, int end = int.MaxValue)
    {
        Invalidate();
        ClearCells(start, end);
        if (formats is { Count: > 0 }) RemoveFormats(start, end);
    }

    private void RemoveFormats(int start, int end)
    {
        foreach (int column in formats!.Keys.Where(column => column >= start && column < end).ToArray())
        {
            memory?.Add(-64 - TextSize(formats[column]));
            formats.Remove(column);
        }
    }

    public void Erase(int start, int end, TerminalStyle erase)
    {
        if (start < cells.Count && cells[start].Width == -1) start--;
        if (end < cells.Count && cells[end].Width == -1) end++;
        Clear(start, end);
        if (erase == default) return;
        EnsureCapacity(end);
        while (cells.Count < end) cells.Add(default);
        long revision = Interlocked.Increment(ref nextContentRevision);
        for (int index = start; index < end; index++) cells[index] = new Cell(null, 0, erase, revision);
    }

    public void AddFormat(int column, Rune value)
    {
        Invalidate();
        RecordedColumns = null;
        formats ??= new();
        formats.TryGetValue(column, out string? text);
        if (text is { Length: >= 256 }) throw new InvalidOperationException("Terminal format text exceeds its size limit.");
        memory?.Add((text is null ? 64 + 24 : 0) + value.Utf16SequenceLength * 2L);
        formats[column] = text + value.ToString();
        formatRevision = Interlocked.Increment(ref nextContentRevision);
    }

    public void ClearCells(int start, int end)
    {
        Invalidate();
        RecordedColumns = null;
        if (tabs is { Count: > 0 }) RemoveTabs(start, end);
        if (start >= cells.Count || end <= start) return;
        end = Math.Min(end, cells.Count);
        if (cells[start].Width == -1) start--;
        if (end < cells.Count && cells[end].Width == -1) end++;
        long revision = Interlocked.Increment(ref nextContentRevision);
        for (int index = start; index < end; index++)
        {
            memory?.Add(-TextSize(cells[index].Text));
            cells[index] = new Cell(null, 0, default, revision);
        }
        int last = cells.Count;
        while (last > 0 && cells[last - 1].Width == 0 && cells[last - 1].Style == default) last--;
        if (last < cells.Count) cells.RemoveRange(last, cells.Count - last);
    }

    private void RemoveTabs(int start, int end)
    {
        foreach (int column in tabs!.Keys.Where(column => column < end && tabs[column] > start).ToArray())
        {
            memory?.Add(-64);
            tabs.Remove(column);
        }
    }

    private bool HasFormatInsideTab(int start, int end)
    {
        if (formats is null) return false;
        foreach (int position in formats.Keys)
        {
            if (position >= end) break;
            if (position > start) return true;
        }
        return false;
    }

    public string GetText(bool preserveTabs = false, bool pendingOnly = false, int? serializedColumn = 0)
    {
        if (!preserveTabs && cachedText is not null) return cachedText;
        if (preserveTabs && !pendingOnly && !HasTabs)
        {
            // Revisit completed Text rows without serializing or owning them twice.
            if (submittedText is not null && submittedTextRevision == HistoryRevision) return submittedText;
            if (cachedText is not null) return cachedText;
        }
        if (!pendingOnly && formats is not { Count: > 0 } && (!preserveTabs || !HasTabs))
        {
            string text = CreateCellText();
            if (!preserveTabs)
            {
                cachedTextMemory = budget?.Reserve(TextSize(text));
                cachedText = text;
            }
            return text;
        }
        var result = new StringBuilder(cells.Count);
        int contentEnd = cells.Count;
        while (contentEnd > 0 && cells[contentEnd - 1].Width == 0) contentEnd--;
        int last = formats is { Count: > 0 } ? Math.Max(contentEnd, formats.Last().Key) : contentEnd;
        if (preserveTabs && tabs is { Count: > 0 }) last = Math.Max(last, tabs.Last().Value);
        for (int column = 0; column <= last; column++)
        {
            bool recorded = pendingOnly && IsColumnRecorded(column);
            if (!recorded && formats is not null && formats.TryGetValue(column, out string? text)) result.Append(text);
            if (preserveTabs && tabs is not null && tabs.TryGetValue(column, out int end) &&
                serializedColumn is { } prefix && end - column == 8 - (prefix + column) % 8 &&
                (!pendingOnly || RecordedColumns is null) &&
                !HasFormatInsideTab(column, end))
            {
                result.Append('\t');
                column = end - 1;
                continue;
            }
            if (column == last) break;
            Cell cell = column < cells.Count ? cells[column] : default;
            if (recorded) continue;
            if (cell.Width == 0) result.Append(' ');
            else if (cell.Width > 0) result.Append(cell.Text);
        }

        string value = result.ToString();
        if (!preserveTabs)
        {
            cachedTextMemory = budget?.Reserve(TextSize(value));
            cachedText = value;
        }
        return value;
    }

    private string CreateCellText()
    {
        int end = cells.Count;
        while (end > 0 && cells[end - 1].Width == 0) end--;
        int length = 0;
        for (int column = 0; column < end; column++)
        {
            Cell cell = cells[column];
            length = checked(length + (cell.Width == 0 ? 1 : cell.Width > 0 ? cell.Text?.Length ?? 0 : 0));
        }
        return string.Create(length, (Cells: cells, End: end), static (output, state) =>
        {
            int offset = 0;
            for (int column = 0; column < state.End; column++)
            {
                Cell cell = state.Cells[column];
                if (cell.Width == 0) output[offset++] = ' ';
                else if (cell.Width > 0)
                {
                    cell.Text.AsSpan().CopyTo(output[offset..]);
                    offset += cell.Text?.Length ?? 0;
                }
            }
        });
    }

    public ReadOnlyMemory<TerminalCell> GetCells()
    {
        if (cachedCells is not null) return cachedCells.AsMemory(0, cachedCellCount);
        int offset = 0;
        int end = formats is { Count: > 0 } ? Math.Max(cells.Count, formats.Last().Key) : cells.Count;
        cachedCellsMemory = budget?.Reserve(end * 64L + cells.Sum(cell => TextSize(cell.Text)));
        var result = new TerminalCell[end];
        int length = 0;
        for (int column = 0; column < end; column++)
        {
            if (formats is not null && formats.TryGetValue(column, out string? format)) offset += format.Length;
            Cell cell = column < cells.Count ? cells[column] : default;
            if (cell.Width < 0) continue;
            string text = cell.Text ?? " ";
            result[length++] = new TerminalCell(column, Math.Max(1, cell.Width), offset, text, cell.Style, cell.Width == 0);
            offset += text.Length;
        }
        cachedCells = result;
        cachedCellCount = length;
        return cachedCells.AsMemory(0, length);
    }

    internal TerminalTextFingerprint TextFingerprint() =>
        cachedFingerprint ??= TerminalTextFingerprint.FromText(GetText());

    internal long FormatRevision => formatRevision;
    internal void PreserveFormatRevision(long revision) => formatRevision = revision;

    internal long RevisionAt(int column) => column < cells.Count ? cells[column].Revision : 0;

    internal void PreserveRevision(int column, long revision)
    {
        if (column < cells.Count) cells[column] = cells[column] with { Revision = revision };
    }

    internal long ContentRevision(int from, int to)
    {
        bool whole = from == 0 && to == GetText().Length;
        if (whole && cachedContentRevision is { } cached) return cached;
        long revision = 0;
        int offset = 0;
        int end = cells.Count;
        while (end != 0 && cells[end - 1].Width == 0) end--;
        if (formats is { Count: > 0 }) end = Math.Max(end, formats.Last().Key);
        for (int column = 0; column <= end && offset < to; column++)
        {
            if (formats?.TryGetValue(column, out var value) == true)
            {
                offset += value.Length;
                if (offset > from) revision = Math.Max(revision, formatRevision);
            }
            if (column == end) break;
            Cell cell = column < cells.Count ? cells[column] : default;
            if (cell.Width < 0) continue;
            offset += cell.Text?.Length ?? 1;
            if (offset > from) revision = Math.Max(revision, cell.Revision);
        }
        if (whole) cachedContentRevision = revision;
        return revision;
    }

    internal int TextClusterCount() => cachedClusters ??= CountClusters(GetText());

    internal ReadOnlyMemory<TerminalCell> GetAiCells(int first, int last, int columns)
    {
        int start = Math.Max(0, first - 1);
        if (start < cells.Count && cells[start].Width < 0) start--;
        var result = new List<TerminalCell>();
        for (int column = Math.Max(0, start); column < Math.Min(last, columns); column++)
        {
            Cell cell = column < cells.Count ? cells[column] : default;
            if (cell.Width < 0) continue;
            string? format = formats?.GetValueOrDefault(column);
            string? trailing = column == columns - 1 ? formats?.GetValueOrDefault(columns) : null;
            string text = cell.Text ?? " ";
            if (format is not null || trailing is not null) text = format + text + trailing;
            result.Add(new TerminalCell(column, Math.Max(1, cell.Width), 0, text, cell.Style, cell.Width == 0));
        }
        return result.ToArray();
    }

    internal static int CountClusters(ReadOnlySpan<char> text)
    {
        int count = 0;
        while (!text.IsEmpty)
        {
            text = text[Unicode17.NextGraphemeLength(text)..];
            count++;
        }
        return count;
    }

    private readonly record struct Cell(string? Text, int Width, TerminalStyle Style, long Revision = 0);
}
