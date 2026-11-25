namespace TabTty.Core;

internal static class TerminalSearch
{
    internal const int Limit = 10_000;

    internal static TerminalMatch? FindNext(TerminalSnapshot snapshot, string query, bool matchCase,
        TerminalMatch? anchor, bool backwards, CancellationToken token)
    {
        foreach (var match in OrderedMatches(snapshot, query, matchCase, anchor, backwards, token))
            return match;
        return null;
    }

    internal static (int Count, TerminalMatch[] Matches) Aggregate(TerminalSnapshot snapshot,
        string query, bool matchCase, (int First, int End)[] viewports, CancellationToken token)
    {
        var visible = new List<TerminalMatch>();
        var offscreen = new List<TerminalMatch>();
        int count = 0;
        foreach (TerminalMatch match in snapshot.FindMatches(query, matchCase, token))
        {
            if (count <= Limit) count++;
            if (viewports.Any(view => match.StartRow < view.End && match.EndRow >= view.First))
            {
                if (visible.Count == Limit) break;
                if (visible.Count + offscreen.Count == Limit) offscreen.RemoveAt(offscreen.Count - 1);
                visible.Add(match);
            }
            else if (visible.Count + offscreen.Count < Limit) offscreen.Add(match);
        }
        return (count, [.. visible, .. offscreen]);
    }

    private static IEnumerable<TerminalMatch> OrderedMatches(TerminalSnapshot snapshot, string query, bool matchCase,
        TerminalMatch? anchor, bool backwards, CancellationToken token)
    {
        var all = backwards ? snapshot.FindMatchesBackwards(query, matchCase, token)
            : snapshot.FindMatches(query, matchCase, token);
        if (anchor is not { } from)
        {
            foreach (var match in all) yield return match;
            yield break;
        }

        bool BeyondAnchor(TerminalMatch match)
        {
            int relation = (match.StartRow, match.StartOffset).CompareTo((from.StartRow, from.StartOffset));
            return backwards ? relation < 0 : relation > 0;
        }
        var near = backwards ? snapshot.FindMatchesBackwards(query, matchCase, token, from.StartRow)
            : snapshot.FindMatchesFrom(query, matchCase, from.StartRow, token);
        foreach (var match in near)
            if (BeyondAnchor(match)) yield return match;
        foreach (var match in all)
        {
            if (BeyondAnchor(match)) yield break;
            yield return match;
        }
    }
}
