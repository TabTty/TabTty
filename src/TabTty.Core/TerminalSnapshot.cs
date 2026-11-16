using System.Collections;

namespace TabTty.Core;

public readonly record struct TerminalMatch(int StartRow, int StartOffset, int EndRow, int EndOffset);

public sealed class TerminalSnapshot : IReadOnlyList<string>
{
    internal readonly record struct Row(long Id, long LogicalId, long LogicalOffset, string Text,
        bool SoftWrapToNext, ReadOnlyMemory<TerminalCell> EagerCells,
        ResourceReservation? TextMemory, ResourceReservation? CellMemory, long DisplayRevision,
        CompactTerminalCells? DeferredCells = null)
    {
        public ReadOnlyMemory<TerminalCell> Cells => DeferredCells is null ? EagerCells : DeferredCells.GetCells();
    }
    private readonly Row[] rows;
    private readonly ResourceReservation? memory;
    private Dictionary<long, int>? indices;
    private Dictionary<long, int>? logicalIndices;
    public bool IsAlternateScreen { get; }
    public int Count => rows.Length;
    public string this[int index] => rows[index].Text;
    public int CursorRow { get; }
    public int CursorColumn { get; }
    public int Columns { get; }
    public int ScreenRows { get; }
    public int ScreenStartRow { get; }
    public bool CursorVisible { get; }
    public TerminalCursorShape CursorShape { get; }
    public bool CursorBlink { get; }
    public TerminalMouseMode Mouse { get; }
    public bool IsSoftWrapped(int row) => rows[row].SoftWrapToNext;
    public ReadOnlyMemory<TerminalCell> GetCells(int row) => rows[row].Cells;
    internal (long Id, long Revision) DisplayIdentity(int row) => (rows[row].Id, rows[row].DisplayRevision);

    internal bool HasSameHighlightText(TerminalSnapshot previous)
    {
        if (ReferenceEquals(this, previous)) return true;
        if (IsAlternateScreen != previous.IsAlternateScreen || Count != previous.Count) return false;
        // New output normally changes the tail. Shared row text makes unchanged rows cheap to compare.
        for (int index = Count - 1; index >= 0; index--)
        {
            Row current = rows[index], old = previous.rows[index];
            if (current.Id != old.Id || current.SoftWrapToNext != old.SoftWrapToNext || current.Text != old.Text)
                return false;
        }
        return true;
    }

    internal TerminalSnapshot(Row[] rows, bool isAlternateScreen, int cursorRow, int cursorColumn, int columns,
        int screenRows, int screenStartRow, bool cursorVisible, TerminalCursorShape cursorShape, bool cursorBlink,
        TerminalMouseMode mouse, ResourceQuota? budget = null)
    {
        memory = budget?.Reserve(rows.LongLength * 256 + 256);
        this.rows = rows;
        IsAlternateScreen = isAlternateScreen;
        CursorRow = cursorRow;
        CursorColumn = cursorColumn;
        Columns = columns;
        ScreenRows = screenRows;
        ScreenStartRow = screenStartRow;
        CursorVisible = cursorVisible;
        CursorShape = cursorShape;
        CursorBlink = cursorBlink;
        Mouse = mouse;
    }

    public IEnumerator<string> GetEnumerator() => rows.Select(static row => row.Text).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public TerminalMatch? Find(string query, bool matchCase, TerminalMatch? current = null, bool backwards = false,
        CancellationToken cancellationToken = default)
    {
        TerminalMatch? first = null;
        TerminalMatch? last = null;
        TerminalMatch? previous = null;
        foreach (TerminalMatch match in FindMatches(query, matchCase, cancellationToken))
        {
            first ??= match;
            last = match;
            int relation = current is { } anchor
                ? match.StartRow != anchor.StartRow ? match.StartRow.CompareTo(anchor.StartRow) :
                    match.StartOffset.CompareTo(anchor.StartOffset)
                : 0;
            if (!backwards && (current is null || relation > 0)) return match;
            if (backwards && relation < 0) previous = match;
        }
        return backwards ? previous ?? last : first;
    }

    public IEnumerable<TerminalMatch> FindMatches(string query, bool matchCase,
        CancellationToken cancellationToken = default)
    {
        if (query.Length is 0 or > 1024 || query.Contains('\n') || query.Contains('\r') || IsAlternateScreen)
            yield break;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string carry = "";
        for (int row = 0; row < Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = carry + rows[row].Text;
            int search = 0;
            while (search <= text.Length - query.Length)
            {
                int offset = text.IndexOf(query, search, comparison);
                if (offset < 0) break;
                int end = offset + query.Length;
                int startRow = row;
                int startOffset = offset - carry.Length;
                while (startOffset < 0)
                    startOffset += rows[--startRow].Text.Length;
                yield return new TerminalMatch(startRow, startOffset, row, end - carry.Length);
                search = offset + 1;
            }

            carry = rows[row].SoftWrapToNext ? text[^Math.Min(text.Length, query.Length - 1)..] : "";
        }
    }

    public TerminalMatch? MapMatchFrom(TerminalSnapshot previous, TerminalMatch match)
    {
        if (IsAlternateScreen != previous.IsAlternateScreen) return null;
        int length = match.EndRow - match.StartRow + 1;
        if (match.StartRow < 0 || length <= 0 || match.EndRow >= previous.Count) return null;
        long anchor = previous.rows[match.StartRow].Id;
        var lookup = LazyInitializer.EnsureInitialized(ref indices, () => rows
            .Select(static (row, index) => (row.Id, index)).ToDictionary(static entry => entry.Id, static entry => entry.index));
        if (lookup.TryGetValue(anchor, out int start) && start + length <= Count)
        {
            bool sameRows = true;
            for (int index = 0; index < length; index++)
            {
                Row now = rows[start + index], old = previous.rows[match.StartRow + index];
                sameRows &= now.Id == old.Id && (index == length - 1 || now.SoftWrapToNext == old.SoftWrapToNext);
            }
            var candidate = match with { StartRow = start, EndRow = start + length - 1 };
            if (sameRows && match.StartOffset <= rows[start].Text.Length &&
                match.EndOffset <= rows[start + length - 1].Text.Length && EqualRange(previous, match, candidate))
                return candidate;
        }
        if (Columns == previous.Columns) return null;
        var first = MapPoint(previous, match.StartRow, match.StartOffset, end: false);
        var last = MapPoint(previous, match.EndRow, match.EndOffset, end: true);
        if (first is null || last is null) return null;
        var remapped = new TerminalMatch(first.Value.Row, first.Value.Offset, last.Value.Row, last.Value.Offset);
        return EqualRange(previous, match, remapped) ? remapped : null;
    }

    public int MapViewportRowFrom(TerminalSnapshot previous, int row)
    {
        if (previous.IsAlternateScreen != IsAlternateScreen || Count == 0) return 0;
        if (row < 0 || row >= previous.Count) return 0;
        var lookup = LazyInitializer.EnsureInitialized(ref indices, () => rows
            .Select(static (entry, index) => (entry.Id, index)).ToDictionary(static entry => entry.Id, static entry => entry.index));
        if (Columns == previous.Columns && lookup.TryGetValue(previous.rows[row].Id, out int index)) return index;
        return MapPoint(previous, row, 0, end: false)?.Row ?? 0;
    }

    private (int Row, int Offset)? MapPoint(TerminalSnapshot previous, int row, int offset, bool end)
    {
        if ((uint)offset > (uint)previous[row].Length) return null;
        var lookup = LazyInitializer.EnsureInitialized(ref logicalIndices, () =>
        {
            var result = new Dictionary<long, int>();
            for (int index = 0; index < rows.Length; index++) result.TryAdd(rows[index].LogicalId, index);
            return result;
        });
        Row old = previous.rows[row];
        long position = old.LogicalOffset + offset;
        if (!lookup.TryGetValue(old.LogicalId, out int target) || position < rows[target].LogicalOffset) return null;
        while (target + 1 < Count && rows[target + 1].LogicalId == old.LogicalId &&
            (position > rows[target].LogicalOffset + rows[target].Text.Length ||
                (!end && position == rows[target].LogicalOffset + rows[target].Text.Length)))
        {
            target++;
        }
        long relative = position - rows[target].LogicalOffset;
        return relative >= 0 && relative <= rows[target].Text.Length ? (target, (int)relative) : null;
    }

    private bool EqualRange(TerminalSnapshot previous, TerminalMatch before, TerminalMatch after)
    {
        using var old = previous.RangePieces(before).GetEnumerator();
        using var current = RangePieces(after).GetEnumerator();
        ReadOnlyMemory<char> left = default, right = default;
        while (true)
        {
            while (left.IsEmpty && old.MoveNext()) left = old.Current;
            while (right.IsEmpty && current.MoveNext()) right = current.Current;
            if (left.IsEmpty || right.IsEmpty) return left.IsEmpty && right.IsEmpty;
            int count = Math.Min(left.Length, right.Length);
            if (!left.Span[..count].SequenceEqual(right.Span[..count])) return false;
            left = left[count..];
            right = right[count..];
        }
    }

    private IEnumerable<ReadOnlyMemory<char>> RangePieces(TerminalMatch range)
    {
        for (int row = range.StartRow; row <= range.EndRow; row++)
        {
            int first = row == range.StartRow ? range.StartOffset : 0;
            int end = row == range.EndRow ? range.EndOffset : this[row].Length;
            if (end > first) yield return this[row].AsMemory(first, end - first);
            if (row < range.EndRow && !rows[row].SoftWrapToNext) yield return "\n".AsMemory();
        }
    }
}
