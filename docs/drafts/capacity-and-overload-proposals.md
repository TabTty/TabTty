# Confirmed initial capacity targets and overload behavior

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q159-Q160 accepted as recommended on 2026-11-10 after Q158
acceptance. Specification only; no implementation, prototype, load generator,
benchmark, runtime test, or publication is authorized.

Read with `connection-profiles-issue.md`, `retention-and-log-io-proposals.md`,
`highlighting-details-proposals.md`, `receive-logging-issue.md`, and
`specification-frontier.md`. These are initial acceptance targets and user-visible
failure policies. They are not measured capabilities or permission to silently
reduce previously accepted features.

## Q159: Target twenty concurrent connections with responsive local operations

Use an initial acceptance target of twenty concurrent connected tabs across
the application's windows, each using the default 5,000 retained normal rows.
Twenty is a validation baseline, not a hard tab-count limit and not a claim of
unlimited additional sessions. Admission above it still depends on explicit
resource budgets. Retained disconnected tabs count toward those budgets even
though they do not count as active connections.

Use a reference Windows 11 x64 PC with at least four CPU cores, 16 GiB RAM, and
local SSD storage for the baseline. Record the exact hardware, OS, runtime, and
OpenSSL versions when testing is later authorized. This is a test reference,
not a newly imposed 16 GiB installation requirement or proof that every CPU/SSD
matching those broad labels performs identically.

Use two windows with one selected terminal in each, a 120-column by 40-row grid,
and twenty connected sessions in total. The sustained-output target is 1 MiB/s
(1,048,576 received terminal-payload bytes per second) in aggregate, not per tab.
Run a 30-minute representative UTF-8 text/ordinary-VT workload after reaching the
retained-row limit, with all three log formats enabled and ten simple highlight
rules per session. Include both distributed output and one busy producer rather
than allowing a quiet-tab-only result to satisfy the target. Define deterministic
workload contents and regexes in the acceptance plan; do not imply that arbitrary
expensive patterns or control streams have equivalent processing cost.

For ordinary local UI operations, target a 95th-percentile response of at most
100 ms under that baseline: local key acceptance/queuing, tab selection, and
opening local controls. The input metric ends at local acceptance/queueing, not
remote echo, authentication, command execution, or network round-trip completion.
For tab selection, measure presentation of a current valid viewport snapshot,
not completion of all historical search/highlight work.
Measure from the initiating input/action timestamp, including dispatcher waiting;
starting the timer only when a busy UI handler finally runs would hide latency.

During overload/failure tests, target acceptance of Cancel/Disconnect by local
UI within 250 ms at the 95th percentile. This is not a 250 ms guarantee for
network teardown, file flush, driver cancellation, or completion of modal user
choices. Existing SSH close grace and logging close-drain policies remain intact.
Record tail latency/stalls as well as the percentile; a percentile must not
conceal repeated multi-second hangs or a blocked UI.

Default-row testing does not remove functional support for the accepted
100-100,000 range, other encodings, COM, alternate screens, or complex edits.
Test those in their own correctness/resource cases; twenty tabs each at maximum
retention are not implicitly part of this performance baseline. No numeric
throughput or latency claim is valid until measured on the recorded workload.
Report missed targets and seek an explicit scope/target adjustment if necessary,
rather than silently relaxing the criterion or dropping output to pass it.

## Q160: Preserve existing sessions and fail explicitly at resource boundaries

Prioritize existing sessions when application-owned resource budgets
cannot admit a new connection/tab or a new background operation. Reject/defer the
new operation with a clear capacity status; do not close an unrelated tab or
discard its retained output to make room. Do not automatically retry refused
connections or silently change their saved profile values.

Use bounded terminal/input/transport work and supported flow control before a
failure boundary. Do not promise that every transport can be slowed indefinitely:
serial behavior retains its configured handshake and does not gain automatic
flow-control changes. Slow logging alone still follows Q147, not a policy of
stopping terminal reception to let the disk catch up.

If an active session reaches a hard terminal-processing/state budget and cannot
continue correctly with bounded resources, stop that session with an explicit
resource-limit error and retain its already available output. Other sessions
continue where their own budgets permit. Cancel unsent input and do not replay it
on reconnect. Keep logging/finalization best-effort within existing ownership
rules, clearly reporting any unrecorded tail; do not invent a complete-capture
guarantee at the failure cutoff.

Do not silently discard received text, clamp a saved retention choice, evict
history earlier than the accepted row-limit policy, disable configured recording,
or reinterpret the connection's encoding to keep the session apparently healthy.
Ordinary quota retirement/reflow under Q106/Q146 still operates as agreed. A hard
budget must be checked before destructive state mutation where practical; a
recoverable resource-limit rejection is distinct from an already fatal process
out-of-memory condition. Do not promise that arbitrary OS allocation failure
always leaves every window recoverable.

Preserve existing narrower degradation rules instead of turning every limit
into a disconnection: regex overload may skip/suspend highlights, search uses
its bounded navigation/cache behavior, and logging I/O/backlog failure stops
recording while keeping the connection alive. Caches and recomputable overlays
can be discarded/recomputed; retained terminal output and accepted log payload
are governed by their existing retention/failure contracts.

Keep counters, errors, and cleanup bounded too. Do not accumulate one failed task,
dialog, worker, or queued retry for every incoming byte or repeated action.
Exact application/session budgets for cells, character clusters, parser input,
workers, queues, and cleanup remain technical contracts to complete. Do not
equate an application-owned payload budget with an exact OS working-set ceiling.

## Basis and remaining validation work

Microsoft's WPF threading guidance describes the dispatcher/input responsiveness
problem when long work occupies the UI thread. It does not prescribe twenty
sessions, 1 MiB/s, the selected reference machine, or the latency numbers; those
are accepted TabTty targets. Existing no-loss/explicit-failure boundaries follow
the accepted recording, retention, and connection policies in the linked drafts.

- [Microsoft WPF threading and responsiveness](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model)

With these product choices accepted, specify the internal resource accounting
and deterministic acceptance matrix, including serial overrun, slow/full storage,
malformed/oversized terminal sequences, resize/reflow, bursts, many inactive tabs,
regex timeouts, and orderly cancellation/closure. Do not require an unapproved
prototype or benchmark during the current documentation-only phase. Initial
implementation readiness still requires the remaining technical contracts.
