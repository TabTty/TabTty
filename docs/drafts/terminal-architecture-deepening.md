# Terminal architecture deepening interview

Status: all three candidates confirmed and ready for separately requested
implementation, 2026-11-16. Q14 completes the final shared-understanding review.
The user selected all three
candidates from the architecture review, in the order below. This continues
the [earlier architecture interview](architecture-deepening.md), whose Q1-Q27
remain accepted. Question numbers in this document are local to this interview.

## Review sequence

1. Deepen recording operations inside the complete Rust terminal module.
2. Concentrate native parser rules shared by admission and execution.
3. Concentrate ownership of explicit terminal-tab and window closure.

Complete each candidate's design discussion and confirm the shared understanding
before moving to the next. Implementation and prototyping require a separate
explicit request. No implementation or runtime validation is part of this review.

## Direction D1: complete terminal processing in Rust

Accepted by explicit user instruction on 2026-11-16, after clarification of
the current native-to-managed handoff. Move currently unsupported terminal
processing into Rust and complete terminal processing without switching to C#.
[ADR 0007](../adr/0007-complete-terminal-processing-in-rust.md) supersedes
ADR 0006 for the target architecture. The
[complete Rust design](../specification/native-terminal-core.md) records the
coverage, ownership, and validation consequences.

All supported receive/transmit encodings, VT/input semantics, reflow,
normal/alternate screens, terminal recording state and screen/history
observation use the authoritative Rust model. C# retains session ordering,
transports, actual file I/O, authorization/publication, and WPF. Supported
features never select a managed terminal, including under resource pressure.
The initial release still excludes a macro runtime.

Q1 and Q2's accepted responsibility and test-surface principles remain useful;
their handoff examples are superseded by D1. The original Q3 was not accepted;
the revised Rust-only Q3 below is now accepted. D1 is a design direction, not implementation
authorization or confirmation that the detailed design is complete.

## 1. Recording attachment

The [session and recording contract](../specification/session-and-recording.md)
defines recording behavior. ADR 0007 requires native terminal state throughout
recording operations. Ordered effects, no replay, and C# ownership of recording
files remain; model handoff and recording-position import disappear from the
target design.

### Starting evidence from the current partial implementation

- `src/TabTty.Core/TerminalOutput.cs` exposes native recording compatibility,
  native and managed callbacks, handoff preparation, and recording-position import.
- `src/TabTty.Core/TerminalRecordings.cs` coordinates these details in addition
  to format selection, receive-only projection, recording parts, and intervals.
- `src/TabTty.Core/RecordedTextPosition.cs` tracks the serialized Text log
  position that must survive handoff.

The opportunity is to remove implementation knowledge from the recording
caller's interface obligations. Adding a forwarding module without removing
that knowledge would not improve depth or locality.

### Confirmed decisions

**Q1 — Responsibility scope: accepted, updated by D1.** Keep Raw/Text/Terminal selection,
receive-only versus combined-output projection, part destinations, and file
ownership with the recording module. Concentrate terminal-side recording
attachment/detachment, eligibility and serialized Text position inside the
Rust terminal module. The original answer also assigned handoff preparation
and position transfer to the terminal module; D1 removes those operations
entirely. The recording module still initiates recording interval changes;
the terminal module handles their internal mechanics. Q4 selects the interface
shape below.

The existing session ordering of receive processing and recording changes
remains in force; this allocation does not move session scheduling into the
terminal module.

**Q2 — Test surface: accepted, updated by D1.** Exercise complete recording operations through
the same interface used by the production caller. At the terminal recording
seam, use the real Rust implementation and observe emitted Text/Terminal
bytes and terminal output across resize, screen/recording transitions,
incomplete receive units, and effect-delivery failures. Every supported path
must remain native. Existing recording/session integration
checks continue to cover Raw bytes, format coordination, and real file writes.
Preserve direct expected-output cases; the former C# implementation can be an
isolated test reference for native-versus-managed comparisons, not a production
fallback. Do not make callback wiring or private helper invocation counts
the acceptance surface. A controlled test adapter does not replace actual
file-writing and session integration checks.

**Q3 — Failed activation: accepted after D1.** If preparation for a new recording interval
fails after only some projections are ready, leave the new interval inactive
for every selected recording format. The recording module owns the group-level
outcome; the terminal module owns cleanup of its Rust attachment and eligibility
state. The caller must not restore callbacks or interval counters manually.
The previous handoff-preservation clause is withdrawn under D1. For an
established session, continue reception in Rust when
terminal state remains valid; an unrecoverable terminal-state failure follows
the existing affected-session failure policy. Initial recording setup failure
still prevents transport startup. File cleanup retains its existing owner and
actual-completion rules. Existing output and files are not part of a rollback
transaction.

**Q4 — Recording operation interface: accepted, option C.** Use three named
terminal operations: `BeginRecording(projections)`, `CutoffRecording()`, and
`AbortRecording()`. The terminal module and managed adapter own the interval
lifetime, effect delivery, and terminal-side cleanup. The caller does not own
an interval handle or restore callbacks/counters. C# start/resume/retry use
Begin; orderly pause/finalization use Cutoff; recording failure uses Abort.
C# retains group/file state, Raw admission, and actual I/O completion. EOF
and terminal disposal remain separate terminal-lifetime operations. Preserve
Q1-Q3, including all-formats-inactive activation failure. ABI representation
and implementation-specific type layouts are not selected by this decision.

**Q5 — Candidate 1 final shared understanding: accepted.** Q1-Q4 and the operation
contract below are confirmed. The recording-interface design is complete;
the user explicitly requested proceeding to candidate 2's parser discussion. This does
not complete the full Rust replacement or candidate 3's explicit-close design,
and does not authorize implementation or prototyping.

The following completion of Q4 is accepted:

- Outcomes distinguish applied, already inactive/no change, and rejected
  operations. They report the resulting terminal-side recording activity,
  terminal usability, and any failure reason. They never require the caller
  to drain native results or repair state. The C# recording module owns the
  corresponding all-formats group result and actual file-operation completion.
- Begin while an interval is active is rejected before mutation; it must not
  replace the active interval or reset serialized Text position. Q3's
  all-inactive preparation-failure guarantee applies to a valid new-interval
  request, not to rejecting an invalid request while another interval is active.
  The existing public Resume no-op for an already-recording group remains a
  C# concern and need not call native Begin.
- Cutoff or Abort when already inactive completes without new output, an
  interval increment, or decoder/parser/position changes. EOF and a subsequent
  Cutoff do not duplicate tails. A recording-disabled connection cannot use
  these operations to enable formats absent from its captured settings.
- The C# module/adapter verifies session and recording owner identity in the
  same ordered event that applies a completion or failure. An old writer or
  opening request cannot call Abort against a newer interval or activate an
  obsolete part. Internal identity metadata suffices; no public interval
  capability is introduced.
- Cancellation or invalidation before activation prevents Begin from committing
  and leaves cleanup with the prepared file owner. Once an ordered operation
  commits, cancellation of a caller's wait cannot undo that result, abandon
  cleanup, or imply durable storage. Any subsequent stop follows normal session
  ordering. Preserve the existing serialization of pause/resume/retry/close;
  do not make pause preempt an in-progress file-open operation.
- Begin success means native activation is complete, followed by the C# group's
  non-failing publication in the same event. Cutoff success means terminal-side
  final delivery/admission and eligibility closure are complete; it does not
  promise disk durability. A successful C# Pause result requires completion of
  the admitted writer work, durable flush, and close; failure at those stages
  is reported as failure, not a successful Paused result.
- A recording-delivery failure preserves valid terminal updates and unrelated
  effects from that native operation. The adapter does not replay the operation,
  drop its terminal replies/display updates, or switch to C#. It performs the
  recording cleanup internally and retains every issued buffer/file owner
  until actual completion. Unrecoverable terminal failure still follows the
  existing affected-session policy.

Q2's observable verification must include repeated/invalid calls, cancellation
before/after activation, old-writer failures after a new interval, and output
delivery failure accompanied by valid non-recording effects. Verify complete
old/new part bytes, terminal state and replies, resulting group state, and
actual resource ownership. These are later implementation checks, not tests
executed by this design review.

### Interface comparison for Q4

Three independent design explorations compared the same seam: the C# recording
module calls a managed terminal adapter that owns access to the real Rust
terminal module. These are documentation sketches, not implemented interfaces
or a new ABI. The user accepted C in Q4; A and B remain comparison history.

**A. One typed command entry point.** A closed command union describes the
recording intent; one operation returns the terminal-side activity, failure,
and terminal usability.

```text
ApplyRecording(Begin(projections) | OrderlyCutoff | FailureAbort) -> outcome
```

The adapter owns effect delivery and cleanup. This concentrates dispatch and
has few entry points, but callers still need to understand every command and
ordering rule. One method does not by itself make the interface deeper; an
open-ended command bag would weaken type checking and locality.

**B. An explicit interval capability.** Successful activation returns one
opaque interval handle; the recording module retains it until finishing or
aborting that interval.

```text
OpenInterval(projections) -> interval or failure
interval.Finish() -> outcome
interval.Abort() -> outcome
```

The handle owns recording eligibility, not the terminal, projections, or files.
It makes old/new interval ownership explicit and can protect against stale
use. It also exposes handle retention and one-time consumption obligations.
The adapter still owns final-effect delivery, just as in A and C.
C# reference aliasing and garbage collection cannot
enforce the required ordered lifetime; a session-owned registry and runtime
checks remain necessary. For one fixed-format group per session, this adds
caller obligations without an established need for transferable ownership.

**C. Named operations with module-owned interval lifetime (accepted).**
Expose the three distinct terminal meanings directly, keeping interval
identity and effect delivery inside the terminal module and its adapter.

```text
BeginRecording(projections) -> outcome
CutoffRecording() -> outcome
AbortRecording() -> outcome
```

The C# recording module uses Begin for start/resume/retry, Cutoff for an orderly
pause/finalization, and Abort for recording failure. It continues to own the
group state and complete file operation. Rust does not duplicate the C#
Opening/Pausing/Paused/Failed states or distinguish a new part from a retry.
Projection selection comes from the fixed connection snapshot; Raw remains
in C# and is never a native projection.

The normal caller requests a complete recording operation, for example
`await recording.ResumeAsync()`. Inside that module, all selected files and
writer admission are prepared before the ordered Begin; success is published
for the whole group without another fallible allocation or callback. Failure
leaves all formats inactive, with each owner completing its cleanup. The
caller receives an outcome, not instructions to drain native results, restore
callbacks, or dispose an activation handle.

Cutoff submits eligible tails once and ends eligibility; it does not mean that
bytes are durable. The recording module waits for admitted writer work,
flush, and close outside the session mutation guard. Abort discards unissued
recording-only pending state without normal tail submission. Neither operation
destroys decoder/parser, screen/history, or the receive-only projection that
must continue updating while recording is inactive. Receive EOF remains a
separate source-finalization operation, and terminal disposal remains a
separate lifetime operation. Their ordering must prevent duplicate output.

| Dimension | A: commands | B: interval capability | C: named operations |
| --- | --- | --- | --- |
| Depth | Complete operations behind one entry point; command knowledge remains | Hides projection mechanics but exposes interval lifetime | Complete terminal operations with no caller-managed interval or effect delivery |
| Locality | Command handling and cleanup concentrate in adapter/Rust | Ownership checks span handle, registry, adapter, and recording module | Terminal semantics concentrate in Rust/adapter; group/file lifecycle stays in C# |
| Seam | Typed intents and outcomes | Owned capability and operation outcomes | Explicit operation names and outcomes |

A and C can provide equal depth and leverage by owning the same complete
operations. C was selected because its operation names make the existing
caller's intent explicit at each call site, with no present need for a command
union or B's public lifetime protocol. A would fit a caller that already
stores or transports typed commands, but no such requirement motivates this
seam. All alternatives still need explicit invalid/repeated/stale-operation
behavior; names alone cannot guarantee correct state transitions.

Dependency placement is common to these proposals. Rust is an in-process
dependency exercised directly, not replaced by a fake terminal interface.
The managed adapter owns bounded immutable native results and submits their
Text/Terminal bytes through a stable internal output seam. A production
adapter uses the C# recording group's bounded writer admission; a test adapter
captures bytes or rejects admission. Synchronous rejection closes group
admission and clears terminal recording eligibility within the same ordered
operation. Asynchronous writer failure closes group admission immediately and
delivers terminal cleanup through the session's existing order. Old writer
completions remain bound to their old owner. Real-file/session integration
checks remain required alongside the controlled adapter tests.

### Existing behavior and current-source evidence

The source investigation confirms constraints to preserve, rather than new
user decisions:

- The session orders receive, local echo, resize, recording changes, and close.
  `TerminalSession.PauseRecordingAsync` applies cutoff under the session state
  guard and then waits for the writer outside that guard. Resume/retry finishes
  the prior writer and opens the next part before applying the new interval.
- `TerminalRecordings.Cutoff` flushes eligible output, detaches recording, and
  advances the interval. `Abort` discards pending projection buffers and detaches
  recording without flushing or advancing the interval. These operations have
  different purposes and cannot be collapsed into an ambiguous stop operation.
- `SessionRecording.Fail` stops writer admission and reports failure; it does
  not directly mutate the terminal interval. Retry establishes a fresh part.
- `RecordedTextPosition` is serialized Text log column/grapheme state, not the
  display cursor or a file offset. The current implementation imports it on
  handoff. Under D1 Rust owns this state throughout; interval reset follows
  the recording eligibility contract without import/export between engines.
- Current `TerminalRecordings.Start` has no active-interval guard; public
  `TerminalSession.ResumeRecordingAsync` supplies an already-recording no-op.
  Q5 makes direct native Begin rejection explicit so callers need not rely on
  a destructive internal Start being unreachable. Inactive Cutoff already
  returns without advancing the interval; Abort does not submit tails.
- Current pause cancellation after cutoff stops waiting, not the writer's
  ownership. Resume cleanup closes unadopted and late file preparations.
  These actual-completion guarantees remain; the new adapter also filters old
  writer failures before applying its argument-free Abort to a current interval.
- Current ordinary writer failure stops admission without immediately clearing
  terminal recording eligibility, and the receive-EOF path awaits writer
  completion inside the session state guard. The target closes the eligibility
  through the ordered adapter and moves disk completion waits outside terminal
  mutation. These are target changes, not claims about existing behavior.

Source locations: `TerminalSession.cs` recording operations,
`TerminalRecordings.cs` Start/Cutoff/Abort, `SessionRecording.cs` Fail, and
`TerminalOutput.cs` recording-position import. Behavioral authority remains
the session and recording contract; the new implementation allocation is ADR 0007.

### Existing test evidence

The maintained recording tests check actual files, including split-unit resume,
reflow, Text TAB behavior, and independent formats. The split-unit resume case
currently checks the new part, not the complete old part. Native terminal
contract comparisons check terminal state and replies without recording sinks.

The 2026-11-15 adoption artifacts also report successful complete-file/part
comparisons for seven broad and thirteen strictly native cases. Their broad
editing cases hand off during resize before pause/resume, so they do not alone
establish handoff at pause. Retain this as evidence for the earlier partial
implementation. Under D1, maintained tests must additionally demonstrate
complete native execution through those transitions, rather than test a
production handoff. The review has run no tests.

## 2. Native parser rules

In progress after accepted Q5. The user explicitly requested proceeding to this
candidate. Source and test inspection is complete and Q6's shared scope is
accepted. Q7 selects the shared admission/execution walks and Q8 confirms
candidate 2's final shared understanding. No implementation, prototype, or new
test execution occurred.

### Starting evidence from the current partial implementation

`src/TabTty.Terminal/src/model.rs` contains an execution `Model` and an admission
`Probe`. They share some helpers but separately decide important aspects of
the same input:

| Rule | Execution | Admission |
| --- | --- | --- |
| Incremental UTF-8, malformed prefixes, end-of-source replacement | `Model::decode`, line 646 | `Probe::decode`, line 1656 |
| Initial BOM and ESC/C0/CSI transitions | `emit`/`apply`/`escape`, lines 682/700/1001 | `Probe::emit`, line 1715 |
| CSI spelling, parameters, command classification and limits | `csi`/`apply_sgr`, lines 1039/1912 | `supported_csi`, line 1809 |
| Source-last-graphic and display-cluster limits | Model graphic handling, line 766 onward | `Probe::graphic`, line 1631 |

`consume_control_string` (line 1545) already shares string termination,
cancellation, ESC reinterpretation, and the string limit; Unicode `joins` is
also shared. The opportunity is to concentrate the remaining interpretation
rules, not to add a wrapper around entirely independent implementations.

One static difference is the order of invalid-spelling and parameter-limit
checks for SGR. For ESC `[` followed by `?`, 32 semicolons, then `m`, Probe
rejects the 33 main parameters while direct `apply_sgr` returns without an
effect on seeing the invalid `?`. Native FFI admission rejects this input
before execution. This is evidence of different rule paths, not evidence of
an admitted operation corrupting terminal state; no such runtime defect was
established by this review.

### Existing safety obligations and dependencies

- `NativeTerminalCore.TryPrepare` performs native preflight, reserves quota,
  and supplies the allowance. Native FFI repeats admission before feed or
  complete (`lib.rs`, lines 320/328). `Model::feed` itself does not provide
  this guarantee. Preserve rejection before authoritative native state/effect
  changes; earlier accepted operations and C# Raw admission retain their
  existing ownership and ordering rules.
- Probe needs source decoder/BOM/escape/string/parameter/last-graphic state
  and the display's current cluster. EOF processes sources in order without
  resetting that shared display cluster. Input bytes and a source ID alone
  are insufficient. The target must also preserve source charset, incomplete
  unit provenance, and the independent ordinal/eligibility of embedded C0.
- Admission additionally depends on geometry, cursor, row contents and
  submitted revisions, pending recording output, actual capacities, and effect
  growth. A parsed command is not proof that applying it fits the budget.
  Any prepared commands or retained payload also consume budget.
- The current 1024-byte preflight cache stores an owned payload and required
  charge, not parsed commands. Its key includes exact bytes, operation,
  source, flags, and a generation covering state/capacity changes; allowance
  is rechecked. Neither that cache size nor its representation is a new
  interface requirement. Display/content revision alone cannot replace all
  of those validity dependencies.
- Current `Unsupported(2)` conflates omitted features, parser/cluster limits,
  effect-size refusal, and insufficient allowance before managed handoff.
  Under D1 these need their existing product meanings: excluded well-framed
  controls are consumed/ignored in Rust; resource pressure follows the narrow
  or affected-session policy; an unimplemented contracted feature prevents
  replacement readiness. None selects C#.
- The parser and admission/execution logic are in-process dependencies.
  Exercise the real Rust module through the production interface. No fake
  parser, external parser port, new dependency, or public parser-state handle
  is justified by this candidate. The recording output adapter from Q4 stays
  at the output seam.

### Confirmed scope

**Q6 — Shared interpretation scope: accepted.** Share the source-side rules from
incremental receive decoding through charset handling, VT framing, parameter
interpretation, completed-unit classification, parser limits, and continuation state.
Admission and execution must use the same definition of what the input means;
do not maintain a separate CSI support list or independently implemented
decoder/parser state machines. Sharing rules does not merge source states or
forbid an isolated preview state. Preserve DATA/stderr independence, chunk versus EOF semantics,
local-echo ordering, and recording-unit provenance across intervals.

Keep resource admission and terminal-state execution as distinct duties:
admission validates the operation and reserves its bounded peak charge before
authoritative mutation; execution applies admitted meanings to the appropriate
Rust projections. The shared interpretation module does not acquire the
screen/history, recording-file, session-scheduling, or transport responsibilities.
State-dependent screen effects remain execution's responsibility, and their
possible memory/effect growth remains admission's responsibility.

This scope was selected because it removes the duplicated knowledge needed
to add a codec/control or fix fragmented-input behavior. Sharing only a command
list would leave decoder and state-transition discrepancies outside the seam.
It increases depth and leverage for both consumers, and improves locality of
interpretation fixes. Q7 selects the shared-walk structure below. Private type
layouts and cache optimization details remain implementation design, subject
to the accepted preparation-validity and memory-ownership obligations.

**Q7 — Internal structure: accepted, option A.** Use one shared interpreter in
an isolated admission walk and then an authoritative execution walk. The
normal valid path walks the same input twice, starting execution from the
original state, not the preview's final state. Preserve pre-mutation admission,
source independence, owned results, the existing resource/failure policies,
and Q1-Q5 recording behavior. No owned parsed-command tape or whole-terminal
staged-publication redesign is selected.
The ordinary caller does not manage parser state, a preparation handle, or
native effect delivery. Parser and terminal-state responsibilities remain
distinct even when one terminal operation coordinates them internally.

**Q8 — Candidate 2 final shared understanding: accepted.** Q6-Q7 and their
preparation, failure, and verification obligations below are confirmed. The
parser design is complete, and the user requested proceeding to candidate 3.
This is a design confirmation, not implementation authorization or proof of
performance.

The final understanding is: shared interpretation runs first against isolated
preview state, then against the same original authoritative state and input;
all preview/expansion/output capacity is charged and reserved; stale
preparation is discarded before authoritative execution; native admission
rejection or cancellation before execution changes no part of that native
operation; after execution starts, wait cancellation does not undo it or
abandon effects/cleanup. Q5 preserves valid terminal updates and replies when
recording alone fails. Source independence, complete contracted Rust coverage,
and the existing narrow/affected-session resource policies remain binding.
Validation uses real Rust through the production interface and independent
expected outputs, covering source/encoding/chunk/state transitions, limits,
rejection invariance, stale preparation, cancellation, ownership and measured
performance. Independent design audits found no additional product choice;
private types, exact workspace representation, and cache tuning remain
implementation details. Candidate 3 now follows this confirmed design.

### Internal structure comparison for Q7

Three independent design explorations used the same ordinary caller: one
complete ordered terminal operation through the managed adapter. The sketches
below describe private Rust interfaces, not a new ABI or an exposed
prepare/commit protocol. All alternatives keep C# session ordering and global
quota ownership. Internal preparation/reservation/execution may span adapter
calls; they do not bypass that ownership or call a writer during parsing.
The user selected A in Q7; B and C remain comparison history.

**A. Shared interpreter, admission walk followed by execution walk
(accepted).** One interpretation module streams temporary meanings to a
private Estimate or Execute consumer using the same rules.

```text
walk(source_states, input, meaning_consumer) -> interpretation_outcome

process(input):
    reserve bounded preview workspace
    walk(isolated source states, input, Estimate(base state + preview))
    reserve the admitted state/effect peak
    validate the original input, state basis, and reservations
    walk(authoritative source states, same input, Execute(reserved storage))
```

Estimate does not emit effects or advance live source state. Execute begins
from the original state, not Estimate's ending state. Both use the same codec,
charset, framing, spelling, parameter, parser-limit, source-REP, and continuation
definitions. Consumers cannot reparse CSI or maintain private support lists.

The state-dependent screen work still needs a sound preview: read-only current
state with charged bounded changes, shared screen-transition definitions, or
conservative upper bounds where sufficient. Do not replace the second parser
with a second independently implemented screen engine. Cluster validity and
mode/reset consequences cannot be omitted by merely counting input bytes.
Full-screen cloning is not assumed, and preview workspace is charged before
growth. This is real remaining complexity, not free computation.

A keeps preparation local to the operation and needs no retained command tape.
The normal valid path walks twice; invalidation can require re-preparation.
Source preview, capacity checks, and temporary effects still have a memory
cost. Two walks are not a claim of improved throughput or the lowest peak
memory for every workload.

**B. Owned parsed-command plan.** Parse once for each distinct interpretation
stream into compact owned commands, with candidate source continuations and
unit provenance. Admission and execution consume that same interpretation.

```text
plan = prepare(source_basis, owned_input, charged_plan_storage)
admitted = admit(current_projections, plan, reservations)
consume(admitted)
```

Consumption commits candidate source continuations with admitted terminal
changes. Owned effects still follow the adapter's ordered delivery and
actual-completion ownership; consuming a plan is not a delivery receipt.

Only prepare belongs to the source interpretation module; admission and
execution remain the terminal owner's duties. Commands retain relative
meanings rather than guessed absolute edits. REP can keep source-resolved
text plus a count, with expanded state/effects admitted separately. Compatible
receive interpretations can serve both projections, but incompatible source
continuations must never be merged to achieve a parse-once claim.

Parsing success is not resource admission. Plan storage, decoded text,
parameters, candidate continuation state, provenance, effect preview and copies
are all charged before allocation/growth. The plan is internal, single-use,
and tied to its complete state/capacity basis. It eliminates re-decoding and
re-classification, but not state-dependent admission or execution. Its extra
ownership and validity rules are justified only by the value of retaining the
interpreted result; no speedup is established here.

**C. Stage real changes, then publish.** Run interpretation and actual reducers
against isolated, bounded changed state. Publish once the entire operation has
been validated and all reservations have succeeded.

```text
stage = begin_bounded_stage(current_owners)
interpret_and_apply(input, stage, reserve_before_each_growth)
validate_complete_operation(stage)
publish_without_allocation_or_external_callbacks(stage)
```

This retains actual resulting changes rather than A's estimation preview or
B's command plan. Each screen/history/source/recording owner supplies isolated
changes, coordinated by the terminal operation; the parser does not acquire
screen/history or recording ownership. Publication is unobservable until complete under session ordering,
not a promise of one CPU atomic instruction or a filesystem transaction.
External delivery still follows Q4/Q5 after the terminal commit.

Bounded copy-on-write pages or consolidated deltas avoid assuming a whole-model
clone or unbounded undo history. Old and new allocations remain charged until
actually released, even if the final screen will be smaller. This is coherent
but changes the storage and publication discipline of many state owners;
parser sharing alone does not require that redesign.

| Dimension | A: shared walks | B: owned commands | C: staged changes |
| --- | --- | --- | --- |
| Interpretation | Same rules run for admission and execution | One interpretation per distinct stream; retained meanings reused | One interpretation while real changes are staged |
| Extra temporary ownership | Source preview and state-dependent estimation workspace | Command storage and validity, plus admission workspace | Isolated source/screen/history/recording changes and effects |
| Depth at the ordinary caller | Complete operation; adapter owns preparation | Same complete operation; no public plan | Same complete operation; no public stage |
| Locality | Interpretation fixes in one module; estimator/executor duties remain distinct | Interpretation fixes plus a common command representation; plan ownership adds rules | Interpretation and actual changes agree by construction; publication rules reach all owners |
| Private seam | Temporary meanings consumed during each walk | Owned semantic commands passed to admission/execution | Terminal operation coordinating isolated state publication |

**Selected: A.** It removes the specific duplicated interpretation
knowledge identified in this review while preserving the current admission-
then-execution structure. B offers a useful reusable result at the cost of
command storage/lifetime machinery; C expands this candidate into terminal
storage redesign. All three can give the ordinary caller equal depth. A is
preferred for the scope of change and locality, not because fewer private
types or two passes prove better performance.

### Obligations retained with accepted Q7

These obligations follow Q6, ADR 0007, and the existing session/resource
contracts and are retained by the selected A structure. The comparison did
not reopen their product behavior.

- Keep the exact requested input and the complete preparation basis stable
  through admission and execution/publication. If hidden adapter phases lose
  that exclusion, validate source/configuration/projection/recording/geometry,
  pending-effect and capacity dependencies before mutation. Discard invalid
  work and re-prepare; a display/content revision alone is insufficient.
  Any cache remains private and checks exact input identity and current
  allowance. Track the current operation's held reservations and expected
  accounting changes explicitly: do not charge an existing reservation twice
  or invalidate preparation solely because that reservation was granted.
  Unexpected basis changes still invalidate it. The old 1024-byte cache is
  not a target requirement.
- Source-local last graphic is distinct from each projection's display
  cluster. Apply charset/reset/save-restore changes before interpreting later
  bytes that depend on them. Preserve ordered EOF and do not reset the shared
  display cluster merely when changing the source being finalized.
- Charge input ownership, source snapshots, preview/plan/stage capacity,
  output expansion, overlapping old/new allocation, and native/managed copies
  before allocation or growth. Compact REP or text runs are not permission
  for unbounded effects. No early history eviction or reduced retention is an
  admission shortcut.
- Rejection by native admission changes no authoritative native state/effects
  from that requested operation, including a valid prefix. Any scheduling
  batches are chosen before operating on them; do not turn one rejected
  operation into a committed prefix. Earlier accepted operations and C# Raw
  admission retain their separate ordering and ownership contracts.
- Cancellation before authoritative execution begins releases private
  preparation with no prefix effects. Once authoritative execution begins, cancelling
  a wait does not undo it or abandon effects/cleanup. Unexpected failures
  after admission are not ordinary rejection with a rollback guarantee;
  preserve the affected-session policy and never replay partial execution.
- Recording-only refusal remains narrow under Q5. A preview may stage that
  result, but it does not change live eligibility before terminal admission.
  Preserve valid terminal updates and unrelated replies/effects, complete
  internal Abort cleanup, and retain issued ownership until actual completion.
  There is no C# terminal fallback in any failure path.
- Parser interpretation and state computation are in-process dependencies,
  tested with real Rust through the production terminal interface. The stable
  recording output seam retains its production admission and test capture/
  rejection adapters. Private walk counts, command layouts, and stage types
  are not acceptance criteria. Functional, overload, and performance checks
  are required after implementation authorization; none ran in this review.

### Verification consequences of the accepted scope

The existing production-interface/real-Rust test principle continues. Preserve
independent expected state, replies, and complete Text/Terminal bytes in
addition to isolated C# reference comparisons. Comparing two consumers of a
shared rule cannot by itself establish that the rule implements the contract.

Existing source coverage includes fragmented UTF-8/CSI/OSC, source-independent
partials and EOF, parser/cluster limits, prefix-preserving rejection, and
preflight-cache validity (`lib.rs` tests and `admission_tests.rs`). However,
`check_preview` in `admission_tests.rs:114` compares bulk Probe with one-byte
Probe, not Probe with Model execution. `NativeTerminalContracts.cs:48` requires
handoff after its native prefix, and line 126 expects legacy encodings to start
managed. Those cases are evidence for the partial implementation only. The
2026-11-15 Debug/Release artifacts record 29 Rust checks passing; this review
has not rerun them or validated the replacement.

Later verification must cover all 48 encodings, source interleaving, relevant
byte splits, malformed-prefix recovery, codec-owned ESC/SO/SI, raw versus
decoded C1 behavior, CAN/SUB/ESC/ST/BEL, full command spelling, numeric overflow,
and immediately-below/at/above-limit cases. Preserve the 32-main/16-subparameter,
4096-byte sequence, 1 MiB string, and 256-scalar cluster contracts. Include
valid prefixes before rejection and continuation after ignored controls.
Repeat across resize, alternate screens, recording transitions, and observation
while remaining native. Operations rejected by native admission must preserve
authoritative state and native effects; accepted operations must have the specified meaning
and execute eligible embedded controls once. Test observable results and failure
classification rather than helper call counts or the old cache's layout.

## 3. Explicit closure

Complete after accepted Q14. Q9 accepts the closure module's scope. In Q10
the user chose to align both bulk and automatic disconnected-tab closure with
explicit closure, superseding the earlier automatic-result-retention rule.
Q11 accepts named complete operations and Q13 accepts failure/abandoned-wait
ownership. Q12 was temporarily held because the previous explanation was
unclear. The user subsequently accepted the clarified Q12a and Q12b: ordinary
same-target progress does not restart confirmation, and window closure joins
an existing tab close without requiring another click. Q12 is no longer on
hold; Q14 accepts the exceptional changed-target outcome and confirms the
complete design. The review has not changed implementation, produced a
prototype, or run tests. The accepted
scope and behavior are consolidated
in the [tab/window closure design](../specification/tab-and-window-closure.md).

### Starting evidence and caller obligations

The current UI repeats explicit closure orchestration across paths in
`src/TabTty.App/MainWindow.cs`:

| Path | Current behavior |
| --- | --- |
| `CloseTab`, line 821 | Confirm; mark closing; end AI sharing; start transfer-window closure; disconnect; await connection/transfer completion or cleanup adoption; remove the tab |
| `OnClosing`/`FinishCloseAsync`, lines 867/906 | Capture all terminals and an active subset; confirm once; commit window-close intent; close transfers; disconnect active terminals; wait; close the WPF window |
| Window close with no active terminals, line 879 | Commit closure and start transfer-window close tasks without awaiting them |
| `OnTerminalDisconnected`, line 138 | Automatically remove an eligible disconnected tab without closing its transfer window |
| `CloseDisconnectedTabs`, line 593 | Remove eligible disconnected tabs through the same removal path, preserving transfer windows in current code |
| Tab move and pane/group changes | Keep session lifetime separate from visual placement; close an empty source only after destination placement commits |

The ordinary UI caller knows how to combine `Disconnect`, `Completion`,
`CloseFileTransferAsync`, `AbandonCleanup`, removal, and application cleanup
adoption. `RemoveTerminalTab` also repeats sharing cleanup. A forwarding
wrapper that leaves this recipe with both tab and window callers would not
increase depth or locality.

Source inspection also identifies obligations that completion results must
represent accurately:

- `WaitForCleanupAsync` (`MainWindow.cs:918`) offers the existing five-second
  choice. Close with loss returns only after `App.OwnCleanup` accepts the
  task; a full cleanup-owner allowance keeps the caller waiting. That owner
  observes faults and retains tasks until actual completion (`App.cs:198`).
- `FileTransferWindow.CloseWithTerminalAsync` (`:494`) waits at most two seconds
  for work before closing the view. Its completion therefore differs from
  provider I/O completion. A second call while closing returns immediately
  rather than joining the earlier close task. File-operation ownership already
  retains pending provider work separately until completion.
- The explicit tab/window await paths do not contain a complete terminal
  outcome for a faulted cleanup task; transfer-owner closure also differs from
  transfer-window-only closure in reporting known unresolved outcomes. These
  are source-level gaps to address in the target design, not reproduced failures.
- Confirmation uses a nested modal dispatcher and precedes the closing flags.
  Connection and asynchronous state can change during review. Deferred file
  activation is not a general freeze of session, tab, or generation state.
  Closure must use current target/ownership evidence before effects start.
- Current code includes macro-related close branches. They are historical
  source facts, not requirements to add a macro runtime to the initial release.

### Retained terminal state has a separate lifetime

`TerminalTab.ConnectAsync` (`TerminalTab.cs:273`) awaits session disposal after
connection end, then retains that session and its final output.
`TerminalSession.DisposeAsync` (`TerminalSession.cs:781`) finishes transport
and recording work but does not destroy the retained terminal model.
The production calls to `TerminalOutput.ReleaseNativeState` found by inspection
release the receive-only recording projection, not the combined retained model
on final tab removal. The native handle has finalization; this is evidence that
deterministic final ownership release is not explicit, not proof of a permanent
memory leak.

Under ADR 0007, EOF/disconnect and final retained-state release stay distinct.
Moving a tab or unloading its WPF view cannot release its Rust terminal state.
A retained disconnected tab keeps that state; final removal ends the applicable
terminal ownership through its owner. Under amended Q10, automatic removal
and user-invoked bulk closure also close associated transfer windows using
the same complete closure workflow. Disconnect without tab removal retains
the transfer results.
Owned immutable snapshots/effects and issued I/O keep their own charges until
actual release. Final terminal release does not turn retained output into a
managed terminal model. Independently owned snapshots remain valid without
borrowing destroyed native storage.

### Existing policies to preserve

The [profile/lifecycle decisions](connection-profiles-issue.md),
[window contract](../specification/persistence-and-windows.md),
[session/recording contract](../specification/session-and-recording.md),
[transfer contract](../specification/file-transfer.md), and
[broadcast/split contract](../specification/broadcast-and-split-views.md)
remain authoritative:

- Confirm closing a live/connecting terminal tab; a window combines affected
  connections and active file work in one confirmation. A disconnected tab
  has no connection warning. Rejecting confirmation causes no close-induced
  cancellation or removal and allows deferred activations to resume.
- Capture required approval before close-induced cancellation of associated
  file work. Explicit, bulk and automatic tab closure close associated transfer
  windows under amended Q10. Disconnect/peer loss retains transfer results
  while the tab and application remain running. A peer's disconnect itself
  is not undone by declining a later closure decision.
- After approved closure, cancel affected unsent input and establish the
  final receive cutoff with once-only finalization. Transport and log cleanup
  retain separate deadlines/ownership. The five-second choice defaults to
  Continue waiting, and that choice overrides automatic stall abandonment.
  Close with unsaved data does not release issued buffers before completion.
- Closing/removing the final tab of any kind closes its window; settings/editor
  tabs count and empty groups do not. Initial construction is not final-tab
  removal. Final application exit follows the existing activation/exit rules;
  a transfer-results window does not create a background-only application run.
- Moving a tab preserves its session, logging, transfer work, and state. The
  accepted BroadcastInput module owns complete move intent and existing layout
  policy owns the empty-source-window rule. Candidate 3 uses those contracts;
  it does not take over move/capture cleanup or change pane/group removal into
  connection closure.
- Settings/editor Save/Cancel and unsaved-change policies stay with their
  existing owners. Closure adds no autosave or new settings dialog policy.
  AI sharing/handle invalidation uses the accepted AI module. Authentication,
  vault lifetime, and future-macro constraints are unchanged.

### Confirmed scope and disconnected-tab behavior

**Q9 — Explicit-closure ownership scope: accepted.** Concentrate one complete
explicit tab/window close workflow in a C# application module: identify and
review affected targets, capture the existing confirmation decision, validate
target/generation ownership, coordinate accepted input/AI/transfer/session
termination, own the wait and failure result, use existing cleanup adoption
when allowed, and request visual removal through existing layout/application
coordination. Include deterministic final retained-terminal ownership release.
The UI invokes a close intent, presents required decisions, and receives the
operation outcome; it does not assemble the cleanup recipe.

Actual transport, recording, SFTP/provider, broadcast/input, and native-terminal
resources stay with their existing owners. The module coordinates their
complete operations rather than duplicating them. C# owns closure orchestration
under ADR 0007; Rust still owns terminal interpretation/finalization/state.
Visual closure, bounded cleanup adoption, and actual resource completion must
remain distinguishable; removing a view is not proof that all I/O has ended.

This scope was selected to improve depth and leverage for both explicit
callers and to concentrate order, duplicate-request, and ownership knowledge
for locality. Concrete interface shape, concurrent-request outcomes, and
confirmation-staleness handling follow the scope decision. Native WPF decisions
and layout commits remain presentation/coordinator duties behind the appropriate
internal seams.

**Q10 — Bulk and automatic disconnected-tab closure: accepted, amended.**
The user first aligned user-invoked bulk closure with explicit tab/window
closure, then explicitly amended automatic removal to follow the same policy.
Both close associated transfer windows using the existing cleanup/reporting
rules. The bulk command selects eligible disconnected tabs in the current
window; automatic closure selects its affected disconnected tab. Disconnect
without removing the tab still retains transfer results while the application
runs. Final application exit follows its existing rule.

This deliberately changes both current source paths. The earlier interview's
automatic-result-retention recommendation and acceptance are superseded. The
original bulk contract was unspecified; the original automatic-retention rule
is now explicitly replaced. Do not broaden either target to live/connecting
tabs, other windows, or unrelated result windows. Do not add a connection
warning to disconnected targets. Automatic closure still performs required
cleanup/reporting and wait/loss decisions; its trigger is not permission to
silently discard known unresolved results or unsaved data.

### Accepted interface, coordination and completion decisions

**Q11 — Closure operation interface: accepted C.** Use named complete
operations for tab/window/disconnected-tab closure, owning the complete Q9
workflow and amended Q10 behavior. Confirmation and native UI remain behind
the appropriate adapters. Actual resource and layout ownership stays unchanged.
The caller supplies stable IDs, not a review ticket, termination recipe, or
cleanup/disposal obligation. Automatic lifecycle notifications enter the same
complete workflow for their affected tab; no fourth user command is needed.
Q12a-Q12b independently establish the coordination rules below.

**Q12a — Progress during confirmation: accepted.** If a transfer on the same
reviewed target finishes while the close confirmation is open, accept the
existing answer and continue closing those targets without restarting the
interaction. Ordinary progress/completion that reduces reviewed work is not
replacement of its target or connection generation. It does not authorize
additional targets or effects. A peer disconnect may also trigger automatic
closure; any resulting overlap follows Q12b's ownership rule.

**Q12b — Window close while one tab is already closing: accepted joining.**
In a window with A and B, A's previously approved close may be saving its log
when a window-close request arrives. Keep A's existing execution, obtain any
required confirmation for remaining targets such as B, and close the window
once the targets meet the existing closure requirements. No second click is
needed because A was already closing; no duplicate A termination is started.

The earlier combined Q12 question was held because its explanation was unclear.
Its strict partial-overlap rejection was never accepted and is superseded by
the clarified joining choice. This is independent of interface C's selection.
Use one execution/result per target lifetime and generation, observed by each
participating intent. Exact repeats and automatic/bulk overlap use the same
ownership rule. A wider intent does not widen an earlier approval.

If the later window confirmation is declined, B receives no new close-induced
effects, while A's earlier accepted closure continues. Results distinguish
new effects from joined and independently completed work. Joined work retains
its existing cleanup owner, deadline, loss decision and capacity accounting;
joining does not reset the five-second clock, duplicate a prompt, undo Continue
waiting, or release issued work early. Window completion follows each target's
actual required visual/cleanup disposition, not an unconditional aggregate
success.

Source inspection supports these examples: `MainWindow.CloseTab` marks the tab
closing and awaits cleanup without disabling the window (`:821-836`), whereas
window close captures all its terminal tabs (`:877-896`). Confirmation uses
`ThemedMessageBox`'s modal dialog, and profile-file activation is deferred by
`App`'s modal coordination. Do not explain the issue using a user manually
reconnecting or moving a tab in that blocked UI while its confirmation is open.
In-flight connection/file work can still complete. These are source facts,
not newly executed UI tests. Invalid approval for genuinely new/replaced targets
must not start their effects. The precise changed-target return behavior was
accepted in Q14, rather than inferred from Q12a alone.

**Q13 — Failure results and abandoned waits: accepted.**
Before effects begin, a declined/cancelled/invalid confirmation or failed
confirmation display produces a no-new-effects result. Once effects begin,
the module keeps ownership and observes every target's cleanup even if a
caller stops waiting or a view disappears. Expected failures produce an
immutable, bounded report of actual target dispositions, known loss or
unresolved outcomes, and remaining owned work; do not imply rollback or
unconditional success. Observation of eventual actual release is read-only
and imposes no cleanup or disposal duty on the caller. A failure to display
the log-loss choice grants no loss permission. Failure to deliver a required
unresolved-transfer report prevents silent dismissal and remains a reported
failure. Preserve retained terminal state if final visual removal fails.

### Interface comparison for Q11

Three independent designs considered the same Q9 ownership scope and the
then-accepted bulk part of Q10. Its later automatic-closure amendment applies
to the selected C workflow as recorded above. The following signatures
illustrate shapes, not implementation
or finalized public type names. The comparison includes ordinary callers,
confirmation interaction, outcome observation, and hidden ownership together.

**A — One typed intent entry point.**

```csharp
await closure.CloseAsync(CloseIntent.Tab(tabId));
await closure.CloseAsync(CloseIntent.Window(windowId));
await closure.CloseAsync(CloseIntent.DisconnectedTabs(windowId));
```

The module captures and reviews targets through its internal presentation
adapter, coordinates the entire workflow, and returns an outcome. A closed
intent union gives one entry point without free-form flags or caller-selected
cleanup steps. It has high leverage for dispatching different UI commands
through the same interface. The ordinary caller must construct the appropriate
intent; this is a small cost, not a loss of depth. Adding a new intent changes
the union and its complete semantics.

**B — Explicit review and decision.**

```csharp
var prepared = await closure.PrepareAsync(CloseScope.Window(windowId));
// For a prepared review, present its question or use its no-warning decision.
var decision = await presentation.DecideAsync(prepared.Review);
var result = await closure.DecideAsync(prepared.Review, decision);
```

The module issues an immutable, bounded review capability tied to the target
facts, then validates the decision and owns all execution. Its receipt is
observation-only; neither an abandoned review nor an ignored receipt creates
a caller cleanup duty. Reviews hold IDs and display facts, not session locks,
native-resource leases, or cleanup-capacity reservations. Nevertheless, the
public interface must explain capability validity, preparation outcomes,
approval ordering, stale reviews, and repeated decisions. B offers leverage
when a caller genuinely needs to present or coordinate reviews independently;
the current callers have no such established requirement. Complete-operation
tests also have to traverse this public prepare/decide sequence.

**C — Named complete operations (accepted in Q11).**

```csharp
await closure.CloseTabAsync(tabId);
await closure.CloseWindowAsync(windowId);
await closure.CloseDisconnectedTabsAsync(windowId);
```

The module owns confirmation through its internal presentation adapter and all
subsequent orchestration. The method names match the three existing commands
and constrain their arguments; the caller receives an outcome without a
review ticket, cleanup recipe, or Begin/End obligation. Stable IDs identify
the requested scope. The module, not the caller, captures connection generation,
transfer ownership, and current placement. No caller-supplied callback performs
resource termination.

| Criterion | A: typed intent | B: review/decision | C: named operations |
| --- | --- | --- | --- |
| Depth | Hides the complete close workflow | Hides execution, exposes review lifecycle | Hides the complete close workflow |
| Locality | Admission, approval validity and completion in one module | Validity spans preparation, decision and execution records | Admission, approval validity and completion in one module |
| External seam | UI intent to complete operation | UI prepares and answers an owned review | UI command to complete named operation |
| Leverage | One dispatcher handles all supported intents | Independent review presentation and coordination | Existing callers use their exact operation names |
| Interface cost | Intent union plus outcome/observation | Scope, preparation, review, decision, receipt and observation | Three methods plus outcome/observation |

A and C have essentially equal depth when they hide the same complete
semantics. C was selected for discoverability and operation-specific arguments,
not because three methods inherently hide more complexity. B's extra public
review lifecycle is useful only with a concrete need for independent review
coordination. All three must count outcome observation and error handling as
part of their interface rather than claiming simplicity from entry-point count.

Deleting the closure module would put approval validity, termination order,
wait/loss choice, cleanup adoption and result aggregation back in multiple UI
paths. Keeping those obligations behind its seam therefore adds depth. Moving
provider cleanup, AI authority, BroadcastInput move/capture rules or layout
policy into it would duplicate established owners and damage locality.

The shape did not settle coordination or failure behavior by itself. Q13 and
the clarified Q12a-Q12b were separately accepted. C's independent designer
considered joining partially overlapping requests per target; Q12b selects
that behavior for later window closure, with separate intent approval and
target execution records. Declining the window request cannot undo the earlier
tab close. The initial strict conflict-rejection recommendation is superseded.
Joining existing work is not approval of new targets or an automatic
reconfirmation loop.

### Dependencies and complete-operation verification seam

Actual session, Rust terminal, recording, AI, BroadcastInput and layout
ownership are in-process dependencies. Controlled wire, writer/provider and
clock behavior use their existing local-substitutable seams. WPF confirmation
and native-window events need a production presentation adapter and a
controlled test adapter; both support the same closure interface. Windows,
real files and SSH/SFTP peers remain external integration dependencies.
No new remote-but-owned dependency or replacement terminal implementation is
introduced.

WPF's synchronous Closing event may defer native closure and dispatch the
complete operation; the final window close is requested through existing
coordination. That adapter does not reproduce the asynchronous cleanup recipe.
The module retains and observes operation tasks, including faults, rather than
depending on an async-void event handler or on the caller awaiting a receipt.
Expected failures are operation results; existing owners continue to account
for issued resources until they actually finish.

Final retained-terminal release follows committed final removal through the
terminal owner. A failed removal must not leave a visible tab with already
destroyed state. Independent snapshots/effects and pending native/I/O work keep
their own ownership. Amended Q10 makes automatic closure close associated
transfer windows too. It joins existing close ownership rather than independently
repeating termination/removal under accepted Q12b. These rules use existing
layout and ownership coordination, not a new source of placement authority.

### Existing evidence and later verification

Test source covers disconnected automatic/bulk removal and editor/active/failed
attempt preservation (`tests/TabTty.App.Tests/Program.cs:870` onward), last-tab
movement and failure (`BroadcastAndSplitViews.cs:131/190/213`), and stale file
operation/provider ownership (`FileOperationsTests.cs:14/66/71`). Historical
2026-11-15 adoption artifacts record relevant WPF checks passing, but are for
the partial Rust/handoff implementation. Optional SSH fixtures were not rerun
in that adoption evidence, and this review neither reran tests nor revalidated
source/artifact identity.

No directly corresponding maintained test was found in the inspected sources
for refusing live explicit tab/window closure, the five-second choice with a
blocked writer, or Continue waiting versus the ten-second stall. The current
writer code has per-write/flush deadlines; this observation is a reason to
verify the required interaction, not an executed demonstration of a violation.

Later checks should exercise complete closure intents through the production
interface, with real Rust terminal state and controlled confirmation, I/O,
clock, and layout behavior where those dependencies vary. Preserve real WPF,
file, transport, and provider integration coverage. Observe cancellation with
no close-induced effects, one combined window confirmation, duplicate/stale
requests, once-only EOF/tails, cleanup faults and timeout choices, original
resource ownership after visual removal, Q10's closure of both bulk and automatic
targets' transfer windows versus retained-tab result retention, move versus
close/activation ordering, final-window exit, and deterministic Rust state
release with independently owned snapshots remaining valid. Do not substitute
private helper invocation counts or the old managed-handoff tests for those
outcomes. No new production parser/terminal replacement seam is needed.

### Final shared understanding

**Q14 — Candidate 3 completion and design readiness: accepted.** The user
confirmed the consolidated closure design and its complete-operation verification matrix:
named complete operations; explicit/bulk/automatic transfer-window closure;
ordinary progress accepted without renewed confirmation; existing per-target
closure joined by later intents; no new effects from a declined request but
continued independently accepted work; and distinguishable visual, cleanup-
adoption and actual-release outcomes. Existing owners, modal ordering, final
window rules, native-only terminal processing and issued-resource accounting
remain authoritative.

The accepted exceptional-case rule makes invalid approval observable:
if the intended new effects genuinely concern a different target/generation
or unreviewed work, end that request without starting those effects and return
a changed-target explanation. Do not automatically replace its target or loop
through new confirmations. A fresh request obtains a fresh valid decision;
previously committed closures continue. This is separate from ordinary
completion and accepted overlap, which do not require another click.

The module's per-target execution records prevent repeated termination and
cyclic waits. Session-disconnect notifications must not await a close operation
which is waiting for that session's completion. Final-tab/native-window close
reentry acknowledges or reevaluates the existing visual commit rather than
starting and awaiting the same whole-window operation. Shared wait decisions
remain scoped to their actual owners. These are engineering consequences of
Q9-Q13, not new user commands or permissions.

Q14 completes shared understanding for all three architecture candidates:
recording operations in the complete Rust terminal module, shared native parser
rules, and tab/window closure. All three designs are ready for separately
requested implementation. The user then requested ticket preparation; this does
not authorize implementation or prototyping. Existing test and performance
evidence is historical; no implementation, prototype, runtime test or performance
claim has been added by this design review.

During ticket preparation the user required consultation before adopting
custom character-codec processing, including ports of current C# custom logic.
The [consultation record](terminal-codec-consultation.md) now records approval
of current ISO-8859-10/11/14/16 table reuse on 2026-11-16 and Japanese proposals
A+B on 2026-11-17. The latter approve Microsoft CP932 as the candidate base,
Rust ownership of the selected shared JIS/EUC 94-by-94 repertoire and reverse
selection, exact JIS Roman/IN/OUT/kana and unsupported-repertoire state, and
the specified shared receive-recovery and exact whole-send policies.

These known custom scopes no longer await a codec-design decision. Newly
discovered custom work remains subject to consultation before adoption;
`Cp932Duplicates` ports and new mapping corrections are not approved without
a concrete mismatch and a further decision. The three architecture designs
remain ready for separately requested implementation, with the accepted
48-codec target unchanged. No approval
here establishes runtime parity or authorizes implementation, prototyping, or
conversion probes; ticket-breakdown approval does not imply approval of further
codec changes.

On 2026-11-17, the user approved the ticket breakdown and requested publication.
The [implementation plan #16](https://github.com/TabTty/TabTty/issues/16) and its
23 child implementation tickets are published. The codec conditions above
remain in force. Implementation of this redesign has not started; publication
does not authorize implementation or prototyping.
