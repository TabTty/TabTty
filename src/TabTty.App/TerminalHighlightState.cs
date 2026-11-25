using TabTty.Core;

namespace TabTty.App;

internal enum HighlightChange { Rules, Styles, Suspension }

// All panes in one terminal tab observe the same rule and suspension revisions on the dispatcher.
internal sealed class TerminalHighlightState
{
    private HighlightSet? set;
    private Dictionary<Guid, TerminalHighlightRule> rules = [];
    private readonly HashSet<Guid> suspended = [];
    internal IReadOnlySet<Guid> Suspended => suspended;
    internal TerminalHighlightRules? Matcher { get; private set; }
    internal bool MissingSet { get; private set; }
    internal event Action<HighlightChange>? Changed;

    internal TerminalHighlightStyle Style(Guid rule) => rules[rule].Style;

    internal void SetRules(HighlightSet? updated, bool missing = false)
    {
        if (ReferenceEquals(updated, set) && MissingSet == missing) return;
        var old = rules;
        var next = updated?.Rules.ToDictionary(rule => rule.Id) ?? [];
        suspended.RemoveWhere(id => !old.TryGetValue(id, out var original) ||
            !next.TryGetValue(id, out var replacement) || original.Pattern != replacement.Pattern ||
            original.IgnoreCase != replacement.IgnoreCase);
        bool sameMatches = set is { Enabled: true } previous && updated is { Enabled: true } &&
            previous.Rules.Count == updated.Rules.Count && previous.Rules.Zip(updated.Rules).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Pattern == pair.Second.Pattern &&
                pair.First.IgnoreCase == pair.Second.IgnoreCase && pair.First.Enabled == pair.Second.Enabled);
        set = updated;
        rules = next;
        MissingSet = missing;
        Matcher = updated is { Enabled: true } ? new TerminalHighlightRules(updated.Rules) : null;
        Changed?.Invoke(sameMatches ? HighlightChange.Styles : HighlightChange.Rules);
    }

    internal bool Suspend(HashSet<Guid> timedOut)
    {
        int count = suspended.Count;
        suspended.UnionWith(timedOut);
        if (count == suspended.Count) return false;
        Changed?.Invoke(HighlightChange.Suspension);
        return true;
    }

    internal void Resume()
    {
        if (suspended.Count == 0) return;
        suspended.Clear();
        Changed?.Invoke(HighlightChange.Rules);
    }
}
