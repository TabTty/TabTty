using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TerminalHighlightView
{
    internal static SemaphoreSlim Workers { get; } = new(2);
    private readonly TerminalSurface surface;
    private readonly DispatcherTimer timer;
    private readonly TerminalHighlightState state;
    private bool cacheReusable = true;
    private TerminalSnapshot? snapshot;
    private TerminalHighlightRules? rules;
    private CancellationTokenSource? work;
    private List<LineResult> cached = [];
    private Dictionary<int, TerminalHighlightSpan[]> rowOverlays = [];
    private HashSet<int> evaluatedRows = [];
    private (int First, int End) evaluatedViewport;
    private long lastOutput;
    private TerminalSnapshot? evaluated;
    private bool pending;
    private bool immediate;
    private bool activateOnNextSnapshot;
    private (int First, int End) lastViewport;

    public event Action<bool>? Suspended;

    public TerminalHighlightView(TerminalSurface surface, TerminalHighlightState? owner = null)
    {
        this.surface = surface;
        state = owner ?? new TerminalHighlightState();
        state.Changed += RulesChanged;
        timer = new DispatcherTimer(DispatcherPriority.Background, surface.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        RulesChanged(false);
        timer.Tick += async (_, _) => await UpdateAsync();
        surface.ViewportChanged += ViewportChanged;
        surface.PresentationChanged += () =>
        {
            if (surface.IsPresentationActive)
            {
                activateOnNextSnapshot = true;
                Queue(immediately: true);
            }
            else
            {
                timer.Stop();
                work?.Cancel();
            }
        };
    }

    public void SetRules(HighlightSet? updated) => state.SetRules(updated);

    public void Resume() => state.Resume();

    public void Detach()
    {
        state.Changed -= RulesChanged;
        timer.Stop();
        work?.Cancel();
    }

    private void RulesChanged(bool preserveKnownSpans)
    {
        rules = state.Matcher;
        if (preserveKnownSpans)
        {
            cacheReusable = false;
            work?.Cancel();
            evaluated = null;
            Publish();
            Queue(immediately: true);
        }
        else
        {
            cacheReusable = true;
            Reset();
        }
        Suspended?.Invoke(state.Suspended.Count > 0);
    }

    public void SetSnapshot(TerminalSnapshot updated)
    {
        var previous = snapshot;
        bool textChanged = rules is { IsEmpty: false } && (previous is null || !updated.HasSameHighlightText(previous));
        if (textChanged && previous is not null && cached.Count > 0)
            cached = Remap(updated, previous, cached);
        snapshot = updated;
        if (textChanged)
        {
            evaluated = null;
            Publish();
            lastOutput = Environment.TickCount64;
        }
        else if (evaluated is not null && ReferenceEquals(evaluated, previous))
            evaluated = updated;
        if (textChanged || activateOnNextSnapshot) Queue(immediately: activateOnNextSnapshot);
        activateOnNextSnapshot = false;
    }

    private void Reset()
    {
        work?.Cancel();
        cached.Clear();
        evaluated = null;
        Publish();
        Queue(immediately: true);
    }

    private void ViewportChanged(bool automatic)
    {
        var viewport = surface.VisibleRows();
        if (viewport == lastViewport) return;
        lastViewport = viewport;
        if (!automatic) Queue(immediately: true);
    }

    private void Queue(bool immediately)
    {
        if (snapshot is null || rules is null || rules.IsEmpty || !surface.IsPresentationActive) return;
        pending = true;
        immediate |= immediately;
        timer.Start();
    }

    private async Task UpdateAsync()
    {
        if (work is not null) return;
        if (!pending || snapshot is not { } captured ||
            rules is not { IsEmpty: false } matcher || !surface.IsPresentationActive)
        {
            timer.Stop();
            return;
        }
        long now = Environment.TickCount64;
        if (!immediate && now - lastOutput < 500) return;
        pending = false;
        immediate = false;
        timer.Stop();
        using var cancellation = new CancellationTokenSource();
        work = cancellation;
        var token = cancellation.Token;
        var (first, end) = surface.VisibleRows();
        lastViewport = (first, end);
        while (first > 0 && captured.IsSoftWrapped(first - 1)) first--;
        var result = new List<LineResult>();
        var excluded = new HashSet<Guid>(state.Suspended);
        var existing = cacheReusable ? cached.ToDictionary(line => line.Anchor.StartRow) : [];
        try
        {
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
                            if (existing.TryGetValue(start, out LineResult? line))
                            {
                                result.Add(line);
                                start = line.Anchor.EndRow + 1;
                            }
                            else
                            {
                                LineResult calculated = MatchLine(captured, start, matcher, excluded, token);
                                if (calculated.Memory is not null) result.Add(calculated);
                                start = calculated.Anchor.EndRow + 1;
                            }
                        }
                        while (start < end && Stopwatch.GetElapsedTime(slice).TotalMilliseconds < 20);
                        return start;
                    }, token);
                }
                finally { Workers.Release(); }
            }
            if (token.IsCancellationRequested || snapshot is not { } latest) return;
            cached = ReferenceEquals(latest, captured) ? result : Remap(latest, captured, result);
            evaluatedViewport = lastViewport;
            if (state.Suspend(excluded)) return;
            cacheReusable = true;
            evaluated = latest.HasSameHighlightText(captured) ? latest : null;
            Publish();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { work = null; }
    }

    private static LineResult MatchLine(TerminalSnapshot snapshot, int first, TerminalHighlightRules matcher,
        HashSet<Guid> excluded, CancellationToken token)
    {
        ResourceReservation? memory;
        try { memory = ApplicationResources.SearchCache.Reserve(65536 * 4 + 4096 * 128 + 256); }
        catch (ResourceLimitException) { memory = null; }
        var text = new StringBuilder();
        int last = first;
        bool limit = false;
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
        if (limit || memory is null)
        {
            memory?.Resize(128);
            return new LineResult(anchor, [], memory);
        }
        TerminalHighlightResult matches = matcher.MatchWithRuleIds(text.ToString(), excluded, token);
        excluded.UnionWith(matches.TimedOut);
        memory.Resize(128 + matches.Spans.LongLength * 128);
        return new LineResult(anchor, matches.Spans, memory);
    }

    private static List<LineResult> Remap(TerminalSnapshot latest, TerminalSnapshot previous,
        IEnumerable<LineResult> old) => old.Select(line => latest.MapMatchFrom(previous, line.Anchor) is { } match &&
        match.StartOffset == 0 && match.EndOffset == latest[match.EndRow].Length &&
        (match.StartRow == 0 || !latest.IsSoftWrapped(match.StartRow - 1)) &&
        (match.EndRow + 1 == latest.Count || !latest.IsSoftWrapped(match.EndRow))
        ? line with { Anchor = match } : null).OfType<LineResult>().ToList();

    private void Publish()
    {
        var overlays = new Dictionary<int, TerminalHighlightSpan[]>();
        var currentRows = new HashSet<int>();
        if (snapshot is { } view)
        {
            foreach (LineResult line in cached)
            {
                for (int row = Math.Max(line.Anchor.StartRow, evaluatedViewport.First);
                    row <= line.Anchor.EndRow && row < evaluatedViewport.End; row++)
                    currentRows.Add(row);
                if (line.Spans.Length == 0) continue;
                int offset = 0;
                for (int row = line.Anchor.StartRow; row <= line.Anchor.EndRow; row++)
                {
                    int end = offset + view[row].Length;
                    var ranges = line.Spans.Where(span => !state.Suspended.Contains(span.RuleId) && span.Start < end && span.End > offset)
                        .Select(span => span with { Start = Math.Max(0, span.Start - offset),
                            End = Math.Min(end, span.End) - offset }).ToArray();
                    if (ranges.Length != 0) overlays.Add(row, ranges);
                    offset = end;
                }
            }
        }
        rowOverlays = overlays;
        evaluatedRows = currentRows;
        surface.SetHighlights(overlays);
    }

    private sealed record LineResult(TerminalMatch Anchor, TerminalHighlightSpan[] Spans, ResourceReservation? Memory);

    internal (long Id, long Revision, TerminalHighlightSpan[] Spans)? CaptureAiRow(int row)
    {
        if (snapshot is not { } view || view != evaluated || work is not null) return null;
        int index = view.ScreenStartRow + row - 1;
        if (!evaluatedRows.Contains(index) || index >= view.Count) return null;
        var identity = view.DisplayIdentity(index);
        return (identity.Id, identity.Revision, rowOverlays.GetValueOrDefault(index) ?? []);
    }
}
