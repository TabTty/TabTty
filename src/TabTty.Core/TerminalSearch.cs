namespace TabTty.Core;

internal static class TerminalSearch
{
    internal const int Limit = 10_000;

    internal static async Task<(int Count, TerminalMatch[] Matches)> ScanAsync(TerminalSnapshot snapshot,
        string query, bool matchCase, TerminalMatch? anchor, bool backwards, (int First, int End)[] viewports,
        Func<TerminalMatch?, Task>? navigate, CancellationToken token)
    {
        var visible = new List<TerminalMatch>();
        var offscreen = new List<TerminalMatch>();
        int count = 0;
        foreach (TerminalMatch match in Scan())
        {
            if (navigate is not null)
            {
                await navigate(match).ConfigureAwait(false);
                navigate = null;
            }
            if (count <= Limit) count++;
            if (viewports.Any(view => match.StartRow < view.End && match.EndRow >= view.First))
            {
                if (visible.Count == Limit) break;
                if (visible.Count + offscreen.Count == Limit) offscreen.RemoveAt(offscreen.Count - 1);
                visible.Add(match);
            }
            else if (visible.Count + offscreen.Count < Limit) offscreen.Add(match);
        }
        if (navigate is not null) await navigate(null).ConfigureAwait(false);
        return (count, [.. visible, .. offscreen]);

        IEnumerable<TerminalMatch> Scan()
        {
            var all = backwards ? snapshot.FindMatchesBackwards(query, matchCase, token)
                : snapshot.FindMatches(query, matchCase, token);
            if (navigate is null || anchor is not { } from)
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
}
