using TabTty.Core;

namespace TabTty.App;

// All panes in one terminal tab observe the same rule and suspension revisions on the dispatcher.
internal sealed class TerminalHighlightState
{
    private HighlightSet? set;
    private readonly HashSet<Guid> suspended = [];
    internal IReadOnlySet<Guid> Suspended => suspended;
    internal TerminalHighlightRules? Matcher { get; private set; }
    internal event Action<bool>? Changed;

    internal void SetRules(HighlightSet? updated)
    {
        if (ReferenceEquals(updated, set)) return;
        var old = set?.Rules.ToDictionary(rule => rule.Id);
        var next = updated?.Rules.ToDictionary(rule => rule.Id);
        suspended.RemoveWhere(id => old is null || next is null || !old.TryGetValue(id, out var original) ||
            !next.TryGetValue(id, out var replacement) || original.Pattern != replacement.Pattern ||
            original.IgnoreCase != replacement.IgnoreCase);
        set = updated;
        Matcher = updated is { Enabled: true } ? new TerminalHighlightRules(updated.Rules) : null;
        Changed?.Invoke(false);
    }

    internal bool Suspend(HashSet<Guid> timedOut)
    {
        int count = suspended.Count;
        suspended.UnionWith(timedOut);
        if (count == suspended.Count) return false;
        Changed?.Invoke(true);
        return true;
    }

    internal void Resume()
    {
        if (suspended.Count == 0) return;
        suspended.Clear();
        Changed?.Invoke(false);
    }
}
