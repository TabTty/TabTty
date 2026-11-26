using System.Text.RegularExpressions;

namespace TabTty.Core;

public readonly record struct TerminalHighlightStyle(uint? Foreground, uint? Background, bool? Bold, bool? Underline);
public sealed record TerminalHighlightRule(Guid Id, string Name, string Pattern, bool Enabled, bool IgnoreCase,
    TerminalHighlightStyle Style);
public readonly record struct TerminalHighlightSpan(int Start, int End, TerminalHighlightStyle Style)
{
    internal Guid RuleId { get; init; }
}
public sealed record TerminalHighlightResult(TerminalHighlightSpan[] Spans, Guid[] TimedOut, bool LimitReached);

public sealed class TerminalHighlightRules
{
    private readonly (TerminalHighlightRule Rule, Regex Expression)[] enabled;
    public bool IsEmpty => enabled.Length == 0;

    public TerminalHighlightRules(IReadOnlyList<TerminalHighlightRule> rules)
    {
        if (rules.Count > 100) throw new ArgumentException("At most 100 highlight rules are allowed.", nameof(rules));
        var identifiers = new HashSet<Guid>();
        var compiled = new List<(TerminalHighlightRule, Regex)>();
        foreach (TerminalHighlightRule rule in rules)
        {
            if (rule.Id == Guid.Empty || !identifiers.Add(rule.Id) || string.IsNullOrWhiteSpace(rule.Name) ||
                rule.Pattern.Length > 4096 || rule.Style.Foreground > 0xFFFFFF || rule.Style.Background > 0xFFFFFF)
                throw new ArgumentException("Invalid terminal highlight rule.", nameof(rules));
            if (!rule.Enabled) continue;
            var options = RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            compiled.Add((rule, new Regex(rule.Pattern, options, TimeSpan.FromMilliseconds(50))));
        }
        enabled = compiled.ToArray();
    }

    public TerminalHighlightResult Match(ReadOnlySpan<char> line, IReadOnlySet<Guid> suspended,
        CancellationToken cancellationToken = default)
        => MatchCore(line, suspended, includeRuleIds: false, cancellationToken);

    internal TerminalHighlightResult MatchWithRuleIds(ReadOnlySpan<char> line, IReadOnlySet<Guid> suspended,
        CancellationToken cancellationToken, Action<Guid>? onTimeout = null) =>
        MatchCore(line, suspended, includeRuleIds: true, cancellationToken, onTimeout);

    private TerminalHighlightResult MatchCore(ReadOnlySpan<char> line, IReadOnlySet<Guid> suspended,
        bool includeRuleIds, CancellationToken cancellationToken, Action<Guid>? onTimeout = null)
    {
        if (line.Length > 65536) return new TerminalHighlightResult([], [], true);
        var spans = new List<TerminalHighlightSpan>();
        var timeouts = new List<Guid>();
        int count = 0;
        foreach (var (rule, expression) in enabled)
        {
            if (suspended.Contains(rule.Id)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            int previous = spans.Count;
            try
            {
                foreach (var match in expression.EnumerateMatches(line))
                {
                    if (++count > 4096) return new TerminalHighlightResult([], timeouts.ToArray(), true);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (match.Length != 0)
                        spans.Add(new TerminalHighlightSpan(match.Index, match.Index + match.Length, rule.Style)
                            { RuleId = includeRuleIds ? rule.Id : Guid.Empty });
                }
            }
            catch (RegexMatchTimeoutException)
            {
                spans.RemoveRange(previous, spans.Count - previous);
                timeouts.Add(rule.Id);
                onTimeout?.Invoke(rule.Id);
            }
        }
        return new TerminalHighlightResult(spans.ToArray(), timeouts.ToArray(), false);
    }
}
