# Confirmed highlighting behavior and limits

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q121-Q124 accepted as recommended on 2026-11-08, following
Q117-Q120 acceptance (no user-selectable cursor settings). Specification only;
no implementation, runtime validation, or publication is authorized.
Preserve the confirmed requirements in `highlighting-issue.md`.

## Q121: Regex semantics and visible-text mapping

Use the standard .NET 10 backtracking regex engine with an explicit timeout.
Use invariant-culture matching independent of Windows/UI language;
case-sensitive by default, with the accepted per-rule Ignore case option.
Allow standard inline options such as `(?i)` and `(?-i)` with normal .NET
precedence. Leave Multiline and Singleline off by default, without separate UI
switches; inline options cannot bypass the per-logical-line input boundary.
Keep lookarounds and backreferences rather than forcing NonBacktracking and
silently restricting the promised .NET syntax. Do not expose .NET 11-only
options in a .NET 10 specification.

Ignore zero-length matches for drawing because they cover no characters, while
counting them toward work limits and using normal forward-moving enumeration.
For a nonempty match, map its span to the displayed grapheme/cell ranges. A
combining sequence or emoji cluster is the minimum indivisible styling unit;
if only part matches, style that cluster rather than splitting its glyph.
This is a narrow clarification of matched-portion-only styling, not a whole-line
mode. First-rule-wins still applies per displayed cluster/cell; unspecified
attributes inherit the terminal attributes, not lower-priority regex rules.

Match current visible-content logical text from the actual normal terminal
state, including enabled local echo. Do not match the raw stream or the separate
receive-only Text logging model. Soft wraps join without a newline; cursor
overwrite/erase and backspace editing have already taken effect. Represent
screen layout gaps, including tab-expanded gaps, as spaces, preserve explicit
spaces, and omit unused trailing padding. There are no embedded escape sequences
or log-added separators. A line's CR/LF terminator is not part of regex input.
Do not normalize characters merely for matching. Logs/copy retain their existing
independent semantics and receive/local-echo provenance.

## Q122: Rule editing, sharing, and invalid patterns

Use no built-in enabled rule set by default. Keep the accepted named,
reusable sets with explicit no-highlighting selection. Give sets/rules stable
identifiers, editable names, enable/disable, duplication, and ordered rules.
Profiles refer to a selected shared set; changing that selection in a profile
applies on the next connection under the existing captured-settings policy.

Rule-editor changes remain a draft until Save. Cancel leaves every active tab
unchanged. Save applies a new set revision to all tabs already using that shared
set and reevaluates existing visible output first. Show which known active tabs
use a shared set before saving/deleting it. To change only some profiles, duplicate
the set and select the copy in those profiles instead of silently cloning on edit.
Saving rule changes does not rewrite profiles or any logs.

An invalid regex may be saved only as a disabled rule, with its parse error
visible. Reject saving it as enabled until corrected or disabled. Missing or
invalid externally loaded rules produce a nonfatal highlighting status; do not
block a trusted profile's connection or silently repair its pattern. Never run
a disabled rule. No unsaved typing in the editor changes live styling.

Deleting a set requires an explicit editor action and confirmation showing
known uses. Remove its overlays from affected active tabs; preserve underlying
terminal colors. Do not rewrite external profile files or substitute a different
set. Retained references become unresolved and use no highlighting with a
visible nonfatal indication until corrected. Removing a rule/set does not affect
terminal contents, connections, or logging. Exact storage schema is technical
work. Later accepted Q156 in `application-settings-proposals.md` supplies offline
manual-copy portability through a separate `highlighting.json`, preserving set
and rule IDs. It does not add an import/export UI, automatic merge, or hot reload.
Q155 keeps edits to a selected shared set live after successful save; changing
the selected reference in a connection profile applies on the next connection.

## Q123: Output activity, edits, and stale results

Define the 500 ms idle trigger by changes to matchable terminal
text, including local echo and received overwrite/erase, rather than every
transport byte. Cursor movement with no text change, ANSI color changes alone,
queries, protocol negotiation, blink, mouse scrolling, and focus changes do not
keep postponing unchanged text's evaluation. Terminal attributes update normally
without waiting for regex reevaluation; overlay composition uses current attrs.

Use the current logical-line revision after edits, not historical receive chunks.
Mark lines affected by cursor-addressed changes dirty. Discard affected stale
highlight ranges immediately so an old ERROR highlight cannot remain on newly
written OK. Apply worker results only when the referenced content and rule-set
revisions still match; otherwise discard and reschedule. A pure layout reflow
remaps valid ranges without inventing a new text revision, while quota retirement
invalidates ranges for actually discarded content.

Preserve the approximately 2-second bounded opportunities during continuous
changes and defer hidden-tab evaluation. Rule saves/enables/resumes and scrolling
to unevaluated visible content request bounded work without waiting for a new
receive event. Reception, rendering, and all logging never wait for highlighting.
An alternate screen remains excluded; returning evaluates eligible current
normal-screen content. Do not reopen the accepted scheduling intervals.

## Q124: Overload behavior and accepted initial limits

Use bounded inputs/work and predictable omission of highlighting rather than
truncating terminal content or blocking display/logging. Accepted initial limits:

| Limit | Accepted initial value | Outcome when exceeded or exhausted |
| --- | --- | --- |
| Rules per set | 100 | Reject adding/saving more rules in the editor; diagnose external excess without blocking the connection. |
| Pattern length | 4,096 UTF-16 code units | Reject the oversized pattern for highlighting; keep configuration errors visible. |
| Logical line input | 65,536 UTF-16 code units | Skip regex on that whole line; do not truncate into misleading partial matches. |
| Enumerated matches per line across all rules | 4,096, including zero-length matches | Skip that line's regex overlay for the current revision; do not publish partial/inconsistent precedence. |
| Single regex search timeout | 50 ms | Suspend that rule in that tab and notify once, as already agreed. |
| Cooperative background work slice | Approximately 20 ms | Yield between units and resume later; this is not a regex timeout or rule suspension. |

These values are accepted product starting points, not Microsoft-prescribed
limits or measured performance results. UTF-16 counting here is a resource
measure, not terminal width. The regex timeout is approximate and cannot
interrupt at an exact wall-clock deadline; a running search can exceed the
cooperative slice before returning. Bound worker concurrency, candidate results,
and scheduling fairness across tabs separately. Never run expensive matching on
the UI/receive/logging path or accumulate unlimited pending revisions.

Long/match-dense lines remain fully displayed, copied, and recorded subject to
their normal independent limits. Show a quiet tab status for skipped highlighting,
not a modal dialog or repeated per-line notification. Reconsider a skipped line
after its content or rules change; do not repeatedly retry the unchanged revision.
Timeout suspension remains tab-local and persists until explicit resume or a
saved correction to that rule; restarting a new session starts fresh. Changing
an unrelated rule must not silently reactivate the suspended one. A resume that
times out again creates a new suspension with one notification.

Timeouts apply during match enumeration too, not just when obtaining a lazy
Matches collection. The user-authored patterns are trusted local configuration;
timeouts and length limits are not a sandbox for arbitrary hostile regex source
or a proof of bounded parsing/compilation cost. This does not add profile approval
or weaken the accepted basic activation checks. Final total memory/queue bounds,
multi-tab performance acceptance, and cancellation/failure transitions remain
technical specification work before implementation readiness.

## Primary guidance reviewed on 2026-11-08

Microsoft documents culture-dependent IgnoreCase by default, CultureInvariant,
inline-option precedence, the restricted syntax of NonBacktracking, approximate
search timeouts, lazy Matches evaluation, and forward progress after empty matches.
The limits, text-to-cell mapping, and UI policies above are TabTty decisions
derived from those facts, not framework guarantees.

- [.NET regex options and invariant culture](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options)
- [.NET 10 Regex.MatchTimeout](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.matchtimeout?view=net-10.0)
- [.NET 10 Regex.Matches](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.matches?view=net-10.0)
- [.NET 10 Match.NextMatch](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.match.nextmatch?view=net-10.0)
- [Regex best practices and trusted patterns](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-regex)
