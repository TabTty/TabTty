using System.Diagnostics;
using System.Text;

namespace TabTty.Core;

internal static class TerminalHistory
{
    private const int KiB = 1024;
    internal const int PageOutputBudget = 384 * KiB;
    internal sealed class HistoryException(string code) : Exception(code);
    internal static readonly ResourceQuota WorkQuota = new(64 * 1024 * 1024, collectOnPressure: false);
    internal readonly record struct HistoryProof(TerminalTextFingerprint Content, long Revision)
    {
        internal static HistoryProof Empty => new(TerminalTextFingerprint.Empty, 0);
        internal HistoryProof Append(HistoryProof suffix) =>
            new(Content.Append(suffix.Content), Math.Max(Revision, suffix.Revision));
    }
    internal sealed record Point(long Line, long Offset);
    internal sealed record Span(Point Start, Point End, HistoryProof Proof, bool EndBoundary, int Rows, int Bytes);
    internal sealed record HistoryCursor(Span Range, Point Position);
    internal sealed record Fragment(long logical_line_id, long cluster_offset, long content_offset, string text,
        bool line_end, bool continues_before, bool continues_after, bool retained_prefix_missing, bool retained_suffix_missing);
    internal sealed record HistoryPage(AiTerminalState State, Fragment[] Fragments, Span? Range, Span? Earlier,
        Point? Next)
    {
        internal Span[] Dependencies => new[] { Range, Earlier }.OfType<Span>().ToArray();
    }
    internal sealed record SearchCursor(string Query, bool CaseSensitive, int Limit, int Context, Point Position,
        Point End, HistoryProof RemainingProof);
    internal sealed record SearchMatch(string Text, Point Position, Span Context);
    internal sealed record SearchPage(AiTerminalState State, SearchMatch[] Matches, Span? Scanned,
        Span? Covered, SearchCursor Start, SearchCursor? Next)
    {
        internal Span[] Dependencies => Matches.Select(match => match.Context)
            .Concat(new[] { Covered, Next is { } cursor ? new Span(cursor.Position, cursor.End, cursor.RemainingProof, false, 200, 64 * KiB) : null }.OfType<Span>())
            .Distinct().ToArray();
    }
    internal static HistoryPage Read(TerminalOutput model, AiTerminalState state, Span? requested,
        Point? position, int rowLimit, int byteLimit, int outputBudget, CancellationToken token)
    {
        var history = model.GetHistoryRows(trackClusters: true);
        if (history.Length == 0)
        {
            if (requested is not null) throw new HistoryException("range_evicted");
            return new HistoryPage(state, [], null, null, null);
        }
        if (requested is null)
        {
            var range = Tail(history, history.Length - 1, history[^1].GetText().Length, rowLimit, byteLimit, token);
            if (range.Start == range.End && history[^1].GetText().Length != 0)
                throw new HistoryException("resource_limit");
            return BuildHistoryPage(history, state, range, range.Start, rowLimit, byteLimit, outputBudget, token);
        }
        Verify(history, requested, token);
        return BuildHistoryPage(history, state, requested, position ?? requested.Start, rowLimit, byteLimit, outputBudget, token);
    }

    internal static string? Validate(TerminalOutput model, Span[] ranges, CancellationToken token)
    {
        try
        {
            long started = Stopwatch.GetTimestamp();
            var rows = model.GetHistoryRows(allowBuild: false);
            foreach (var range in ranges) Verify(rows, range, token, started);
            return null;
        }
        catch (HistoryException error) { return error.Message; }
    }

    private static HistoryPage BuildHistoryPage(RetainedHistoryRows rows, AiTerminalState state, Span range, Point start,
        int rowLimit, int byteLimit, int outputBudget, CancellationToken token)
    {
        var (first, offset) = Find(rows, start, false);
        var (last, end) = Find(rows, range.End, true);
        var fragments = new List<Fragment>();
        int bytes = 0, serialized = 0;
        Point? next = null;
        for (int index = first; index <= last; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = rows[index];
            string text = row.GetText();
            int from = index == first ? offset : 0, to = index == last ? end : text.Length;
            if (fragments.Count == rowLimit || serialized + 640 > outputBudget)
            {
                next = At(row, from);
                break;
            }
            int cut = from;
            while (cut < to)
            {
                int length = Unicode17.NextGraphemeLength(text.AsSpan(cut, to - cut));
                int count = Encoding.UTF8.GetByteCount(text.AsSpan(cut, length));
                int json = MirroredTextCost(text.AsSpan(cut, length));
                if (bytes + count > byteLimit || serialized + json + 640 > outputBudget)
                {
                    if (cut == from && fragments.Count == 0) throw new HistoryException("resource_limit");
                    break;
                }
                bytes += count;
                serialized += json;
                cut += length;
            }
            bool endOfLine = cut == text.Length && (index < last && !row.SoftWrapToNext ||
                index == last && range.EndBoundary);
            if (cut != from || to == from)
                fragments.Add(new Fragment(row.LogicalId, row.LogicalClusterOffset + TerminalRow.CountClusters(text.AsSpan(0, from)),
                    row.LogicalOffset + from,
                    text[from..cut], endOfLine, row.LogicalOffset + from > 0, cut < text.Length || row.SoftWrapToNext,
                    rows[0].LogicalId == row.LogicalId && rows[0].LogicalOffset > 0,
                    index == rows.Length - 1 && row.SoftWrapToNext));
            serialized += 640;
            if (cut != to) { next = At(row, cut); break; }
        }
        var (startRow, startOffset) = Find(rows, range.Start, false);
        Span? earlier = startOffset > 0 || startRow > 0
            ? Tail(rows, startOffset == 0 ? startRow - 1 : startRow,
                startOffset == 0 ? rows[startRow - 1].GetText().Length : startOffset, rowLimit, byteLimit, token) : null;
        return new HistoryPage(state, fragments.ToArray(), range, earlier, next);
    }

    private static Span Tail(RetainedHistoryRows rows, int last, int end, int rowLimit, int byteLimit, CancellationToken token)
    {
        int first = last, offset = end, bytes = 0;
        for (int index = last; index >= 0 && last - index < rowLimit; index--)
        {
            token.ThrowIfCancellationRequested();
            var text = rows[index].GetText();
            int limit = index == last ? end : text.Length;
            int count = Encoding.UTF8.GetByteCount(text.AsSpan(0, limit));
            if (bytes + count <= byteLimit)
            {
                (first, offset, bytes) = (index, 0, bytes + count);
                continue;
            }
            int start = 0;
            while (start < limit && bytes + count > byteLimit)
            {
                int length = Unicode17.NextGraphemeLength(text.AsSpan(start, limit - start));
                count -= Encoding.UTF8.GetByteCount(text.AsSpan(start, length));
                start += length;
            }
            if (start < limit) (first, offset) = (index, start);
            break;
        }
        return MakeSpan(rows, At(rows[first], offset), At(rows[last], end), rowLimit, byteLimit, token);
    }

    private static Point At(TerminalRow row, int offset) => new(row.LogicalId, row.LogicalOffset + offset);

    private static (int Index, int Offset) Find(RetainedHistoryRows rows, Point point, bool end)
    {
        if (rows.Line(point.Line) is not { } bounds) throw new HistoryException("range_evicted");
        if (point.Offset < rows[bounds.First].LogicalOffset) throw new HistoryException("range_evicted");
        int low = bounds.First, high = bounds.Last;
        while (low < high)
        {
            int middle = (low + high) / 2;
            var row = rows[middle];
            long stop = row.LogicalOffset + row.GetText().Length;
            if (stop < point.Offset || !end && stop == point.Offset) low = middle + 1;
            else high = middle;
        }
        var found = rows[low];
        long length = point.Offset - found.LogicalOffset;
        if (length < 0 || length > found.GetText().Length) throw new HistoryException("range_changed");
        return (low, (int)length);
    }

    private static Span MakeSpan(RetainedHistoryRows rows, Point start, Point end, int rowLimit, int byteLimit,
        CancellationToken token, long searchStarted = 0)
    {
        var (last, offset) = Find(rows, end, true);
        bool boundary = last + 1 < rows.Length && offset == rows[last].GetText().Length && !rows[last].SoftWrapToNext;
        return new(start, end, Proof(rows, start, end, token, searchStarted), boundary, rowLimit, byteLimit);
    }

    private static void Verify(RetainedHistoryRows rows, Span range, CancellationToken token, long searchStarted = 0)
    {
        if (range.EndBoundary)
        {
            var (last, offset) = Find(rows, range.End, true);
            if (last + 1 == rows.Length || rows[last].SoftWrapToNext || offset != rows[last].GetText().Length)
                throw new HistoryException("range_changed");
        }
        if (range.Proof != Proof(rows, range.Start, range.End, token, searchStarted))
            throw new HistoryException("range_changed");
    }

    private static HistoryProof Proof(RetainedHistoryRows rows, Point start, Point end, CancellationToken token,
        long searchStarted = 0)
    {
        token.ThrowIfCancellationRequested();
        if (searchStarted != 0 && Stopwatch.GetElapsedTime(searchStarted).TotalMilliseconds >= 100)
            throw new HistoryException("busy");
        var (first, from) = Find(rows, start, false);
        var (last, to) = Find(rows, end, true);
        if (first > last && start == end) return HistoryProof.Empty;
        if (first > last || first == last && from > to) throw new HistoryException("range_changed");
        if (first == last) return FragmentProof(rows[first], from, to, false);
        return FragmentProof(rows[first], from, rows[first].GetText().Length, !rows[first].SoftWrapToNext)
            .Append(rows.Aggregate(first + 1, last))
            .Append(FragmentProof(rows[last], 0, to, false));

        static HistoryProof FragmentProof(TerminalRow row, int from, int to, bool boundary)
        {
            string text = row.GetText();
            var content = from == 0 && to == text.Length ? row.TextFingerprint() :
                TerminalTextFingerprint.FromText(text.AsSpan(from, to - from));
            if (boundary) content = content.Append(TerminalTextFingerprint.LineBreak);
            return new(content, row.ContentRevision(from, to));
        }
    }


    internal sealed record SearchPart(Point Start, string Text, int Owned, Point End);
    internal sealed record SearchWindow(Point Start, Point End,
        SearchPart[] Parts, Span? Dependency, HistoryProof Remaining, long Started);
    internal sealed record LocatedMatch(string Text, Point Position);
    internal sealed record ScanResult(LocatedMatch[] Matches, Point Progress);

    internal static SearchWindow CaptureSearch(TerminalOutput model, SearchCursor? cursor, string query, CancellationToken token, long started)
    {
        var rows = model.GetHistoryRows();
        Point start = cursor?.Position ?? At(rows[0], 0);
        Point end = cursor?.End ?? At(rows[^1], rows[^1].GetText().Length);
        if (cursor is not null && Proof(rows, start, end, token, started) != cursor.RemainingProof)
            throw new HistoryException("range_changed");
        var (first, offset) = Find(rows, start, false);
        var (last, endOffset) = Find(rows, end, true);
        var parts = new List<SearchPart>();
        Point progress = start, covered = start, partStart = start;
        var partText = new StringBuilder();
        int bytes = 0;
        bool open = false;
        for (int index = first; index <= last && index - first < 1024; index++)
        {
            token.ThrowIfCancellationRequested();
            if (index > first && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 20) break;
            TerminalRow row = rows[index];
            string text = row.GetText();
            int from = index == first ? offset : 0, to = index == last ? endOffset : text.Length;
            int cut = from;
            while (cut < to)
            {
                int length = Unicode17.NextGraphemeLength(text.AsSpan(cut, to - cut));
                int size = Encoding.UTF8.GetByteCount(text.AsSpan(cut, length));
                if (size > 256 * KiB - bytes) break;
                bytes += size;
                cut += length;
            }
            if (cut == from && cut != to) break;
            if (!open)
            {
                partStart = At(row, from);
                open = true;
            }
            partText.Append(text.AsSpan(from, cut - from));
            covered = At(row, cut);
            progress = cut < to ? covered : index == last ? end : At(rows[index + 1], 0);
            if (cut == to && (!row.SoftWrapToNext || index == last))
            {
                parts.Add(new(partStart, partText.ToString(), partText.Length, progress));
                partText.Clear();
                open = false;
            }
            if (cut < to || bytes == 256 * KiB) break;
        }
        if (open)
        {
            int owned = partText.Length;
            var (index, at) = Find(rows, covered, false);
            while (index <= last && rows[index].LogicalId == partStart.Line &&
                partText.Length - owned < query.Length - 1)
            {
                TerminalRow row = rows[index];
                string text = row.GetText();
                int available = index == last ? endOffset : text.Length;
                int begin = at;
                while (at < available && partText.Length - owned + at - begin < query.Length - 1)
                    at += Unicode17.NextGraphemeLength(text.AsSpan(at, available - at));
                partText.Append(text.AsSpan(begin, at - begin));
                covered = At(row, at);
                if (!row.SoftWrapToNext) break;
                index++;
                at = 0;
            }
            parts.Add(new(partStart, partText.ToString(), owned, progress));
        }
        Span? dependency = start == covered ? null : MakeSpan(rows, start, covered, 200, 64 * KiB, token, started);
        return new(start, end, parts.ToArray(), dependency,
            Proof(rows, start, end, token, started), started);
    }

    internal static ScanResult Scan(SearchWindow window, string query, bool caseSensitive, int limit, CancellationToken token,
        int outputBudget = PageOutputBudget)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<LocatedMatch>();
        Point progress = window.Start;
        int resultBytes = 0;
        var slice = Stopwatch.GetTimestamp();
        foreach (var part in window.Parts)
        {
            token.ThrowIfCancellationRequested();
            for (int search = 0; search < part.Owned;)
            {
                int found = part.Text.IndexOf(query, search, comparison);
                if (found < 0 || found >= part.Owned) break;
                var position = new Point(part.Start.Line, part.Start.Offset + found);
                int size = MirroredTextCost(part.Text.AsSpan(found, query.Length)) + 512;
                if (matches.Count == limit || resultBytes + size > outputBudget ||
                    matches.Count != 0 && SliceExpired())
                    return new(matches.ToArray(), position);
                matches.Add(new(part.Text.Substring(found, query.Length), position));
                resultBytes += size;
                search = found + 1;
            }
            progress = part.End;
            if (SliceExpired()) break;
        }
        return new(matches.ToArray(), progress);

        bool SliceExpired() => Stopwatch.GetElapsedTime(slice).TotalMilliseconds >= 20 ||
            Stopwatch.GetElapsedTime(window.Started).TotalMilliseconds >= 100;
    }

    internal static SearchPage CompleteSearch(TerminalOutput model, AiTerminalState state, SearchWindow window,
        ScanResult result, string query, bool caseSensitive, int limit, int context, CancellationToken token)
    {
        var rows = model.GetHistoryRows(allowBuild: false);
        if (window.Dependency is { } dependency) Verify(rows, dependency, token);
        if (Proof(rows, window.Start, window.End, token) != window.Remaining)
            throw new HistoryException("range_changed");
        SearchMatch[] matches = result.Matches.Select(match =>
        {
            int index = Find(rows, match.Position, false).Index;
            var (left, right) = rows.Line(rows[index].LogicalId)!.Value;
            for (int i = 0; i < context && left > 0; i++)
                left = rows.Line(rows[left - 1].LogicalId)!.Value.First;
            for (int i = 0; i < context && right + 1 < rows.Length; i++)
                right = rows.Line(rows[right + 1].LogicalId)!.Value.Last;
            return new SearchMatch(match.Text, match.Position, MakeSpan(rows, At(rows[left], 0),
                At(rows[right], rows[right].GetText().Length), 200, 64 * KiB, token));
        }).ToArray();
        Span? scanned = result.Progress == window.Start ? null :
            MakeSpan(rows, window.Start, result.Progress, 200, 64 * KiB, token);
        SearchCursor? next = result.Progress == window.End ? null :
            new(query, caseSensitive, limit, context, result.Progress, window.End,
                Proof(rows, result.Progress, window.End, token));
        var start = new SearchCursor(query, caseSensitive, limit, context, window.Start, window.End, window.Remaining);
        return new(state, matches, scanned, window.Dependency, start, next);
    }
    private static int MirroredTextCost(ReadOnlySpan<char> text)
    {
        int size = 0;
        foreach (char value in text)
        {
            // The result includes a structured string and the serialized JSON string.
            size += value switch
            {
                '\\' => 6,
                '"' => 12,
                >= ' ' and <= '~' when value is not ('<' or '>' or '&' or '\'' or '+' or '`') => 2,
                _ => 16,
            };
        }
        return size;
    }
}
