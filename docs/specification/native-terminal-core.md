# Complete Rust terminal module design

Architecture direction accepted on 2026-11-16 in
[ADR 0007](../adr/0007-complete-terminal-processing-in-rust.md).
This is the target design, not implemented behavior or new test evidence.
The [current implementation](implementation-status.md) still has a partial
Rust implementation and managed handoff. The recording-interface design is
confirmed through Q5. Q6-Q8 confirm shared interpretation rules with separate
admission and execution walks and their verification contract. The
[closure design](tab-and-window-closure.md) records Q9's ownership scope,
amended Q10's common transfer-window closure policy for explicit, bulk, and
automatic tab removal, Q11's named complete operations, and Q13's failure
results and ownership after a caller stops waiting. Accepted Q12a preserves an
open close confirmation when an ordinary transfer completes for the same target.
Accepted Q12b lets a later window-close request join an existing tab closure and
handle the other tabs with any required confirmation, without duplicate work or
requiring the user to retry. Q14 in the
[architecture interview](../drafts/terminal-architecture-deepening.md) accepts
the exceptional changed-target outcome and confirms candidate 3's final shared
understanding. All three designs are ready for separately requested implementation.

## Scope and ownership

The [codec decision record](../drafts/terminal-codec-consultation.md) records
47 supported encodings. Use Win32 strict conversion and Windows ICU for the
designated identities. Windows ICU supplies the Japanese JIS shift sequences
and repertoires. Rust owns integration, source separation, VT framing,
resource admission, exact-send validation, and result delivery.

Complete the existing [terminal and encoding](terminal-and-encodings.md),
[session and recording](session-and-recording.md), and
[history observation](ai-history-observation.md) contracts in Rust. Their
accepted behavior remains authoritative. Complete coverage means those
contracts, including their deliberate exclusions, rather than every xterm/VT
extension. The SSH allocation in ADR 0001 and the tab identity in ADR 0002 remain.

| Responsibility | Owner in the target design |
| --- | --- |
| All 47 receive/transmit encodings, independent source state, exact-send and malformed-unit behavior | Rust terminal module calling the approved converters |
| VT parsing, charset/rendition/modes, normal/alternate screens, Unicode clusters/width, retained rows, reflow and anchor/revision metadata | Rust terminal module |
| Terminal-dependent key/mouse encoding, replies, strict input preflight, codec closure and local-echo effects | Rust terminal module |
| Receive-only Text and combined display/Terminal projections, recording eligibility/revisions, serialized Text position, bounded Text/Terminal bytes | Rust terminal module |
| Bounded screen/history capture, literal history search, checkpoints and content/retention validation | Rust terminal module |
| Connection/profile snapshots, session generations/order, input permissions/queue, paste pacing, broadcast recipients, transport writes and acknowledgements | Existing C# session/application modules |
| Recording format/projection selection, Raw admission, file names/parts, file preparation, disk writes and actual I/O cleanup | Existing C# recording modules |
| AI counterpart permissions, external handles/expiry, JSON/frame sizing, ordered publication | Existing C# AI collaboration module |
| WPF windows/panes, fonts/rendering, IME and native mouse capture, UIA presentation, visual highlighting and view navigation | Existing C# presentation modules |

The session remains the lifetime and ordering owner. It serializes terminal
events through a managed adapter, while Rust owns their terminal meaning and
authoritative state. C# may hold owned immutable snapshots and presentation
caches; it does not maintain a mutable C# terminal replica or call managed
codecs/parsers to complete a Rust terminal operation.

Text recording still needs a receive-only projection when configured. It and
the combined receive/local-echo projection use the same Rust editing engine
but can have different content and cursor positions. Two projections serve
different recording semantics; they are not native/managed shadow copies.
EOF finalizes reception, not retained display/history ownership. Disconnect
and tab movement preserve the applicable Rust state until its actual owner
releases it; reconnect creates a new session generation.

## Coverage that replaces managed execution

| Current managed route | Required native behavior |
| --- | --- |
| Legacy encoding at model creation | Every supported encoding starts in Rust, including independent DATA/stderr partials, JIS/kana states and strict transmit preflight |
| Resize after reception | Reflow normal/parked output with retained row/logical anchors, recording provenance, wide clusters and retirement semantics intact |
| Alternate screen and additional controls | Implement the complete contracted screen/mode/control behavior, including per-source REP, scrolling regions, tabs, save/restore, reset, replies and input modes |
| Recording start/pause/resume/retry/EOF | Apply eligible interval transitions and native recording-position continuity without replacing the terminal model |
| AI screen/history access | Produce bounded immutable observations and opaque validation metadata directly from the authoritative Rust model |
| Observer attachment | Expose ordered bounded results without selecting another terminal implementation; preserve future macro constraints |
| Snapshot/effect size or quota pressure | Apply bounded native preparation and the existing explicit resource policy; resource pressure never selects C# |
| Missing/mismatched native binary or diagnostic managed selection | Report packaging failure; production has no managed-terminal mode |

Unknown well-framed controls outside the product contract are consumed/ignored
as specified. A control that the contract supports but Rust has not implemented
is a replacement-readiness defect, not a new product exclusion or a reason to
hand off. Do not silently reduce encodings, retained content, logging formats,
source independence, or observation features to meet this design.

## Ordering, effects, and the interface

Keep one ordered mutation stream per session, including receive, accepted
input/echo, resize, recording transitions, EOF, and close. Raw admission occurs
after protocol filtering and before decoding. SSH/SFTP protocol bytes remain
outside terminal parsing and session logs. File writers, network waits, WPF
dispatch, and JSON serialization stay outside terminal mutation work.

The ordinary interface carries terminal intentions and returns bounded owned
results. Its implementation hides parsing, projection state, position tracking,
and consistency metadata. It must not expose a callback-wiring recipe, mutable
rows, or a caller-controlled Rust/C# choice. Accepted Q4 selects the named
recording operations below. Native ABI layout and implementation-specific
ownership types remain implementation design details.

Native admission validates the complete requested operation and reserves its
bounded peak state/effect charge before mutation. Bounded batching must keep
the existing scheduler fairness and cancellation opportunities. A rejected
input does not advance live encoder state or emit a prefix. Transport submission
results determine when encoder progress and local echo are committed under the
existing input contract; speculative echo and automatic resend remain excluded.

Snapshots, observation pages, and queued recording effects cannot borrow native
memory across mutation or disposal. Use explicit owned immutable results and
account for native state, copies, in-flight output, and cleanup until their
actual release. Admission is not disk durability or remote receipt. Late
results belong to their original session/recording generation.

Define cells, Unicode scalars, grapheme offsets, UTF-16 offsets and UTF-8 byte
counts separately at the seam. Preserve their existing meanings rather than
using one integer offset interchangeably. C# display/layout and UIA adapters
consume these results without reconstructing mutable terminal state.

## Shared interpretation rules (accepted Q6)

Concentrate source-side incremental receive decoding, BOM/charset handling,
VT framing, parameter interpretation, completed-unit classification, parser
limits, and continuation rules in one Rust module. Admission and execution
use the same definitions rather than separate support lists or independently
implemented decoder/parser state machines. Cover all contracted encodings
and controls under ADR 0007, not only the current partial implementation.

Shared rules do not merge source states or forbid isolated preview state.
Preserve DATA/stderr independence, source-local last-printable/REP meaning,
chunk versus EOF behavior, local-echo ordering, and incomplete-unit recording
provenance, including independently executed embedded C0 controls.

Resource admission and terminal execution retain distinct responsibilities.
Admission validates the complete operation and reserves bounded peak state,
working-memory, and effect charges before authoritative native mutation.
Execution applies admitted meanings to the appropriate Rust projections.
The interpretation module does not own screen/history, recording files,
session scheduling, or transport. Stateful screen effects remain execution's
responsibility; estimating their possible resource growth remains admission's.
Parsing alone does not establish that applying an operation fits the budget.

### Shared admission and execution walks (accepted Q7)

Use one private interpretation interface with Estimate and Execute consumers.
For a valid operation, first walk the input with isolated source state and a
bounded estimation workspace. Reserve the admitted peak state/effect charge,
validate the input and preparation basis, then walk the same input with the
original authoritative source state and Execute. Never begin execution from
the preview's ending state. Both consumers use the same interpretation rules;
neither reparses CSI or maintains an independent support list.

The managed adapter owns this complete operation, including hidden native
preflight/reservation/execution calls where needed. C# retains session ordering
and global quota ownership. No parser-state/preparation handle, command tape,
or effect-draining obligation is exposed to the ordinary caller.

Estimate emits no native effects and does not change live source, projection,
or recording state. Its state-dependent checks use the actual read-only basis
with bounded charged preview changes, shared screen-transition definitions,
or conservative upper bounds where sufficient. Do not create a second screen
engine to replace the removed second parser. Count-only estimates must not
miss cluster, mode/reset, or recording-provenance consequences. A whole-screen
clone is not assumed; preview memory is accounted before growth.

Keep input and the relevant source/configuration/projection/recording/geometry,
pending-effect, and capacity basis valid through execution. If hidden phases
allow relevant changes, validate again before mutation; discard stale work and
re-prepare. Any private cache validates exact input and the current allowance,
not just a display/content revision. The historical 1024-byte cache layout is
not required. Invalidation may add walks beyond the normal two.
Track held reservations and expected accounting changes explicitly. Revalidate
using the current operation's existing reservation without charging it twice
or invalidating preparation solely because its own reservation was granted.
Unexpected changes to the preparation basis still invalidate it.

Charge source snapshots, input ownership, preview workspace, expansion, effects,
and overlapping native/managed copies before allocation or growth. Compact
input or REP is not permission for unbounded output. Do not shorten retention,
evict history early, or publish a prefix of a rejected operation to fit.
Earlier accepted operations and C# Raw admission keep their separate ownership.

Before authoritative execution, cancellation releases private preparation
without native prefix effects. Once execution starts, cancelling a wait does
not roll it back or abandon delivery/cleanup. Unexpected execution failure
after successful admission is not an ordinary resource rejection with a
rollback guarantee; follow the affected-session policy without replay.
Recording-only refusal continues to follow Q5, preserving valid terminal
updates and unrelated effects. Preview never changes live recording eligibility.

This choice removes duplicate interpretation definitions while retaining the
admission/execution structure. It does not establish a speed or memory advantage
without measurement. Independent expected outputs and functional/overload/
performance checks remain required. Q8 of the
[architecture interview](../drafts/terminal-architecture-deepening.md) confirms
candidate 2's complete design and verification contract.
Private type layouts and native ABI details are implementation design work;
neither implementation nor prototyping is authorized.

## Recording and observation continuity

Keep the recording group's selection, start/stop requests, part ownership and
file failures in C#, under session ordering. Rust handles the terminal-side
eligibility changes, incomplete decoder/parser units, normal/alternate Text
rules, canonical Terminal operations, and serialized Text column/grapheme state.
Pause preserves reception and projection updates. Resume starts eligible new
content without backfill. Orderly cutoff, final EOF, and fatal abort retain
their distinct outcomes.

Recording admission/I/O failure still stops all selected recording formats
while a healthy terminal connection continues. Initial recording preparation must
succeed before transport startup. Unrecoverable terminal-state failure follows
the affected-session policy in [resources and validation](resources-and-validation.md).
Under accepted Q3, if preparation succeeds for only some projections, the new
interval remains inactive for every selected format. The recording module owns
that group outcome and actual file cleanup; the terminal module cleans up its
attachment and eligibility state without caller-managed repair. A healthy Rust
terminal continues receiving. This is not an all-effects rollback: already
written bytes remain written.

### Recording operation interface (accepted Q4)

The C# recording module uses three named operations through the managed terminal
adapter. The names specify terminal meanings, not a new native ABI.

| Operation | Terminal meaning | C# use |
| --- | --- | --- |
| `BeginRecording(projections)` | Begin one new eligible interval across all selected Rust projections, with internal cleanup on activation failure | Start/resume/retry after preparing every selected file and writer admission |
| `CutoffRecording()` | Submit eligible tails once and end recording eligibility using the orderly cutoff rules | Pause/finalization, followed by actual writer completion, flush, and close |
| `AbortRecording()` | End recording eligibility and discard unissued recording-only pending state without normal tail submission | Recording admission or writer failure |

Projection selection comes from the fixed connection snapshot. C# owns the
recording group state, part numbers, Raw admission, and retry policy; Rust does
not duplicate Opening/Pausing/Paused/Failed or distinguish resume from retry.
The receive-only projection, when configured, continues updating while
recording is inactive. Neither cutoff nor abort destroys terminal state.
Receive EOF finalizes its source; it does not end the retained terminal's
lifetime or replace recording cutoff. Subsequent cutoff must not resubmit
content already finalized at EOF.

The adapter owns bounded immutable native results, their ordered Text/Terminal
delivery, release, and recording-failure cleanup. The recording caller receives
an outcome, not a result-draining recipe, callback repair task, or public
interval handle. A stable internal output seam supplies production writer
admission or a controlled test capture/rejection adapter. Synchronous admission
failure ends group admission and terminal eligibility before the ordered
operation returns; asynchronous writer failure closes group admission at once
and orders terminal cleanup through the session.

Native cutoff completion is distinct from disk durability. The C# recording
module owns the complete file operation and waits for accepted writer work
outside the session mutation guard. It must not transfer that completion or
cleanup obligation to the UI or another recording caller.

Accepted Q5 completes this interface contract:

- Outcomes distinguish applied, already inactive/no change, and rejected
  operations, and report resulting terminal-side recording activity, terminal
  usability, and any failure reason. The C# module owns the corresponding
  all-formats group result; native activity does not describe Raw admission.
- Begin while already active is rejected before mutation, preserving the
  existing interval and serialized Text position. Q3 applies to preparation
  failure for a valid new interval. C# handles an already-recording public
  Resume as a no-op without calling Begin.
- Inactive Cutoff/Abort completes without output, interval increments, or
  decoder/parser/position changes. A connection with recording disabled cannot
  use these operations to enable formats absent from its captured settings.
- Validate session and recording owner identity in the same ordered event
  that applies a completion/failure. An old writer/opening request cannot abort
  a new interval or activate an obsolete part. Identity stays internal.
- Cancellation or invalidation before activation prevents its commit and
  leaves prepared-file cleanup with its owner. After commit, cancelling a wait
  cannot roll back the result, abandon cleanup, or imply durable storage.
  Subsequent stops follow session ordering; pause does not preempt file open.
- Begin success completes native activation and the C# group's non-failing
  publication within the same event. Cutoff success completes final terminal
  delivery/admission and eligibility closure. Successful C# Pause additionally
  requires writer completion, durable flush, and close; those failures are not
  reported as a successful Paused result.
- Recording-delivery failure preserves valid terminal updates and unrelated
  effects, including replies, from the same operation. Never replay that
  operation or drop those effects because recording failed. Internal cleanup
  retains issued resources until actual completion; unrecoverable terminal
  failure still follows the affected-session policy.

### Observation continuity

AI content/revision/retention checks use the Rust model, including edit-then-
restore detection; final text equality alone is insufficient. Text logging's
content revisions and AI change evidence retain their different semantics.
Literal search preserves the contracted Ordinal/OrdinalIgnoreCase comparison,
UTF-16 query/overlap limits, whole-cluster positions, and UTF-8 response limits.
Rust lowercase or normalization is not a substitute for those comparison rules.

Keep [ordered publication](ai-history-observation.md#publication-as-one-ordered-outcome):
terminal validation and C# authority/expiry/cancellation checks, nonblocking
queue admission, and staged-handle activation form one ordered outcome with
terminal mutation excluded. Copying or validating in Rust, releasing exclusion,
and publishing later without revalidation would break that contract. The
internal transition is bounded and synchronous; external callers do not manage
the terminal gate or raw validation callbacks. MCP/permission policy stays in C#.

Future receive observers consume ordered results independently of rendering,
without stealing data from the terminal/logger. Preserve
[future macro constraints](../drafts/future-macro-design.md); this redesign adds
no macro runtime to the initial release. Shared codec use by SFTP must retain
its existing filename rules and strictness without adding a managed codec
callback to terminal processing.

## Verification and replacement readiness

Use the production terminal interface with the real Rust implementation, plus
controlled I/O/publication adapters where effects vary. The interface is the
test surface. Existing real file, transport, WPF, UIA, and publication checks
remain necessary. Test-only C# comparisons can preserve a migration reference;
independent specified expected output also remains required.

| Verification group | Required observations |
| --- | --- |
| Native-only execution | Every supported creation/operation/encoding remains Rust; zero managed starts, handoffs, fallback calls or mutable managed replicas, including fault and pressure paths |
| Encoding and input | All 47 identities, chunk splits/malformed input, independent sources, Windows/ICU mappings, strict rejected sends, terminal keys/mouse, paste closure and accepted local echo |
| Shared interpretation | Independent expected state/replies/recording bytes; full command spelling, partial and EOF behavior, embedded controls once, parser limits and numeric overflow; no authoritative native state/effect change on admission rejection |
| Terminal state | Contracted control/mode combinations, full-screen use, reflow/retirement, alternate parked output, reset, Unicode clusters, snapshots and retained disconnected tabs |
| Recording lifecycle | All format selections, every old/new part's complete bytes, partial units at pause/resume, tabs/serialized position, alternate transitions, EOF, failure/retry, repeated/invalid operations, cancellation before/after activation, stale writer failure, and valid non-recording effects despite recording rejection |
| Observation | Screen/history/search with native state, edit-then-restore, eviction, Unicode offset units, permission/cancellation/expiry and queue-rejection races |
| Ownership and overload | Pre-mutation rejection, bounded working/results memory, delayed native-result disposal, stale generation completion and actual I/O cleanup |
| Performance and presentation | Existing responsiveness, retention and recording workloads, now also crossing resize/alternate-screen/recording/observation paths; real WPF frames and memory peaks |

Historical 2026-11-15 results demonstrate the partial implementation only.
Re-run the relevant acceptance workloads after the complete replacement;
neither functional parity nor Rust residency alone proves the response target
or whole-process memory outcome. Preserve failed/overload results as evidence.

After implementation is separately authorized, migrate against a frozen test
reference, complete feature/encoding coverage, run cross-feature and integration
checks, then remove production managed selection/handoff and verify packaging.
The dependency policy continues to permit Rust and its standard library under
ADR 0007; this design introduces no third-party crate. The recording-interface
design and shared-walk parser design are confirmed. Closure scope, named
complete operations, and failure/abandoned-wait rules are accepted, together with
Q12a's confirmation-progress and Q12b's joined tab/window-closure rules. Q14
accepts the exceptional changed-target outcome and completes final confirmation.
All three designs are ready for separately requested implementation. This review
performed no implementation, prototyping, or runtime validation; current source
still implements the earlier partial Rust design.
