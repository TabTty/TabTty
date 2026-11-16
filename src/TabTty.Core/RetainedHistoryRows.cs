using System.Diagnostics;
using static TabTty.Core.TerminalHistory;

namespace TabTty.Core;

// The archive owns no text. Its leaves summarize the rows already retained by the model.
// The mutable page stays small (at most 512 rows) and is summarized separately.
internal sealed class RetainedHistoryRows
{
    private readonly TerminalRow?[] archive;
    private readonly HistoryProof[] tree;
    private readonly Dictionary<long, (long First, long Last)> lines = [];
    private readonly ResourceReservation memory;
    private readonly int capacity;
    private long head, tail;
    private LinkedListNode<TerminalRow>? next;
    private List<TerminalRow> page = [];
    private readonly Dictionary<long, (int First, int Last)> pageLines = new(512);
    private readonly Dictionary<TerminalRow, (long Id, long Offset, bool Wrap, HistoryProof Proof)> pageProofs = [];
    public int Length => Count + page.Count;
    private int Count => checked((int)(tail - head));
    public TerminalRow this[int index] => index < Count ? archive[(int)((head + index) % capacity)]! : page[index - Count];
    public TerminalRow this[Index index] => this[index.GetOffset(Length)];

    internal RetainedHistoryRows(int limit, LinkedList<TerminalRow> rows)
    {
        capacity = 1;
        while (capacity < limit) capacity <<= 1;
        memory = WorkQuota.Reserve(capacity * 120L + limit * 128L + 65_536);
        archive = new TerminalRow[capacity];
        tree = new HistoryProof[capacity * 2];
        Array.Fill(tree, HistoryProof.Empty);
        next = rows.First;
    }

    internal void Prepare(List<TerminalRow> live, bool allowBuild, bool trackClusters)
    {
        if (next is not null)
        {
            if (!allowBuild) throw new HistoryException("busy");
            long started = Stopwatch.GetTimestamp();
            while (next is { } node)
            {
                AppendIndexed(node.Value);
                next = node.Next;
                if (next is not null && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 10)
                    throw new HistoryException("busy");
            }
        }
        page = live;
        TerminalRow? previous = Count == 0 ? null : this[Count - 1];
        pageLines.Clear();
        for (int i = 0; i < page.Count; i++)
        {
            TerminalRow row = page[i];
            Normalize(row, previous, trackClusters);
            previous = row;
            if (pageLines.TryGetValue(row.LogicalId, out var bounds))
                pageLines[row.LogicalId] = (bounds.First, i);
            else pageLines.Add(row.LogicalId, (i, i));
        }
        if (pageProofs.Count > 1024) pageProofs.Clear();
    }

    internal void Append(TerminalRow row)
    {
        if (next is null) AppendIndexed(row);
    }

    private void AppendIndexed(TerminalRow row)
    {
        TerminalRow? previous = Count == 0 ? null : this[Count - 1];
        Normalize(row, previous, trackClusters: true);
        HistoryProof proof = Whole(row);
        if (lines.TryGetValue(row.LogicalId, out var positions))
            lines[row.LogicalId] = (positions.First, tail);
        else lines.Add(row.LogicalId, (tail, tail));
        int slot = (int)(tail++ % capacity);
        archive[slot] = row;
        Set(slot, proof);
    }

    internal void Remove(LinkedListNode<TerminalRow> node)
    {
        if (Count == 0)
        {
            if (ReferenceEquals(next, node)) next = node.Next;
            return;
        }
        int slot = (int)(head++ % capacity);
        TerminalRow row = archive[slot]!;
        var positions = lines[row.LogicalId];
        if (positions.Last < head) lines.Remove(row.LogicalId);
        else lines[row.LogicalId] = (head, positions.Last);
        archive[slot] = null;
        Set(slot, HistoryProof.Empty);
        pageProofs.Remove(row);
    }

    internal void Dispose() => memory.Dispose();

    private static void Normalize(TerminalRow row, TerminalRow? previous, bool trackClusters)
    {
        if (previous is null) return;
        row.LogicalId = previous.SoftWrapToNext ? previous.LogicalId : row.Id;
        row.LogicalOffset = previous.SoftWrapToNext ? previous.LogicalOffset + previous.GetText().Length : 0;
        if (trackClusters) row.LogicalClusterOffset = previous.SoftWrapToNext
            ? previous.LogicalClusterOffset + previous.TextClusterCount() : 0;
    }

    internal (int First, int Last)? Line(long id)
    {
        int first = -1, last = -1;
        if (lines.TryGetValue(id, out var positions))
            (first, last) = ((int)(positions.First - head), (int)(positions.Last - head));
        if (pageLines.TryGetValue(id, out var live))
        {
            if (first < 0) first = Count + live.First;
            last = Count + live.Last;
        }
        return first < 0 ? null : (first, last);
    }

    internal HistoryProof Aggregate(int start, int end)
    {
        HistoryProof result = HistoryProof.Empty;
        int archived = Count;
        if (start < archived)
        {
            int count = Math.Min(end, archived) - start;
            int left = (int)((head + start) % capacity);
            int contiguous = Math.Min(count, capacity - left);
            result = Query(left, left + contiguous);
            if (contiguous < count) result = result.Append(Query(0, count - contiguous));
            start += count;
        }
        for (int i = start; i < end; i++)
        {
            TerminalRow row = this[i];
            if (!pageProofs.TryGetValue(row, out var cached) || cached.Id != row.Id ||
                cached.Offset != row.HistoryRevision || cached.Wrap != row.SoftWrapToNext)
                pageProofs[row] = cached = (row.Id, row.HistoryRevision, row.SoftWrapToNext, Whole(row));
            result = result.Append(cached.Proof);
        }
        return result;
    }

    private static HistoryProof Whole(TerminalRow row)
    {
        var content = row.TextFingerprint();
        if (!row.SoftWrapToNext) content = content.Append(TerminalTextFingerprint.LineBreak);
        return new(content, row.ContentRevision(0, row.GetText().Length));
    }

    private void Set(int slot, HistoryProof value)
    {
        int index = capacity + slot;
        tree[index] = value;
        while ((index >>= 1) != 0) tree[index] = tree[index * 2].Append(tree[index * 2 + 1]);
    }

    private HistoryProof Query(int start, int end)
    {
        HistoryProof left = HistoryProof.Empty, right = HistoryProof.Empty;
        for (int l = capacity + start, r = capacity + end; l < r; l >>= 1, r >>= 1)
        {
            if ((l & 1) != 0) left = left.Append(tree[l++]);
            if ((r & 1) != 0) right = tree[--r].Append(right);
        }
        return left.Append(right);
    }
}
