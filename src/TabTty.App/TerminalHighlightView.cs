using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TerminalHighlightView
{
    internal static SemaphoreSlim Workers { get; } = new(2);
    private readonly Dictionary<TerminalSurface, (Action<bool> Viewport, Action Presentation)> surfaces = [];
    private readonly DispatcherTimer timer;
    private readonly TerminalHighlightState state;
    private TerminalSnapshot? snapshot;
    private TerminalHighlightRules? rules;
    private CancellationTokenSource? work;
    private List<LineResult> cached = [];
    private Dictionary<int, ReadOnlyMemory<TerminalHighlightSpan>> rowOverlays = [];
    private (int First, int End)[] visible = [];
    private long lastOutput;
    private long evaluationRevision;
    private long projectAfter, retryMatchAfter;
    private bool pending;
    private bool immediate;

    public TerminalHighlightView(Dispatcher dispatcher, TerminalHighlightState state)
    {
        this.state = state;
        state.Changed += RulesChanged;
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        RulesChanged(HighlightChange.Rules);
        timer.Tick += async (_, _) => await UpdateAsync();
    }

    internal void Attach(TerminalSurface surface)
    {
        Action<bool> viewport = automatic => UpdateScope(immediately: !automatic);
        Action presentation = () => UpdateScope(immediately: true);
        surfaces.Add(surface, (viewport, presentation));
        surface.ViewportChanged += viewport;
        surface.PresentationChanged += presentation;
        surface.SetHighlights(rowOverlays);
        UpdateScope(immediately: true);
    }

    internal void Detach(TerminalSurface surface)
    {
        if (!surfaces.Remove(surface, out var callbacks)) return;
        surface.ViewportChanged -= callbacks.Viewport;
        surface.PresentationChanged -= callbacks.Presentation;
        surface.SetHighlights(new Dictionary<int, ReadOnlyMemory<TerminalHighlightSpan>>());
        UpdateScope(immediately: true);
    }

    public void Detach()
    {
        state.Changed -= RulesChanged;
        foreach (var surface in surfaces.Keys.ToArray()) Detach(surface);
        ResetForSession();
    }

    internal void ResetForSession()
    {
        evaluationRevision++;
        timer.Stop();
        work?.Cancel();
        pending = immediate = false;
        projectAfter = retryMatchAfter = 0;
        snapshot = null;
        visible = [];
        ReplaceCache([]);
        Publish();
    }

    private void RulesChanged(HighlightChange change)
    {
        rules = state.Matcher;
        if (change == HighlightChange.Styles)
        {
            foreach (var line in cached)
                if (line.Projection is { } projection) projection.UpdateStyles(state);
            foreach (var surface in surfaces.Keys) surface.SetHighlights(rowOverlays);
            return;
        }
        if (change == HighlightChange.Rules)
        {
            evaluationRevision++;
            work?.Cancel();
            projectAfter = retryMatchAfter = 0;
            ReplaceCache([]);
            Queue(immediately: true);
        }
        else if (change == HighlightChange.Suspension)
        {
            foreach (var line in cached)
                line.Projection?.RemoveSuspended(state.Suspended);
            Queue(immediately: true);
        }
        Publish();
    }

    public void SetSnapshot(TerminalSnapshot updated)
    {
        var previous = snapshot;
        bool textChanged = rules is { IsEmpty: false } && (previous is null || !updated.HasSameHighlightText(previous));
        if (textChanged && previous is not null && cached.Count > 0)
            ReplaceCache(Remap(updated, previous, cached));
        snapshot = updated;
        if (textChanged)
        {
            lastOutput = Environment.TickCount64;
            Queue(immediately: false);
        }
        UpdateScope(immediately: false, textChanged);
    }

    private void UpdateScope(bool immediately, bool textChanged = false)
    {
        if (snapshot is not { } view) return;
        var ranges = new List<(int First, int End)>();
        foreach (var viewport in surfaces.Keys.Where(surface => !view.IsAlternateScreen && surface.IsPresentationActive)
            .Select(surface => surface.VisibleRows()).Where(range => range.First < range.End).OrderBy(range => range.First))
        {
            int first = Math.Min(viewport.First, view.Count);
            int end = Math.Min(viewport.End, view.Count);
            while (first > 0 && view.IsSoftWrapped(first - 1)) first--;
            while (end > 0 && end < view.Count && view.IsSoftWrapped(end - 1)) end++;
            if (first >= end) continue;
            if (ranges.Count != 0 && first <= ranges[^1].End)
                ranges[^1] = (ranges[^1].First, Math.Max(ranges[^1].End, end));
            else ranges.Add((first, end));
        }
        bool scopeChanged = !ranges.SequenceEqual(visible);
        if (scopeChanged) visible = ranges.ToArray();
        bool evicted = cached.Any(line => !InScope(line));
        if (evicted || scopeChanged && visible.Length == 0)
        {
            work?.Cancel();
            ReplaceCache(cached.Where(InScope).ToList());
        }
        if (scopeChanged || evicted || textChanged) Publish();
        if (scopeChanged || evicted) Queue(immediately);
    }

    private bool InScope(LineResult line) => visible.Any(range =>
        line.Anchor.StartRow < range.End && line.Anchor.EndRow >= range.First);

    private void Queue(bool immediately)
    {
        if (snapshot is null || rules is null || rules.IsEmpty || visible.Length == 0) return;
        pending = true;
        immediate |= immediately;
        timer.Start();
    }

    private async Task UpdateAsync()
    {
        if (work is not null) return;
        long now = Environment.TickCount64;
        if (projectAfter != 0 && now >= projectAfter) Publish();
        if (retryMatchAfter != 0 && now >= retryMatchAfter)
        {
            pending = true;
            retryMatchAfter = 0;
        }
        if (!pending || snapshot is not { } captured ||
            rules is not { IsEmpty: false } matcher || visible.Length == 0)
        {
            if (projectAfter == 0 && retryMatchAfter == 0) timer.Stop();
            return;
        }
        if (!immediate && now - lastOutput < 500) return;
        pending = false;
        immediate = false;
        if (projectAfter == 0 && retryMatchAfter == 0) timer.Stop();
        using var cancellation = new CancellationTokenSource();
        work = cancellation;
        var token = cancellation.Token;
        var scope = visible;
        long revision = evaluationRevision;
        var calculatedResults = new List<LineResult>();
        var excluded = new HashSet<Guid>(state.Suspended);
        var timeouts = new HashSet<Guid>();
        bool retryMatch = false;
        var existing = cached.ToDictionary(line => line.Anchor.StartRow, line => line.Anchor.EndRow);
        try
        {
            foreach (var (first, end) in scope)
            for (int row = first; row < end;)
            {
                await Workers.WaitAsync(token);
                try
                {
                    int start = row;
                    row = await Task.Run(() =>
                    {
                        long slice = Stopwatch.GetTimestamp();
                        do
                        {
                            if (existing.TryGetValue(start, out int last))
                                start = last + 1;
                            else
                            {
                                LineResult calculated = MatchLine(captured, start, matcher, excluded, token, rule =>
                                {
                                    timeouts.Add(rule);
                                    excluded.Add(rule);
                                });
                                if (calculated.Memory is not null) calculatedResults.Add(calculated);
                                retryMatch |= calculated.ResourceLimited;
                                start = calculated.Anchor.EndRow + 1;
                            }
                        }
                        while (start < end && Stopwatch.GetElapsedTime(slice).TotalMilliseconds < 20);
                        return start;
                    }, token);
                }
                finally { Workers.Release(); }
            }
            if (token.IsCancellationRequested || revision != evaluationRevision || snapshot is not { } latest) return;
            var updated = ReferenceEquals(latest, captured) ? calculatedResults : Remap(latest, captured, calculatedResults);
            var merged = cached.ToDictionary(line => line.Anchor.StartRow);
            foreach (LineResult line in updated)
                if (InScope(line)) merged.TryAdd(line.Anchor.StartRow, line);
            ReplaceCache(merged.Values.ToList());
            if (retryMatch)
            {
                retryMatchAfter = Environment.TickCount64 + 500;
                timer.Start();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            var retained = cached.Select(line => line.Memory).ToHashSet();
            foreach (var line in calculatedResults)
                if (!retained.Contains(line.Memory)) line.Memory!.Dispose();
            work = null;
            // A viewport can leave after a regex times out, while the session and matcher still own that timeout.
            if (revision == evaluationRevision && !state.Suspend(timeouts) && !token.IsCancellationRequested)
                Publish();
        }
    }

    private void ReplaceCache(List<LineResult> next)
    {
        var retained = next.Select(line => line.Memory).ToHashSet();
        var projections = next.Select(line => line.Projection).ToHashSet();
        foreach (var line in cached)
        {
            if (!retained.Contains(line.Memory)) line.Memory?.Dispose();
            if (!projections.Contains(line.Projection)) line.Projection?.Memory.Dispose();
        }
        cached = next;
    }

    private static LineResult MatchLine(TerminalSnapshot snapshot, int first, TerminalHighlightRules matcher,
        HashSet<Guid> excluded, CancellationToken token, Action<Guid> onTimeout)
    {
        ResourceReservation? memory;
        try { memory = ApplicationResources.SearchCache.Reserve(65536 * 4 + 4096 * 128 + 256); }
        catch (ResourceLimitException) { memory = null; }
        var text = new StringBuilder();
        int last = first;
        bool limit = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string row = snapshot[last];
                limit |= row.Length > 65536 - text.Length;
                if (!limit && memory is not null) text.Append(row);
                if (last + 1 == snapshot.Count || !snapshot.IsSoftWrapped(last)) break;
                last++;
            }
            var anchor = new TerminalMatch(first, 0, last, snapshot[last].Length);
            if (limit || memory is null) return new LineResult(anchor, [], null, ResourceLimited: !limit);
            TerminalHighlightResult matches = matcher.MatchWithRuleIds(text.ToString(), excluded, token, onTimeout);
            if (matches.LimitReached) return new LineResult(anchor, [], null);
            memory.Resize(128 + matches.Spans.LongLength * 128);
            var result = new LineResult(anchor, matches.Spans, memory);
            memory = null;
            return result;
        }
        finally { memory?.Dispose(); }
    }

    private static List<LineResult> Remap(TerminalSnapshot latest, TerminalSnapshot previous,
        IEnumerable<LineResult> old)
    {
        var result = new List<LineResult>();
        foreach (var line in old)
        {
            if (latest.MapMatchFrom(previous, line.Anchor) is not { } match || match.StartOffset != 0 ||
                match.EndOffset != latest[match.EndRow].Length ||
                match.StartRow > 0 && latest.IsSoftWrapped(match.StartRow - 1) ||
                match.EndRow + 1 < latest.Count && latest.IsSoftWrapped(match.EndRow)) continue;
            bool sameRows = line.Anchor.EndRow - line.Anchor.StartRow == match.EndRow - match.StartRow;
            for (int offset = 0; sameRows && match.StartRow + offset <= match.EndRow; offset++)
                sameRows = previous[line.Anchor.StartRow + offset].Length == latest[match.StartRow + offset].Length;
            result.Add(line with { Anchor = match, Projection = sameRows ? line.Projection : null });
        }
        return result;
    }

    private void Publish()
    {
        var overlays = new Dictionary<int, ReadOnlyMemory<TerminalHighlightSpan>>();
        bool needsProjection = false;
        long now = Environment.TickCount64;
        if (snapshot is { } view)
        {
            foreach (LineResult line in cached)
            {
                if (line.Spans.Length == 0) continue;
                if (line.Projection is null && now >= projectAfter)
                {
                    try { line.Projection = ProjectLine(view, line); }
                    catch (ResourceLimitException) { }
                }
                if (line.Projection is { } projection)
                {
                    foreach (var row in projection.Rows)
                        if (!row.Spans.IsEmpty) overlays.Add(line.Anchor.StartRow + row.Offset, row.Spans);
                }
                else needsProjection = true;
            }
        }
        if (needsProjection)
        {
            if (now >= projectAfter) projectAfter = now + 500;
            timer.Start();
        }
        else projectAfter = 0;
        rowOverlays = overlays;
        foreach (var surface in surfaces.Keys) surface.SetHighlights(overlays);
    }

    private LineProjection ProjectLine(TerminalSnapshot view, LineResult line)
    {
        int first = line.Anchor.StartRow;
        int rows = line.Anchor.EndRow - first + 1;
        int spanSize = Unsafe.SizeOf<TerminalHighlightSpan>();
        using var staging = ApplicationResources.SearchCache.Reserve((rows + 1) * sizeof(int) + 32L);
        var offsets = new int[rows + 1];
        for (int row = 0; row < rows; row++) offsets[row + 1] = offsets[row] + view[first + row].Length;

        var byRow = new Dictionary<int, List<TerminalHighlightSpan>>();
        foreach (var span in line.Spans)
        {
            if (state.Suspended.Contains(span.RuleId)) continue;
            var style = state.Style(span.RuleId);
            int left = 0, right = rows;
            while (left < right)
            {
                int middle = left + (right - left) / 2;
                if (offsets[middle + 1] <= span.Start) left = middle + 1;
                else right = middle;
            }
            for (int row = left; row < rows && offsets[row] < span.End; row++)
            {
                if (offsets[row] == offsets[row + 1]) continue;
                if (!byRow.TryGetValue(row, out var spans))
                {
                    staging.Add(192);
                    byRow.Add(row, spans = []);
                }
                staging.Add(spanSize * 4L);
                spans.Add(span with { Start = Math.Max(0, span.Start - offsets[row]),
                    End = Math.Min(offsets[row + 1], span.End) - offsets[row], Style = style });
            }
        }
        var projected = ApplicationResources.SearchCache.Reserve(byRow.Values.Sum(spans => 128L + spans.Count * (long)spanSize));
        try { return new LineProjection(byRow.Select(pair => new ProjectedRow(pair.Key, pair.Value.ToArray())).ToArray(), projected); }
        catch { projected.Dispose(); throw; }
    }

    private sealed record LineResult(TerminalMatch Anchor, TerminalHighlightSpan[] Spans, ResourceReservation? Memory,
        bool ResourceLimited = false)
    {
        internal LineProjection? Projection { get; set; }
    }

    private readonly record struct ProjectedRow(int Offset, Memory<TerminalHighlightSpan> Spans);

    private sealed record LineProjection(ProjectedRow[] Rows, ResourceReservation Memory)
    {
        internal void UpdateStyles(TerminalHighlightState state)
        {
            foreach (var row in Rows)
                foreach (ref var span in row.Spans.Span)
                    span = span with { Style = state.Style(span.RuleId) };
        }

        internal void RemoveSuspended(IReadOnlySet<Guid> suspended)
        {
            for (int i = 0; i < Rows.Length; i++)
            {
                var row = Rows[i];
                var spans = row.Spans.Span;
                int kept = 0;
                foreach (var span in spans)
                    if (!suspended.Contains(span.RuleId)) spans[kept++] = span;
                if (kept != spans.Length) Rows[i] = row with { Spans = row.Spans[..kept] };
            }
        }
    }

    internal (long Id, long Revision, ReadOnlyMemory<TerminalHighlightSpan> Spans)? CaptureAiRow(int row)
    {
        if (snapshot is not { } view) return null;
        int index = view.ScreenStartRow + row - 1;
        if (index >= view.Count || !cached.Any(line => InScope(line) && line.Anchor.StartRow <= index && index <= line.Anchor.EndRow &&
            (line.Spans.Length == 0 || line.Projection is not null))) return null;
        var identity = view.DisplayIdentity(index);
        return (identity.Id, identity.Revision, rowOverlays.GetValueOrDefault(index));
    }
}
