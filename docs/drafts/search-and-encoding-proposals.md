# Confirmed search and encoding operation

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q125-Q128 accepted as recommended on 2026-11-08, following
Q121-Q124 acceptance. Specification only; no implementation, prototype,
runtime experiment, or publication is authorized.

## Q125: Encoding changes apply on the next connection

Capture receive/transmit encoding and associated JIS/kana choices
at connection start. Edits to the saved profile apply to the next connection or
explicit reconnect, not the active session. Do not provide a temporary live
encoding switch in the initial release. This extends the accepted profile
snapshot policy and avoids an implicit mid-character/stateful-encoding reset.

Show the effective receive/transmit choices in session information; if the
profile has since changed, distinguish saved next-connection settings from the
active ones. Do not re-decode existing output, rewrite existing logs, replay old
bytes, or silently reconnect on a profile edit. A disconnected tab retains its
already decoded text. A new connection creates fresh decoder/encoder state and
uses the latest saved profile under the accepted basic loading checks.

Retain UTF-8 as default, manual selection, linked receive/transmit choices with
optional separation, the accepted 48-choice Tera Term inventory, invalid-receive
replacement, and whole-unit rejection of unencodable transmit text. This choice
does not settle the remaining JIS/kana/mapping fidelity matrix. Q144-Q145 in
`legacy-encoding-proposals.md` accept backend coverage and Japanese
compatibility policy. Their settings follow this connection snapshot rule. The no-live-switch
scope is a TabTty choice, not a claim about Tera Term 5.7.1 runtime behavior.

## Q126: UTF-8 initial signature without automatic detection

Do not automatically insert a BOM in transmitted terminal data, Text logs,
or Terminal logs. Raw remains a headerless exact receive-payload stream and
preserves every received byte, including an actual peer-supplied BOM. Deliberately
entered/pasted U+FEFF is user content, not an automatic preamble: do not strip it
under this rule; apply the ordinary transmit encoding/control validation.

Only when receive encoding is already UTF-8, recognize one complete `EF BB BF`
at byte offset zero of a newly connected terminal receive stream as an initial
signature. Suppress that signature from terminal text/display and therefore
Text/Terminal output; Raw still captures it. Recognition can span read chunks.
A mismatched or incomplete prefix follows normal decoding/finalization rules.
Never select UTF-8 based on these bytes when another encoding is configured.

Accepted Q141 in `ssh-session-proposals.md` clarifies this boundary for SSH:
normal channel DATA and separately delivered extended stderr have independent
incremental decoder/parser state. Apply initial-signature recognition at byte
offset zero of each new source stream. Raw preserves both sources' signature
bytes in reception order; neither a new part nor rekey creates a new opportunity.

Stream start is the start of that connection's terminal receive source, not the start
of a socket read, SSH packet, new log part, recording resume, screen clear, tab
move, or terminal reset. Those events do not grant another signature-suppression
opportunity. A second or midstream U+FEFF is retained as content, represented as
a zero-width format character without inventing a visible one-cell glyph; it
remains in eligible logical text/copy/log serialization. Do not remove every
U+FEFF in a stream. This is an explicit TabTty policy, not a verified claim of
identical Tera Term BOM behavior.

## Q127: Literal-search navigation, matching, and bounds

Preserve Ctrl+Shift+F, normal retained-buffer search, literal matching, and the
accepted default case-insensitive/case-sensitive option. Use explicit
OrdinalIgnoreCase/Ordinal comparison, respectively, rather than current-locale
linguistic comparison. Do not add width/kana/diacritic folding or Unicode
normalization: `A` and `a` can match in default mode, but full-width `Ａ` versus
`A`, hiragana versus katakana, and composed versus decomposed spellings are not
silently equated. The selected .NET runtime's ordinal casing rules apply.

Search the same edited logical text used for display-oriented highlighting,
including enabled local echo, tab-expanded layout spaces, and joined soft wraps,
while preserving the search-specific literal comparison and independent limits.
Do not match across real line boundaries. A matched part of a display cluster
highlights the whole cluster without changing the query or source text.

Use Enter/Shift+Enter for Next/Previous while search owns keyboard focus,
and Escape to close the search UI and return focus to the terminal without
sending that key remotely. Keep the current viewport on close, remove search
overlays, and preserve any independent terminal selection. Reopening search
retains its query and case option only within that tab's lifetime; there is no
persisted search history or cross-tab query sharing. IME composition consumes
its own confirmation keys before search navigation, as in normal text editing.

Next/Previous moves through matches in text order and wraps at the buffer ends
with a quiet indication that the beginning/end was reached. With no current
result, start forward at the visible area's beginning or backward at its end.
Advance from a match's start by at least one Unicode scalar to permit distinct
overlapping occurrences without getting stuck at the same index. If only one
match exists, wrapping can return to that match with the wrap indication.
No matches leaves the viewport unchanged and reports that status. Results from
background updates never automatically select/scroll to a different match.

Use a single-line query up to 1,024 UTF-16 code units. Reject overlong,
ill-formed Unicode, or CR/LF-containing query input with an inline message rather
than silently truncating it. Empty query clears matches. Cache at most 10,000
match ranges per tab; use a `10,000+` count when there are more and compute further
navigation on demand. This is a cache/count limit, not a boundary excluding
later retained output from search. Long lines skipped by regex highlighting
remain searchable; do not inherit its 65,536-unit regex-input exclusion.

Perform bounded background scans with revision checks and chunk overlap sufficient
for matches crossing work boundaries. Keep existing 500 ms/~2 s text-update
scheduling, but query/options/navigation changes request bounded work promptly.
When work is incomplete, show searching/partial state instead of a false exact
count, no-match result, or wrap indication. Do not hold evicted output solely
for a pending search. Exact global cache/work budgets remain resource contracts;
these accepted limits are not measured performance claims.

## Q128: Selection and search when switching screens

Clear terminal text selection whenever switching between normal
and alternate screens, without changing clipboard contents. Do not restore an
old selection automatically on return. The underlying normal buffer is parked/
restored under existing terminal semantics; this decision affects selection only.

If search is open when an alternate screen becomes active, retain its query and
case option but suspend search navigation/overlays. Indicate that search is
available on the normal screen. Keep Escape/close available; do not search the
parked normal buffer or alternate screen through that UI. A user can still make
a new ordinary selection/copy on the current alternate screen under existing
mouse rules; that selection is cleared when switching back.

On return to the normal screen, if search remains open, recompute its matches
with the retained query and no automatically selected current result or viewport
jump. Normal screen restoration/resize proceeds independently. If the user
closed search while it was suspended, do not reopen it on return. No screen
switch changes existing log pause/eligibility semantics or creates synthetic
log entries. This resolves the selection/search interaction, not all terminal
alternate-screen control variants.

## Primary evidence reviewed on 2026-11-08

Microsoft documents that UTF8Encoding.GetBytes does not prepend a BOM even when
GetPreamble returns one; BOM insertion remains an application/writer choice.
Unicode distinguishes an initial UTF-8 signature from midstream U+FEFF content.
The current Tera Term encoding UI documentation does not establish the pinned
release's live-switch boundary or complete BOM policy; no parity claim is made.

Tera Term 5.7.1 lists word search as a requested feature, and its pinned Edit
menu has no built-in terminal search command. Its VT key named Find is not a
search dialog. Q127-Q128 therefore specify TabTty behavior, not copied Tera Term
search defaults. Microsoft ordinal comparisons avoid locale-driven linguistic
equivalence; query/cache/UI choices above are accepted product decisions.

- [Microsoft UTF8Encoding construction and preamble behavior](https://learn.microsoft.com/en-us/dotnet/api/system.text.utf8encoding.-ctor?view=net-10.0)
- [Microsoft UTF8Encoding.GetPreamble](https://learn.microsoft.com/en-us/dotnet/api/system.text.utf8encoding.getpreamble?view=net-10.0)
- [Unicode BOM FAQ](https://www.unicode.org/faq/utf_bom.html)
- [Current Tera Term encoding UI, not pinned runtime evidence](https://teratermproject.github.io/manual/5/en/menu/setup-additional-coding.html)
- [Tera Term 5.7.1 requested features](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/about/requests.html)
- [Tera Term 5.7.1 Edit menu](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/ttermpro.rc#L672)
- [.NET StringComparison](https://learn.microsoft.com/en-us/dotnet/api/system.stringcomparison?view=net-10.0)
- [Microsoft string comparison guidance](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-strings)
- [.NET CompareOptions](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.compareoptions?view=net-10.0)
