# Whole-codebase architecture deepening interview

Status: design review complete, 2026-11-18. The user selected all six candidates
from the whole-codebase review for sequential exploration and has now finalized
candidate 6 and the entire review. Candidates 1-5 are design-ready through
accepted Q1-Q3, Q4-Q6, Q7-Q11, Q12-Q16 and Q17-Q19 respectively. Candidate 6's
revised complete contract and 39-case future verification plan are accepted in
Q23, integrating Q20-Q22 and Q24-Q27. All six contracts are consolidated into
the corresponding English and Japanese specifications and are ready for
separately requested implementation.

Candidate 6 retains shared input once under the existing 4 GiB application
envelope, broadened to terminal state plus paste ownership, and uses the existing
2 MiB/256-unit target input-work allowance. The 30-second broadcast preparation
deadline, configurable 512 MiB default per-paste source limit and confirmation
for CR/LF or source size above 1 KiB remain in force. The independent Q28/Q29
aggregate guard is superseded. Q24 removes the old fixed 1 MiB encoded-paste cap;
Q25 accepts bounded stored spelling with continued processing of long graphemes;
Q27 preserves exact input with scalar/codec-safe Stop. The completed Tera
Term/RLogin comparison informs the accepted bounded-retention adaptation.

No design decision remains open for these six candidates. This interview
authorizes specification and documentation work only; implementation, prototyping
and runtime verification require the user's separate explicit request.

## Review sequence

1. Session log finalization and waiting ownership.
2. Terminal-payload transport ownership.
3. Verified file commit and reconciliation.
4. Credential vault management-operation ownership.
5. Shared SSH channel invariants.
6. Shared grapheme-contract ownership.

Each candidate's decision tree and final shared-understanding review was
completed in sequence. The initial review used current source at `b0a10cf`,
test source, specifications, ADRs, and a read-only GitHub issue inventory. It did
not execute tests or establish runtime reproduction. Historical statements that
the earlier Rust, history, file-operation, broadcast, and closure designs remain
unimplemented do not describe all current source.

## 1. Session log finalization and waiting ownership

### Existing contract and source evidence

The [session and recording contract](../specification/session-and-recording.md#writers-and-failures),
[retention and log I/O decisions](retention-and-log-io-proposals.md),
[closure design](../specification/tab-and-window-closure.md), and
[native recording design](../specification/native-terminal-core.md#recording-operation-interface-accepted-q4)
already define the five-second close choice, ten-second pending-I/O no-progress
watchdog, bounded ownership, native recording semantics, and complete C# file
finalization. This candidate completes their ownership and verification seams;
it does not introduce a different recording or terminal policy.

Current source observations:

- `SessionRecording.cs:129` stops admission and returns its writer task without
  distinguishing ordinary finalization from explicit close-drain waiting.
- `SessionRecording.cs:171,196` applies unconditional ten-second deadlines to
  writes and flushes. The asynchronous write receives the cancellation token;
  synchronous `FlushToDisk` does not, although its timeout callback marks failure.
- `MainWindow.TerminalClosure.cs:258-277` handles the five-second choice.
  Continue waiting awaits cleanup without forwarding that intent to the writer.
  The close-with-loss path does forward abandonment through the session.
- That choice currently observes `TerminalTab.Completion`, which includes
  connection cleanup rather than identifying pending recording work. The existing
  contract only offers the log-loss choice for unresolved recording work.
- The current close path joins a repeated close request without a separate way
  to change a previous wait choice to close-with-loss. Q148 already requires an
  accessible later loss action; this is not a new proposed command policy.
- EOF calls the same writer finish that disposes file handles. The design must
  retain the accepted distinction between EOF part/session ownership and
  pause/close release; sharing a finalization owner does not erase that distinction.
- `tests/TabTty.Core.Tests/Program.cs:1150-1175` injects recording failure using
  `MarkIncomplete`; `tests/TabTty.App.Tests/TerminalTabClose.cs:171,256,268`
  exercises waiting with a delayed UI/session completion. These checks do not
  establish blocked-write or blocked-flush behavior across both deadlines.

The source establishes missing wait-intent propagation, not guaranteed byte
loss at exactly ten seconds. Actual completed I/O errors can still fail a
recording while the user waits. Diagnosing a stall, requesting cancellation,
and actual I/O completion remain different events.

### Confirmed decisions

**Q1 - Ownership scope: accepted.** The user accepted the following recommended
division of responsibility:

| Responsibility | Owner |
| --- | --- |
| Recorded content, terminal eligibility and cutoffs | Rust terminal module through its existing managed adapter |
| Accepted writes, durable flush, stall handling, wait policy, failure outcome and actual resource release | C# recording module |
| Five-second choice presentation, forwarding the user's decision, and tab/window closure coordination | Existing closure module |

Pause, receive EOF, disconnect and close use the same C# recording ownership.
Their distinct content-finalization and file-lifetime rules remain as specified;
common ownership does not make those operations semantically identical.
The recording interface hides the file-completion recipe so callers do not
assemble deadline, cancellation and resource-release steps themselves.

This concentrates changes for locality and provides leverage across the
existing finalization callers. Its depth comes from owning those obligations,
not from moving methods or adding a forwarding module. Deleting that ownership
would distribute the completion recipe back among callers.

Investigate internal file/time seams with production adapters and controlled
blocked, failed and late-completion adapters. Callers and tests should exercise
the same recording interface; real-file and WPF integration checks remain useful.
Q2 below selects the interface shape. Q3 confirms the verification matrix and
final shared understanding.

Q1 preserves the existing five-second and ten-second policies, recording
contents, Rust terminal ownership, resource limits, and cryptographic behavior.
It does not change the initial-release exclusion of macros.

**Q2 - Interface shape: accepted C.** The user accepted named complete recording
operations after comparison with a desired-state interface and module-owned
operation observations. The ordinary caller makes a complete request matching
Pause, receive completion, disconnect completion or explicit close. Resume and
retry remain complete operations of the same owner under their existing rules.
The illustrated method names below are explanatory, not finalized type names.

The recording module owns each accepted operation and its expected failure
observation even if the caller stops waiting. The interface includes bounded
immutable results/status and read-only actual-release observation, with no
caller-owned cleanup recipe, operation lease, or native interval handle.
Close decisions act on the same session-generation owner while its close is
pending. Closure forwards Continue waiting and explicit loss decisions; the
recording module applies them to its owned work under the existing cleanup
admission and adoption rules.

C is selected for discoverability and correspondence with existing callers.
Its depth comes from complete ownership, and its locality from keeping file
completion policy together. Its leverage spans ordinary lifecycle and closure
callers. The decision does not assert that a larger or smaller method count
alone determines depth.

**Q3 - Final shared understanding and verification: accepted.** The user
accepted the consolidated Q1-Q2 design and complete-operation verification
matrix, explicitly finalized candidate 1, and requested proceeding to candidate 2.
This confirms close protection from acceptance through the pending question and
continued waiting; the later explicit loss action; separate recording/transport
results; distinct Pause/EOF/Close meanings; shared overlapping work; and actual
resource ownership through delayed completion, observation cancellation and
old-generation callbacks. Verification uses the ordinary interface with real
Rust behavior, controlled file/time adapters and retained integration coverage.

Candidate 1 is ready for separately requested implementation. No implementation,
prototype or runtime test was performed during this interview. Finalization of
this design does not authorize those activities or change the initial scope.

### Interface alternatives for Q2

Three independent designs explored the accepted Q1 scope. The sketches below
describe alternatives, not implemented types or selected interface names.

**A - Desired state with a revision basis.** Use two entry points for requesting
a target state and observing recording state:

```csharp
var change = recording.SetTarget(RecordingTarget.Paused, currentObservation);
var result = await change.Completion.WaitAsync(stopObserving);
```

Targets include Capturing, Paused, and Closed with either required preservation
or explicit loss permission. EOF is an ordered session fact, not a caller-set
target. The module owns the transition and returns read-only completion and
release observations. A lifecycle revision prevents a delayed Capturing request
from turning into an unintended retry after failure; duplicate requests from
the same basis observe the same result. I/O progress alone does not invalidate
that basis. This design has few entry points, but callers must understand the
target-state model, observation revisions, and how an intentional retry differs
from a repeated request.

**B - Module-owned operation observations and decisions.** Register a complete
intent and receive an observation of its owned operation:

```csharp
var operation = recording.Attach(RecordingIntent.Close);
var result = await operation.Completion.WaitAsync(stopObserving);
// A separate closure action may send a decision through operation.CloseDrain.
```

Operation observations expose bounded current state and completion; a close
decision capability forwards Continue waiting or accepted loss to the same
owned execution. These references are not leases, public native interval
handles, or disposal obligations. Multiple observers and overlapping intents
can refer to one execution while preserving their distinct requested outcomes.
This gives the most flexible observation but adds reference validity,
observation revisions, activation-cancellation capability, and control-lifetime
rules to the interface. The current callers do not establish a need for this
much externally exposed coordination.

**C - Named complete operations, with decisions on the same recording owner
(accepted in Q2).**
The ordinary caller requests the operation matching its existing intent:

```csharp
await recording.PauseAsync();
// Separate lifecycle examples, not a sequence to call together:
await recording.CompleteReceiveAsync();
await recording.CompleteDisconnectAsync();
```

For explicit closure, `CloseAsync()` accepts or joins the recording owner's
close execution and provides its result. While that operation is pending,
closure forwards the user's choice through `ContinueWaiting()` or
`CloseWithUnsavedDataAsync()` on the same owner. Immutable bounded status and a
read-only actual-release observation remain available. Resume and explicit
retry also belong to that owner under their existing contracts. No operation
handle, generation recipe, or cleanup obligation is required of an ordinary
caller. The instance belongs to one terminal-session generation; its close
decisions act only on that generation's owned close.

| Criterion | A: desired state | B: operation observations | C: named operations |
| --- | --- | --- | --- |
| Depth | Owns transitions and file completion, exposes state/revision rules | Owns execution, exposes observation/control lifetimes | Owns completion behind ordinary lifecycle requests |
| Locality | File policy and convergence in one module | File policy and shared execution in one module | File policy and finalization in one module |
| Leverage | One request form serves multiple desired states | Independent observers share operation state and controls | Existing lifecycle callers each make one complete request |
| Seam cost | Target model, fresh basis and stale-request outcomes | Intent, reference, revision, capability and cancellation semantics | Named operations, bounded result/status, and close-decision meaning |

Q2 selects C. Its advantage is correspondence with the actual
callers, not the number of methods. All three can have comparable depth if they
own the same complete behavior. A small method count does not make A's entire
interface smaller; B's flexibility lacks a demonstrated need here.

### Constraints retained by the selected interface

These are inherited requirements and design consequences of Q1-Q2, not
additional user decisions implied by choosing a method name:

- Admission of an explicit close protects its accepted recording work while
  awaiting the final cutoff, the five-second question, or a later answer.
  Close may join an existing pause/EOF/disconnect drain; it does not wait for
  that drain to finish before communicating the protected wait policy. Already
  completed failures remain failures. Ordinary resource-admission failures and
  actual I/O errors are not converted to success by this protection.
- Preserve the distinction between a directional receive EOF, disconnect and
  final close. Under Q143, EOF finalizes received content and flushes through
  that ordered position, retaining part/session ownership and permitted
  opposite-direction interaction. It does not globally pause recording or
  end any otherwise permitted local-echo recording. SSH local echo remains off.
- The recording result, pending recording work, transport completion, visual
  removal, cleanup adoption, and actual resource release remain distinguishable.
  Only unresolved recording work causes the log-loss question. A later loss
  action remains accessible without restarting the question every five seconds.
- Repeated and overlapping intents reuse the underlying admitted I/O and
  once-only terminal finalization. EOF, Pause and Close can have different
  completion conditions even when they share underlying work. Existing closure
  joins retain the prior timer and decision rather than recreating either.
- Await only the required terminal/session ordering step, not aggregate session
  or closure completion that already includes this recording operation.
  EOF/disconnect notifications must not await the complete close that depends
  on those notifications. This avoids cyclic waits without changing ownership.
- Stopping observation does not cancel an accepted operation. The owner keeps
  observing failures and cleaning up even after the caller stops waiting.
  Activation cancellation before commit remains a distinct existing rule.
- Loss permission does not prove OS cancellation or storage rollback. Preserve
  issued buffers and handles until actual completion. If work is already saved,
  a late loss answer has no destructive effect. Old callbacks and decisions
  cannot affect replacement parts or a new session generation.
  When saving has succeeded but release remains pending, retain that save result
  and report remaining release obligations separately.
- Existing application cleanup admission and closure adoption remain with
  their current owner. A complete recording loss operation cooperates with
  that owner through the appropriate internal seam; it does not create an
  independent allowance or transfer actual file/buffer ownership to the UI.
  If required admission is unavailable, report it and preserve the waiting
  operation. Do not expose a reservation-and-release recipe to the caller.
- File and clock adapters are internal, local-substitutable dependencies.
  Production and controlled adapters justify these seams. Native terminal
  meaning and session ordering use the existing in-process owners. Tests cross
  the ordinary recording interface with real Rust behavior where relevant.

### Complete-operation verification accepted in Q3

Use the same named operations as ordinary callers. Exercise actual Rust
recording semantics with independent expected bytes, controlled file and clock
adapters for pending/failing/late I/O, and controlled closure presentation where
the five-second choice is relevant. The file adapter must model issued work
and its actual completion separately from a cancellation request. Keep real
file, WPF and transport integration coverage; a controlled test is not evidence
of real Windows storage cancellation or device behavior.

| Case | Required observable result |
| --- | --- |
| Healthy Pause with selected Raw/Text/Terminal formats | Eligible content appears once and in order; successful Pause requires write completion, durable flush and part close, while terminal reception/display remain usable |
| Resume/retry or initial preparation succeeds, fails partway, or is invalidated before activation | Start only the whole selected part set, use fresh parts without backfill, and preserve the existing cancellation/commit distinction; partial preparation never activates a subset and its original owner cleans up |
| Receive EOF, then later disconnect/close | Receive content is finalized and flushed once through its ordered position; EOF retains part/session ownership and permitted opposite-direction interaction; later close releases without replaying tails |
| Recording disabled or repeated completed operations | No files are opened to enable a disabled recording; repeated finalization does not duplicate content or release |
| Active recording has pending I/O with no completed progress for ten seconds | Group failure follows the existing stall policy; no-pending-I/O idle time does not fail recording; accepted and still-owned work stays accounted |
| Explicit close is accepted while a write/flush or Pause drain is pending, including near the watchdog deadline | The same admitted work becomes protected before waiting for long-running completion or a prompt; no timer-driven loss while the question is pending; a prior completed error is not undone |
| Continue waiting, then delayed successful I/O completion | Advancing past both time thresholds does not abandon the accepted queue; successful completion reports its real save result; the wait does not create repeated prompts |
| Continue waiting, then the later explicit close-with-loss action | The same owned close accepts the decision, abandons only unissued work, requests cancellation as appropriate, and distinguishes incomplete recording/cleanup adoption from actual release |
| Cancellation is delayed or an issued synchronous flush remains pending | Issued buffers, handles and charges remain owned until actual completion; visual removal or a cancellation request does not release them |
| Actual write/flush/close failure, including a partly written suffix | The result is failed/incomplete or explicitly uncertain, never successful Pause/save; all selected formats stop admission; no replay or guessed missing-byte count; healthy terminal behavior survives recording-only failure |
| Repeated tab close or a window close joins existing work | No duplicated terminal tails, writers, cleanup allowance or target prompt; existing wait choice/timing survives; each intent still observes the appropriate completion conditions |
| EOF/disconnect notification or final closure reenters the owner | Existing ordered work completes without waiting on an aggregate operation that depends on itself; no cyclic wait or duplicate finalization |
| Log work is complete while transport remains pending, or saving completes before a delayed loss answer | No log-loss question for transport-only work; a late answer does not damage the completed recording or affect another generation |
| Caller cancels observation, a prompt fails to display, or a view disappears | Recording ownership and failure observation continue; no implicit loss permission; existing closure failure/reporting rules remain authoritative |
| Old-part completion/failure or a delayed open arrives during retry or final close | Only the original owner's work changes; obsolete activation is not committed; new parts keep their own results and files; abandoned preparation remains bounded |
| Cleanup allowance is unavailable or occupied by failed prior work | No unbounded writer/preparation accumulation or early resource reuse; admission is rejected or deferred visibly while existing work remains owned |

Assertions cover resulting bytes, durable-flush outcomes, valid current state,
reported uncertainty and actual resource lifetime. They do not depend on private
helper counts. Existing file/clock owner behavior is replaced only where the
selected production seam requires it; no second terminal implementation or
macro runtime is introduced for the tests. This matrix is a future verification
plan, not executed evidence.

### Readiness

Q1-Q3 complete candidate 1's design review. The
[session and recording contract](../specification/session-and-recording.md#writers-and-failures)
records the selected ownership and operation shape. The verification matrix
above remains required future work, not evidence of implemented guarantees.

## 2. Terminal-payload transport ownership

Status: design-ready. Q4 accepted the ownership scope, Q5 selected alternative C,
and Q6 confirmed the complete contract and verification plan. The user explicitly
requested moving to candidate 3; no implementation or prototyping is authorized.

### Existing contract and source evidence

The [transport contract](../specification/transports-and-authentication.md),
[session contract](../specification/session-and-recording.md),
[SSH session decisions](ssh-session-proposals.md), and
[future macro constraints](future-macro-design.md) already require ordered
terminal payload, distinct readiness/EOF/closure, and session operations usable
without WPF. This candidate concerns placement of existing protocol knowledge.

- `SessionWire.cs:5` exposes raw reads/writes, a current source and credit
  acknowledgement. `TerminalSession.cs:176,279,289,453,683` separately knows
  Telnet startup, receive filtering/finalization, input framing and NAWS.
- `TerminalSession.cs:440` combines the user's local-echo policy with Telnet's
  negotiated remote-echo fact. Those are distinct responsibilities even if their
  present implementations share a class.
- `SshSessionWire.cs:94,384,802,871` takes a factory for `TerminalSession` and
  calls the resulting session while PTY/shell startup is pending. Early terminal
  payload must remain processed before readiness; removal of this concrete
  dependency cannot mean waiting until connection completion to process it.
- `TerminalSession.cs:683-690` supplies committed geometry through different
  protocol paths. The chosen grid is a terminal/session decision; NAWS and SSH
  window-change representation belong to their transport implementations.

The review has not established a runtime transport defect. Source inspection
shows knowledge crossing both directions of the current seam. Preserve
ADR 0001's TabTty-owned SSH/OpenSSL arrangement, ADR 0003's shared authenticated
connection for SFTP, and ADR 0007's Rust terminal semantics. Internal sharing of
SSH channel rules is candidate 5, not part of this scope decision.

### Confirmed decisions

**Q4 - Ownership scope: accepted.** The user accepted the recommended division
between transport, terminal session and Rust terminal processing:

| Responsibility | Owner |
| --- | --- |
| Protocol framing and negotiation; transport-specific startup; protocol representation of committed geometry; readiness, receive EOF, closure and negotiated-echo facts | Transport module |
| Input permission and ordering; delivery to recording and Rust; local-echo policy; chosen terminal geometry | Terminal session |
| Receive/transmit character encoding, VT interpretation, terminal state, reflow and recorded terminal content | Rust terminal module |

Telnet IAC/NVT handling is transport framing, distinct from Rust's character
encoding. Raw receives terminal payload after that framing is removed, before
character decoding. SSH DATA and stderr retain their source and ordered payload;
authentication, protocol messages and SFTP payload stay outside terminal/log
content. The transport reports negotiated remote echo; the session combines
that fact with the existing local-echo choice.

Legitimate SSH output remains processed and recorded during startup before
input readiness. The concrete dependency on constructing/calling
`TerminalSession` is not a required part of that behavior. Receive EOF and
transport closure retain their distinct meanings and existing opposite-direction
rules. Serial-specific operations and SFTP remain dedicated operations rather
than a universal capability collection. ADR 0001, ADR 0003 and ADR 0007 remain
authoritative, as does candidate 1's separate recording-finalization ownership.

Concentrating these protocol obligations inside the transport adapter provides
locality for protocol changes and depth at the session-facing interface. Its
leverage is shared across SSH, Telnet and serial session callers and their
verification. A new module that merely forwards raw reads/writes while leaving
the protocol recipe in the session would not achieve that depth. Deleting the
deepened ownership would return negotiation, framing and startup-delivery
knowledge to its callers.

**Q5 - Concrete interface: C accepted.** The user selected named complete
transport operations with a typed, awaited receive consumer bound before startup.
The operation names below are illustrative. The accepted shape includes source
and lifecycle facts, automatic downstream-consumption accounting, independent
bounded protocol progress, and distinct operation/actual-release outcomes.
It does not expose a read/acknowledge recipe or depend on constructing a concrete
`TerminalSession`.

**Q6 - Final shared understanding: accepted.** The user confirmed the complete
operation, ordering, cancellation/failure and verification contract below, and
explicitly requested proceeding to candidate 3. Candidate 2 is design-ready for
separately requested implementation. The review performed no implementation,
prototyping or runtime validation.

### Interface alternatives for Q5

Three independent designs explored the accepted scope. The sketches describe
interface shapes, not implementation or final type names. Each must hide the
protocol recipe, preserve bounded ownership, and use the same ordinary
interface for production callers and verification.

**A - One owned run with typed commands and ordered delivery.**

```csharp
var run = transport.OpenRun(plan, session.TransportConsumer);
var accepted = run.Submit(Send(preparedInput));
run.Submit(CommitGeometry(committedGrid));
run.Submit(Disconnect());
```

The transport owns execution, command admission, completion and actual release.
One ordered delivery path carries payload, readiness, echo facts and lifecycle
facts. This has few entry points, but the interface includes command/result
correlation, admission and ownership transfer, plus the delivery protocol.
Its leverage is strongest when callers already use this message model. Current
session callers would need to learn that model for otherwise named operations.

**B - A typed pull stream with explicit processing acknowledgement.**

```csharp
var run = transport.Start(plan, committedGrid);
while (await run.Receive.TakeAsync(stopWaiting) is { } delivery)
{
    await session.ApplyTransportEventAsync(delivery.Event);
    run.Receive.Processed(delivery.Position);
}
```

Pulling starts immediately, independently of readiness. The stream has one
authoritative consumer; UI and future observers use session observations.
The caller must acknowledge actual downstream processing, handle abandonment,
and distinguish stopping a wait from ending the transport. Immutable copied
bytes remove a borrowed-buffer disposal obligation but do not make copying or
dequeueing equivalent to processing. B is flexible for polling/composition,
but leaves these ownership and credit obligations in the caller's interface.

**C - Named complete operations with a bound, awaited receive consumer.**

```csharp
var transport = transports.Create(plan, session.TransportConsumer);
await transport.StartAsync();
// Separate examples through the session's existing ordering owners:
await transport.SendAsync(preparedInput);
await transport.ApplyGeometryAsync(committedGrid);
await transport.DisconnectAsync();
```

Creation binds the receive consumer before I/O starts. Its single ordered
`ConsumeAsync(fact)` operation accepts payload with its source, negotiated echo,
readiness, receive EOF and closure facts. The transport does not construct or
depend on the concrete `TerminalSession`. Successful payload consumption is
the transport's evidence for eligible credit return; no public read/acknowledge
recipe is required. Bounded immutable outcomes/status distinguish operation
completion from actual resource release. The interface still includes callback
lifetime and reentrancy rules; named methods do not remove those obligations.
Payload memory remains valid for the consumer call and cannot be retained after
it completes without separately admitted ownership. The session also supplies
access to its current committed geometry; that read/notification ordering is
part of the interface even if its final delegate/type is omitted from the sketch.

### Selected interface constraints accepted in Q6

- Prepare the session and selected recordings before starting transport work.
  Deliver legitimate early SSH payload while PTY/shell startup is pending,
  including output preceding startup failure. Ordered readiness follows earlier
  payload processing; startup success must not let input readiness overtake it.
  Telnet and serial retain their existing readiness definitions.
- Preserve source identity and transport receive order. Telnet echo changes must
  be represented at their ordered positions, not as only the final echo value
  attached to an entire mixed receive chunk. Raw receives payload after Telnet
  framing and before Rust decoding. Authentication, protocol control, diagnostics
  and SFTP remain outside terminal/log content.
- Consume payload through Rust and recording admission before releasing that
  received allocation. Copying into another pending receive queue is not enough.
  Do not wait for disk flush, painting, or transmission of generated terminal
  replies to return SSH credit. Replies enter the existing bounded session send
  owner; they are not transport protocol control. Recording-only failure retains
  valid terminal effects and follows its independent failure policy.
- Separate protocol progress from ordered application delivery. A slow consumer
  or a session operation waiting for send credit must not prevent processing
  SSH window adjustments, rekey or closure. Do not hold a transport lock across
  consumer code, or make a send's I/O result depend on a later consumer call
  requiring the same session reducer. EOF/closed consumers commit their ordered
  facts without awaiting aggregate completion that depends on those callbacks.
- A separate reader task alone does not ensure progress. Account outstanding
  reads, advertised credit, queued payload, source metadata and control work
  within the existing bounds. A byte window alone does not reserve enough items
  for many tiny interleaved DATA/stderr messages. Internal bounded packing and
  admission must preserve order and control progress. Use backpressure where it
  can recover; unresolvable hard pressure follows the existing affected-session
  resource-failure policy, never silent loss or an unbounded queue. Serial keeps
  its selected handshake and explicit overrun behavior.
- Session/Rust supply encoded input and its complete-unit boundaries. Transport
  owns wire framing and segmentation, not character/grapheme interpretation,
  input permission, paste pacing or local echo. Results preserve the definitely
  submitted complete prefix and any partial/uncertain remainder. A result proves
  local submission only; no automatic replay or partial-character echo follows.
  The exact per-unit versus bounded-batch representation remains an engineering
  detail provided it preserves prompt per-unit completion and existing pacing.
- Session supplies committed geometry. PTY uses current dimensions; startup
  changes coalesce and the latest dimensions are notified when permitted, as
  already required by the SSH session decisions. An initial factory snapshot
  must not freeze dimensions through a long authentication dialog. Geometry
  admission must not require holding session ordering until readiness. Transport
  represents NAWS/window-change; it does not choose dimensions or imply remote
  rendering. Startup callers do not hold the session reducer while awaiting an
  operation that needs geometry or receive callbacks through that reducer.
  Serial needs no wire geometry notification.
- Preserve directional receive EOF separately from closure and actual release.
  Closure without prior EOF still finalizes admitted receive content once.
  Observation cancellation does not abandon accepted work. Explicit attempt
  cancellation invalidates the attempt; issued I/O retains its original buffers,
  handles and accounting until actual completion. Late completion cannot affect a replacement
  generation. Candidate 1 continues to own recording finalization independently.
- SFTP and serial/Telnet-specific actions retain dedicated operations. The
  selected common interface does not create a universal capability collection.
  Authentication, host trust and secret lifetimes remain with existing owners.

These are consequences of the accepted scope and existing contracts, not new
cryptographic, authentication or product policies. No runtime failure was
reproduced in this comparison.

### Comparison and selected rationale

All three designs improve locality by concentrating protocol knowledge in the
transport module. C offers the best depth for current session callers: complete
operations hide startup/framing/credit/cleanup while the awaited consumer removes
B's public acknowledgement recipe. A achieves a small method count at the cost
of a second command/result protocol. B's composition flexibility does not yet
justify its additional correctness obligations for TabTty's single receive
owner. Q5 selected C, with source-bearing typed facts, automatic consumption
accounting, independent bounded protocol progress, and explicit operation versus
actual-release outcomes.

Rust and session ordering are real in-process dependencies. Network/device I/O
and time provide internal seams with production and controlled adapters. Real
SSH/Telnet peers and COM hardware remain external integration coverage. Future
verification uses the ordinary selected interface and independent expected
bytes/events; it does not replace Rust or create another terminal model.
Required cases include early output followed by startup refusal, interleaved
source/echo facts, stalled delivery with credit/rekey, queue-item pressure,
startup resize, EOF with permitted sending, partial submission, consumer failure
and delayed cancellation/release. No implementation or tests were run here.

### Complete-operation contract accepted in Q6

The following makes accepted Q4-Q6 concrete. It is the confirmed design
contract, not a claim that the source already implements these guarantees.
No additional product-policy change is introduced.

| Operation or observation | Completion and ownership contract |
| --- | --- |
| Create/bind | Capture the connection generation/settings and bind one ordered receive consumer plus access to committed geometry before any transport I/O. Session and recording preparation remain session-owned prerequisites. |
| Start | One startup attempt per transport. Repeated observation or an overlapping request joins that attempt; failure never restarts it. Success follows the protocol readiness condition and consumption of preceding ordered facts. Failure/cancellation retains output already processed and settles admitted delivery under the failure rules below. The outcome does not claim log completion or actual I/O release. |
| Send prepared input | Receive session-authorized, encoded work with complete-unit boundaries through the one input owner. Distinguish rejection before admission, accepted but unsent work, definitely submitted complete units, and any partial/uncertain suffix. Report completion as units are submitted so local echo and pacing do not wait for the whole paste. Repeating a send is not an observation or an implicit safe retry. |
| Apply committed geometry | Accept current session-chosen dimensions and report notification outcome without blocking the reducer until readiness. Obtain current dimensions for PTY, coalesce startup changes, and send the latest when permitted. Supersession or lack of a serial notification is distinguishable from failure; notification never proves remote rendering. |
| Consume a payload/fact | One ordered, nonrecursive callback at a time. Payload completion means Rust processing, separately accounted recording admission or its narrow failure, bounded admission of any generated replies, and release of the received allocation. Fact completion commits its session-ordered meaning. Neither waits for reply transmission, file flush or aggregate lifecycle completion. |
| Receive EOF / closed | EOF finalizes the receive direction once after preceding payload; opposite-direction input remains permitted under the existing rules. Closed also supplies the final receive cutoff if no EOF arrived. These facts do not prove that recordings or issued OS operations have completed. |
| Disconnect | Accept or join one shutdown, stop input admission, cancel unsent work and make the existing best-effort protocol close, including SSH's up to two seconds of peer grace. Report the transport outcome and pending delivery/release truthfully; that grace is not a guarantee of actual I/O release or log completion. Repeated calls do not duplicate shutdown, finalization or prompts. |
| Status / actual-release observation | Bounded immutable state and outcomes remain observable even if the consumer fails. Actual release waits for owned I/O, buffers and pending delivery ownership to settle. It is transport-specific and does not claim release of retained terminal output or recording resources. Stopping observation does not cancel that responsibility. |

**Ordering and progress.** All payload and session-visible facts preserve their
transport order, while the session assigns reducer positions relative to input
completion, resize and recording changes. Protocol progress may advance before
application delivery; it does not permit concurrent or reordered callbacks.
The reducer admits work and commits outcomes, but does not remain occupied
across remote-credit, rekey, long-running I/O, startup callback, geometry
notification or peer-close waits. Receive processing must be able to advance
while a send waits. Keep the single input
owner and immutable prepared work; allowing receive progress does not introduce
concurrent input senders. A consumer must not await a transport operation whose
completion requires that consumer or a later delivery to finish. These are
interface obligations; the task/queue/lock arrangement is an implementation
detail.

EOF and closure callbacks commit the ordered receive cutoff and hand recording
finalization to candidate 1's owner. Recording completion, continued waiting and
explicit loss choices remain separately observable; they are not obligations
of the transport consumer or the transport's actual-release observation.

**Three cancellation meanings.** Cancelling an observer's wait does not alter
accepted work. Stop Paste or explicit send cancellation follows the session's
existing input policy: stop unissued input at the required complete-unit point,
and on a healthy connection preserve necessary codec/bracket closure and queued
reply order. This alone does not invalidate the connection generation. Attempt
cancellation or Disconnect invalidates the attempt, prevents new input and
readiness activation, and starts transport cleanup. It does not recall submitted
bytes, replay input, or release issued resources before actual completion.

During SSH peer-close grace, continue processing valid terminal payload until
channel closure or SSH DISCONNECT/transport termination. The local Disconnect
request alone is not the final receive cutoff. Do not parse late socket bytes
as SSH payload after protocol termination. This preserves the existing Q143
close-grace rule while preventing readiness or new input for the closing attempt.

**Failure and the final receive cutoff.** Startup refusal, timeout and ordinary
transport closure preserve earlier processed output and drain already-admitted
payload in its original generation before the ordinary final closed fact.
No new payload is admitted after that generation's receive cutoff. Invalidating
an attempt must not discard previously admitted work merely because its startup
result is now stale; stale activation and new work are rejected, while owned
draining/cleanup continues against the original owner. A replacement session
never receives those callbacks, credit or submission results.

If the consumer fails after partially applying a payload, do not call it again
with that payload or claim atomic rollback. Stop ordinary delivery and report
the affected-session failure through independent status/outcomes, preserving
available output and marking capture incomplete under the existing failure
policy. A failed consumer is not required to accept a subsequent closed callback
to make the failure observable. Settle pending ownership explicitly; do not
grant consumption credit or report success for unprocessed data. Hard resource
or terminal-state failure may prevent normal draining, so retain what is safely
available and report incompleteness rather than promising lossless completion.
A recording-only failure remains narrow: valid terminal updates and replies
survive, and the healthy transport continues.

### Candidate 2 verification plan

Use the selected ordinary interface with real session ordering and Rust terminal
processing. Internal I/O/time seams have both production adapters and controlled
adapters for fragmentation, blocked work, delayed cancellation and failures.
Retain independent SSH/Telnet peer tests and COM/device integration; controlled
adapters do not establish real protocol interoperability or driver behavior.
Replace tests coupled only to a retired raw-wire recipe where that recipe moves
behind the module, while preserving independent protocol-vector coverage.

| Case | Required observable result |
| --- | --- |
| Initial log preparation fails; otherwise start SSH/Telnet/serial | No transport I/O before successful required preparation; each transport uses its existing readiness definition, with no remote login inference for Telnet or silent serial setting substitution |
| SSH DATA/stderr before PTY/shell success, then success, refusal, timeout or cancellation | Payload is processed/log-eligible during startup in source order; earlier output survives failure; preceding facts settle before readiness, and cancelled attempts never activate input |
| Many receive fragments with DATA/stderr, Telnet IAC/NVT CR state and negotiated echo changes | Independent expected Raw bytes and terminal state; source-local decoder/parser state survives chunks; echo facts take effect at the correct reducer positions; framing/control/SFTP never appears as terminal output |
| A payload generates a terminal reply while peer send credit is zero, including rekey/window adjustment | Receive consumption/credit does not wait for transmitting the reply; protocol control and bounded receive processing progress; replies retain their session order and paste rules |
| Consumer is held while legal advertised payload arrives, including over 1024 tiny alternating DATA/stderr messages | Account all queued/in-flight bytes and source metadata; no credit before actual consumption, no unbounded item expansion or control-progress deadlock; recover through bounded storage/backpressure where possible |
| Ordered echo/control metadata grows without corresponding payload credit, or pressure cannot recover | Metadata stays bounded; explicit affected-session failure with available output/incomplete status replaces silent drops or an indefinitely healthy stall; cancellation remains admissible |
| Disk write/flush stalls or fails while receive processing is healthy | SSH credit follows downstream consumption rather than disk completion; recording failure stays narrow; terminal output and valid replies remain usable |
| Geometry changes during authentication, PTY/shell startup and ordinary operation | PTY uses current committed dimensions; startup changes coalesce to the latest allowed notification; stale revisions do not overwrite current geometry; session reflow/logging behavior remains unchanged |
| Partial send, Stop Paste, cancelled observation, and Disconnect race | Complete submitted units alone produce applicable local echo; uncertain suffix is not replayed; Stop preserves healthy codec/bracket cleanup, observation stop preserves owned work, Disconnect cancels unsent work; valid output during SSH's bounded peer grace is processed until the actual receive cutoff |
| EOF followed by permitted sending, repeated EOF/close, or close without EOF | Receive sources finalize once in the specified order; EOF preserves permitted reverse input and recording part ownership; later closure neither duplicates tails nor waits for an optional exit status |
| Consumer throws after partial application or cannot accept lifecycle facts | No redelivery or false success; available output survives; independent failure/status remains observable and all pending resources retain an owner |
| Delayed reads/writes/open/close complete after cancellation, release observation or replacement | Charges/buffers/handles are released only at actual completion; callbacks stay with the original generation; no replacement activation, credit, echo or geometry mutation |
| Repeated Start/Disconnect and concurrent closure/recording actions | One attempt/shutdown and once-only receive finalization; no duplicate callbacks/prompts, no aggregate completion cycle, and candidate 1 retains recording drain/wait ownership |
| SFTP activity, serial line/BREAK actions, and real peers/devices | Dedicated operations preserve their accepted behavior and resource isolation; SFTP payload is absent from terminal/Raw logs; serial overrun is explicit, BREAK cleanup is attempted, and no lossless serial backpressure is invented |

Assertions cover observable byte/state/event order, failure outcomes, resource
accounting and actual completion, not private helper counts or a prescribed
thread arrangement. Exercise controlled stalls and orderings without relying
on sleeps to establish correctness.

Source inspection found existing optional early-output-success coverage in
`tests/TabTty.Core.Tests/Program.cs:279` with the scripted peer at
`tests/fixtures/ssh-peer.cjs:155`. It checks Raw progress before connection
completion, followed by final decoded output and Raw bytes. This is evidence
of a test case, not a test execution or coverage of refusal/cancellation after
early output. The existing receive path in `TerminalSession.cs:297-298` sends
automatic replies before acknowledging payload, so the new independence of
credit from reply transmission must be verified during implementation. Existing
direct Telnet checks, loopback sessions and SSH integration cases remain useful;
their presence does not establish the complete selected-interface contract.

### Candidate 2 readiness

Q4-Q6 complete candidate 2's design review. The
[session contract](../specification/session-and-recording.md) consolidates the
accepted ownership and interface. The complete-operation verification matrix
above is required future work, not executed evidence. Names, bounded packing
and exact internal scheduling remain engineering details. This review performed
documentation changes and source inspection only; it ran no tests and authorizes
neither implementation nor prototyping.

## 3. Verified file commit and reconciliation

Status: design-ready. Ownership scope was accepted in Q7, alternative A selected
in Q8, partial-publication adoption and quiescent closing accepted in Q9-Q10,
and the complete contract and verification plan confirmed in Q11.

### Existing contract and source evidence

The [save and recovery contract](../specification/persistence-and-windows.md#save-and-recovery)
already requires validated candidates, original-file comparison, same-directory
write/flush, replace or non-overwriting creation, inspection of actual outcomes,
and publication of the verified effective revision. Ambiguous outcomes require
target/candidate/backup inspection and freezing affected writes until resolved.
The [vault contract](../specification/vault-format.md#save-backup-restore-and-failures)
adds encrypted-only files, restrictive access, cooperating-writer exclusion,
authenticated verification and retained roots during uncertain password changes.
The [shared vault ownership ADR](../adr/0008-share-vault-ownership-with-file-editors.md)
keeps the selected vault's committed revisions shared across its consumers.

Current source repeats the physical save recipe, with different outcome handling:

| Area | Source observation |
| --- | --- |
| Connection profile | `src/TabTty.Core/ConnectionProfile.cs:230-288` validates/serializes, creates and flushes a same-directory candidate, compares the original and replaces with `.bak`. Existing-file saves reread even after a replacement exception and ignore observation cancellation during that check. New-file creation at line 252 proceeds to source-revision publication without the same reread path. |
| Application JSON stores | `src/TabTty.App/JsonStoreRecovery.cs:99-136` repeats staging, flush, snapshot comparison, create/replace, target digest inspection and candidate retention on uncertainty. Its ordinary save and explicit recovery select different backup names. UI recovery, JSON parsing and unknown-property preservation also reside in this class, but are separate responsibilities. |
| Credential vault | `src/TabTty.Core/CredentialVault.cs:110-146,393-474,496-572` repeats the recipe for recovery, replacement and creation, adding private file creation and a lock independent of the replaced target. Recovery verifies both the restored target and the preserved original; ordinary replacement compares the installed digest. These paths do not all provide identical evidence. |
| Effective application state | Profile source bytes change after save at `ConnectionProfile.cs:278-279`; settings are adopted and applied after `JsonStoreRecovery.Save` at `src/TabTty.App/App.cs:333-340`; highlighting follows the same pattern at `HighlightCatalog.cs:324-328`. Host trust returns approval only after saving at `SshHostTrust.cs:80-81`. These are format/application decisions after file verification. |
| Secret lifetime | `CredentialVault.cs:292-323` keeps the proposed password-change root separately if commit becomes uncertain. The common file mechanism must not clear it, choose a root, or unblock stale credential retrieval. |

The existing profile/JSON implementations inspect mainly the target after an
uncertain replacement. They do not implement the full documented reconciliation
of target, candidate and backup plus an owner-held affected-write freeze.
The profile/JSON pre-replacement comparison is also not an atomic compare-and-swap
against external writers. A final reread describes the observed revision at that
point; it does not prevent a later external edit. This is source-level evidence
of duplicated knowledge and differing guarantees, not a reproduced data-loss
incident or a claim that all save paths are unimplemented.

Vault pre-commit conflict handling also differs from its stated artifact policy:
`CredentialVault.cs:400-401,429` can fail the current-generation check while
candidate preservation is still false and then delete that candidate. The vault
contract requires preserving the external file and an owned recovery candidate
on writer conflict. The later interface review must account for that distinction
instead of copying the current cleanup branch as the target contract.

Existing test source covers profile unknown-property/ID/backup preservation and
pre-save external changes (`tests/TabTty.Core.Tests/Program.cs:859`), initial
directory creation (line 917), and explicit JSON recovery with original-file
preservation (`tests/TabTty.App.Tests/Program.cs:1484`). These cases do not by
themselves establish replacement-after-error, failed verification reads, delayed
cancellation or races between the last comparison and publication. No tests
were executed for this exploration.

### Confirmed ownership scope

**Q7 - Ownership scope: accepted.** The user accepted one deep module for
verified single-file publication and reconciliation, used by connection
profiles, application JSON stores and encrypted vault files. It owns the complete
file operation rather than a collection of write/move helpers.

| Responsibility | Owner |
| --- | --- |
| Same-directory candidate creation, required access policy and writer exclusion, write/flush, baseline recheck, non-overwriting create or guarded replacement, preservation of required backup/recovery artifacts | Shared file-commit module, enforcing the format owner's established requirements through filesystem adapters |
| Inspecting actual target/candidate/backup state after publication, invoking format verification, distinguishing verified publication, unchanged original, conflict and unresolved outcome, and retaining owned work/artifacts through cleanup or reconciliation | Shared file-commit module |
| Serialization, bounded schema/content validation, expected file/revision identity, unknown-property preservation, approved paths, and which backup/recovery policy applies | Existing profile or application-store owner |
| Encryption/authentication, vault/entry identity, root/key ownership, stale-vault access policy, unlock lifetime and adoption of a verified encrypted snapshot | Existing vault owner under its established security contract |
| Publishing effective settings/profile revisions or host trust, presenting errors/recovery choices, and applying the existing affected-store write freeze | Existing format/application owner, based on the common operation's verified outcome |

Format verification is a real adapter at this seam: JSON validation and vault
authentication have different obligations. The common module orchestrates that
verification and result reconciliation; callers do not receive a raw filesystem
exception and reconstruct the save recipe themselves. Vault data entering the
file mechanism is encrypted. Cleartext, key derivation and root selection remain
inside the vault owner, including verification-specific temporary plaintext.
This scope does not select a new unlock or decryption for every reread. The
selected interface distinguishes evidence of equality with an already
authenticated candidate from authentication/schema validation of a separately
selected restore candidate.

This scope has depth because callers request a complete save and obtain an honest
outcome, with the operation retaining uncertain file work. Its leverage spans
profiles, settings, highlighting, local state, host records and vault saves.
Its locality keeps fixes for ambiguous publication and late I/O together.
Deleting the module would redistribute that state machine across those callers.
A helper that wraps only `File.Replace` would not achieve this depth.

**Q8 - Concrete interface: A accepted.** The user accepted complete typed
publication requests with a module-owned operation and a read-only observation
view. Accepted operation identity is available before cancellable observation;
reconciliation inspects that same operation without repeating publication.
Existing domain owners retain paths, editing bases, effective revisions and
secret ownership. No public per-file owner or caller-controlled staging
protocol is introduced. Names remain illustrative; the detailed result,
reconciliation, cancellation and verification contract follows this selection.

### Constraints retained under Q7

- Keep format-specific validity and recovery decisions. Ordinary backups retain
  the previous validated generation; explicit recovery may instead preserve a
  damaged original under a unique recovery name. Newer schemas are not silently
  treated as corruption. No automatic restore, merge, overwrite or empty reset
  follows an uncertain save.
- Keep non-overwriting creation, including new profiles and encrypted copies.
  Existing empty-file initialization, restore and archive decisions retain their
  own identity/path rules; sharing mechanics does not make them arbitrary
  replacement permissions.
- Keep each existing file's size limits and supported location rules. Apply the
  required writer exclusion and recheck observed identity/content, without
  claiming a transaction against every noncooperating or cloud writer. Preserve
  per-format writer policy; vault-specific lock behavior is not automatically
  imposed on every JSON store by this scope decision.
- Cancellation before publication can stop unissued work. Once publication may
  have happened, finish determining its outcome or report unresolved ownership;
  do not equate cancellation or a lost response with rollback. Issued work and
  verification resources remain owned until actual completion.
- Verified publication, an unchanged original, conflict, unresolved state and
  cleanup completion are different facts. A cleanup error must not hide a known
  publication result; a successful filesystem call must not replace required
  verification. Retain only owned artifacts, and do not blindly retry a publish
  when its prior outcome is uncertain.
- The affected format owner keeps unverified revisions from becoming effective.
  Its freeze/recovery policy follows existing contracts. Vault-specific blocking
  of new retrieval and writes, old/candidate root retention and shared revisions
  under ADR 0008 remain intact. Reconciliation of this application's own save
  does not authorize reopening a stale selected vault or clearing an external
  conflict.
- A profile save and registration/settings update remain distinct file outcomes,
  not a multi-file transaction. Streaming session-log writes are candidate 1;
  SFTP file operations retain their own accepted design. Candidate 4 covers
  complete vault management operations; candidate 3 does not absorb those
  operations, secret lifetimes or their UI coordination.

Use production filesystem adapters and controlled adapters for observed-name
states, partial writes, flush failures, conflicts and delayed completion. Tests
would cross the same selected interface as callers and assert actual files,
verified revisions and ownership, with real-file integration retained. The
complete interface and verification matrix follow the accepted scope.

### Interface alternatives for Q8

Three independent designs explored Q7. The sketches are illustrative, not
implemented types or additional authorized operations. Every alternative must
hide the physical save/reconciliation recipe and use the same ordinary interface
for production callers and later verification.

**A - A complete typed request with a module-owned operation.**

```csharp
// Accepted-admission path only; rejection has no file effects.
var operation = commits.Submit(
    UpdateExisting(target, originalBasis, preparedCandidate, requirements),
    requestCancellation);
var result = await operation.InitialObservation.WaitAsync(stopWaiting);
// If a later inspection is needed:
var inspected = await commits.ReconcileAsync(operation.Id, stopWaiting);
```

The request distinguishes non-overwriting creation, update of an observed
existing file, and explicitly authorized restoration. It fixes the target,
immutable editing basis, bounded candidate, required preservation and existing
format-specific storage rules. The module owns staging, publication, inspection
and cleanup. The caller receives an identified operation before any cancellable
observation wait; admission itself does not wait on filesystem I/O. The operation
view has no disposal or cleanup obligation.

The ordinary caller awaits one initial observation. Bounded current status and
actual-resource-release observation remain available independently. An observation
may establish that the outcome is unresolved; obtaining it does not itself
establish a successful save. Reconciliation joins inspection of that same
operation and never repeats publication. No event/version loop is necessary for
ordinary saving. Stopping observation, losing a view or dropping the operation
reference does not release the module's admitted work.

Typed request variants retain C's clear operation intent without requiring a
second public owner for every file. The interface still includes request
preconditions, validation evidence, admission, cancellation and result meanings;
few entry points alone do not establish depth.

**B - Prepared capabilities with caller-controlled phases.**

```csharp
var preparation = commits.Stage(plan, immutableCandidate);
var prepared = await preparation.ObservePreparedAsync(stopWaiting);
var verified = await prepared.VerifyAsync(formatVerifier, stopWaiting);
var operation = verified.Publish();
var result = await operation.ObserveAsync(stopWaiting);
```

The caller chooses when verification and publication begin. Typed capabilities
refer to one module-owned operation, not disposable filesystem handles. The
module still performs every write, flush, comparison, replacement, inspection
and cleanup. Repeated publication of the same capability cannot repeat the
physical mutation.

This supports delayed publication, but adds preparing, prepared, verifying,
verified, abandoned and expired states. Callers must finish or abandon prepared
work and understand admission/expiry races. Dropping a capability or expiring
its admission cannot free issued I/O. B would need a bounded preparation lifetime
and additional retention rules. Current save callers show little need for this
flexibility, so those obligations have limited demonstrated leverage.

**C - A public per-file owner with named complete operations.**

```csharp
var file = commits.ForFile(target, establishedRequirements);
var operation = file.SaveExisting(originalBasis, preparedCandidate);
var result = await operation.InitialObservation.WaitAsync(stopWaiting);
// Other intents: file.CreateNew(...) / file.RestoreExplicitly(...).
// Further inspection: file.ReconcileAsync(operation.Id, stopWaiting).
```

The file owner captures stable storage requirements and provides discoverable
complete operations. It owns serialization of physical work, uncertain outcomes
and actual I/O lifetime. An editor must still supply its original immutable
basis: another editor's successful save cannot silently rebase that draft.
As in A, accepted operation identity must exist before observation can be
cancelled; an async method that loses that identity on cancellation is insufficient.

The cost is a public file-owner identity, registry and lifetime in addition to
existing profile/store/vault owners. The file owner cannot become a second
authority over effective revisions or secret material. Internal target admission
is necessary in A too, but current callers do not demonstrate a need to manage
another public owner. Standalone archive and empty-file initialization workflows
are not newly selected operations through this comparison; their established
domain policies and candidate 4's scope remain intact.

### Result and lifetime constraints in the comparison

The following apply whichever interface is selected. They make the accepted
scope concrete without choosing final result types or numeric budgets.

| Observable fact | Required distinction |
| --- | --- |
| Target state | The expected candidate is verified at the target; the original is verified intact; an unexpected revision is present; the target is missing; or the observation is unavailable. A comparison is evidence at that observation, not immunity from future external edits. |
| Required preservation | No preservation is required, the previous valid backup is verified, an explicit restore's original recovery copy is verified, preservation is known to have failed, or evidence is unavailable. A damaged preserved original is not called a valid backup. |
| Overall result | Complete verified publication, known failure without publication, known partial effect, conflict and unresolved outcome are different results. Candidate-at-target plus missing required preservation is not either plain Saved or an unchanged original. |
| Resources and artifacts | Issued work/verification has actually completed or remains owned. Cleanup failure/pending work and deliberately retained recovery artifacts are reported independently of known target effects. Resource release does not prove preservation, resolution or permission to resume writes. |

Complete successful publication requires evidence for the installed candidate
and every required preservation obligation. The common module reports that
evidence; adopting an effective profile/settings/vault revision remains the
domain owner's responsibility. Partial effects and unresolved outcomes must not
be collapsed into a generic failure that invites blind resubmission, or into
ordinary success. Their exact domain settlement is part of the final contract
review, especially when a password-change candidate is installed but an artifact
obligation remains unresolved.

Verification evidence binds the exact bounded immutable candidate and expected
domain identity to the applicable validation. JSON owners retain schema/content
rules. The vault owner can identify an observed file by equality with an already
authenticated encrypted image; unknown bytes do not become authenticated because
their vault ID matches. An independently selected restore image needs its own
authentication/schema evidence. This does not select repeated unlock/decryption
for every reread or pass keys/plaintext to the common module. Verification work
must not create a callback/lock cycle with the domain owner or prolong a secret
reference beyond its existing operation lifetime.

Cancellation of observation stops only that wait. An explicit operation-cancel
request can prevent unissued work; after publication may have happened, it cannot
prove rollback and must leave inspection or unresolved ownership intact. Issued
resources and required domain verification remain owned until actual completion.
The vault owner retains the old and proposed roots under its existing rules;
reconciliation does not choose a root or clear an external-stale restriction.

Every new submission is a new requested mutation. Re-observing or reconciling an
identified operation is not another submission. Concurrent reconciliation joins
existing inspection; it never creates another candidate or changes the original
basis. The module blocks conflicting mutations while an earlier operation still
has unresolved or potentially mutating work, and coordinates the established
affected-store freeze with its domain owner.

Admission reserves bounded operation, byte and artifact capacity before taking
ownership. Repeated cancellations cannot create unlimited outstanding workers
or retained candidates. Cleanup acts only on artifacts demonstrably owned by
that operation; a reused pathname is not proof. Existing approved location,
identity, size, access and per-format writer-exclusion rules remain binding.
Internal target coordination is not atomic compare-and-swap against external
writers or a new profile-registration identity scheme. Tokens identify
in-process operations; this design does not introduce a journal or automatic
restart recovery.

### Comparison and selected rationale

Q8 selected **A with typed complete requests and a read-only operation view**.
All three alternatives concentrate the physical recipe and therefore improve
locality. A provides the same depth without B's caller-controlled phase protocol
or C's additional public file-owner lifecycle. Existing domain owners already
hold the paths, editing bases and effective revisions. Clear request variants
provide operation discoverability while the shared module retains uncertain
work and resources. In this case, C's named operations do not establish more
leverage than A's complete requests.

This recommendation removes the ordinary observer's event/version loop from
the initial minimal-entry exploration: await the initial result, inspect bounded
status when needed, and explicitly request reconciliation of the same operation.
It does not remove internal conflict admission or ownership tracking. The exact
validation-evidence representation, target/alias admission rules, bounded capacity,
retirement of unresolved operation records and partial-effect settlement remain
for the selected interface's final review. They cannot be silently assumed to
match today's duplicated implementations.

Domain validation and result classification are in-process work. Filesystem I/O
and scheduling/time have internal local-substitutable seams with production and
controlled adapters. Tests use the ordinary interface and independent expected
files/revisions/ownership; real-filesystem and supported UNC integration remain
necessary. No implementation, prototype or runtime test was performed in this
comparison.

### Complete-operation contract accepted in Q11

Q8 selected the interface; Q9-Q10 settled adoption and closing. Q11 confirmed
the following complete contract and verification plan as design-ready.
This is an implementation contract, not a claim of implemented behavior.

**Admission and immutable evidence.** Synchronous admission reserves bounded
capacity and records an operation ID before returning an accepted operation
view. Rejection transfers no ownership and makes no file changes. Filesystem
identity resolution, validation work and I/O occur in owned execution after
admission; acceptance is not proof that the target can safely be changed.
Establish target coordination before any mutation. New submissions remain new
operations, while observation and reconciliation identify existing work.

Bind the request to its original editing basis and exact bounded immutable
candidate. Validation evidence identifies the applicable format/rules, domain
identity/revision, length and content digest. It is internal evidence produced
by the existing format owner, not a caller-supplied path or UUID that grants
trust. Ownership transfer or a bounded copy must prevent later draft/buffer
changes from altering the validated image. The original editing basis never
silently advances because another editor saved or reconciliation saw new data.

Each target/candidate/backup comparison applies to one bounded acquired image
with its own observed identity. Do not hash one read and validate a different
read as if they were a single observation. Separate file observations are not
an atomic multi-file snapshot; detected change invalidates the relevant evidence
and produces conflict or unresolved state. Evidence for equality with an already
authenticated encrypted image can identify that vault generation without another
unlock. Independently selected restore content requires its own authentication
and schema validation. Unknown bytes with the same vault ID are insufficient.
The shared module never receives root keys, a master password or plaintext
credentials. Verification and domain result handling do not run while holding
a mutex they may need to reacquire; logical operation admission remains held.

**Target coordination.** Keep the normalized opened location and its target-name
reservation through publication and reconciliation, using the existing directory
case/short-name/path rules. Coordinate a missing target through its parent
namespace and final name. Also detect collisions through observed physical
identities and known aliases. If safe coordination cannot be established for
potentially conflicting admitted work, refuse/defer mutation rather than assume
the names are independent. This does not merge profile registrations or silently
redirect the requested opening path.

Track the old target and candidate/installed identities separately. The file
installed by replacement can have a different ID, so the old handle's identity
alone cannot maintain the reservation. The requested name remains protected by
internal admission while the physical observations change. Neither those
observations nor an independent cooperating-writer lock make the operation an
atomic compare-and-swap against arbitrary external writers. Per-format writable
location, access and writer-lock policies remain unchanged.

**Observation and cancellation.** Include the operation ID and a monotonically
increasing observation revision in bounded immutable results. The domain owner
ignores obsolete observations for its pending operation; old callbacks cannot
overwrite a newer effective revision. This does not require a monitoring loop
for ordinary saving. Initial observation, current status, reconciliation and
actual resource release remain distinct.

Serialize operation cancellation against issuing publication. Only preventing
publication from being issued can establish that this operation did not publish;
even then, an external change is reported separately. Once a mutation has been
issued or may have been issued, inspect its actual result instead of reporting
rollback. Seeing the old target or an absent create destination is not a final
no-publication result while issued work could still change it. Cancelling a wait
only ends that observation. Concurrent reconciliation joins the same inspection,
does not change its editing basis and never republishes or restores automatically.

**Independent outcome facts.** The table defines the evidence available to the
domain owner. Accepted Q9 governs adoption for the partial-preservation rows.

| Observed facts after relevant issued work settles | Result contract |
| --- | --- |
| Candidate verified at target and all required preservation verified | Complete verified publication; adopt through the existing domain owner. Cleanup status remains separate. |
| Candidate verified at target, required backup/recovery copy known missing or different | Known partial publication with preservation failure; neither Saved nor unchanged. Preserve evidence of what reached the target. |
| Candidate verified at target, required preservation unreadable/unverified | Target publication is known; preservation remains unresolved. Do not erase the known target fact or report complete success. |
| Original update/restore target verified intact | No new effective revision; retain draft and prior effective state. Report any candidate/backup side effects separately. |
| Create destination verified absent | Creation did not become effective. Do not call this an intact original, because no original existed. |
| Unexpected revision or detected external change | Conflict; preserve external data and the owned recovery candidate required by policy. Do not clear vault external-stale restrictions. |
| Target missing/damaged or inaccessible | Distinguish observed absence/damage from unavailable evidence. Keep the existing freeze/recovery policy; no automatic restore, empty reset or blind retry. |
| Only cleanup fails or remains pending | Preserve the known publication outcome. Report remaining artifacts and actual-resource lifetime without converting success into "nothing saved". |

An explicit restore's recovery copy must match the observed original bytes even
when those bytes are damaged. It is not a validated ordinary backup. Complete
publication remains possible after a filesystem exception if all required
outcome evidence is verified; successful return from a filesystem call alone
does not establish it.

### Q9 - Adoption after partial publication: accepted

The user accepted the strict complete-publication condition for adopting a
new effective revision. If the target contains the candidate but required
preservation is missing or unverified, keep the last effective in-memory state
and draft, block further ordinary writes to the affected target, and report
that the target changed but the save's preservation obligation did not complete.
Do not label it Saved, not saved, or unchanged without the accompanying facts.

For a vault, also block new credential retrieval from the affected owner.
Existing terminal connections continue. Keep the old and proposed roots only
under that owner's existing lifetime/active-operation rules; do not adopt a root
as ordinary success or clear a needed root merely because the target copy is
known. In a password change, clearly report that the observed on-disk generation
is the candidate generation, so the user is not told the old password still
describes that file. The common module makes no credential decision.

Reinspection can resolve unavailable evidence. A known missing artifact does
not improve through repeated inspection, so do not loop indefinitely or silently
recreate it. Remain stopped until the existing explicit recovery or next-
application-run workflow establishes the next usable state. Ordinary reopening
alone does not clear this partial/unresolved restriction. Keep the stricter
selected-vault external-stale rule when applicable. Recovery of this operation
must not clear an independently detected external conflict.

An explicitly authorized recovery is a new operation, not another issuance of
the old save. It may receive the target reservation from the quiescent unresolved
operation only after all earlier potentially mutating work has actually ended,
the domain owner has chosen the established recovery action, and required
artifacts/evidence remain owned. Hand over the reservation without a gap; a
failed recovery cannot silently clear the previous restriction. A later
application run reads and validates the actual file through the ordinary format
workflow; it does not retrospectively turn the earlier partial save into success.

The alternative is to adopt the verified target immediately while reporting
degraded preservation. That would create an explicit exception to the current
failure-keeps-effective-state rule, including root adoption during password
change. Q9 selected the strict adoption rule and rejected that exception for
this refinement.

### Q10 - Quiescent uncertainty and closing: accepted

The user accepted distinguishing pending physical work from a quiescent but
unresolved save. A disappeared view or cancelled observer cannot dispose issued
I/O or domain verification still using its owner. Retain the existing operation/close
guards while such work is pending; do not import the recording five-/ten-second
policy, declare cancellation complete or create a hidden process lifetime.

Once all issued I/O and owner-dependent verification have actually ended, allow
the user to close an editor or exit with the unresolved/partial outcome clearly
identified. Integrate that fact into the existing closing decision, including
the relevant target and retained recovery-artifact locations. Choosing to leave
does not mark the draft saved, discard preserved files, repeat publication, or
silently approve a degraded revision. Exact localized presentation remains
with the existing UI owners; this decision does not create a generic recovery UI
inside the file module. Serialize the quiescence check and owner termination
against admission of further inspection or owner-dependent verification. A late
callback cannot start new verification against a released root/owner. Later
application-owned inspection uses retained non-secret evidence; any work needing
a new domain owner must follow that owner's ordinary authorization/lifetime.

If the application continues, the module retains compact operation/evidence
records and target admission restrictions independently of the closed view.
Reopening that location cannot bypass the restriction by creating a new owner.
Selected-vault root lifetime still lasts through the application run; an editor-
only vault follows its existing release-after-in-flight-settlement rule. Ending
the responsible domain owner's lifetime ends its secret retention rather than
keeping roots solely for the common module's inspection records. Encrypted
artifacts and non-secret evidence must remain sufficient for the file module to
report the unresolved state without owning secrets.

On full application exit, normal secret cleanup and lifetime termination apply.
Preserved files remain for explicit later recovery, but in-memory operation IDs
do not survive and no journal, automatic recovery, hidden tray run or guaranteed
cleanup after process termination is introduced. Q10 chooses visible exit from
quiescent uncertainty, rather than forcing a user to keep an editor/application
open indefinitely because a file cannot currently be inspected. It does not
authorize forgetting live I/O or automatically deleting recovery artifacts.

### Bounded ownership and retirement

The common module requires finite, versioned internal limits for admitted
operation records, owned memory, retained artifact count and retained artifact
bytes. These limits are engineering configuration, not new settings UI. Reserve
capacity before acceptance and before further owned allocation; reject/defer
new work without effects when the required capacity is unavailable. Count
candidate/baseline copies, verification buffers, metadata, issued work and
retained recovery artifacts. Count shared immutable bytes once only with actual
shared ownership. Keep all per-format size limits.

One conflicting target admits at most one potentially mutating or unresolved
operation, except for the explicit quiescent recovery handover described above.
Once I/O has settled and sufficient comparison evidence remains, release large
candidate buffers and retain compact identity/length/digest, outcome, artifact
and reservation records. A wait cancellation, timeout, TTL or pressure eviction
cannot erase unresolved state or release its admission restriction. Deliberately
preserved files can transfer to an existing domain recovery owner only with
explicit ownership/accounting and evidence; this is not a quota-avoidance trick.
Normal long-lived user files and validated `.bak` files are distinct from the
module's transient or unresolved artifacts.

Actual-resource release is scoped to the identified inspection pass/observation
revision: its issued I/O and verification callbacks must finish before their
handles/buffers can be freed. Later reconciliation of an unresolved operation is
a newly admitted inspection pass and can have pending resources again; a past
release observation does not establish present quiescence. Only retirement is
terminal for that operation's inspection admission. File deletion or a verified
handover determines artifact-accounting release separately. Only resolved,
resource-free records can retire from active tracking. Retired/unknown IDs
return that fact and never become new publication requests. Do not keep an
unbounded completed-operation history or delete unidentified files merely to
make room.

The existing four concurrent preparations/eight cancelled-but-incomplete
preparations are not a complete budget for generic commits or retained artifacts.
Current source has `ApplicationResources.FilePreparations` with four slots in
`src/TabTty.Core/ResourceQuota.cs:110`; that does not establish all the specified
abandoned-work accounting. Reuse shared preparation admission where applicable,
without treating cancellation as an extra worker allowance. Freeze the new
module's finite limit values in versioned internal policy at implementation
start, then test at and beyond those configured limits. No new numeric constant
or measured capacity is claimed by this design review. Capacity bounds do not
guarantee an unresponsive OS I/O operation will finish by a deadline.

### Candidate 3 verification plan

Use ordinary submission/observation/reconciliation with real format validation
and vault cryptographic behavior. Internal filesystem/time adapters provide
production behavior and controlled faults; real-file integration and independent
cryptographic fixtures remain necessary. Use literal expected bytes, independent
expected state and owned-resource observations, not the same serializer as the
only oracle or private helper counts.

| Case | Required observable result |
| --- | --- |
| Ordinary update, create and authorized restore | Correct target and required backup/recovery contents; effective revision changes only under the selected adoption contract |
| Replacement reports error after candidate becomes target | Classify by verified actual effects; observe/reconcile the same ID without issuing another replacement |
| Candidate installed but preservation missing, different or unreadable | Keep target and preservation facts separate; apply Q9 rather than ordinary success or false unchanged state |
| Original intact, create target absent, unexpected target, target missing/damaged or unreadable | Distinct results with no guessed rollback, automatic overwrite, restore or empty reset |
| New-file collision and external change between comparison/publication/verification | Reject/refuse on detected conflict, preserve observed external data and required own recovery material, and report discovered effects; do not promise preservation against an undetected noncooperating-writer race or claim atomic compare-and-swap |
| Stale editor basis, case-sensitive names, short/long aliases and known linked locations | Do not rebase drafts; coordinate known physical/name conflicts without merging registrations or changing opening-location semantics |
| Replacement changes file identity; new request arrives through the same name/known alias | Maintain target-name exclusion through identity changes and unresolved state; no second mutation through a stale identity key |
| Candidate buffer/draft changes after validation, or same-ID vault has different bytes | Immutable accepted evidence remains authoritative; reject mismatches, never infer authentication from ID alone |
| Separately chosen restore image or corrupt original | Authenticate/validate restore content independently; preserve exact original bytes without calling the corrupt copy a valid backup |
| Cancellation before/after publication issue, lost wait and concurrent reconciliation | Operation identity survives; no early release or repeat publication; old observations do not reverse newer owned outcomes |
| Cleanup error or reused candidate pathname | Keep known publication facts; never delete replacement files without proof of ownership; preserve charges until real release or accepted handover |
| Password-change uncertainty and unrelated external vault stale | No premature root disposal/adoption, no secret in common records, and no reconciliation that unblocks external-stale retrieval |
| View closure, late verification/I/O and quiescent exit | Apply Q10; pending work retains owners, quiescent unresolved evidence survives view closure, and exit never reports false save success |
| Operation/byte/artifact capacity reached, repeated cancellations or retired IDs | Reject/defer new work without effects, retain existing charges/freezes, bound status/history, and never replay via an old ID |
| Profile publication succeeds but registration/settings update fails | Preserve the separate file outcomes; do not claim a multi-file transaction |

Retain real local-filesystem and supported-location integration for permissions,
same-directory publication, backup contents, conflicts and actual completion.
UNC verification applies only to formats that already support that location;
the vault's supported writable-location policy is unchanged. Existing profile,
JSON recovery and conditional vault tests cited above provide useful source
coverage, not execution evidence for this matrix. No tests were run here.

### Candidate 3 readiness

**Q11 - Final shared understanding: accepted.** The user accepted the complete
contract, its 15-case future verification plan, specification consolidation and
proceeding to candidate 4. Q7-Q11 complete candidate 3's design review.

Final read-only reviews of outcome/adoption/recovery and
identity/resource/secret lifetime found no further material inconsistency or
product decision. The [save/recovery specification](../specification/persistence-and-windows.md#verified-file-operations)
and [vault integration](../specification/vault-format.md#file-operation-integration-and-unresolved-ownership),
with their Japanese counterparts, consolidate the accepted contract. The finite
limit values and concrete type names are implementation engineering details,
with explicit policy-freeze and validation obligations above. Candidate 3 is
ready for separately requested implementation. No implementation, prototype or
runtime test was performed or authorized by this review.

### Platform guidance checked for this exploration

Microsoft's [ReplaceFileW documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
describes error cases with different remaining file-name states and requires the
replacement/backup/target to be on the same volume. The module must therefore
inspect actual outcomes rather than interpret an exception as unchanged files.
Microsoft also lists replacement among operations that do not support the
ordinary I/O cancellation mechanisms in
[Canceling Pending I/O Operations](https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations).
Cancellation intent cannot establish that publication did not occur.
The [FlushFileBuffers contract](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)
describes flushing a file's buffered data; it is not a multi-file transaction.

The [FILE_ID_INFO contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
identifies open-file equality using volume identity together with file ID.
ReplaceFileW's remarks state that the result retains the replacement file's ID.
Consequently, this design keeps the target-name reservation while separately
tracking observed old and installed identities; it does not use file IDs to merge
registrations or promise future pathname contents.

These sources were rechecked on 2026-11-17 to ground the existing file-outcome
contract. This scope does not change cryptographic algorithms, authentication,
secret lifetime or storage guarantees. The existing security recheck/review gates
remain required at implementation start and before release. Do not claim universal
power-loss durability, cloud synchronization safety or portable-file rollback
prevention from flushes, locks and digest comparisons.

### Candidate 3 decision status

No design decision remains open for candidate 3. Implementation-time parameter
selection and the future verification/review gates remain required work.

## 4. Credential vault management-operation ownership

Status: design-ready. Ownership scope was accepted in Q12, C and the operation
policies in Q13-Q15, and the complete operation/interaction contract and final
shared understanding in Q16 on 2026-11-18.

### Existing contract and source evidence

[ADR 0008](../adr/0008-share-vault-ownership-with-file-editors.md) already makes
connections, management tabs and the selected vault's standalone editor share
one owner and its committed revisions. Editors for other files have their own
owners and keep their opened paths. Selection transfers roles after affected
operations settle. The [vault contract](../specification/vault-format.md#secret-owners-and-cleanup)
fixes the root and transient-secret lifetimes. Older drafts excluding multiple
open vaults do not override this later editor-only ownership: connections still
use only one selected vault.

Candidate 3 fixes the seam for physical file publication and reconciliation.
Candidate 4 concerns the complete vault operation around that seam, including
vault validation, secret ownership and adoption. It must not introduce a second
authority for the file's committed revision or move keys into the file module.

| Scattered responsibility in current source | Evidence |
| --- | --- |
| UI construction composes serialization, a mutation and optional metadata refresh | `src/TabTty.App/VaultController.cs:188-204` combines `RunAsync`, the action and `RefreshManagementAsync`, and extracts `Unlocked` plus an entry for callers |
| Restore selects different lower-level paths and owner adoption/disposal for locked and unlocked files | `src/TabTty.App/VaultFileController.cs:415-443` calls either the current owner's restore or recovery that returns a new owner, then refreshes the management view |
| Create/initialize/unlock dispatch, returned-owner adoption and disposed-view handling live with interaction code | `src/TabTty.App/VaultFileController.cs:253-320` |
| Archive completion updates owner/metadata separately from editor closure | `VaultFileController.cs:51-66` and `VaultController.cs:172-179` |
| Restore and master-password change repeat candidate-root transfer and uncertainty retention | `src/TabTty.Core/CredentialVault.cs:253-323` separately manage replacement roots, old-root clearing and `unresolvedRoot` |
| Role lifetime and selection ordering span the registry and settings caller | `VaultController.cs:17-61` tracks selected/editor/busy/stale state; `src/TabTty.App/App.cs:314-340` checks path changes, commits settings and notifies selection change |

These are static source observations, not reproduced runtime failures. The
registry has real ownership responsibilities and is not disposable merely
because it delegates. `VaultUnlockInteraction` likewise owns shared waiters and
cancellation and is not a pass-through. The opportunity is to remove the
callers' need to assemble a vault operation, not to delete useful modules by count.

### Q12 - Ownership scope: accepted

The user accepted a deep vault-management module that completes each admitted
operation for the appropriate shared file owner. Callers request the user's operation;
they do not obtain the unlocked vault, arrange serialization and remember a
separate refresh. Q13 subsequently selected C's concrete interface below;
internal composition remains an implementation engineering detail.

| Responsibility | Owner |
| --- | --- |
| Complete create/unlock, entry add/update/delete, key import, password change, encrypted backup, restore and established archive operations | Vault-management module, preserving each operation's existing authorization and file-identity constraints |
| Per-file operation admission, shared unlock coordination, immutable operation target, vault validation, transient secrets, old/candidate root lifetime, verified snapshot adoption and committed metadata/revision notification | Vault-management module using the existing shared owner rather than a second revision authority |
| Selected/editor role transitions and release eligibility after affected work settles | Vault ownership logic in that module, coordinated with the existing application settings/window owners |
| Physical publication, target/candidate/backup evidence, retained artifacts and reconciliation | Candidate 3's shared file module; the vault module interprets its results under the accepted adoption and stale rules |
| Localized forms, master/password input controls, file pickers, existing confirmation and impact display, window hosting/closure and list presentation | UI owners; owned secret input passes to the vault operation with the established cleanup lifetime |
| Finding known registered-profile references and committing profile/settings files | Existing profile/settings owners; collaborate for the existing impact confirmation and selection workflow without a cross-file transaction |

The module must complete publication/adoption and notify shared committed state
without a caller-supplied refresh recipe. A failed presentation refresh cannot
reverse a known commit or turn it into a retry of the mutation. UI ownership of
confirmation does not let the vault operation bypass a required decision or
apply one obtained for another target. The later interface review must make this
coordination explicit without embedding WPF or translated strings in vault logic.

This scope adds depth by making a full management intent the interface's unit
of work. Its leverage spans management tabs, standalone editors and the selected
vault's credential consumers; locality concentrates operation completion,
uncertainty and secret cleanup. Under the deletion test, removing this module
would redistribute those recipes across its callers. Adding a forwarding layer
while retaining `RunAsync`/`Unlocked`/refresh obligations would not achieve it.

### Constraints already settled

- Preserve the selected/editor roles and unlock lifetime of ADR 0008. Do not
  add automatic relocking or cache all decrypted entries. Credential retrieval
  for authentication uses only the selected vault and existing host-trust and
  exact reference checks.
- Preserve [Q111's shared-entry rules](profile-management-proposals.md#q111-shared-credential-entries-and-migration):
  exact vault/entry/kind identity, known registered-profile impact display before
  destructive or secret-changing operations, new entry for a changed kind, and
  unresolved references after deletion. Do not silently rewrite profile files
  or infer references in unknown files. Active authenticated sessions continue.
- Preserve the accepted key-import allowlist, canonical PKCS#8 storage, unchanged
  source files, transient source passphrases and one expensive import/KDF at a
  time. Native work retains live buffers through actual completion even after
  cancellation. Cryptographic construction is not reconsidered by Q12.
- Preserve candidate 3's independent target/preservation facts, strict complete
  adoption, no ordinary reopening bypass, explicit recovery, quiescent closing
  and bounded non-secret records. External-stale restrictions remain independent.
- Preserve manual encrypted copy and restore semantics; archive is not a new
  general overwrite permission or a generic operation added to candidate 3.
  Plaintext export, cloud merge, OS automatic unlock, recovery keys and initial
  macro execution remain outside scope.
- Settings persistence, selection-role transfer and visual hosting remain
  separate responsibilities. A file operation does not silently retarget an
  editor or make settings/vault/profile updates one transaction. Exact busy/
  deferred-selection behavior must follow existing requirements or be explicitly
  decided if the later interface requires a product change.

### Initialization and archive facts to preserve or resolve

The [editor contract](../specification/persistence-and-windows.md#file-explorer-and-editors)
rejects missing/empty vaults in Explorer editors and their credential picker,
permits initialization through vault management, and requires target-path
confirmation for restore/archive. Archive success in a standalone editor shows
the destination and closes that editor after acknowledgement. Failed unlock of
a nonempty file never becomes empty initialization.

Current source and `README.md:158-178` additionally describe initialization of a
missing/zero-byte configured file and archival to a unique sibling without
changing the selected path. `CredentialVault.cs:433-495` parks only an observed
empty regular file, creates without overwriting, and archives by non-overwriting
move under the writer lock. Archive preserves the old bytes/identity/password;
later creation at the configured path creates a different vault identity.
Archive itself does not create the new vault or move its `.bak`.

These code paths do not establish all desired guards. Archive currently does
not authenticate contents or compare the file to an owner/confirmation snapshot;
zero-byte initialization rechecks emptiness but does not track physical file
identity across the check and move. Source also permits initialization from
some ordinary profile-picker/authentication paths. The accepted documents
did not clearly settle all those entry points or archive checks before this
review. Do not silently promote these implementation details into accepted
policy. Q14-Q15 and the complete contract below now settle their authorization
and guard requirements. The accepted module scope does not authorize arbitrary
empty-file replacement or archival of changed data.

### Verification direction

Ordinary callers and tests should use the same eventual interface for complete
operations, including shared metadata/revision effects and secret ownership.
Use real vault schema/cryptographic behavior, candidate 3's file module and
controlled internal adapters for delayed completion, filesystem faults and
interaction outcomes. Production and controlled adapters justify those seams;
a mock vault that bypasses authentication/adoption is not the only test surface.

Current tests provide useful foundations: `tests/TabTty.App.Tests/FileEditors.cs:45`
checks owner reuse/selection; app `Program.cs:1434` checks unlock waiter failure
and individual cancellation; core `Program.cs:373-497` covers entry changes,
password change, restore, recovery, empty initialization and archive. They do
not establish the whole operation contract for shared revision propagation,
uncertain root adoption, closure/selection races or unresolved reopening.
No tests were run for this exploration.

### Interface alternatives for Q13

Three independent designs explored the accepted Q12 scope. All examples below
are specification sketches, not implemented types, prototypes or authorization
for new product behavior. Q14-Q15 separately address initialization and archive.
The user-facing framing preceded the parallel exploration: complete operations
must preserve shared owners, secret lifetimes, bound confirmation and candidate
3's result rules while keeping UI presentation outside vault logic.

**A - Complete typed commands with central observation.**

```csharp
// Accepted-admission path only; rejection has no effects or ownership transfer.
var admitted = vaults.Submit(
    DeleteEntry(current.Basis, entryId), interactionOrigin, requestStop);
var result = await vaults.Observe(InitialResult(admitted.Id), stopWaiting);
```

Three top-level entry points cover submission, observation and owner-role
coordination. Closed command types describe complete vault operations, queries
return bounded non-secret state, and role-change requests cover editor release,
selection and application exit. No string dispatch, arbitrary callback mutation
or extensible command bus is implied. The module owns interaction, validation,
publication and shared revision updates. Reconciliation identifies existing
work rather than admitting another mutation.

This offers leverage across many callers with few entry points. Its interface
still includes the command/query/result families, target basis, role epochs and
interaction protocol: method count does not make those obligations disappear.
Ordinary WPF actions must learn request construction and observation queries in
addition to the domain operation. Locality is strong, but the seam is less direct
for the existing named-action callers.

**B - Prepared operations and one-use approval capabilities.**

```csharp
var preparation = vaults.Prepare(intent, interactionOrigin, requestStop);
var prepared = await preparation.ObserveAsync(stopWaiting); // Ready branch only.
var approval = await ui.ReviewAsync(prepared.Review, stopWaiting);
var operation = vaults.Commit(prepared.Token, approval);
var result = await operation.InitialObservation.WaitAsync(stopWaiting);
```

Preparation first returns an identity/observation view and owns asynchronous work;
its ready result captures the exact target, basis, candidate and review/impact facts.
Its non-secret token is bound to one operation. Approval authorizes only that
review, and commit checks and consumes it once before mutation. Stale target,
entry or relevant selection evidence requires a new review, not automatic
rebasing. The module still owns crypto, secrets and actual work; tokens do not
grant general file access. Abandonment/expiry of an uncommitted preparation can
cancel unused work but cannot release live native resources or an unresolved
issued publication.

This seam makes inspect/review/commit reusable and separates a long human review
from file-mutation admission. It also gives callers a public lifecycle to manage:
preparation, review, consumption, abandonment, expiry and stale-review recovery.
Prepared secrets/candidates require bounded retention. Its leverage is greatest
when multiple products need independently hosted previews; current management
tabs and editors share one operation flow. Locality weakens if each caller must
reassemble those phases, even when internal ownership remains sound.

**C - Named complete operations on a file-bound context.**

```csharp
var file = vaults.ForEditor(editorRole, openedLocation, interactionHost);
// The selected-vault management tab obtains an equivalent selected-role context.
var operation = file.DeleteEntry(selectedEntry.Anchor);
var result = await operation.ObserveAsync(stopWaiting);
// Committed metadata is published by the module; no Unlock/Run/Refresh recipe.
```

The context exposes named complete operations such as `Unlock`, `AddPassword`,
`EditEntry`, `DeleteEntry`, `ImportPrivateKey`, `ChangeMasterPassword`, `Backup`,
`Restore` and `Archive`, with explicit initialization subject to Q14. It carries
an immutable opened-location/binding identity and references the existing shared
owner internally. It owns no root, plaintext, independent encrypted snapshot or
effective revision. Holding a context cannot extend unlock lifetime; it is not a
second public unlocked owner. The app/window coordinator, not ordinary button
code, collaborates on selected-role changes and editor/application termination.

Each operation performs bounded synchronous admission without filesystem I/O
waits, then returns an owned operation view or typed rejection. The operation
fixes its target and entry anchor before deferred execution. Input, unlock and
required decisions are obtained internally through a typed interaction adapter.
The caller neither selects a locked/unlocked restore path nor arranges root
adoption or a later metadata refresh. Names are illustrative, not fixed method
signatures.

An entry anchor includes binding identity, vault/entry IDs, expected kind and
observed revision. The operation view distinguishes initial/current observation,
requesting work cancellation, reconciling the same operation and actual-resource
release. Results are bounded and non-secret. Metadata subscriptions coalesce
current state; discarding a subscription or operation view only ends observation.
No ordinary caller must maintain an event loop to establish completion.

This has more named methods, but each maps to a user operation already present
in the product. It concentrates the complete workflow behind the existing vault
owner's seam, offering direct leverage for management tabs and editors. The
principal cost is implementing the interaction/lifetime protocol internally;
that complexity exists in every sound alternative rather than vanishing with
fewer method names.

### Shared interface requirements exposed by the comparison

These requirements carried the accepted ownership and security rules into the
interface comparison. Q13 selected C; the complete operation/interaction contract
below consolidates the final-review result accepted in Q16.

- **Immutable target and review.** Requests and interaction replies identify
  the operation, opened location, binding/selection epoch where applicable, and
  relevant entry/revision or acquired file image. A boolean obtained for another
  target is not authority. Detected change yields conflict/expired review, not
  automatic rebase. A selected-only context is superseded for new requests after
  selection changes; previously admitted work remains bound to its original file.
  Editor contexts keep their opening location. No copied vault ID alone merges
  different locations or owners.
- **Interaction and secrets.** WPF, localization, input controls and modal
  hosting remain in the production interaction adapter. Scripted responses are
  a second real adapter for tests. Input transfers once to an accepting operation;
  unaccepted/late replies and unsubmitted controls remain the adapter's cleanup
  responsibility. Owned buffers stay live through native completion. Results,
  operation history and shared file records never carry passwords, keys or
  decrypted payloads. Expected errors use non-secret codes; authentication
  failure does not diagnose wrong password versus tampering with certainty.
- **Shared unlock and actual work.** Concurrent consumers join one per-file
  unlock attempt. A consumer's stop request removes its interest; stopping only
  observation does not cancel the operation. Other consumers are not cancelled
  by one departure. Ending the shared attempt does not free still-running native
  work. Hosting may transfer or end through the existing coordinator, without
  automatic prompt loops. The single expensive KDF/import worker rule remains.
- **Admission and reentrancy.** Serialize conflicting file operations and owner
  transitions. UI/profile/settings callbacks run without a physical mutex they
  might reacquire; necessary logical admission stays owned. Pending prompts and
  input/operation/observer records have finite bounds, as do secret/encrypted
  buffers and artifacts. Repeated cancellation cannot create unlimited work.
  Concrete finite values require internal policy and threshold verification at
  implementation start, not new settings UI or a guessed measurement.
- **Adoption and notification.** Precompute the non-secret metadata projection
  from the validated candidate and publish it with the adopted encrypted snapshot,
  effective revision and any new root in the shared owner. Candidate 3's complete
  target/preservation proof is required; partial publication keeps the accepted
  freeze and truthful on-disk generation facts. UI delivery failure does not
  reverse the commit or permit a retry. A later current-state read can catch up.
- **Outcomes.** Separate completed operation, user decline, stopped-before-issue,
  validation/authentication failure, detected conflict, known partial publication
  and unavailable evidence. Preserve file effects, cleanup, actual release and
  the effective revision as distinct facts. Backup need not change the source's
  effective revision. Reconciliation coalesces inspection and never re-encrypts,
  republishes or repeats archive. Resource release is scoped to the observed pass;
  a later inspection can hold resources again.
- **Profile and settings collaboration.** Existing profile owners find exact
  known registered references for impact display, including unavailable/scope
  information. They retain profile persistence. Existing settings owners commit
  their own draft/revision. Owner-role coordination reserves affected files,
  admits a transition only after their affected active work has settled, and
  transfers selection only after the
  settings owner adopts a verified selected-path change. Failure/partial settings
  publication does not switch the effective vault role. Preserve separate
  outcomes, editor targets and existing owner promotion; no cross-file transaction.
  After verified settings adoption, the reserved role transfer is owned in-memory
  completion, not another optional UI step that view closure can abandon.
  The complete contract below preserves the current pre-save Busy rejection
  rather than introducing automatically deferred settings application.
- **Closing.** Actual I/O/native/owner-dependent verification and new inspection
  admission are coordinated with owner termination. Once quiescent, the accepted
  visible closing/exit rules apply. Selected/editor roots follow ADR 0008 and
  never survive solely for non-secret uncertainty records. A new binding cannot
  bypass an unresolved restriction or the independent external-stale policy.
- **Physical integration.** Candidate 3 remains the complete publication and
  reconciliation dependency. Guarded empty initialization and archive are narrow
  vault operations whose internal mechanics share target/namespace admission
  with that dependency. Do not add a public generic move or spread reserve/
  write/commit/release steps back into UI callers.

Domain ordering, schema, state and adoption are in-process behavior. File I/O
and completion/time have internal local-substitutable adapters; use real local
files plus controlled fault/delay adapters. Cryptographic tests execute the real
vault/OpenSSL path with independent fixtures/vectors rather than a mock vault as
their only oracle. Existing UI, profile and settings modules are local
collaborators, not remote services; injected production and scripted adapters
are justified where their responses actually vary. Tests exercise the same
complete interface as callers and observe committed revisions, files, bounded
metadata and ownership, with native/Windows integration retained.

### Q13 - Interface selection: C accepted

The user accepted **C**, named complete operations on a non-secret file-bound context.
The existing shared file owner gives this context a real domain role, and the
current UI already presents named management actions. C removes `RunAsync`,
`Unlocked`, locked/unlocked restore dispatch and caller-managed refresh from
that UI while keeping operation intent explicit. It gives the strongest leverage
for the present callers without another public prepared-operation lifecycle.

A achieves similar depth and locality, but shifts discovery and ordering into
command/query/result families. B offers independently hosted review/commit
flexibility that the current shared forms do not require, at the cost of token,
expiry and stale-plan obligations. C retains A's typed outcomes and owned
observations and B's exact confirmation binding internally; that does not require
publishing their additional dispatch or preparation protocols. Q13 selects the
interface shape, not an implementation class count or an exemption from Q14-Q15.

### Q14 - Explicit initialization entry points: accepted

The user accepted allowing new-vault creation and initialization of an existing
zero-byte regular file only as an explicitly chosen management operation.
Management can explain a missing/empty target and offer that operation; a
plain Unlock request, authentication credential retrieval and every credential
picker instead report the missing/empty vault and direct the user to management.
Explorer-launched editors retain their existing refusal to initialize. This
narrows the additional implicit initialization paths present in current source;
it is a new accepted product decision, not a finding that all such paths
violated an explicit rule before Q14.

Bind the operation and confirmation to the exact opened location and observed
missing/empty state. For an empty file, establish the identity and zero length
of the same regular file before changing it; preserve it through guarded
initialization until the new outcome permits cleanup. A different file, a
nonempty target, or inability to establish the prerequisite produces a conflict
or unavailable result, never automatic overwrite or a silently broadened create.
An observed missing path uses non-overwriting creation. Failed unlock of a
nonempty file, unsupported format, external-stale state and a partial/unresolved
save never authorize initialization.

The new master input follows the existing confirmation/normalization rules.
Creation makes a new vault identity and root; it does not repair references to
an older vault or rewrite profiles. The configured path remains unchanged unless
the settings owner separately commits a user-requested selection. Exact UI
placement is not selected by Q14. The alternative is retaining initialization
as an implicit side effect of several unlock/retrieval entry points; Q14 did not
select it because the user's intent to use saved credentials is different
from creating an unrelated empty vault.

### Q15 - Archive confirmation and source identity: accepted

The user accepted preserving explicit archival without requiring successful vault
unlock/content authentication. This permits retaining a locked vault whose
password is forgotten, or readable damaged/unsupported bytes, before starting
over. Archival moves bytes; it does not validate them, recover a password or
declare the retained file usable. Existing external-stale and partial/unresolved
restrictions still apply; Archive is not a bypass for them.

Before confirmation, acquire bounded evidence of the specified regular file's
opened location, physical identity, length and contents. Reject directories,
reparse targets and cases where the required evidence cannot be acquired within
the established resource/location policy. If an unlocked owner exists, also
compare with its editing basis; detected external change takes the existing
stale path. Show the source path and effect before asking to proceed. Bind the
answer to that operation and evidence, then recheck before issuing the move.
Detected replacement/change invalidates the answer; do not rebase or move the
new bytes on the old confirmation. UI confirmation need not expose internal
IDs or digests. Identity evidence alone does not prove content authentication.

Move only the confirmed source to a unique sibling name without overwriting.
Maintain source-name coordination and ownership through actual completion and
inspection of the source/destination; an error or cancelled wait is not proof
that no move happened. Do not report archive success until the intended bytes
are verified at the archive location and the source outcome is established.
Distinguish unchanged source, known completed archive, conflict and unresolved
effects; retain evidence and block conflicting work when needed. Never repeat
the move blindly, delete an unidentified file, or promise immunity to every
noncooperating external writer. This is a complete vault operation with internal
filesystem mechanics, not a new generic move request on candidate 3's interface.
These mechanics and guarded empty-file initialization must use the same internal
target/namespace admission authority as candidate 3, including unresolved
restrictions. A separate move lock or registry cannot admit conflicting work
through a second authority. Sharing internal coordination does not expose a
caller-controlled reserve/move/release recipe.

A completed archive ends the old file owner's usable state and releases its
root after dependent work settles. Notify all consumers; existing authenticated
terminal sessions continue. Keep the configured path and profile references,
and do not move/delete the previous `.bak` or create a new vault automatically.
Show the archive destination and retain the accepted standalone-editor closure
after acknowledgement. A later creation is a separate explicit operation under
the selected initialization policy and gets a different vault identity. Exact
resource accounting and late-result contracts are completed with the selected
interface; no new numeric file limit is selected here.

Requiring a successful unlock would make forgotten-password archival impossible;
moving whatever happens to occupy a confirmed path would authorize different
data from what was reviewed. Q15 selected byte-preserving archival with
source evidence and confirmation binding, without either restriction.

### Guidance checked for the interface comparison

On 2026-11-17, the affected memory and file-identity guidance was rechecked.
[OWASP's in-memory secret guidance](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#25-handling-secrets-in-memory)
supports minimizing exposure and using clearable buffers in managed languages.
[OpenSSL's memory documentation](https://docs.openssl.org/3.5/man3/OPENSSL_malloc/)
describes clearing sensitive buffers before release. These support explicit
input ownership and actual-completion cleanup; they do not justify force-freeing
live buffers, changing the accepted unlock lifetime or claiming complete erasure.

Microsoft's [FILE_ID_INFO contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
identifies equality of open file handles using volume identity and file ID.
[SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
supports rename information for an appropriately opened handle. This is a basis
for investigating source-bound move mechanics, not proof of future pathname
contents or a requirement to use a particular implementation. Bounded content
evidence, coordination and actual-outcome verification remain separate.
Q14-Q15 are TabTty product decisions, not rules mandated by these sources.
No cryptographic algorithms or key-file compatibility choices change in this
comparison. The existing implementation-start and release review gates remain.

### Complete operation contract accepted in Q16

Q12-Q15 fix the module scope, C interface and initialization/archive policies.
This section completes their ordering, result and lifetime contract for final
shared-understanding review. It defines future behavior, not implemented code.

**Context, admission and observation.** A selected/editor context carries only
non-secret binding and role information. It neither creates an independent
vault owner nor prolongs an owner's lifetime when retained by a view. Ordinary
metadata subscriptions likewise do not own roots. Bindings distinguish opened
locations and role epochs even for copied files with the same vault ID. Existing
directory/alias and candidate 3 coordination rules still apply.
An A-to-B-to-A selection sequence does not revive the first A context. An editor
binding ends with its editor role even when another role still keeps that file's
shared owner alive.

Named methods reserve bounded operation capacity synchronously before returning
an accepted ID or typed rejection, without waiting for filesystem/native work.
Acceptance transfers only the explicitly accepted inputs. Any deferred operation
retains its fixed target, kind and entry/revision anchor; it does not consult a
new current selection when it eventually executes. The per-file owner orders
conflicting work. Queued inputs, shared unlock participation and acquired images
remain owned and charged. Reject an obsolete context/anchor without substituting
a same-named entry or silently acquiring a newer editing basis.

One initial observation suffices for an ordinary caller. Current operation and
metadata state can be read separately from actual resource release. Observations
carry an operation ID and increasing observation revision; metadata also carries
binding/owner epoch and effective vault revision. Late observations cannot
overwrite newer state or revive an archived/released binding. UI notification
is coalesced; a late subscriber obtains current state without replaying an
unbounded event history. Re-reading state never re-executes a mutation.

**Operation-specific completion.** Each row includes the already accepted
input, validation, secret and preservation requirements. Failure does not bypass
those requirements or turn an operation into a different user intent.

| Operation | Required result and adoption |
| --- | --- |
| Unlock existing file | Validate/authenticate the bounded acquired image, then publish one shared unlocked owner and metadata for that attempt. No filesystem publication, implicit initialization or plaintext-entry cache. Authentication failure is not a reliable distinction between wrong password and corruption. |
| Explicit create at a missing path | Non-overwriting publication of a new validated encrypted vault with new identity/root; adopt only after target and applicable preservation conditions are verified. A newly appearing target is a collision, not permission to replace it. |
| Explicit initialization of an empty file | Revalidate the confirmed same regular file, preserve its exact empty original through guarded initialization, and adopt the new vault only after target and required preservation proof. Parking the original is a physical effect even if new-vault publication fails. |
| Add/edit/delete entry | Exact vault/entry/kind and revision; existing known-profile impact confirmation for secret-changing/destructive operations. Kind change creates a new entry. Complete publication precedes shared revision/metadata adoption. Deletion leaves profile references unresolved; profiles and existing authenticated sessions are not rewritten or disconnected. |
| Import private key | Use one bounded acquired source image and its matching passphrase interaction, the accepted envelope allowlist and real key validation. Publish only validated canonical PKCS#8; retain native-owned buffers until actual completion. Leave the source file intact and retain neither its path as an authentication dependency nor its passphrase. |
| Change master password | Apply existing new-password rules and fresh root/wrapper/payload encryption. Complete publication proof precedes root/snapshot/metadata adoption. During uncertainty retain needed old/candidate roots only within the established owner/work lifetime and report the observed on-disk generation truthfully. Old copies retain their original password. |
| Encrypted backup | Copy the exact authenticated encrypted generation to a separately selected non-overwriting destination and verify that destination. Do not change the source revision or re-encrypt to an implied new password. |
| Explicit restore | Independently authenticate/validate the selected backup, enforce the existing identity conditions below, preserve exact original target bytes where present, and verify target plus required recovery copy before adopting its snapshot/root. The editor keeps its target path. |
| Archive | Apply Q15's source-bound non-overwriting move and verify actual source/destination effects. Completed archive ends the old usable owner state; it neither initializes another vault nor changes selected path, profiles or `.bak`. The UI shows the archive path and acknowledges standalone-editor closure. |

The existing restore identity checks are retained, not generalized into a new
same-name recovery rule. An unlocked owner requires the independently validated
backup's vault ID to match its own. Locked recovery obtains an expected ID only
when the bounded original passes the v1 framing/header/length validation; that
ID constrains the restore but does not authenticate the damaged target. Missing,
zero-byte or structurally invalid originals have no established ID, so the
authenticated backup supplies it. Do not infer the ID from profile references.
Preserve the current recognizable-newer-version and v1 nonzero-flags refusal
instead of interpreting those files as corrupt empty vaults. These distinctions
come from `CredentialVault.cs:84-107,253-257`; accepted recovery still requires
independent authentication and original preservation. Explicit restore of a
missing target is recovery, not the implicit empty initialization forbidden by Q14.

Preserve [Q108's encrypted rescue-copy allowance](resize-and-vault-proposals.md#saving-backup-and-conflicts)
after external-stale detection when an authenticated encrypted snapshot remains
available. Copy only that exact retained generation to a separately selected
non-overwriting destination, identifying its generation and stale status. This
does not read an unknown current file as trusted, retrieve credentials, write
the blocked source or clear any restriction. If the authenticated image is no
longer available, report that rather than reconstruct it from partial evidence.
Current `CredentialVault.BackupAsync` passes through `CheckCurrent` and therefore
does not establish this accepted rescue path. The path belongs to the complete
vault operation; it is not a new secret-export feature.

**Physical effects and truthful results.** Keep the domain result, each observed
file fact, effective revision/root adoption, restrictions, artifacts and actual
pending work separate. Typed decline, host loss, invalid input, authentication
failure, unsupported input, conflict and storage outcome are different facts.
An input/confirmation adapter exception before mutation fails that interaction;
it is not an authentication failure or consent. An error after publication may
coexist with a verified commit. UI display/notification failure cannot turn that
commit into failure or require caller-driven replay.
Do not retain raw exceptions, parser/OpenSSL error data or secret input objects
in operation history/diagnostics. A source-passphrase input request is not a
failed vault-unlock result. Later external change does not retroactively turn
an earlier verified commit into failure; current file state and restrictions
are separate observations.

Candidate 3 governs ordinary publication, preservation and reconciliation.
For guarded initialization and archive, the narrow internal mechanics use the
same target/namespace admission authority for every affected source, destination,
candidate and parked/recovery name. Reserve these in a consistent internal
order; a separate move registry cannot permit overlapping mutations. This does
not expose locks or staging phases to UI callers or add a generic public move.

For empty initialization, report an original parked elsewhere, absent target,
installed candidate and remaining recovery material independently. In the
original execution only, the existing narrow compensation may return the exact
owned empty original to a confirmed-absent source with a non-overwriting move
when candidate installation was prevented or has definitively settled without
installing it. Verify the compensation and report any failure. Never overwrite
a competing file, race still-issued work or use reinspection to perform this
move. An uncertain publication retains evidence and restrictions for the existing
explicit recovery path. Once required preservation is proven, failure to delete
an owned parked empty file is a cleanup outcome, not failed publication.

For archive, intended bytes at the archive destination do not prove the original
name is absent. If a different file appears at the source or the source cannot
be inspected, retain the known archive fact and report conflict/unresolved source
state. Do not operate on the new file or allow credential use from the old owner.
An unchanged source and absent archive destination can establish no move only
after issued work settles. Missing required facts never justify a second move.
Known missing artifacts require recovery action, not an infinite inspection loop.

**Interaction and temporary secrets.** The interaction coordinator routes
typed requests to a live UI host and binds each reply to its operation, request
ID, target, relevant revision and inspected source facts. If those prerequisites
change, reject the answer and end with a stale-review/conflict result; do not
silently modify the approved operation or loop on confirmations. Show only the
existing required confirmations, including known registered-profile impact and
its incomplete-search scope. Unknown profile files are not a frozen global
reference inventory. A known change invalidating the displayed impact requires
fresh review; it does not authorize rewriting profiles.

The interaction adapter owns controls and unaccepted replies; the operation
owns accepted input. Define the acceptance/cleanup handoff once, so a late or
duplicate reply is cleared by its current owner and never reused. Do not acquire
secret input before bounded execution can own it. Clear master/KEK, import
passphrase, decrypted payload and authentication working copies at their
established end of use, after actual native use ends. Retaining a failed draft
means preserving non-secret edits and outcome information; it does not extend
the lifetime of passwords/passphrases or cache all plaintext entries. Retrying
may require fresh input. Necessary candidate roots and encrypted recovery images
follow their separate already accepted uncertainty/lifetime rules.

**Unlock consumers versus observers.** Track actual operation consumers
separately from result observers. Cancelling `ObserveAsync` or dropping a view
does not remove the operation's unlock interest. An explicit stop request or
coordinated host/role termination can remove that consumer; other consumers
continue the shared attempt. User cancellation/failure of the shared prompt ends
its participants without automatic prompting loops. The coordinator can transfer
hosting to a surviving window or resolve the interaction under the existing rule.

When the last actual consumer leaves, retire admission to that unlock attempt
and request cancellation. Keep its native work, input and worker charge until
actual completion. New consumers cannot attach to a cancelled attempt or adopt
its late success under another epoch. A new attempt uses fresh admission after
the old work is safe to retire; no second expensive worker is created by repeated
cancellation. Serialize successful root adoption against consumer cancellation
and owner termination. A root never adopted by a surviving authorized owner is
cleared after its actual use ends, not published by a late callback.
Shared unlock completion must not depend on releasing a mutation admission that
is itself waiting for that unlock. Do not occupy the global expensive-work slot
while waiting for human input, file pickers or impact confirmation. Physical
mutexes are released before callbacks that can reenter the module; logical
operation ownership does not imply holding a mutex during an asynchronous wait.

The existing authentication resolver remains selected-vault-only and transfers
only the requested exact credential to its authentication owner. Check selection
epoch, revision eligibility and restrictions at handoff; a late extraction for
a superseded selection is cleared, not delivered or retried against another
vault automatically. After a valid handoff, the authentication owner follows
its existing lifetime. Management updates do not disconnect established sessions
or retain their plaintext credentials as a vault cache. No macro runtime or new
secret consumer is introduced.

**Selection changes and closing.** Preserve the current Busy rejection before
settings save when affected old/new vault work is active (`VaultController.cs:40-47`,
`App.cs:314-338`). Keep the settings draft and current selection, report Busy,
and let the user retry. No automatically deferred settings apply or new waiting
dialog is selected here. The normative requirement to apply selection only after
affected save/unlock work settles remains satisfied.

Once quiescent, reserve affected roles and validate the settings/selection basis
before allowing the settings owner to publish. No new conflicting operation can
slip between that check and role transfer. Settings failure/partial publication
keeps the prior effective selected role and its own outcome/restrictions. Verified
settings adoption and the reserved in-memory selection epoch/role update form one
ordered visibility transition for new credential requests; there is no interval
with a new effective selected path and the old credential owner. UI closure or
lost observation cannot abandon the role update after adoption. Settings and
vault files still have separate physical outcomes, not a multi-file transaction.

Apply this same coordination to settings restore/default recovery or later
adoption after settings reconciliation, not just the ordinary Save Settings
button. The existing `App.cs:345-353` recovery callback is a second caller that
must not bypass it. If effective settings remain unresolved, keep non-secret
transition evidence; do not retain an otherwise unneeded editor-only root for
settings inspection. Any later role transfer reacquires valid ordinary owner
roles and never uses an expired owner. Preserve stale-location history when
switching away and back. Editors keep their opening paths, and an available
usable editor owner is promoted without another unlock.
An already admitted transition owns the old/new owner references needed for its
actual work. If the destination editor closes during settings publication, do
not release a still-used root before transfer settles: promote on verified
adoption, or reevaluate release under the unchanged roles on failure. A quiescent
unresolved transition alone does not extend an editor-only secret lifetime.

Coordinate editor/application termination with operation-stop requests, prompt
hosting, issued work and new owner-dependent inspection. Context/subscription
release is not root disposal. Pending native/I/O/verification use keeps necessary
owners alive; after quiescence, Q10's visible unresolved exit is allowed and
non-secret evidence/restrictions survive view closure while the app continues.
Release of one inspection pass is not terminal for future reconciliation.
Only final operation retirement ends its inspection admission. Enforce ADR 0008
root lifetime and Q15's archive termination; no metadata lease, hidden process or
recording five-/ten-second timeout extends or replaces those rules.

**Capacity and final retirement.** Apply finite versioned internal limits to
owner-role/binding records, admitted/queued operations, actual unlock consumers,
observers/subscriptions, pending interactions and replies, queued secret inputs,
native/KDF work, immutable encrypted images, metadata delivery and retained
artifacts. Reserve before acceptance/allocation; reject/defer new work without
effects when full. Unsubscribing bounds metadata delivery but does not erase a
committed outcome or cancel owned work. Do not charge a shared image only once
unless it has actual shared ownership, or keep one copy per observer implicitly.
Include provisional plaintext and native buffers in the byte accounting.
Reserve the capacity for outcome evidence, restrictions, root transfer and the
committed metadata projection before issuing publication; a known disk change
cannot depend on finding an unreserved result-record slot afterward.

Keep actual work charged after cancellation and charge non-secret unresolved
records after an editor owner releases its roots. Use compact evidence when it
suffices; unresolved artifact accounting ends only with verified deletion or an
explicit charged handoff. Retire only resolved records without live resources;
unknown/retired operation IDs return that fact and cannot replay mutations.
Coordinate accounting with candidate 3 so the same owned artifact is neither
omitted nor charged as multiple independent copies. A verified manual backup or
archive destination can become a normal long-lived user file after explicit
ownership/accounting handoff; uncertain destinations and parked/candidate files
remain charged. Final user files are not a pretext to drop unresolved evidence.
Do not evict restrictions by TTL, memory pressure, context recreation or selection
changes. Exact new limits and concrete type names are implementation details
to freeze and test at implementation start. Existing numeric file/KDF/import
limits remain binding; no measured performance or finite native-I/O completion
deadline is claimed.

### Candidate 4 verification plan

Exercise ordinary C operations, their observations, shared metadata and the
existing selected credential resolver. Use real vault/OpenSSL validation with
independent known-answer or independently produced fixtures; a writer/reader
round trip or a successful mock vault alone is insufficient. Controlled internal
filesystem, completion/time, interaction and profile/settings adapters expose
faults and ordering. Retain Windows/native integration for real handles, access
rules, alias coordination and actual completion. Ownership instrumentation may
observe release without recording secret values or replacing real cryptography.

| Case | Required observable result |
| --- | --- |
| Independent unlock/restore fixtures; invalid framing, tag, schema, key material or failed RNG | Real validation rejects invalid inputs without publication/adoption; failure categories remain non-secret and do not diagnose wrong password versus tampering with certainty |
| Entry add/edit/delete through management tab and same-file editor | Shared committed metadata and next eligible credential lookup agree without caller refresh/re-unlock; exact IDs/kinds and unresolved deleted references remain correct |
| Same-ID copies, known aliases and candidate 3 publication competing with archive/initialization | Distinct opened locations are not merged by UUID; shared internal admission prevents conflicting changes across source/destination/park names and preserves supported path semantics |
| Shared unlock with observation cancellation versus one consumer's stop request | Observation cancellation leaves work interested; explicit consumer departure does not stop other consumers or deliver a credential to the departed one |
| Last unlock consumer leaves; native success and a new request arrive later | Old attempt retires without adopting into the new attempt/epoch; buffers and expensive-work charge remain until actual completion, with no extra worker created |
| Host closes; old/new host replies, duplicate replies or adapter failure race | Only the current bound reply is accepted once; unaccepted secrets are cleared by their owner, no prompt loop occurs, and failure is not consent/authentication failure |
| Entry revision, confirmed file image or known impact facts change during review | Return conflict/expired review; no retarget, same-name substitution, automatic rebase or stale confirmation reuse |
| Selected context across A-to-B-to-A; editor closes while shared owner remains | Old selected/editor bindings stay invalid for new operations; surviving roles observe their own valid state without extending secret lifetime through stale contexts |
| Unlock/authentication/all pickers/Explorer versus explicit management initialization | Ordinary entry points never create missing/empty vaults; explicit creation uses new identity and guarded missing/same-empty prerequisites, rejecting replaced/nonempty targets |
| Empty original parked; install fails, conflicts or becomes uncertain; compensation fails | Report every target/park/candidate fact; compensation only in safe owned original execution without overwriting, no blind repair during reconciliation, and cleanup failure stays separate from verified success |
| Import with source change, unsupported key, missing/wrong passphrase or cancellation during native work | Bind passphrase to the acquired image; validate canonical output, leave source intact, enforce one expensive worker and clear transient material after actual use |
| Master change with candidate at target but preservation missing/unreadable | Report on-disk candidate generation, retain old effective revision and applicable roots, block writes/retrieval, and never re-encrypt or repeat publication during inspection |
| Ordinary encrypted backup and external-stale rescue copy | Destination verifies the identified authenticated generation and its existing password without overwriting; source revision/restrictions stay unchanged and unavailable rescue evidence is not invented |
| Locked/unlocked restore with known-ID mismatch, unknown damaged ID and recognizable newer-version/flags fence | Same public operation enforces the appropriate identity restriction and independent backup authentication; preserve exact damaged original, old-password generations and required recovery evidence |
| Archive of locked/readable damaged bytes, or successful archive after an exception | Verify actual unchanged bytes at the unique destination and source outcome without unlock; end old usable owner, preserve `.bak`, settings and profiles, and never initialize implicitly |
| Archive destination verified but source reappears, differs or cannot be inspected | Keep known move facts and source conflict/unresolved status; do not use the old owner, overwrite the new file, repeat the move or clear independent restrictions |
| Busy selection change; settings failure/partial/success; view or destination editor closes | Busy is rejected before settings save with no later automatic apply; adopted settings complete the reserved role transfer, partial settings keep the old role, and used roots are not prematurely released |
| Settings restore/default recovery or later verified reconciliation changes selected path | Use the same coordinator and restrictions; no new credential request observes a new effective path with an old selected owner, and owner reuse follows ordinary lifetime |
| Credential extraction races selection change or new restriction | Recheck eligibility at handoff, clear an invalidated result, and preserve the normal authentication lifetime for an already transferred credential without disconnecting established sessions |
| Failed/slow metadata subscriber and delayed old notification | Commit remains adopted; current-state observation catches up, old epochs/revisions do not win, pending delivery coalesces and subscriptions do not keep roots alive |
| Last window or editor closure with native/I/O work and with quiescent uncertainty; reopen/switch-back | Pending work retains ownership; quiescent close follows visible existing policy, secrets end with their roles, and independent unresolved/external-stale restrictions cannot be bypassed |
| Concurrent reconciliation, a later pass racing owner release and retired IDs | Join the current pass without mutation; admit later verification only against a valid owner/evidence, distinguish per-pass release from final retirement, and never replay through old IDs |
| Capacity at/beyond each record, binding, consumer, subscriber, input, native, byte and artifact threshold | Rejection/defer has no new effects; publication has reserved result/adoption capacity, shared ownership is correctly charged, cancellation cannot free live work and pressure cannot evict unresolved restrictions |

The existing source tests cited above are foundations, not execution evidence
for these 23 cases. No runtime test, cryptographic test or prototype was run in
this interview. Concrete names, finite internal limit values, scheduling and
Windows-handle implementation are engineering details with the stated validation
obligations; they are not unresolved product preferences.

### Candidate 4 readiness and open frontier

Q12-Q16 are accepted. Read-only reviews of operation outcomes, role/interaction
lifetimes and bounded verification found no additional product decision needed
when retaining the existing Busy rejection. The user confirmed the complete
contract and its 23-case future verification plan in Q16 on 2026-11-18.

The accepted contract is consolidated into
[complete vault management operations](../specification/vault-format.md#complete-vault-management-operations)
and [vault selection and operation hosting](../specification/persistence-and-windows.md#vault-selection-and-operation-hosting),
with their [Japanese vault](../specification/vault-format-ja-jp.md#保管庫管理の完了操作)
and [Japanese persistence](../specification/persistence-and-windows-ja-jp.md#保管庫の選択と操作の表示)
counterparts. Candidate 4 is ready for implementation once the user separately
requests implementation. No runtime or cryptographic test was executed and the
future verification plan is not evidence that these cases already pass.

No design decision remains open for candidate 4. Implementation-time parameter
selection and the future verification/review gates remain required work.

## 5. Shared SSH channel invariants

Status: design-ready. Ownership scope and C interface were accepted in Q17-Q18,
and the complete lifecycle contract, verification plan and final shared
understanding in Q19 on 2026-11-18. No new channel capability is added.

### Existing contract and source evidence

[ADR 0001](../adr/0001-own-ssh-client-with-openssl.md) keeps the SSH client and
session layer in TabTty, using OpenSSL for cryptographic operations.
[ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md) places on-demand
SFTP on a separate channel of the terminal tab's authenticated SSH connection.
The [SSH/SFTP contract](../specification/file-transfer.md#ssh-and-sftp-channel)
already requires independent channel credit, buffering, requests and EOF/CLOSE,
while sharing packet read/write, cryptography, rekey and liveness. Candidate 2's
[terminal transport interface](../specification/session-and-recording.md#terminal-payload-transport-ownership)
remains the outward seam; candidate 5 deepens the SSH ownership inside it.

| Responsibility split across current source | Evidence and consequence |
| --- | --- |
| Peer channel ID, windows, packet limits and EOF/CLOSE state | `src/TabTty.Core/SshSessionWire.cs:48-67` and `SshSessionWire.Transfer.cs:109-122` maintain separate state for the same SSH rules. |
| Open confirmation, send segmentation and receive credit | `SshSessionWire.cs:365-381,625-680,854-876` and `SshSessionWire.Transfer.cs:140-178,206-266` repeat validation and accounting. Changes require following both paths. |
| Receive buffering under peer fragmentation | The terminal's 1 MiB byte ring plus source bitset (`SshSessionWire.cs:49-53,608-622`) is independent of packet count. SFTP's 1,024-item `byte[]` queue (`SshSessionWire.Transfer.cs:107-108,164-176`) may exhaust item capacity on small fragments while byte credit remains. Common ownership must account for bytes and metadata; it need not force the same buffer representation. |
| SSH channel-request replies | Terminal startup waits sequentially for success (`SshSessionWire.cs:407-424`); SFTP maps success/failure directly to its startup completion (`SshSessionWire.Transfer.cs:192-198`). A shared outstanding-request FIFO owner does not currently exist. |
| Close, late traffic and actual release | Terminal closure (`SshSessionWire.cs:879-961`) and transfer closure (`SshSessionWire.Transfer.cs:269-324`) have separate signals and cleanup paths. Routing (`SshSessionWire.cs:836-840`, `SshSessionWire.Transfer.cs:12-34,79-102`) must preserve the original closing identity. |

These are static source observations, not reproduced runtime failures. Existing
optional interoperability tests cover SFTP upload/download alongside terminal
input, one authentication, payload/log separation and subsystem refusal
(`tests/TabTty.Core.Tests/Program.cs:157-173,222-276`). Other optional tests cover
early output and normal/stderr data (`Program.cs:279-324`), with rekey/EOF/CLOSE
peer behavior in `tests/fixtures/ssh-peer.cjs:135-155`. Their presence does not
establish the detailed invariant coverage below or constitute test execution.

### Q17 - Ownership scope: accepted

| Owner | Responsibility retained or concentrated |
| --- | --- |
| Shared SSH channel module | Local/peer ID pair and connection generation; open outcome; channel state validation; independent send window, receive credit and maximum packet; DATA/extended DATA ordering and bounded accounting; SSH channel-request reply matching; directional EOF, CLOSE and pending-work completion; late-result rejection and actual resource-release conditions. |
| SSH connection owner | Allocate/register/route channel identities without collision, retaining closing registrations as required by channel retirement. Keep one packet reader and serialized writer, fair bounded send arbitration, connection-wide global-request reply matching, rekey, liveness and connection failures. Per-channel state supplies retirement facts; the registry cannot independently discard a live or closing identity. |
| Terminal-specific transport and session owners | Preserve candidate 2's division: the transport owns PTY then shell request contents, readiness, geometry, protocol exit facts and normal/stderr delivery; the session owns input policy and downstream Rust/recording admission through the ordered consumer. Terminal disconnect retains its existing connection-lifecycle meaning. |
| SFTP owner | Subsystem startup then SFTP INIT/version, frame parsing, SFTP request IDs and legal reply reordering, filesystem operations and their cancellation/outcomes, transfer UI and inert diagnostics. An ordinary SFTP-only refusal/close leaves a healthy terminal running. |

The shared module owns the channel invariants, rather than exposing a recipe
that both callers must execute correctly. Its full interface will include
ordering, failure, cancellation and lifetime, not only message construction.
Connection-wide ID allocation and routing remain coordinated with each
channel's retained identity; this division must not create two state authorities.

Preserve these already accepted constraints during interface comparison:

- The common packet pump progresses bounded protocol/control/rekey work while
  an application consumer is pending. It cannot wait for terminal processing,
  filesystem work, UI or a transfer's send credit. Packet serialization already
  started is not interrupted by cancelling one channel operation.
- Terminal credit returns after the accepted downstream admission/release
  condition, including generated-reply admission, without waiting for disk
  flush, reply transmission or drawing. Early output is retained; readiness
  cannot overtake it. DATA and stderr share credit but retain separate terminal
  decoding state. SFTP data/stderr stay outside terminal decoding and all logs.
- EOF is directional. CLOSE, operation completion, delivery completion and
  actual release remain distinct. Candidate 2's terminal disconnect grace,
  actual receive cutoff and original-generation draining remain intact; they
  do not become one universal SFTP close policy.
- A malformed SFTP frame and an SSH packet/channel protocol violation are
  different failures. Preserve existing channel-local failure isolation and
  connection-wide protocol failure rules; do not silently downgrade every SSH
  violation into an isolated transfer error.
- Preserve existing channel/window/resource limits and explicit SFTP reopen.
  Bounded ownership covers payloads, fragment metadata, outstanding requests,
  deferred controls and retired-but-unreleased work. This scope decision does
  not choose new numeric limits, an ID-reuse algorithm or a concrete interface.

### Depth, dependencies and future verification

The deletion test supports concentrating these rules: deleting the proposed
shared channel module would force ID/state checks, credit, request ordering,
EOF/CLOSE and retirement back into both terminal and SFTP. A message helper
alone leaves that complexity duplicated. This is useful depth and locality for
two actual consumers, giving leverage to future invariant fixes and tests.

Existing modules retain useful depth: `SshPackets.cs:32-110` owns packet framing
and authentication before payload publication; shared receive/rekey/deferred
control paths in `SshSessionWire.cs:722-798,978-986` own connection progress;
`SftpClient.cs:262-357,369-406` owns SFTP framing, request IDs, late-reply draining
and mutation outcomes. `TransferChannel` already contains real responsibility;
the proposal moves its shared SSH knowledge into one owner rather than deleting
it as an empty wrapper.

Channel state/accounting is in-process logic. Peer/socket behavior, time and
actual I/O completion are external dependencies. Future real and controlled
I/O/time adapters can exercise the same channel interface at an internal seam.
Terminal and SFTP remain the real application consumers, without introducing
forwarding, server-initiated channel features, authentication/algorithm changes
or a replacement SSH library. No ADR is reopened by this scope.

With the scope accepted, compare concrete interfaces and specify verification
through the selected interface: differing local/peer IDs and packet limits;
zero credit on one channel while another/control/rekey progresses; overflow and
excess data; tiny-fragment metadata pressure; normal/extended ordering; cancelled
request replies without FIFO drift; late open confirmation; EOF with reverse
direction still usable; CLOSE without EOF and simultaneous CLOSE; cancellation
against an already-started packet; explicit SFTP reopen with old replies;
channel-local versus connection-wide failure; and retained capacity until actual
work/resources settle. This is a future verification agenda, not an executed
test suite or a final candidate-5 contract.

### Primary-source checks and technical follow-up

Reviewed on 2026-11-18: [RFC 4254 sections 4-5](https://www.rfc-editor.org/rfc/rfc4254.html#section-4)
defines global/channel requests, independent endpoint IDs, window/packet limits,
per-channel reply ordering and directional EOF/CLOSE. Its two-sided CLOSE rule
allows protocol ID reuse, while TabTty must also prevent late local work from
being associated with a new generation. [RFC 4253 sections 7 and 9](https://www.rfc-editor.org/rfc/rfc4253.html#section-9)
keeps rekey and directional key transition at connection scope.

The interface comparison retains the existing payload-byte accounting with an
explicit interoperability rationale:
[verified erratum EID 3878](https://www.rfc-editor.org/errata/eid3878) describes
including the SSH string length field in channel window accounting, whereas
the inspected [OpenSSH `channels.c`](https://raw.githubusercontent.com/openssh/openssh-portable/master/channels.c)
(OpenBSD revision 1.478, 2026-10-30, `channel_input_data` and
`channel_input_extended_data`) debits payload length for stream channels and adds
four only for its datagram form. The pinned
[OpenSSH 10.2p1 implementation](https://raw.githubusercontent.com/openssh/openssh-portable/V_10_2_P1/channels.c)
also debits stream payload bytes on send and receive, including stderr. Current
TabTty contracts and source use payload bytes. All alternatives preserve that
accounting, including the existing maximum channel-data interpretation, without
adding four string-length bytes or attempting heuristic negotiation. Transport
packet-size checks still include their own framing overhead independently.

This is a documented interoperability choice differing from EID 3878, not a
claim that its corrected text requires payload-only accounting. It aligns the
existing contract with the inspected OpenSSH stream behavior; it does not adopt
OpenSSH code or broaden the dependency exception. The RFC Editor's
[informative verified-errata rendering](https://www.rfc-editor.org/rfc/inline-errata/rfc4254.html)
was used to inspect EID 3878 because its standalone page was unavailable to the
reader. Independent packet/credit vectors and OpenSSH interoperability must
verify many tiny messages, stderr, exact window exhaustion and maximum payloads
at implementation time, with guidance rechecked before release. No peer test was
run in this review.

### Interface alternatives for Q18

Three fresh-context reviewers independently explored minimal entry points,
flexible composition and the common callers. The following sketches describe
interfaces, not executable code or implementation authorization. Each design
stays inside candidate 2's transport seam and uses the same accepted ownership.
Names and class counts are illustrative.

#### A - One typed work-submission interface

```text
Start(sessionOptions, receiveBinding) -> ChannelRun or rejection
ChannelRun.Submit(typedWork) -> operation observation or rejection

typedWork = RequestWithReply | Notify | Send | StopOriginalWork | Close
observations = outcome, send progress, prior-delivery settlement, actual release
```

The terminal starts a run, submits PTY, awaits its protocol success, submits
shell, and publishes readiness only after the shell reply's preceding delivery
has settled. SFTP starts a run, submits the subsystem request, then constructs
and sends INIT through the same submission entry. Endpoint owners still choose
request contents and their startup sequence. A closed work-type vocabulary does
not expose arbitrary SSH packets or new channel capabilities.

The module hides admission, identifiers, segmentation, flow control, reply FIFO,
closure and cleanup behind two main entries. Stop names its original work and
does not retarget the current channel. Observations are read-only and do not
require caller disposal to finish work. This gives shared work handling good
locality and leverage. However, the work variants, result variants, receive
binding and lifetime rules all count as interface complexity: two method names
do not eliminate that vocabulary. A generic submission entry makes ordinary
operations less discoverable and risks becoming a command-dispatch framework.

#### B - Composable channel capabilities and receive leases

```text
OpenSession(options, peerRequestPolicy) -> SessionChannel or rejection
channel.Requests.Submit(preparedRequest) -> RequestOperation
channel.Sender.Submit(ownedPayload) -> SendOperation
channel.Receive.NextAsync(waitOnly) -> DeliveryLease
lease.ConsumePrefixAsync(length, awaitedConsumer)
lease.ApplyFactAsync(awaitedFactConsumer)
channel.Close(cause) -> CloseOperation
```

The terminal transport runs the receive cursor internally and passes payload
prefixes to candidate 2's awaited consumer. The session never receives a lease
or a public read/acknowledge recipe. SFTP uses the same cursor to fill its bounded
frame storage incrementally. A request result includes a prior-delivery fence,
which the terminal transport uses before publishing readiness.

Admission transfers an explicitly owned immutable payload only on acceptance.
The module owns all SSH state and window/reply accounting. A lease does not
expose an unrestricted `Ack(count)`: only successfully consumed prefixes become
credit-eligible. Disposing a lease or asking for the next item does not prove
consumption; abandoning a suffix triggers the existing consumer-failure policy.
Peer-request recognition and protocol replies progress independently of the
cursor. `ApplyFactAsync` applies only the ordered application fact, not a delayed
decision about a mandatory protocol reply.

This offers the most flexibility for different consumer granularity and
ownership transfer while concentrating SSH invariants. Its cost is the largest
interface: callers must manage cursor, lease, prefix consumption, transferred
payload and operation lifetimes. Keeping a lease while awaiting later receive
data can deadlock the application even though the protocol pump still runs.
Those obligations remain visible at the seam and weaken the default caller's
leverage. The terminal adapter must hide all of them from the session.

#### C - Named complete operations with a prebound consumer

```text
OpenSession(options, receiveBinding) -> accepted channel/open observation or rejection
channel.RequestReply(preparedRequest) -> RequestOperation or rejection
channel.Notify(preparedNotification) -> SubmissionOperation or rejection
channel.Send(bytes, permittedStopOffsets) -> SendOperation or rejection
channel.Close(cause) -> the channel's CloseOperation

receiveBinding = awaited ordered consumer + bounded peer-request policy
operation = current outcome/progress, wait-only observation, explicit RequestStop,
            prior-delivery settlement where relevant, actual-resource release
```

An accepted start returns its channel and open observation together. Normal
send/request/notification work is rejected until open succeeds; `Close` remains
available while opening. Required close/control capacity is reserved with the
channel rather than acquired only when cleanup becomes necessary.

The terminal binds its typed consumer before opening; requests PTY with the
current committed geometry; requests shell after PTY success; then combines
shell success with its prior-delivery settlement and a still-current, unstopped
attempt check before publishing readiness.
Geometry notifications use `Notify`; authorized encoded input uses `Send`.
The session's input, local-echo, Rust and recording owners remain as in candidate
2. Their application semantics do not move into the shared channel module.

SFTP binds its own incremental frame consumer, opens a separate session channel,
requests the subsystem, then constructs INIT and negotiates its version outside
the shared module. Its frame/request-ID/file-operation owners remain intact.
The binding is usable before subsystem success and cannot wait on startup from
inside a payload callback. Unexpected application bytes still follow SFTP's
own validation; early binding does not declare them valid SFTP replies.

The named operations own bounded admission, wire issue, protocol results and
cleanup. `RequestReply` cannot accidentally become a no-reply request; `Notify`
reports local submission without pretending the peer accepted or rendered it.
There is no caller credit-return, packet-construction or release sequence.
For this comparison, bounded request/data copies and required result capacity
are reserved at admission; after a successful call the input memory can be
reused. Rejection leaves inputs with the caller and issues no work. Internal
optimization must preserve that ownership contract and its memory accounting.

The entry count is larger than A, but the operations directly match the two
real callers. There is no public local-EOF operation in the recommended initial
surface: current terminal/SFTP callers close their channels, and a hypothetical
half-close caller does not justify another capability. Received EOF remains a
fully modelled directional fact. Recognized request types and binding policies
are finite internal types, not a channel-extension or arbitrary-callback framework.

### Comparison and selected rationale

| Criterion | A: typed submission | B: capabilities/leases | C: named complete operations |
| --- | --- | --- | --- |
| Depth | High if work variants own complete operations; small entry count alone is misleading. | SSH state is deep, but consumer ownership is partly exposed. | High: common caller actions include admission, protocol progression and cleanup. |
| Locality | State and work dispatch concentrate together; callers retain work-type and result vocabulary. | State concentrates, while lease/completion rules remain in adapters. | SSH rules concentrate; caller code retains only its real terminal/SFTP sequence. |
| Seam | One typed work entry plus ordered delivery and observations. | Capability handles and explicit receive ownership inside the transport seam. | Named operations plus one prebound consumer inside the same transport seam. |
| Caller leverage | Consistent generic work handling, at the cost of a command vocabulary. | Best control of consumption granularity, with the most lifetime obligations. | Direct request/send/close usage without a credit or release recipe. |
| Principal risk | Evolving a generic command bus without reducing conceptual complexity. | Leaked leases, false credit, or awaiting future data while holding the current lease. | Treating a method name as complete while leaving hidden ordering/ownership duties to callers. |

**C was accepted in Q18.** Terminal and SFTP need the same small set of channel operations,
not a general command framework or caller-managed receive ownership. C keeps
their actual differences in the existing owners while hiding the shared SSH
rules. Use the useful explicit outcome/progress/retirement distinctions from
all three designs; do not import B's public lease lifecycle. Internal buffering
can still use different representations behind the shared seam.

### Full-interface requirements carried into Q18

All sound alternatives must satisfy these constraints. Q18 accepted them with C;
they are not evidence that current source already implements them.

1. **Bind before issue.** Reserve a channel's finite receive/control capacity and
   install its awaited consumer and bounded synchronous peer-request policy
   before OPEN can be issued. The connection owner assigns/routes identifiers;
   the shared module owns the channel state and supplies its retirement facts.
   Peer/local IDs and generation stay internal. Rejected admission issues no
   SSH work and does not create an unbounded queue of capacity waiters.
2. **Separate protocol progress from delivery.** The common reader validates and
   routes packets without awaiting application consumption. Ordered per-channel
   delivery can wait without holding transport locks, blocking another channel
   or occupying the session reducer across I/O/credit/rekey waits. Required
   controls use bounded reserved capacity and the connection's serialized writer.
   No application callback recursively invokes later delivery or waits for work
   whose completion needs that callback or a later callback.
3. **Separate receipt from ordering.** A request's protocol result can complete
   while earlier payload is still being consumed. Its immutable receive-position
   fence identifies that preceding delivery; it does not require all future
   output to end. Terminal startup uses both success and the fence. Failure or
   stopped delivery cannot produce a successful readiness fence. SFTP does not
   block partial-frame admission on completion of startup or a future reply.
4. **Preserve reply positions.** Register want-reply requests in actual wire
   order. Work prevented from issue has no wire reply slot; issued or uncertain
   requests retain their original slot until reply or safe channel termination.
   A cancelled wait does not remove it, and a late reply cannot answer the next
   request. No-reply notifications consume no slot. Unsolicited/duplicate replies
   follow protocol-error rules, not silent remapping. Global requests and SFTP
   request IDs retain their separate owners.
5. **Handle inbound requests without consumer waits.** A finite terminal policy
   recognizes bounded exit-status/signal facts and a SFTP policy rejects
   unsupported channel requests. The module owns ordered, once-only protocol
   replies when requested. Fact display stays ordered and outside payload/logs.
   Unknown requests/extended data keep the existing failure/discard rules.
   Recognition cannot perform UI/file I/O, reenter the channel or await data;
   it does not move terminal interpretation into shared SSH state.
6. **Account for real consumption.** Terminal credit eligibility follows all of
   candidate 2's Rust/recording/generated-reply admission and allocation-release
   conditions. SFTP consumes a prefix only after validated bounded frame storage
   owns it, separately charging that storage; waiting for a whole frame, remote
   reply or local file write inside the callback is forbidden. DATA and extended
   DATA share payload-byte credit. Unknown extended types are boundedly discarded
   under the existing policy. Discard after consumer failure is not successful
   delivery, and copying to an unaccounted pending queue is not consumption.
7. **Own send results and permitted stopping.** Preserve per-channel send order,
   checked window reservations and peer/local packet limits. Report a monotonic,
   coalesced definitely-submitted byte prefix promptly, plus unsubmitted or
   partial/uncertain suffix facts. The terminal maps byte progress to its
   complete units for echo/pacing; supplied permitted stopping offsets carry no
   codec knowledge into the channel module. The SFTP owner supplies whole-frame
   stopping rules and cannot splice a new frame into an unfinished old one.
   Local submission never proves peer receipt or authorizes uncertain replay.
8. **Distinguish stopping from observing.** Cancelling a wait changes observation
   only. Explicit work stop can prevent future issue and use allowed stopping
   points; it cannot interrupt an already-committed shared packet, recall bytes,
   or release resources still in use. Refund reserved credit only after proof
   that the corresponding packet was not committed for issue. The final contract
   below defines commitment before irreversible encryption/sequence changes.
   Connection failure retains
   uncertain effects rather than inventing rollback. A stop acknowledgment and
   the stopped work's actual settlement remain distinct.
9. **Retain original identity.** Cancelling OPEN after issue retains the original
   registration. Late confirmation binds its peer ID to that closing instance
   and triggers appropriate cleanup; it never activates a replacement. Late
   failure resolves only the old open. Timeout alone does not authorize ID reuse
   or release of issued work. Reopened SFTP and reconnected terminal generations
   cannot receive old callbacks, replies or stop requests.
10. **Distinguish all completion dimensions.** EOF is directional; CLOSE can
    arrive without it and is answered at most once. Protocol closure, operation
    outcome, preceding/pending delivery, actual resource release and identity
    retirement are separate facts. Terminal's existing two-second peer grace
    and receive cutoff remain with its connection lifecycle. SFTP does not gain
    permission to tear down a healthy connection because its own close waits.
    A failed consumer exposes failure through independent status even when it
    cannot receive a final callback; already-applied effects are not replayed.
11. **Bound every retained owner.** Charge payload, source/fragment metadata,
    prepared requests, send copies, observers/outcome snapshots, pending reply
    slots, deferred controls and closing work/identities. Tiny and zero-byte
    messages cannot create unbounded queue nodes. Reserve before acceptance and
    keep real I/O/delivery charged after cancellation. Existing terminal and SFTP
    quotas remain binding; shared allocations count once only when ownership
    really is shared. A zero window on one channel does not reserve the common
    writer indefinitely or starve another channel/control/rekey work.

### Verification approach

State/accounting is in-process; peer/socket behavior, time and actual I/O
completion are true external dependencies. Use internal real and controlled
adapters for the same ordinary channel interface, not caller-facing test-only
packet injection or clock operations. Keep integration with the real terminal
consumer/Rust and SFTP frame parser. Independent peer vectors and real-peer
interoperability complement controlled ordering/fault tests. Replace redundant
helper tests only after the same behavior is covered at this seam; preserve
independent framing/cryptographic tests that still cover a different contract.
No tests are added or run during this documentation phase.

### Q18 - Interface selection: C accepted

The user accepted C, named complete operations with a prebound awaited consumer
and independent bounded protocol-request handling, on 2026-11-18. This selects
the full ordering, failure and ownership interface above, not only its method
names. The final review below closes the remaining lifetime and verification
details within the accepted scope. It does not authorize implementation.

### Complete channel contract accepted in Q19

The following completes C's lifetime and verification requirements without
changing Q17's ownership division. It preserves the existing one-SFTP-slot
admission rule, terminal transport contract, connection-wait budgets and
file-operation cancellation/recovery rules. No additional channel capability,
authentication behavior or public local-EOF operation is introduced.

#### Admission, observations and actual release

Open admission reserves the instance, binding, receive and control budgets,
operation/result records and registry coordination before any OPEN can be
committed. Ordinary requests, notifications and data require confirmed open;
Close is available during opening. Repeated Close joins the same shutdown and
does not allocate a new close work item or revive the channel. Bounded copies
of request bodies, input bytes and permitted stopping offsets are immutable
after acceptance. The module validates offsets for range/order; the terminal
or SFTP owner supplies their character/frame meaning. Rejection performs no
wire work and retains no caller input. No unbounded task queue waits for capacity.

The ordinary operation view exposes distinct facts, not one overloaded success
or disposal signal:

| Fact | Meaning and limit |
| --- | --- |
| Admission | Work and its bounded ownership were accepted, or rejected without issue. It does not prove remote acceptance. |
| Protocol/submission outcome | Open/request success or failure; notification submission; definite send prefix and uncertain/unsubmitted remainder. Later delivery failure does not erase an already observed protocol result. |
| Stop status | A stop was requested, prevented uncommitted work, or remains pending behind committed work/permitted stopping. It is not proof of rollback or release. |
| Prior-delivery settlement | A fixed receive-position prefix has completed or failed. It does not include an unbounded stream of later output. |
| Protocol closure | The close exchange is resolved, the open was refused or prevented, or the connection generation has terminated and no longer admits SSH packets. This is a logical receive cutoff, not proof that late socket reads/I/O have completed. Scheduling CLOSE is not its transmission or a completed exchange. |
| Channel-owned actual release | All channel-owned writer/control work, payload callbacks, allocations and accepted handoffs have actually settled. It does not claim release of separately transferred SFTP frame/file I/O, recording or retained terminal output. |
| Identity retirement | The connection registry can stop routing this instance without misattributing peer traffic or late local work. A timeout or freed payload buffer alone is insufficient. |

Current outcome/progress revisions are monotonic and coalesced, with bounded
observers and records. Wait cancellation unregisters only that observation;
closed UI and dropped views do not abandon cleanup. The module continues
observing writer, control and consumer completion, including failure. A retired
operation cannot be replayed by looking it up or reusing an old handle. Retain
unresolved original ownership rather than evicting it under memory pressure.
Do not retain a growing history of all progress notifications or completed work.

`RequestStop` is operation-specific. It can prevent an uncommitted open/request/
notification, stop a Send at a permitted point, or initiate opening-attempt
cleanup. An issued request remains correlated with its original reply even if
its caller stops waiting. A committed notification cannot be recalled. Stopping
observation of Close does not undo closing; a stop request cannot reopen it.
All late results remain with the original channel and connection generation.

#### Packet commitment, sending and cancellation

The shared writer defines a packet commitment point **before the first
irreversible outgoing cipher/nonce/sequence change**, not merely before the
socket write call. In current `src/TabTty.Core/SshPackets.cs:32-77`, encryption
state and the sequence advance before `stream.WriteAsync`. Therefore a
per-operation stop must either prevent commitment or leave the committed packet
owned by the connection writer through actual completion. It cannot skip that
packet and continue a healthy encrypted stream with the next one. This is a
static source constraint, not a reproduced cancellation failure.

Register reply correlation and credit reservations in the same serialized
ordering before the packet can cause a peer response. Before commitment,
definitively removed work may relinquish its reservations. After commitment,
individual cancellation does not enter the packet's encryption/write token.
Connection shutdown/failure may stop transport work under its own rules; retain
the actual pending I/O and truthful uncertainty, and never attempt cipher-state
rollback or automatic packet replay. Connection-wide rekey restrictions still
govern when channel/control packets may be committed.

Send exposes a prompt monotonic definite local-submission prefix and identifies
remaining data that is uncommitted versus part of a committed/pending/uncertain
packet. The terminal owner maps newly confirmed complete units to echo/pacing
once; it does not echo a partial encoded character or interpret local completion
as peer receipt. Stop cannot move behind already-committed bytes. On a healthy
channel it stops at the next permitted offset reachable after that work, keeping
necessary suffix ownership while waiting for credit. Existing terminal codec
closure/bracket cleanup remains with the session's send owner.

Close has stronger lifetime meaning than stopping one Send. It rejects new work
and may abandon uncommitted suffixes, including an unfinished application unit,
while preserving their incomplete result. It can progress after the currently
committed packet settles; it must not wait for a zero-credit Send to finish an
entire frame before it can schedule CLOSE. It cannot force an actually stuck
writer to complete or claim release at a deadline. This distinction prevents a
close-versus-send deadlock without interrupting a shared SSH packet.

SFTP preserves frame order. If stopping leaves a frame incomplete, either its
already-admitted completion proceeds safely or the affected channel stops all
later DATA and closes under the existing cancellation/error policy. Never append
a new frame to the abandoned prefix. No guarantee to finish every cancelled
frame or keep that SFTP channel usable is added. The SFTP owner interprets the
shared send outcome: an uncertain final packet may have delivered a complete
mutation request even when the definite prefix is shorter than the frame. That
can require UnknownOutcome, with no blind retry. A boolean set only after the
whole write succeeds is insufficient evidence of non-execution; current
`SftpClient.cs:267,280-283,311-326` illustrates that gap. Shared SSH state does
not acquire file-operation meanings or recovery decisions.

#### Ordered delivery, requests and receive credit

Per-channel delivery remains ordered and nonrecursive. Protocol request results
do not wait for the consumer. A reply's prior-delivery fence names only the
already-admitted receive prefix at that reply position; success, failed delivery
and pending delivery remain distinct. Terminal readiness requires shell success,
successful preceding delivery and a still-current, unstopped attempt in candidate
2's ordering. Startup failure/stop preserves earlier safe output and the original
generation's drain. SFTP partial-frame consumption cannot await subsystem success,
INIT/version completion, another callback or a future request reply.

The synchronous peer-request policy performs bounded recognition/validation
without I/O, UI, reentry or future-data waits. Common state owns required replies
in per-channel receive order and at most once; application facts retain their
ordered delivery independently. A slow consumer cannot delay reply admission.
Unknown requests are refused only when a reply is requested, and existing
extended-data discard rules remain intact. Exit status/signal facts are not
terminal payload, EOF, success-by-default or permission to stop later valid data.

Consumption and window advertisement are separate ledgers. Distinguish bytes
eligible for return, a reserved/queued WINDOW_ADJUST, a committed possibly
delivered adjustment and its completed submission. A queued adjustment alone
does not grant peer entitlement. Reserve the receive capacity covering a new
grant before commitment; an in-flight grant can reach the peer before local
completion, so concurrent receive validation must account for that possibility
without a second grant. Only definitively uncommitted work can be cancelled and
its reservation reused as unadvertised credit. Uncertain issue keeps the effect
and ownership until the connection outcome settles. Never restore window credit
twice or infer that a cancelled observer prevented the adjustment.

Normal and extended DATA use the existing payload-byte window and checked
uint32 arithmetic. Packet framing limits remain independent. Terminal credit
eligibility follows candidate 2's complete consumer/admission/release conditions;
it waits for neither generated-reply transmission, WINDOW_ADJUST transmission,
disk flush nor drawing. Protocol progress and receive callbacks must not create
a cycle by waiting for those writes.

SFTP validates the length prefix before body allocation and retains the 256 KiB
frame-body maximum. Each callback consumes only a prefix actually adopted into
separately bounded parser/frame ownership. It cannot wait for the rest of a
frame or local file completion. Reserve enough reply/parser capacity when
admitting SFTP requests; a capacity wait must not hold the only callback that
can free that capacity. Partial prefixes, original chunks still alive during a
copy, completed frames, pending replies and buffers handed to local file I/O
remain charged for their actual lifetimes. Parser ownership does not disappear
when channel credit returns, and a channel release cannot erase upper-owner
charges or outcomes.

#### Opening, closing and explicit SFTP reopen

Retain one opening/live/closing SFTP slot for the tab's current SSH connection
generation. This preserves
`SshSessionWire.Transfer.cs:12-36`: an existing live/opening attempt rejects a
second start; a closing attempt can be waited on within the existing configured
connection-wait budget. Repeated user requests join a bounded pending admission
or receive a capacity/busy result; they cannot create unlimited waiters or reset
an existing wait's deadline. A timed-out/stopped reopen request does not later
open a channel automatically when the old slot becomes available.

If OPEN is definitively prevented before packet commitment, settle local work
and retire that unexposed registration. Once committed, cancellation/timeout
retains the original identity. A late confirmation assigns its peer ID only for
old-instance cleanup and required CLOSE; it does not run PTY/shell/subsystem
startup. A late refusal resolves that old attempt. A committed OPEN that never
gets a response remains unresolved until safe protocol/connection termination.

Sending CLOSE, receiving CLOSE, completing pending delivery and releasing actual
work are separate. CLOSE may arrive without EOF and must not duplicate EOF
finalization. Received EOF alone leaves permitted reverse-direction sending
available. Already-admitted terminal payload retains candidate 2's cutoff/drain
rules. Close response scheduling is once-only, but pending/rekey-deferred work
and its charge remain until actual settlement. No final callback is required to
succeed before failure can be reported independently.

Free the SFTP slot only when the old channel's protocol identity can retire and
its channel-owned work/resources are released. `TransferChannel.Release()`
currently drains a queue and disposes a reservation without joining every
writer, close/control and consumer owner (`SshSessionWire.Transfer.cs:311-324`);
its name is not proof of this stronger future guarantee. Separately owned parser
storage/local I/O and file-operation admission remain governed by their existing
contracts. In particular, pending cancelled file I/O still blocks new mutations
under [file-operation cancellation](../specification/file-operations.md#confirmation-and-cancellation);
a new channel or UI refresh does not bypass that guard.

If the peer never completes OPEN/CLOSE, explicit SFTP reopen may remain
unavailable on that connection. Return pending/busy/timeout truthfully within
the existing wait policy, retain bounded old identity/accounting, and keep a
healthy terminal usable. No forced terminal disconnect, automatic reconnect,
automatic replay, timeout-based ID reuse or extra closing slot is introduced.
When the old slot safely retires, a still-live explicit bounded request can
admit a fresh channel; otherwise the user may explicitly try again. A connection
ending invalidates admission and makes late old completions cleanup-only. It
does not prove that local I/O already stopped.

An explicitly reconnected tab has a fresh connection registry and may explicitly
open SFTP in that new generation while old-generation cleanup remains pending,
subject to ordinary shared capacity and file-operation guards. The old slot
does not occupy the new generation's slot. Its actual work and charges remain
with their old owners and still count against application-wide limits; reconnect
cannot erase them or use their pending completions to change the new instance.

Terminal Disconnect still owns its existing at-most-two-second peer-close
grace and underlying connection termination. The local request alone is not the
receive cutoff. This is not a general SFTP timeout or a bound on actual native
I/O, consumer, file or recording release. Channel operations do not add another
close confirmation or change window/tab ownership.

#### Failure scope and bounded ownership

Preserve the existing separation between an ordinary SFTP channel/subsystem
refusal, invalid SFTP framing/version/replies, SFTP consumer/file failures and
connection-wide SSH failures. The former stop the affected transfer under its
existing rules while a healthy terminal survives. Malformed SSH framing,
authentication failure of a transport packet, invalid channel identity/state,
excess data/window overflow and connection/rekey/liveness failures follow the
existing SSH protocol/connection-failure contract. Do not reinterpret them as
ordinary SFTP errors. Valid unknown requests/extended-data types retain their
defined refusal/discard behavior. Resource exhaustion is a resource failure,
not a fabricated peer protocol violation.

Consumer failure after partial application preserves already-applied safe
effects, stops ordinary delivery and reports independent status without replay,
false consumption credit or successful final-fact claims. Actual in-flight
owners still settle. Terminal recording-only failure retains its narrow policy;
the channel cannot turn it into an unrelated SFTP or connection failure.

Maintain the existing 1 MiB receive windows, 32 KiB channel-data limits and SFTP
2 MiB per live/closing channel and 32 MiB application buffer guard. Count peer
entitlement, admitted reads/payload, original/copy overlaps, frame/reply storage,
prepared sends and actual pending I/O without omitting ownership or counting a
shared allocation twice. Preserve separate existing terminal/log, listing/result
and local-provider limits; they are not spare capacity for a hidden frame queue.

Apply finite internal bounds also to fragment/source metadata, peer requests and
facts, required replies, fences, request-FIFO records, deferred controls, observers,
outcome snapshots, stop/close state, admission waiters and closing registrations.
Zero-byte/tiny messages and repeated cancellation/Close cannot grow records
indefinitely. Reserve close/control capacity before opening, but do not promise
infinite handling of a peer's windowless request flood. At exhaustion reject new
local work without issue or use the existing affected-resource failure policy;
do not hide pressure in unbounded deferred tasks or stall the common reader on
an application consumer. Keep control/rekey progression and fair transfer
service within the connection's protocol and resource rules.

Release individual allocations when their actual owners finish, while retaining
any unresolved identity/effect records still required. Full channel release and
slot reuse require the stronger conditions above. Concrete new metadata limits,
internal buffer form, registry allocation strategy and instrumentation are
engineering choices to fix and validate at implementation start. There is no
measured performance claim or promised finite completion time for stuck external
I/O, and no unbounded automatic retry to simulate progress.

### Candidate 5 verification plan

Use C's ordinary operations, typed consumers and observations with controlled
internal peer/I/O/time adapters. Combine them with the real terminal/session/Rust
and SFTP parser/file-operation owners; retain independent packet/cryptographic
vectors and real-peer interoperability. The matrix specifies future evidence,
not tests executed in this interview.

| Case | Required observable result |
| --- | --- |
| 1. Admission rejection, invalid stopping offsets, and mutation of original input after acceptance | Rejection issues nothing; accepted bytes/request/offsets stay immutable and charged; no unbounded capacity waiter. |
| 2. Different local/peer IDs, windows and packet limits | Correct routing/recipient IDs and segmentation; independent checked credit, no assumption of matching endpoint IDs. |
| 3. Stop before packet commitment, during cipher/sequence advancement and during pending write | Only uncommitted work can be dropped without disturbing the stream; committed work settles under writer ownership, with no premature refund or packet replay. |
| 4. Small window/packet limits splitting terminal encoded units | Prompt monotonic definite prefix; complete-unit echo/pacing once, no partial-character echo or replay of an uncertain suffix. |
| 5. SFTP partial frame, zero send credit and concurrent Close | Close does not wait for the full frame; after the current committed packet settles, no later DATA/frame is appended on that closing channel; terminal/control continue. |
| 6. SFTP mutation frame's final packet has an uncertain write outcome | Report possible complete-request delivery and the upper owner's UnknownOutcome where applicable, even if whole-frame send never returned success. No blind retry. |
| 7. Queued RequestReply stop, committed request stop, Notify, and late replies | Actual send-order FIFO is preserved; no slot for uncommitted work or Notify; a late response resolves only its original slot. |
| 8. Slow consumer while a request reply arrives, followed by continuing output | Protocol outcome completes independently; its fence covers only the prior prefix and does not wait for all later traffic. |
| 9. Early terminal DATA/stderr, shell success, delivery failure and attempt stop races | Preserve safe early output and source order; readiness requires protocol success, successful prior delivery and current unstopped generation. |
| 10. Inbound exit facts and unknown want-reply requests while consumption is paused | Bounded ordered once-only protocol replies progress independently; facts stay inert and ordered outside terminal payload/logs. |
| 11. One-byte/zero-byte DATA and interleaved extended data through the window limit | Payload-byte credit, source order and finite metadata hold; no packet-count-dependent unbounded memory or false credit. |
| 12. SFTP length-prefix/body splits, maximum body, invalid length and multiple frames per chunk | Validate before allocation; partial consumption needs no future callback; original/copy/parser ownership is accurately charged. |
| 13. Eight SFTP file requests plus browse/control with blocked local file work | Existing request/buffer/provider limits hold; retained frames stay charged; terminal and permitted control progress; new admission fails or waits boundedly. |
| 14. WINDOW_ADJUST reserved, queued, committed, locally completed and uncertain | Eligibility and possible peer entitlement stay distinct; early peer response to an in-flight grant is safe; no duplicate grant or early reservation reuse. |
| 15. One zero-credit channel or blocked consumer during rekey | Other channel and allowed control/rekey work progress; no lock/reducer cycle or protocol-prohibited send; bounded fair transfer service remains. |
| 16. OPEN prevented versus committed then stopped; late confirmation/refusal | Only unexposed work retires immediately; late results clean up the original attempt without startup or replacement activation. |
| 17. Never-arriving OPEN/CLOSE with repeated explicit SFTP reopen | One slot in that connection generation, bounded records/waiters and original wait budget; truthful timeout/busy; terminal remains usable, no ID reuse or surprise later open. |
| 18. Old close retires during a live reopen wait, versus after that wait ended | Admit a fresh channel only for the still-live explicit request; a stopped/timed-out wait never automatically opens later. |
| 19. Directional EOF, reverse sending, exit status without EOF and CLOSE without EOF | Finalize receive once where required, retain permitted reverse traffic and later valid data, and never invent successful exit status. |
| 20. Simultaneous CLOSE with pending write, deferred control and payload callback | At-most-once close issue; protocol closure, delivery, actual release and registry retirement remain distinct until each is established. |
| 21. Terminal Disconnect during valid incoming output and slow recording | Respect existing peer grace and actual receive cutoff; retain admitted generation/recording ownership without claiming a two-second save/release guarantee. |
| 22. Malformed SFTP/version/reply or local file failure versus malformed SSH/invalid ID/window overflow | Correct isolation or connection failure; unknown request/extended-type handling is not confused with protocol corruption. |
| 23. Consumer fails after partial application and rejects the final fact | Independent failure, safe applied effects preserved, no replay/false credit; cleanup observes actual work without needing a successful final callback. |
| 24. Provider/parser buffers survive channel closure; pending file mutation and reopen | Channel release does not erase upper-owner charges or unknown results; existing file-operation admission/refresh requirements still apply. |
| 25. Old connection I/O/consumer/close completion arrives after a fresh generation | Only old ownership settles and old charges remain until then; the new generation has its own slot subject to shared capacity/operation guards. New credit, status, readiness and request matching are untouched. |
| 26. Every metadata/control/result/observer threshold and repeated wait cancellation/Close | Finite records and stable cleanup ownership, no unbounded tasks/history, no evicted unresolved identity and no synthetic release. |
| 27. Independent payload-credit vectors and real OpenSSH peer under tiny messages/stderr/exact exhaustion | Verify the documented EID 3878 interoperability choice, byte totals and packet limits; do not rely only on a self-consistent local encoder/decoder pair. |

### Candidate 5 readiness and open frontier

Q17-Q19 are accepted. Independent read-only reviews of lifecycle/reopen,
send/request ordering and bounded receive ownership found no additional product
choice necessary while retaining the existing one-SFTP-slot admission rule.
The user confirmed the complete contract and 27-case future verification plan
in Q19 on 2026-11-18.

The accepted contract is consolidated into
[shared SSH channel ownership](../specification/transports-and-authentication.md#shared-ssh-channel-ownership),
[SFTP channel lifetime and ownership](../specification/file-transfer.md#sftp-channel-lifetime-and-ownership)
and the existing [terminal transport seam](../specification/session-and-recording.md#terminal-payload-transport-ownership),
with their [Japanese SSH](../specification/transports-and-authentication-ja-jp.md#ssh-チャネルの共通所有),
[Japanese SFTP](../specification/file-transfer-ja-jp.md#sftp-チャネルの寿命と所有権)
and [Japanese session](../specification/session-and-recording-ja-jp.md#端末ペイロードのトランスポート所有権)
counterparts. Candidate 5 is ready for separately requested implementation.
The verification matrix remains required future evidence; this interview ran
no runtime or cryptographic tests and created no prototype or implementation.

No design decision remains open for candidate 5. Concrete engineering limits,
implementation and its verification/review gates remain required work.

## 6. Shared grapheme-contract ownership

Status: Q20 ownership, Q21 interface C, Q22 emoji-recognition policy, Q24's
removal of the old 1 MiB cap and Q27 exact-input/Stop are accepted. The user
restored the original Q26 shared-budget recommendation, then explicitly clarified
that the configurable source-size limit and size-triggered confirmation remain.
The independent Q28/Q29 guard is superseded.
Q25 includes the user's explicit
permission to prefer simpler bounded omission over complex complete preservation.
The user accepted Q23's revised complete contract and future verification plan
on 2026-11-18, finalizing candidate 6 and completing all six design reviews.
Earlier numerical bounds and failure rows describe the original baseline;
Q24-Q27 supersede them as specified below and in the accepted revised Q23
contract. No implementation or ABI layout is selected in this round.

### Existing contract and source evidence

[ADR 0007](../adr/0007-complete-terminal-processing-in-rust.md) makes Rust the
authoritative terminal owner, including encoding, VT, reflow, input meaning,
recording projections and bounded observation. C# retains session/application
orchestration, transport/file I/O, authority and WPF presentation. It supersedes
ADR 0006's managed handoff. This scope does not reopen that decision or introduce
a managed terminal fallback. The [current implementation status](../specification/implementation-status.md)
and source describe production Rust VT for all 47 encodings and removal of the
managed parser/handoff; older partial-Rust statements are historical, not the
current baseline.

The [terminal Unicode contract](../specification/terminal-and-encodings.md#rendition-geometry-and-unicode)
already pins Unicode 17.0.0, UAX29 revision 47, UAX11 revision 44 and Emoji
17/UTS51 revision 29. The original friction is scattered ownership of that same
contract. Q22 additionally makes its previously unspecified recognized-emoji
repertoire explicit; some current heuristic width results consequently change.
The pinned Unicode version and ordinary maximum-base width policy remain.

| Current source evidence | Architectural consequence |
| --- | --- |
| `src/TabTty.Terminal/src/unicode.rs:39,98` and `src/TabTty.Core/Unicode17.cs:34,13` encode joining/width rules; Rust explicitly labels itself a direct port. | Identical data does not make the two rule implementations one authority. Fixes and validation can diverge across languages. |
| `TerminalPaste.cs:39` counts preview units; `TerminalSession.cs:590` chooses paste pacing/stopping units; `TerminalText.cs:25` calls managed segmentation for `src/TabTty.App/TerminalAutomationPeer.cs:330`. | Managed segmentation has real production consumers. It cannot be dismissed as an unused historical terminal implementation. |
| Managed `Unicode17.GraphemeWidth`/`ScalarWidth` have no callers outside their own definitions in the inspected source. | Do not claim two production width engines. Removing unused width code alone would not address the live segmentation split. |
| Rust `model.rs:1543,1563,1617` handles graphic/grid continuation; `RecordedPosition::append` at `model.rs:680` maintains serialized Text position; preflight at `model.rs:3209` repeats related accumulation. | Unicode mechanics can concentrate, but grid, serialized log and admission/execution have different state and effects. One mutable cluster shared among them would be incorrect. |
| `unicode.rs:137`, `history.rs:82` and managed `Unicode17.cs:80` walk/count clusters and interpret UTF-16, with limit checks or trusted-text assumptions distributed across callers; `reflow.rs:58-59` advances UTF-16 and cluster offsets separately. | Shared unit definitions and scanning rules can improve locality without moving history retention, reflow layout or publication into a general text helper. |
| `src/TabTty.App/TerminalSurface.cs:755-758` uses runtime `Rune.GetUnicodeCategory` to decide when to prepend a drawing-only dotted circle. | Eligibility is Unicode-derived semantics that should agree with the pin; rendering the aid remains a WPF responsibility. |
| `TerminalRecordings.cs:20` creates a receive-only `TerminalOutput`, which constructs `NativeTerminalCore` (`TerminalOutput.cs:59`). | The separate Text projection is a real semantic projection, not evidence of a second managed terminal engine. |

The two embedded `Unicode17.bin` files have identical SHA-256 values in this
read-only inspection; the data sources/hash are recorded in
`implementation-status.md:668-671`. That establishes byte identity, not algorithm
conformance or reproducible regeneration. Repository searches did not locate a
Unicode table generator or a `GraphemeBreakTest` conformance fixture. These are
static observations, not executed behavioral tests or reproduced failures.

### Q20 - Ownership scope: accepted

Concentrate the pinned grapheme rules and TabTty's existing cell interpretations
in one Rust-owned module, used through the existing terminal seam. Share Unicode
mechanics and contract facts across actual consumers; keep their mutable state,
ordering and effects with their current owners.

| Owner | Responsibility in the accepted scope |
| --- | --- |
| Shared Rust grapheme module | Pinned property data/version, grapheme joining/segmentation, existing emoji/text presentation and cell-width rules, isolated-mark eligibility, bounded cluster traversal and explicit scalar/grapheme/UTF-16/UTF-8 measures needed by consumers. It owns the Unicode knowledge, not every text-related application policy. |
| Rust terminal stream/grid and admission/execution owners | Per-source decoder/BOM/parser/REP state, continuation eligibility after VT actions, editable-cluster location/provenance, grid placement/wrapping/erase, revisions and separate admission/execution walks. Each uses shared mechanics with its own state. |
| Rust Text/Terminal projection and recording-position owners | Distinct receive-only/combined content, serialized Text cluster/column/TAB position, recording eligibility and interval provenance. A serialized log tail is not assumed to be the current editable screen tail. Actual files remain with the recording owner. |
| Rust input and existing C# session owners | Rust derives encoding/grapheme units and exact-send meaning; C# retains input authority/order, paste pacing, broadcast recipients and transport submission. Paste preview/counting and stopping use the same semantic rules, without a second C# segmentation authority. |
| Rust history/reflow and managed observation adapters | Reuse cluster-safe traversal and explicit units; retain row/logical anchors, retention, text revisions, search comparison, response limits and ordered publication in their existing owners. Managed adapters consume owned results instead of re-segmenting native text independently. |
| WPF/presentation adapters | Retain shaping, font fallback, glyph drawing, IME, UIA presentation and visual navigation. Use shared cluster boundaries and drawing-aid eligibility; never derive terminal cell width from font metrics or the runtime's Unicode generation. |

This module is an internal concentration of real semantics, not a new generic
Unicode-provider framework. It need not merge every cluster accumulator or
expose a low-level scalar-at-a-time recipe across the managed/native seam.
With Q20 accepted, the comparison below addresses concrete interface shapes,
batching and ownership. Exact ABI layout remains implementation work within the
eventual accepted contract. The existing no-third-party-crate constraint and
Rust/standard-library exception remain intact.

Preserve these established distinctions:

- UTF-8 bytes, UTF-16 code units, Unicode scalars, grapheme offsets, terminal
  cells and shaped glyphs are different measures. A wider glyph does not change
  a cluster's stored cells, and an offset conversion does not authorize a new
  split or normalization.
- A complete input batch has a known end; an arbitrary receive chunk does not
  finalize a cluster. EOF, a VT action that changes continuation eligibility,
  projection changes and batch end have their existing distinct meanings.
  Shared rules do not combine receive sources or erase interval provenance.
- Terminal continuation can change an existing cluster's width. Its grid owner
  must update placement before publishing, retain whole-cluster erase/wrap/reflow
  behavior and preserve scalar content. The shared width rule does not own
  cursor changes, row retirement or a log's serialized position.
- Preserve isolated-mark one-cell behavior and the drawing-only dotted circle;
  the synthetic aid stays out of stored/copied/logged text. Keep interior FEFF
  as zero-width content and source-specific first UTF-8 signature handling with
  the decoder. No Unicode normalization or inferred glyph replacement is added.
- Retain the 256-scalar cluster limit without silent split/drop. Input rejection,
  received-session resource failure and an observation `resource_limit` remain
  context-specific outcomes. Invalid external input and trusted retained text
  need not have identical error types or recovery behavior.
- Do not extend this scope to authentication normalization, general UI text
  segmentation, word/line breaking, new search semantics, codec replacement or
  macros. Existing regex/literal comparison and transport responsibilities stay
  where they are; only their use of terminal cluster/offset facts is relevant.

### Depth, dependencies and verification direction

The deletion test supports this scope: without the shared module, grid/Text
continuation and width, paste units, UIA character navigation and history scans
would need to carry the Unicode rules again. That repeated knowledge is the
complexity to remove. A wrapper around both current rule engines, or deletion
of unused managed width methods alone, would not supply the desired depth.
The real consumers give leverage, and concentrating rules plus independent
verification gives locality. Sharing immutable tables alone is insufficient;
sharing all mutable projection state would change behavior incorrectly.

The core dependencies are in-process computation and bounded state. The existing
managed/native adapter remains a meaningful seam, but there is no need for a new
swappable Unicode port solely to make tests possible. Font/OS shaping remains
an external presentation dependency and cannot become a width/segmentation
oracle. Internal representation changes should preserve observations through
the ordinary terminal/input/history interface.

Existing test source includes Rust cluster-limit admission (`lib.rs:1160`),
cluster-prefix offsets after retirement (`lib.rs:1282`), VS15 width shrink/Text position
(`lib.rs:1311`), managed integration for wide erase/wrap and split combining/
VS16/FEFF (`tests/TabTty.Core.Tests/Program.cs:1597,1608`), and UIA ZWJ movement
(`tests/TabTty.App.Tests/Program.cs:1590`). Their existence is not evidence that
every caller already shares one contract or that these tests ran in this review.

The interface and final verification design must cover
official grapheme conformance plus independent TabTty width expectations; whole
batch versus every chunk split; RI/Indic/combining/ZWJ/variation sequences;
255/256/257-scalar cases; CRLF/FEFF/source separation; editable versus serialized
tails; right-edge growth/shrink, partial erase and reflow; paste count/pacing/
stop, UIA character navigation and history pagination/offset units. Agreement
between a C# algorithm and its Rust port alone cannot detect shared mistakes.
Data provenance, generator reproducibility and corruption/version rejection are
also verification concerns, not claims established by matching binary hashes.

### Q21 - Interface C: accepted

Three fresh, independent design explorations compared different constraints:
minimal entry points, maximum flexibility for current consumers, and the
simplest ordinary caller. These are illustrative shapes, not implemented types,
selected ABI declarations or new public product features.
The user accepted C with the cross-cutting contract below on 2026-11-18.

#### A - Value-state scan

Keep two core entry points: construct a bounded state and scan a fragment from
that state. The result owns the next state, typed measures, progress and ordered
cluster changes. A change distinguishes a new cluster, extension of its suffix,
and finalization; extension includes previous and current cell measures.

```text
state = NewScanState(ambiguous_width)
page = Scan(state, text, MoreMayFollow | Complete, allowance)
// page: next_state, owned_changes, consumed_measures, progress
```

Receive owners retain separate values; complete input owners scan to the known
end. Admission uses a charged clone, while execution starts from authoritative
original state. Rust adapters fold scan results into ordinary input, snapshot
and history operations, so managed callers do not drive scalar-by-scalar scans.

This gives high leverage per entry point and good locality for rule changes.
Its cost is the protocol hidden in the small signature: callers still need to
understand provisional tails, ordered changes, finalization and page progress.
Materializing changes and copying tail values also need bounded storage. A small
method count alone does not establish depth.

#### B - Composable cursor and immutable index

Offer a Rust cursor for incremental processing and a snapshot-associated index
for enclosing, moving and paging through clusters. The index supplies the
specific UTF-8/UTF-16/scalar/grapheme/cell measures each consumer requests.

```text
cursor = rules.Cursor(ambiguous_width)
page = cursor.Scan(fragment, end, allowance)
preview = cursor.ForkForPreview(allowance)
index = rules.Index(owned_snapshot, allowance)
range = index.Enclosing(utf16_offset)
range = index.MoveBy(grapheme_offset, grapheme_count)
```

This offers the most flexible traversal for the existing consumers. Unicode
knowledge stays local, while an index can avoid repeated navigation work.
However, the interface exposes more lifetime and correctness obligations:
snapshot identity, stale-index prevention, cursor continuation, index capacity
and result-page ownership. These obligations make it less deep for the ordinary
caller unless an adapter hides most of the interface again. No performance
benefit is established without future measurement.

An index must remain paired with its immutable snapshot. It cannot reinterpret
text from a later terminal revision or blindly re-segment concatenated stored
clusters. Build compact or bounded requested metadata; an eager descriptor with
every offset measure for every ASCII character is not an acceptable assumption.

#### C - Named complete operations and owned unit plans

Expose the facts each current caller needs through its existing operation.
Complete paste analysis works without a live terminal session. Sending consumes
the immutable paste payload with Rust-derived units. Screen, UIA and history
operations return owned text with the relevant authoritative cluster facts.

```text
paste = TerminalPaste.Create(finalized_text)
Preview(paste.character_count, paste.line_count)
session.SendPasteAsync(paste, pacing)

snapshot = terminal.Capture(requested_projection)
uia.NavigateCharacters(snapshot.authoritative_cluster_spans)
renderer.Draw(snapshot.clusters, snapshot.drawing_aid_facts)
```

The shared Rust module supplies complete-text analysis, bounded traversal of
stored clusters and a small private incremental tail. Different grid, source
and serialized Text owners use independent tails. The ordinary caller does not
manage a Unicode cursor, internal result pages, reservation or a scalar recipe.

Paste segmentation/counting is independent of a target profile. Broadcast can
reuse that analysis; each target still performs exact encoding preflight against
its own current encoder and budgets before sending. Width-dependent facts, when
needed, use that target's captured Ambiguous setting. Preview must not require
opening a session or borrowing mutable session state.
The accepted [broadcast contract](../specification/broadcast-and-split-views.md#paste-and-frozen-operations)
requires every frozen target to pass whole-input preparation before any target
payload is released. Shared segmentation is not a shared encoded-send result.

UIA consumes the terminal's stored cluster spans rather than segmenting its
exported string afresh. A VT action can end continuation even when adjacent
exported scalars would join in a fresh UAX29 scan. Snapshot projection retains
that distinction, generated separators and zero-cell content. WPF uses supplied
drawing-aid eligibility; the runtime Unicode category database is not a second
semantic authority.

This has the strongest depth for current callers: joining, width, unit
conversion, complete-input finalization and bounded handoff remain behind the
existing seam. Its costs are richer operation results and careful compact plan
storage. An unrelated future consumer may require another complete operation,
rather than obtaining a generic mutable Unicode cursor.

#### Comparison and recommendation

| Criterion | A: value-state scan | B: cursor/index | C: complete operations |
| --- | --- | --- | --- |
| Interface knowledge | Few entry points; ordered-change and progress protocol | Cursor, index, lifetime and traversal protocol | Existing caller operations plus owned semantic results |
| Depth for current callers | Good inside Rust; adapter must fold results | Good traversal leverage, but more obligations | Highest: ordinary callers request completed useful work |
| Locality | Shared rules and state transition | Shared rules and index/traversal mechanics | Shared rules plus their correct use in input and observations |
| Seam placement | Rust scan; managed adapter hides scan driving | Rust cursor/index; managed adapter hides native lifetime | Existing terminal/input/observation seam; no public Unicode workflow |
| Main cost | Tail/change copies and subtle finality | Index capacity, stale identity and broader interface | Compact plans and richer authoritative snapshot metadata |

**Accepted: C.** It matches ADR 0007 and the actual paste, UIA, WPF and
history callers most directly. Keep A's useful internal properties: independent
bounded cloneable tails and explicit previous/current measures. Reuse B's
snapshot identity discipline and bounded indexing where a current operation
needs them, without exposing its general cursor protocol to managed callers.
This is one Rust-owned module, not three alternative production rule engines.

### Cross-cutting contract accepted in Q21

These constraints apply to all three alternatives; Q21 does not trade away
existing behavior for a smaller interface.
This section records the original Q21 acceptance: its 1 MiB paste cap, shared
input-budget treatment and length-triggered cluster failures were subsequently
superseded by Q24-Q27. The revised Q23 proposal below incorporates those changes.

- A receive chunk, internal output page and complete-input end are distinct.
  Only the relevant semantic owner ends continuation. VT/source/projection and
  recording-interval decisions stay with their accepted owners.
- Preserve ordered width transitions that affect grid placement or admission.
  Combining a same-chunk growth followed by shrink into only the final width
  can hide an intermediate wrap or peak allocation. Rust may process individual
  scalars internally without requiring individual managed/native calls.
- Grid and serialized Text positions have independent tails. Extension reports
  newly eligible suffix text separately from the previously accounted prefix;
  the recording owner does not emit that prefix again. Width shrink uses checked
  previous/new measures, without unsigned underflow.
- Admission clones bounded state and charges its working data. Execution starts
  from authoritative original state, not the preview's ending state. Shared
  mechanics do not imply shared mutations or prefix publication before the
  containing operation passes its existing admission rules.
- Tie each offset to a unit and a source/projection/range origin. UTF-8 length
  of Unicode text is distinct from original received transport bytes and from
  bytes produced by a selected transmit codec. Cell width is not a grid column,
  UTF-16 index or glyph count.
- Owned results remain valid after native mutation/disposal. No borrowed row
  pointer crosses that lifetime. A unit plan belongs to its exact immutable
  input; arbitrary suffix re-segmentation cannot replace preserved context.
- Do not require an array of all offset measures for every scalar or cluster.
  The existing 1 MiB finalized encoded paste-payload limit per target and 2 MiB
  per-session input budget remain. These are not UTF-8 analysis or UTF-16 text
  limits. Compact indexing or bounded internal pages must account for
  payloads, scratch space, clones, capacity and overlapping native/managed copies
  until actual release. Shared storage counts once only with real shared
  ownership. Internal paging must not invent UIA `GetText` truncation/pagination.
- Malformed external UTF-16 or an oversized complete-input cluster rejects the
  whole input without replacement, encoder progress, submission or local echo.
  Received growth past 256 scalars keeps the affected-session resource failure.
  Observation resource pressure keeps its narrow `resource_limit` or explicit
  provider failure. Corrupt retained invariants are not silently replaced or
  misreported as an ordinary user-input validation error.
- The core dependency is in-process computation and bounded memory. Use the
  existing managed/native adapter; no test-only Unicode port, third-party crate,
  managed shadow or OS/font oracle is introduced.

Future verification crosses these same interfaces: independent official
segmentation vectors, separately specified width expectations, all relevant
chunk/page splits, 255/256/257 scalars, typed offsets, separate grid/log tails,
intermediate growth/shrink at the right edge, exact paste preflight and complete
Stop units, broadcast target differences, VT-separated snapshot clusters, UIA
navigation, drawing-only aids and history/reflow anchors. Include maximum-size
paste and observation workloads to establish bounded metadata overhead without
lowering accepted limits. Retain existing terminal/input/UIA integration checks;
replace duplicate helper tests only when the shared contract has equivalent
independent coverage. No runtime or conformance test was executed in this round.

### Primary-source basis

Reviewed the pinned primary sources for this scope:
[UAX29 revision 47](https://www.unicode.org/reports/tr29/tr29-47.html) defines
extended grapheme segmentation, not glyph shaping;
[UAX11 revision 44](https://www.unicode.org/reports/tr11/tr11-44.html) provides
East Asian Width properties and calls for terminal-specific tailoring;
[UTS51 revision 29](https://www.unicode.org/reports/tr51/tr51-29.html) defines
emoji sequences and presentation. TabTty's maximum-base width, Ambiguous profile
choice, isolated-mark aid, edge placement and capacity rules are product
adaptations, not behavior mandated in full by those standards. Future
[Unicode 17.0.0 GraphemeBreakTest data](https://www.unicode.org/Public/17.0.0/ucd/auxiliary/GraphemeBreakTest.txt)
provides independent segmentation expectations; no such corpus was run here.

### Q22 - Explicit emoji recognition policy: accepted

The read-only investigation found a real remaining product choice. Accepted
[Q118](terminal-display-proposals.md#q118-unicode-clusters-cell-width-and-unavailable-glyphs),
the initial-release draft and the canonical width matrix say "supported" or
"recognized" without enumerating that repertoire. They establish two-cell
recognized emoji, text-variation override, ordinary fallback and preserved
content. They do not establish RGI-only recognition or all possible combinations
of emoji properties.

Current `unicode.rs:98` uses property heuristics rather than a full sequence
list: presence of emoji presentation, a VS16 after an Emoji-property scalar,
modifier/base presence or a keycap pattern can select two cells. Any VS15 selects
ordinary width. That is source evidence, not an independently accepted complete
definition of supported sequences. The existing regression fixture
`lib.rs:1311` includes heart + VS16 + VS15; narrowing the text override to only
standard adjacent base/VS15 pairs would change that expectation.

The pinned primary data separates the questions:

- [Emoji 17 sequence data](https://www.unicode.org/Public/17.0.0/emoji/emoji-sequences.txt)
  and [ZWJ sequence data](https://www.unicode.org/Public/17.0.0/emoji/emoji-zwj-sequences.txt)
  enumerate the RGI repertoire, including basic emoji and the specified keycap,
  modifier, flag, tag and ZWJ sets.
- [Emoji 17 test data](https://www.unicode.org/Public/17.0.0/emoji/emoji-test.txt)
  maps fully-qualified and component entries to that repertoire and lists
  minimally-qualified/unqualified forms with missing emoji presentation
  selectors. Unqualified entries also include bare text-default symbols; taking
  every entry as a two-cell emoji would therefore change their ordinary width.
- [Emoji 17 variation sequences](https://www.unicode.org/Public/17.0.0/ucd/emoji/emoji-variation-sequences.txt)
  separately lists valid text/emoji presentation pairs. RGI membership alone
  is not a complete substitute for that presentation-pair list.
- [UTS51 revision 29, section 2.7.1](https://www.unicode.org/reports/tr51/tr51-29.html#Emoji_and_Text_Presentation_Selectors)
  leaves treating missing-selector ZWJ forms like their fully-qualified forms
  to the implementation. Neither those display recommendations nor a valid
  grapheme requires TabTty's particular terminal-cell assignment.

**Accepted on 2026-11-18:** use the fixed official repertoire plus the explicitly
listed compatibility forms below. This is a TabTty policy clarification,
not a claim that current source already implements it or that Unicode mandates
these cell widths.
Q25 later adds one explicit exception for stored content: width recognition
uses only the retained first 256 scalars, while omitted continuations still
advance segmentation. The complete-sequence wording below records Q22's
original policy and applies to that retained spelling after the Q25 amendment.

1. Recognize the Emoji 17 RGI set, every emoji-style pair in the pinned variation
   file, and the multi-scalar minimally-qualified/unqualified entries in the
   pinned test file. Missing-selector compatibility is limited to listed forms;
   do not generate arbitrary new variants or recognize a known substring inside
   an otherwise unsupported sequence.
2. A bare text-default symbol remains under the ordinary maximum-base rule.
   Its appearance in an unqualified singleton test entry does not promote it to
   emoji width. An explicit supported VS16 pair can select two cells.
3. Preserve the existing VS15 priority: any VS15 in the cluster selects the
   ordinary text-width rule, even after a VS16. This cluster-wide precedence is
   TabTty's compatibility choice, distinct from UTS51's valid presentation-pair
   and image-composition rules. Ordinary width can itself be two cells.
4. A recognized core retains its two cells when followed by ordinary combining
   decoration. For recognition lookup, strip only the maximal trailing run whose
   scalars have pinned General_Category Mn/Mc/Me and Grapheme_Cluster_Break
   Extend/SpacingMark,
   excluding every Variation_Selector and U+20E3 COMBINING ENCLOSING KEYCAP, for
   recognition lookup only. Require the entire remaining core to match the
   recognized set. Never strip interior marks, joiners, modifiers, tags, keycap
   structure or variation selectors to manufacture a match. Preserve all original
   scalars in content; this is not normalization or rendering substitution.
5. An otherwise unmatched cluster uses the existing ordinary maximum-base
   rule. Malformed or unlisted ZWJ/modifier/tag/flag combinations, repeated VS16
   and isolated components not in the supported sets are not implicitly added.
   Existing isolated-mark and zero-width FEFF rules retain their special cases.
6. Reevaluate after each accepted extension of an eligible incremental cluster.
   Start from its complete original scalar sequence each time; do not carry
   forward text with decoration removed for an earlier lookup.
   A recognized prefix can become unsupported after more structure arrives;
   width can grow or shrink. A chunk/page end cannot freeze that intermediate
   recognition. The grid/log owners retain the ordered effects described above.

Representative expectations to carry into the final verification matrix:

| Case | Accepted result |
| --- | --- |
| Bare COPYRIGHT SIGN versus COPYRIGHT SIGN + VS16 | Ordinary width versus two cells |
| A listed missing-VS16 keycap or ZWJ form | Two cells without inserting missing selectors into stored text |
| A valid emoji-style variation pair outside an RGI-only lookup | Two cells by the pinned variation list |
| Recognized core followed only by permitted ordinary combining decoration | Two cells; preserve the decoration |
| Known emoji prefix followed by unsupported ZWJ/tag/modifier structure | Ordinary width of the complete cluster, not substring recognition |
| Heart + VS16 + VS15 | Ordinary text width; preserve the existing shrink case |
| Repeated VS16 or an unlisted legacy base + VS16 + modifier form | Ordinary fallback unless the complete form is explicitly listed |
| An isolated RI or an unlisted RI pair | Ordinary fallback; no automatic promotion from Emoji_Presentation alone |

The final review must include source-versus-contract differences, including
fallback widths that the current heuristic would promote, and data provenance
and regeneration checks for the added sequence/variation lists. Pinning the
rules makes those differences reviewable; it does not promise one glyph,
matching every remote application's width, or conformance established by this
read-only research.

For example, COPYRIGHT SIGN + VS16 + ZWJ + COPYRIGHT SIGN is currently promoted
by the VS16 heuristic. Under Q22 its unsupported whole sequence uses
ordinary maximum-base width. Accepting Q22 therefore authorizes a documented
behavior clarification with some changed width results, not only code movement.

No new domain term or ADR is needed for these decisions. Scalar,
cluster, cell and glyph remain established technical distinctions in the
specification, not invented product entities or synonyms for one another.

### Revised complete contract for Q23: accepted

The user accepted this revised complete contract and its future verification
plan on 2026-11-18. It integrates Q20-Q22 and Q24-Q27, including the restored
original Q26, with their ordering, lifetime, failure and verification obligations.
Q28/Q29's independent aggregate guard is superseded. Names and conceptual result
fields below express required facts, not selected ABI layouts.

#### Complete input and target preparation

Complete analysis validates the exact immutable finalized input before returning
a successful plan. It supplies grapheme count, existing newline/count facts and
bounded traversal of the owned input. A semantic input grapheme can be longer
than a work page or transmission chunk; traverse it with offsets and bounded
continuation context rather than collecting another entire-grapheme buffer.
Stored observation spans, input graphemes and indivisible transmission units
are distinct. Existing paste-control validation remains;
no replacement, normalization or insertion of absent variation selectors is
allowed. Internal pages are storage/work units, not separate acceptance units.
Malformed UTF-16 or a forbidden control on the final page rejects the plan;
earlier pages cannot have caused encoder progress, payload submission or echo.
An internal storage page may end within a valid surrogate pair; carry the
decoding context rather than rejecting that artificial split as malformed input.
The complete input itself must contain valid scalar text.

Session-independent analysis means no dependency on a live native terminal
model, not ownerless allocation. The existing input-intent/preview owner and
adapter own admission, immutable capture and release. Broadcast already captures
the intended targets before preview and owns the complete operation under the
[broadcast input design](../specification/broadcast-input-operations.md).
There is no new standalone preview service or uncharged allocation pool.

The clipboard operation owns one exact immutable source shared by all targets
and captures `maxPasteTextBytes` at operation start, default 512 MiB. Measure
actual selected text as two bytes per UTF-16 code unit before target encoding,
excluding the terminating NUL, allocation padding and other clipboard formats.
Count unchanged CR/LF; supplementary scalars use two code units. Reject text
above the captured limit as a whole without trimming; equality remains eligible
for ordinary validation and admission. For otherwise valid text within that
limit, require exactly one confirmation for CR/LF or source size strictly above
1,024 bytes. Equality at 1,024 bytes without a newline takes the direct path.
Retain full-text access through bounded preview pages. Confirmation is not a
guarantee of send admission: the source, work and any real capture/copy overlap
must fit restored Q26's application and per-target accounting.

Before first submission, each frozen target validates the whole payload with
its own current encoder and accepted settings. There is no separate finalized
encoded-payload size cap. Reserve private target work within the existing
2 MiB/256-unit input allowance, shared with ordinary input, and retain the
existing codec/bracket closure reservations. Charge the actual paste-owned
capacity in the broadened application envelope; do not charge the shared source
again against each target's working allowance or create a second input pool.
Capture target-dependent mode and preparation facts at the existing ordered
transition. The common plan neither proves target encodability nor admits
session-dependent effects such as local-echo cluster extension. Preserve the
existing native operation admission and submission/echo rules for those effects.
Broadcast sends no target payload until every required target has passed
preparation. A late unencodable suffix rejects the whole not-yet-started operation.
Keep the existing 30-second broadcast preparation deadline, excluding dialog
time, and recheck it after the last target's conversion before first-send
commitment. Admitted source storage does not promise completion within that deadline.

Keep exact input identity, target generation, current encoder/configuration,
authority and capacity basis valid until their corresponding ordered use.
Discard stale preparation and re-prepare within the existing work budget or
return the existing failure. Never redirect work to a replacement generation.
Revalidation recognizes reservations already held by that operation instead
of invalidating itself or charging them twice. A shared plan does not freeze a
mutable encoder or authorize sending after input permission is lost.

After submission starts, report definite complete units, unsent work and an
uncertain suffix under the existing transport contract. Only definitely
submitted complete units advance the corresponding encoder/echo commitment.
Stop uses Q27's next complete scalar or required larger indivisible codec/
control unit, even inside a short or long grapheme. Do not split CRLF. Internal
chunks are not completed graphemes for counting or pacing; normally successful
sending preserves the complete input. Use reserved healthy codec/bracket cleanup;
no uncertain bytes are replayed. Whole-input preparation does not promise
rollback of sent bytes, remote receipt or atomic broadcast delivery.

#### Authoritative observations and positions

Each owned observation binds immutable text and its cluster facts to one capture,
session generation, projection and requested range. Exported UTF-16 ranges refer
to that exact text, while stable logical anchors and cluster offsets refer to
retained terminal content. Preserve hard boundaries, soft-wrap relationships,
generated separators and zero-cell content. Drawing operations also obtain
their required cell positions/widths and drawing-aid eligibility, retaining
existing rendition, blank and wide-continuation facts. These facts need not be
stored as every possible measure in every cluster descriptor.

Use the existing projections: combined active screen for screen observation,
combined normal screen plus scrollback for AI history even while parked, and
the contracted active normal/alternate contents for UIA. Receive-only Text
recording is not a substitute for any of those observations. Do not add public
DATA/stderr lineage fields merely to implement shared Unicode mechanics.

Authoritative stored spans retain their identity after export. Adjacent scalars
separated by a VT action that ended continuation must not be silently merged
by fresh segmentation of concatenated text. Wide continuation cells do not
become separate characters, and zero-cell content does not disappear because
cell positions coincide. Generated projection separators retain the existing
character-navigation meaning without becoming received or recorded characters.
Rendering aids never appear in returned text or its offsets.

Each stored spelling retains at most its first 256 scalars under Q25. Incoming
segmentation still advances over omitted continuations. Width and drawing-aid
eligibility use the retained prefix; omitted suffixes cannot alter its geometry.
Concatenated retained text need not freshly segment into its authoritative
stored spans. UIA's full-result obligation is for retained text and permits no
additional provider-time truncation.

Ownership preserves a captured result after native mutation/disposal; it does
not prove that result is a valid basis for a later live operation. Never pair
old text with newer metadata or apply an old numeric position to a replacement
projection. Live UIA ranges use stable anchors and an appropriate current
capture, retaining the existing nearest-surviving-boundary collapse on eviction.
AI history/search keep their distinct authority, expiry, content and retention
checks and error precedence, including edit-then-restore detection and unchanged
prefix survival. Pure reflow preserves surviving content anchors. Owned text is
neither a history consistency proof nor an authorization token.

Internal traversal pages cannot change the outward result contract. UIA provides
the requested result under its existing full-result budget or explicitly fails
the provider call; it does not truncate `GetText` implicitly. AI history keeps
its existing bounded external pages and continuation validation. If a caller's
text allowance cannot fit the next supported complete cluster, return the
existing `resource_limit` without progress, not an endless empty continuation.

The serialized Text-position owner remains distinct from the editable grid and
from stored-span observation. It accounts for newly emitted text, previous/new
width and serialized TAB position without replaying an emitted prefix. Never
infer a TAB from an unproved column: use the existing final-spaces/text
representation when faithful TAB expansion cannot be established. Whether an
internal position is represented as unknown is an implementation choice, not
approval of the current source's particular `known=false` threshold. Q25's
bounded retention is normal continued processing, not a source/grid failure;
exporting multiple stored spans must not manufacture an oversized authoritative
cluster. Normal-screen Text uses the retained receive-only spelling, while
alternate Text, Terminal and Raw keep their respective eligible original input.
Serialized-position capacity handling cannot silently discard stored content
or turn a logging-only problem into a new terminal failure policy.

#### Capacity, cancellation and contextual failures

Preserve the 256-scalar stored-spelling guard, existing 2 MiB/256-unit target
input-work allowance, 2 GiB per-session terminal-state bound and unchanged
UIA/AI/recording budgets. Restored Q26 broadens the existing 4 GiB application
envelope to terminal state plus admitted paste-owned storage/work. A stored
span can occupy up to 1024 UTF-8 bytes; observation traversal needs capacity for
one such complete span or returns its defined resource failure. This does not
cap an input grapheme or authorize payload truncation. UTF-16 capacity,
Unicode UTF-8 length and target-encoded bytes remain separate measures.
Check length, capacity and offset arithmetic, including previous/new width
changes, rather than wrapping or saturating an overflow into an apparently
valid result. An intentional bounded column remainder is distinct from hiding
an invalid total length or negative width change with unsigned arithmetic.

Use compact payload storage and bounded indexing/encoding; do not introduce
another product-size rejection to accommodate an eager per-scalar/grapheme index. Paste-owned
capture/compaction overlap, source/plan/index capacity, preview, native/managed
copies, private conversion, queued/in-flight chunks and closure work belong to
the broadened 4 GiB application accounting envelope under restored Q26.
Shared source/plan storage is owned by the operation and counted once when
physically shared. Retained terminal effects such as local echo still use
their existing per-session terminal-state admission as well as this envelope.
The later implementation must demonstrate a bounded representation and capture
path, not assume allocation/copy overhead disappears.

Count a genuinely shared allocation once and each real private copy separately.
Per-target conversion/preflight/queued/in-flight work shares the existing
2 MiB/256 concurrent input-unit allowance; there is no second paste-specific
2 MiB pool. Keep the existing treatment of separately reserved closure capacity
and count its actual allocation/reservation in the application envelope. Do not
retain a full encoded payload for each target. Before first-send commitment,
reserve the bounded remaining send/Stop/closure peak so later paste admission
cannot consume it. References and reservations retain ownership through actual
release, including cancelled-but-incomplete work. Consuming a reservation replaces
its future charge with allocated capacity rather than charging twice. New work
cannot take held send/Stop/closure capacity. There is no independent paste
aggregate setting or available-memory percentage rule. The user-configurable
512 MiB default source cap is a distinct per-paste product limit, not an extra
memory pool or a guarantee that the operation fits the shared envelope.
Subsequent terminal mutations still undergo existing state admission; holding
a confirmed paste does not exempt them from resource limits. Allocation
accounting does not promise an exact process working-set ceiling or recovery
from every OS allocation failure.

Recoverable capacity pressure retains existing cache eviction, reject/defer
and transport backpressure behavior. Apply the original broadcast deadline to
capacity waits/retries; no new waiting mechanism or deadline reset is introduced.
Do not discard retained output, shorten retention or close unrelated sessions
to admit a new paste.
Only an unrecoverable hard-state failure
uses the affected-session outcome below; temporary admission pressure alone
does not terminate a healthy session.

| Context or event | Required outcome and retained ownership |
| --- | --- |
| Invalid complete external input, source above its captured configured limit, unencodable target or unsuccessful pre-send resource admission | Reject the complete not-yet-started operation; no live encoder progress, prefix send or local echo; release private work when actually finished |
| Invalid/stale preparation basis before execution | Discard/re-prepare within existing bounds or return the existing stale/cancelled/failure outcome; no effects on a replacement generation |
| Real received cluster continues beyond 256 scalars | Retain its first 256 scalars; advance break state over every continuation, omit only the stored suffix and begin the next stored cluster at the real boundary; no disconnect or failure notification solely for this omission |
| Terminal hard-state admission fails unrecoverably under the existing resource policy | Existing affected-session resource failure, retained safe output and incomplete status; do not confuse this with Q25's normal storage omission or select C# |
| Snapshot/history workspace or result capacity is unavailable | Existing narrow resource/provider failure without stopping a healthy terminal; no partial response/progress represented as success |
| A corrupted retained invariant or arithmetic inconsistency is detected | Explicit internal failure through the containing operation's existing policy; never reinterpret corrupted text as valid replacement characters or ordinary malformed user input |
| Native binary or pinned rule data is missing, malformed or version-incompatible | Explicit packaging/initialization failure; no OS-data substitution, network fetch or managed fallback |
| Cancellation before authoritative execution/submission | Cancel private preparation and publish no new native effects/payload; ownership survives until workers/results actually release it |
| Cancelled wait after accepted execution, submission or publication | No rollback claim; the existing operation owner completes delivery/cleanup and keeps definite/uncertain outcomes under the original generation |
| Unexpected execution failure after successful admission | Existing affected-session or narrow owner policy as applicable, without replay or falsely reporting a clean pre-mutation rejection |

Earlier accepted operations and separately admitted Raw/log bytes retain their
own outcomes. No blanket rollback or prefix erasure is promised across files,
transport and terminal state. Recording-only failure remains narrow and cannot
suppress valid terminal effects or automatic replies.

#### Pinned data and independent evidence

One production Rust rule owner uses Unicode 17.0.0, UAX29 r47, UAX11 r44 and
Emoji 17/UTS51 r29. Add the accepted sequence/qualification/variation data to
the existing grapheme/East Asian Width/Indic/mark/emoji-property inputs.
Use Unicode 17 [PropList.txt](https://www.unicode.org/Public/17.0.0/ucd/PropList.txt)
for the full Variation_Selector property needed by Q22's decoration exclusion;
excluding only VS15/VS16 would not implement that rule.

The manifest covers `EastAsianWidth.txt`, `auxiliary/GraphemeBreakProperty.txt`,
`DerivedCoreProperties.txt` for InCB, `extracted/DerivedGeneralCategory.txt`
for Mn/Mc/Me, `ucd/emoji/emoji-data.txt`, `PropList.txt`, and the pinned
`emoji-sequences.txt`, `emoji-zwj-sequences.txt`,
`emoji-variation-sequences.txt` and `emoji-test.txt`. Record their actual
versioned download paths, range expansion, property defaults, duplicate-set
union and Q22 status/multi-scalar filtering. Share immutable data only where
ownership is real; session-free analysis passes the same version/data checks.

The implementation deliverable includes a reproducible generation/check path,
an exact source/version manifest and content hashes, data/schema compatibility
checks, and applicable Unicode license notices. Pin the files used for a build;
runtime processing must not download a mutable latest dataset. Verify generated
ranges, sequence membership, required properties and corruption rejection.
Matching the two current binary hashes does not prove any of these properties.
No third-party crate or swappable Unicode-provider interface is required.

Use the official GraphemeBreakTest corpus for segmentation and independently
specified TabTty expectations for width, variation priority, isolated marks,
FEFF, placement and content preservation. Do not derive expected widths from
the old C# implementation or from the new production lookup itself. Retain
source-versus-contract difference vectors for Q22 rather than calling a changed
heuristic result a regression by definition. Font rendering and a single
combined glyph are not segmentation/width oracles.

Use these concrete scalar vectors among the independent width expectations.
Ordinary values below follow the pinned East Asian Width data, not the current
heuristic; additional vectors exercise Ambiguous=2 and ordinary Wide bases.
For stored content these width rules use Q25's retained prefix; an omitted
scalar beyond 256 cannot change geometry even if it is VS15/VS16 or a ZWJ suffix.

| Scalar sequence or extension | Expected cells under Q22 |
| --- | --- |
| `00A9` / `00A9 FE0F` / `0030 FE0F` | 1 / 2 / 2; the digit presentation pair is supported without a keycap |
| `0030 20E3` / `1F441 200D 1F5E8` | 2 / 2; listed missing-selector spellings stay unchanged |
| `00A9 FE0F 0301` | 2; permitted trailing combining decoration |
| `00A9 FE0F 0301 FE0F` | 1; the accent is now interior and the additional selector cannot be stripped |
| `00A9 FE0F 200D 00A9` | 1; a supported prefix does not recognize the entire unsupported sequence |
| `2764 FE0F FE0E` | 1; VS15 overrides the earlier emoji presentation |
| `1F441 FE0F`, then append `200D`, then append `1F5E8` | 2, then 1, then 2; an intermediate unsupported prefix can shrink before the final listed form grows |
| `00A9`, then append `FE0F`, then append `FE0E` | 1, then 2, then 1; preserve all placement effects, even in one receive chunk |

Valid Unicode containing an unsupported emoji spelling is content with ordinary
fallback width, not malformed external input. Supplement the examples with
isolated/listed/unlisted RI forms, unsupported structures with an ordinary Wide
base, every allowed suffix-property branch and excluded variation selectors
beyond VS15/VS16. Expected membership is reviewed against the pinned primary
files separately from generated production lookups.

### Revised candidate 6 verification matrix for Q23: accepted

The user accepted this 39-case verification plan on 2026-11-18. All entries below
remain required future evidence after implementation is separately authorized.
This interview performs document checks only. Run rule
checks through the selected shared interface and integration checks through
ordinary terminal/input/observation operations with the real Rust owner. Use
existing controlled I/O/publication adapters for variable external effects;
do not add a Unicode test-only production port.

| # | Scenario | Required independent observations |
| --- | --- | --- |
| 1 | Regenerate pinned data; change source hash/version/schema; corrupt a range/sequence | Reproducible expected data and explicit build/initialization rejection; no alternate runtime rule source |
| 2 | Official Unicode 17 GraphemeBreakTest corpus | Expected segmentation independent of the former managed port; classify terminal-specific VT/FEFF adaptations separately |
| 3 | Whole input, every single split position, and adversarial multi-part/page partitions for RI, Indic, Hangul, Prepend, combining, ZWJ and CRLF, including encoded-byte and surrogate-pair splits | Identical semantic units and ordered observations; a receive/page end never finalizes a cluster |
| 4 | Narrow, W/F and Ambiguous bases, with profile widths 1 and 2 | Ordinary maximum-base width, independent of UI language/font/OS Unicode data; do not sum base widths |
| 5 | RGI sets, listed MQ/UQ multi-scalar forms and supported emoji-style pairs | Two cells with original scalars preserved; include a valid variation pair outside an RGI-only lookup |
| 6 | Bare text-default symbol, its VS16 form, and text variation after VS16 | Ordinary width, two cells and ordinary width respectively; preserve VS15 priority within the retained spelling |
| 7 | Allowed trailing decoration, interior marks, repeated selectors and later structure | Strip only Q22's maximal permitted suffix for lookup; reevaluate the retained spelling through scalar 256; omitted suffixes do not change geometry |
| 8 | Unsupported ZWJ/modifier/tag/flag forms and isolated non-supported components | Complete-cluster ordinary fallback, which may be one or two cells; no recognized-substring shortcut |
| 9 | Recognized prefix becomes unsupported, then a complete supported sequence | Observe all shrink/growth transitions, including within one chunk; final width alone is insufficient at the right edge |
| 10 | 255, 256, 257 and much longer same-cluster continuations, including page/chunk crossings and supplementary scalars | Store exactly the first 256; continue segmentation without disconnect, artificial split or unbounded storage; preserve later independent clusters; UTF-16 units are not scalar counts |
| 11 | Unpaired surrogate or invalid complete input on the last analysis page | No successful partial plan, silent replacement, payload, live encoder change or echo |
| 12 | Initial UTF-8 signature per source, interior/repeated FEFF and isolated marks | Existing decoder/zero-cell/drawing-only behavior; aid excluded from text/copy/logs and offsets |
| 13 | DATA/stderr/local echo, VT cutoff, independent REP/grid/Text tails and saturation | Preserve provenance and independent break state; no whole-source early return or duplicate prefix; REP replays its retained spelling |
| 14 | Right-edge width growth/shrink, wrap on/off, whole-cluster erase and resize/reflow | Correct complete placement, revisions, retained anchors and recording content under all relevant chunk splits |
| 15 | Serialized Text continuation, retirement, TAB fidelity and concatenation of stored spans | Independent bounded position; normal Text uses retained spelling, alternate Text/Terminal/Raw preserve their eligible original input; no invented overflow or TAB without proven faithful expansion |
| 16 | Same complete finalized paste without a live terminal and in preview/send paths | Identical counts and semantic units; no captured live-session state in shared analysis |
| 17 | Source exceeds its captured maximum, target's final-page unencodable suffix or unsuccessful reservation | Whole pre-send refusal; broadcast sends no payload/start marker to any target when one required preparation fails |
| 18 | Shared paste sent to targets with different encodings, Ambiguous width and bracket modes | Shared segmentation but target-specific bytes, closure, stopping and echo; source limit is checked before target conversion, with no separate encoded-payload cap; application/target admission remains required |
| 19 | Reconnect, authority/mode/encoder-basis changes and reservation revalidation | No stale send or generation redirection; own reservations neither double charged nor spuriously invalidated |
| 20 | Input begins with a continuation that extends an existing local-echo tail | Target-neutral analysis is insufficient; existing target operation admission/echo failure rules still apply without post-send rollback claims |
| 21 | Stop within short/long graphemes, CRLF, multibyte/stateful units; partial/uncertain failure and cancellation | Stop at a complete scalar/required indivisible codec-control unit without splitting CRLF; definite submitted units alone advance echo, completed graphemes alone advance pacing; healthy reserved cleanup and no replay |
| 22 | VT-separated adjacent text, FEFF, wide cells and generated separators in snapshots | Authoritative exported spans and explicit offset units; UIA never reconstructs segmentation from concatenated text |
| 23 | Mutation/disposal after capture; mix metadata from another capture/generation/projection | Original owned result stays readable; mixed or stale live use cannot pass validation |
| 24 | Parked normal history, active alternate screen, pure reflow, eviction and edit-then-restore | Correct projection; UIA collapse and AI error/validation policies remain distinct; unchanged prefix survives unrelated suffix append |
| 25 | AI response too small for the next maximum supported cluster; UIA full-result refusal | Existing resource/provider failure, no split/truncation, false progress or empty continuation loop |
| 26 | Admitted paste beyond the former 1 MiB encoded limit; default 512 MiB/configured source-size boundaries; ASCII, Japanese, supplementary and legacy/stateful data; capture/compaction overlap | Measure actual UTF-16 source bytes correctly; one shared source/compact plan under the 4 GiB envelope and bounded target work in the existing 2 MiB input allowance; source eligibility alone does not prove capacity |
| 27 | Multiple windows, shared broadcast source, real copies, delayed release and old-generation completion | Shared allocation counted once, copies separately, all retained until actual release; aggregate refusal of new work cannot reclaim committed send/closure reservations |
| 28 | Admission rejection and unexpected execution failure with prior Raw/log admission | Preserve prior outcomes; rejection has no new native prefix effects, later failure is not falsely represented as rollback |
| 29 | Existing WPF/UIA, paste, recording, history, transport and responsiveness integration workloads | One production Rust rule authority, existing behavior plus accepted Q22/Q24-Q27 changes and restored Q26; measured performance/overhead rather than assumed gains |
| 30 | Source below/equal/above 1,024 bytes; CR/LF independently present; configured maximum <=1 KiB | For otherwise valid text within its captured maximum, confirm exactly once iff CR/LF exists or source bytes >1,024; reject over-limit text under row 17; ordinary pacing and Remember transitions also apply to size-confirmed newline-free text |
| 31 | Ordinary input/automatic replies and target paste work compete for existing input capacity | Enforce one 2 MiB/256-unit allowance with existing reply/closure ordering; do not add a second paste pool or charge the shared whole source against every target |
| 32 | Huge single line or grapheme in preview | All immutable text remains accessible through bounded scalar-safe pages; no whole-text visual copy, preview omission, surrogate split or page-derived send payload |
| 33 | Existing broadcast deadline expires during final target validation or just before commitment | Recheck deadline after final work; no target payload/start marker, no deadline reset, dialog time excluded |
| 34 | Heart + VS16 + 254 acute accents + VS15, compared with 253 accents | Omitted scalar 257 cannot shrink stored width; retained VS15 at scalar 256 does shrink it under Q22 |
| 35 | Woman + 254 acute accents + ZWJ + laptop + grinning face | Omitted laptop still advances break state; grinning face starts a new stored span, and the retained ZWJ cannot absorb it during reflow/observation |
| 36 | Giant outgoing grapheme and local-echo storage saturation | Full successful payload remains exact while local display retains its prefix; no 256-scalar input rejection or extra chunk pacing delay |
| 37 | Terminal state and paste ownership approach the shared 4 GiB application envelope; multiple windows and shared versus real copied storage | Account terminal state plus owned paste capacities/reservations without duplicate shared-source charges; unsuccessful paste admission sends nothing and does not evict retained output or end unrelated sessions |
| 38 | Capture/copy overlap, later terminal growth, competing work and cancellation with delayed cleanup | Retain charged source/work and held send/closure reservations until actual release; terminal mutations still undergo admission; no uncharged overlap, lifetime gap or whole-process memory guarantee |
| 39 | Old settings, positive/invalid byte values, Save/Cancel/failure and limit changes during capture/preview/send | Default missing maxPasteTextBytes to 536870912; retain each operation's captured limit; no truncation/cancellation from later settings changes and no aggregate setting |

Private Rust type layout, index compression, internal page sizes, cache strategy,
generated-data encoding and exact ABI field layout remain implementation choices.
They cannot change the accepted rule set, introduce a caller-driven scan recipe,
weaken validation/ownership, lower supported limits or select a managed fallback.

### Size and long-cluster review requested after Q23

The user raised both concerns: rejecting large pastes and ending a session
because received content grows beyond a per-cluster bound. They then explicitly
requested Tera Term and RLogin as references for grapheme handling. Q23 was put
on hold at that stage; the request itself did not select revised numbers, permit
text loss, change Unicode data versions or authorize implementation. The later
Q23 acceptance below closes this review after the amended decisions.

Keep the units clear:

- The existing 1 MiB limit covers one target's complete encoded paste, including
  encoder finalization and excluding bracketed-paste markers. Accepted Q78/Q79
  call it an initial product limit, centrally adjustable, not a measured
  throughput guarantee. See [the original paste decision](terminal-interaction-issue.md#payload-validation-and-size-q78-and-q79-accepted).
- The existing 256 limit counts Unicode scalars within one grapheme cluster,
  not the length of a line or whole paste. Ordinary text containing thousands
  of separate characters does not hit that bound just by being long. The
  [resource specification](../specification/resources-and-validation.md#initial-internal-budgets)
  describes this number as an initial engineering guard, not a measured limit.
- Neither number is a mandatory Unicode/transport limit. In particular,
  [UAX29 revision 47's grapheme rules](https://www.unicode.org/reports/tr29/tr29-47.html#Grapheme_Cluster_Boundary_Rules)
  allow continued combining sequences without prescribing a 256-scalar maximum.
  The inference is that a TabTty maximum and its recovery behavior require an
  explicit resource/product policy.

The design review must address actual constraints rather than only increasing
the two constants:

| Concern | Design obligation before a revised proposal is final |
| --- | --- |
| Large total paste | Separate immutable operation-input retention from bounded per-target encoding/transmit work. The present 2 MiB aggregate includes retained input; chunking alone does not permit arbitrary larger payloads. Preserve one immutable text snapshot, whole-input/all-target preflight, progress and cancellation. |
| Received long cluster | Inspect reference terminals' real handling of continuation, storage, width and rendering, including overflow. Distinguish preserving original content from keeping a bounded visual representation. Avoid repeated copying/rescanning of the entire growing prefix. |
| True resource exhaustion | Removing a scalar-count disconnect rule cannot promise infinite storage. Retain explicit actual-resource accounting, recoverable backpressure and an outcome for unresolvable failures. |
| Stop within a very long unit | Current stopping waits for a complete encoded/grapheme boundary. Allowing much larger clusters affects Stop latency; scalar/codec-safe interruption would be an explicit change to that contract. |
| Bounded observations and logs | A cluster may exceed an individual response/work page. Do not silently split logical content, truncate copy/log text or fail a healthy session because one observation cannot fit. Preserve or explicitly revise the narrow response/file-output contracts. |
| Broadcast preparation time | The existing 30-second preparation deadline remains a separate constraint; removing a size cap does not silently remove that deadline. |

Reference research compares exact versions, combining/ZWJ/variation handling,
cell assignment, receive-chunk continuity, content/copy/log fidelity and actual
overflow behavior. Source observations and unverified runtime assumptions must
remain separate. Referencing those products does not adopt their code or
dependencies, or silently replace TabTty's accepted Q22 cell policy.

### Tera Term and RLogin comparison for the reopened limits

The requested primary-source review is recorded in
[Tera Term findings](teraterm-grapheme-reference.md) and
[RLogin findings](rlogin-grapheme-reference.md), with source links for every
claim. It used Tera Term 5.7.1 (`ae000ef3ab4eeda0d078a430465fbf4347c15e45`)
and RLogin 2.31.9 (`66506599f3baa9f1e916b1268a8a659738dbb30a`). No executable,
prototype, runtime test or benchmark was run.

| Question | Tera Term 5.7.1 | RLogin 2.31.9 |
| --- | --- | --- |
| Stored additions | Separate combination buffers, excluding the base: 100 UTF-32 elements and 100 UTF-16 code units | At most 15 UTF-16 code units total, including the base and excluding the terminator |
| Overflow path | Silently skips additional storage when a buffer is full | Append emits a debug trace and returns without the additional text; caller continues |
| Connection effect of that path | No cluster-capacity disconnect in the inspected append path | No cluster-capacity disconnect in the inspected append/caller path |
| Copy/retained content | Clipboard text comes from retained combination buffers, so omitted additions are absent | Clipboard and line-recording text come from retained cell strings, so omitted additions are absent |
| Other recording paths | Text-log character input and binary-log bytes are separate from the display buffer | Character/control and raw logging have paths separate from retained line recording |
| Grapheme/width mechanics | Combining classification, ZWJ and virama handling plus configurable width choices | NSM/VS, modifier, ZWJ/ZWNJ and conditional normalization handling; one/two-cell selection |
| Complete UAX29 equivalence | Not established by these specialized rules or this static review | Not established by these specialized rules or this static review |
| Ordinary paste path inspected | No 1 MiB admission guard found in the selected clipboard/send path | No 1 MiB rejection in the selected paste routines; the 500-character value triggers confirmation rather than rejection |

The references separate continued connection processing from bounded cell text,
but do so partly through lost retained content. They do not establish lossless
support for arbitrary clusters or unlimited pastes. Their narrower storage
limits are not reasons to adopt those numbers in TabTty. Their grouping,
normalization and width choices also do not supersede accepted Q22 or TabTty's
unchanged-text requirement. Log-path findings describe supplied characters/bytes,
not successful durable file output or equivalence to TabTty's log formats.

### Q24 - Large-paste direction: accepted

**Accepted on 2026-11-18:** remove the fixed 1 MiB encoded-payload product rejection
and make paste admission depend on explicitly accounted resources and complete
validation. Treat one paste as one immutable input operation, traversed in
bounded internal pieces; do not silently turn it into independent smaller
pastes with separate confirmation, framing or validation.
Q26 subsequently adds the user-configurable source-size maximum, default
512 MiB per paste. The user's latest clarification retains that limit and the
size-triggered confirmation while restoring the original shared application/
per-target accounting. The old 1 MiB encoded-payload rejection is not restored.

This requires an explicit change to the current input accounting split:

- The operation owns immutable source retention and shared analysis, with
  bounded operation/application admission and actual lifetime accounting.
  Source capture and any compaction overlap belong in that admission.
- Each target owns bounded encoder/preflight/transmit work and reserved closure
  capacity. The current 2 MiB working allowance cannot also secretly retain the
  whole arbitrarily larger source. Charge shared physical storage once and
  retain references until actual release; document per-target work admission
  without making a pointer an uncharged source-storage loophole.
- Preflight the entire immutable input for every frozen target before sending
  any payload or start marker. Then traverse that same input to feed bounded
  target queues, preserving pacing, progress, Stop and no replay.
- Actual resource refusal is still a pre-send whole-operation rejection. This
  direction is not an unlimited-memory promise or an initial UI setting to
  disable all resource guards. Start with explicitly admitted memory retention;
  automatic temporary-file spooling is not included in this recommendation.

The exact retention/accounting limits and the existing 30-second broadcast
preparation deadline need a coherent follow-on contract before final readiness.
Q24 does not silently remove that deadline or establish a tested maximum size.
Do not substitute another unexplained fixed product-size constant for 1 MiB.

### Q25 - Long-grapheme direction: accepted with a simplicity condition

The user accepted the direction on 2026-11-18 but explicitly allowed handling
like the reference terminals if complete preservation would complicate the
specification or implementation. This condition authorizes choosing the bounded
omission alternative; it does not require asking the same permission again.

**Selected adaptation:** retain at most the first 256 Unicode scalars of each
stored cluster and consume further continuations without ending the session.
Keep 256 as the existing internal storage guard rather than replacing it with
the references' smaller UTF-16-based values. This is not a limit on ordinary
line length or total paste length. The previous full-content storage plus
separate rendering-fallback proposal is not selected.

Full preservation would require variable-sized retained clusters and source
REP spellings, oversized snapshot/history units, larger shaping fallbacks and
additional ownership through observations and recording effects. The bounded
choice retains the existing storage/observation scale while keeping connection
processing independent of storage saturation. It does not adopt reference
source, dependencies, normalization or their full grouping/width policies.

#### Stored prefix and complete-stream segmentation

The shared Rust module separates a bounded retained prefix from a small
incremental break state. Every eligible scalar, including an omitted one,
advances that break state. Retaining only the prefix and repeatedly asking
`joins(prefix, next)` is insufficient: a saved trailing ZWJ or Indic context
can otherwise cause unrelated later text to disappear.

Within a continuing cluster, preserve the first 256 scalars exactly, omit
only subsequent scalars from that owner's stored spelling, and begin a new
stored cluster at the next real segmentation break or existing VT eligibility
cutoff. Do not manufacture a break at scalar 257, split a surrogate pair, insert
a replacement/ellipsis into stored text, or queue omitted text for later replay.
The incremental state needs the rule-relevant categories and suffix context,
not an unbounded original-text copy or an unbounded omission-event list.

Source-local last-graphic/REP state and each projection's editable-grid tail
remain independent. One owner's saturation must not return early from the whole
source operation: another projection may start a new stored cluster, recording
may still admit the original scalar, and eligibility/provenance must still be
processed. Estimate and Execute make the same decisions from their independent
states. Existing source isolation, controls, FEFF handling and recording-interval
rules remain; omitted content cannot leak into a later interval as backfill.

#### Width and observable content

For a stored cluster, Q22's recognition, width and drawing-aid rules use its
retained prefix. Reevaluate through scalar 256; subsequent omitted scalars do
not change that stored cluster's cells or presentation attributes. An omitted
VS15/VS16 or ZWJ suffix therefore does not change its geometry. This is the
explicit narrow exception to Q22's earlier complete-original-sequence
reevaluation promise, authorized by the user-permitted storage omission.
It keeps the stored text and its width basis together. Segmentation still uses
the complete incoming continuation context, independently of that width choice.

Use authoritative stored spans for reflow, selection, UIA and history traversal.
Concatenating stored prefixes can expose scalar adjacencies that were not actual
incoming grapheme boundaries. Do not re-segment that concatenation to redefine
the stored spans or insert invisible separators merely to make it self-describing.
External clipboard text contains the retained original scalars only.

| Consumer or projection | Consequence of storage saturation |
| --- | --- |
| Screen, scrollback, copy, UIA, screen/history observation and search | Use retained text and authoritative spans; omitted continuations are absent |
| Normal-screen Text recording | Uses the receive-only projection's retained edited text, so its omitted continuations are absent |
| Alternate-screen Text recording | Continues to receive original eligible incoming scalars under its existing streaming contract; grid-storage omission does not remove them |
| Terminal recording | Continues to receive original eligible graphic/local-echo operations under its existing canonical-stream contract; grid-storage omission does not remove them |
| Raw recording | Continues to admit original terminal-payload bytes before decoding under the existing interval/I/O policy |
| REP | Replays the source-local retained spelling, at most 256 scalars, through the existing resolved-text path; it cannot reconstruct an omitted suffix |
| Outgoing paste | Must not reuse a display prefix as its payload or silently discard input; its large-single-grapheme handling is resolved separately in Q27 |
| Local-echo display | Uses the same retained-prefix policy even when the corresponding submitted input was longer; existing receive-only Raw/Text exclusions remain |

Do not describe all Text recording as cell-derived: the normal and alternate
screen contracts differ. Current source evidence is `model.rs:1543-1577`, where
`operation_scalar` and alternate `text_scalar` run before grid insertion, and
`model.rs:1465-1476`, where REP uses the source spelling. These are ownership
evidence, not runtime verification of the proposed behavior.

The serialized Text-position owner must not become a text-discarding mechanism.
Keep bounded position reasoning and the existing rule that a TAB is emitted
only when its serialized expansion is proven faithful; otherwise use its final
spaces/text. An internal unknown position does not erase already emitted text
or justify session failure. Do not copy a grid tail or introduce a full-text
archive merely for this calculation.

This specific storage omission is normal continued processing. It does not set
the existing fatal terminal/incomplete status or fail the recording group,
which would terminate or stop work contrary to Q25. No per-scalar dialog,
warning stream or new notification UI is added. Actual resource exhaustion,
transport/I/O failure and narrow observation refusal retain their existing
policies. UIA's full-result promise remains about the retained text; it does
not authorize additional truncation during a provider call. AI observations
can retain their capacity for a stored span of 256 scalars/1024 UTF-8 bytes.

#### Required replacement verification for Q25

The original Q23 proposal's length-triggered failure rows are superseded;
the revised matrix above incorporates the following required cases:

- 255/256/257 and much longer same-cluster continuations, including supplementary
  scalars and every relevant chunk split: retain exactly the first 256 without
  disconnect, split/drop of later independent clusters or an unbounded buffer.
- Heart + VS16 + 254 combining acute accents + VS15: VS15 is scalar 257 and is
  omitted from the stored spelling, which remains two cells. With 253 accents,
  VS15 is retained at scalar 256 and selects ordinary width under Q22.
- Woman + 254 combining acute accents + ZWJ + laptop + grinning face: laptop
  is the omitted 257th continuation; grinning face starts a new cluster based
  on the real incoming tail. The retained prefix's trailing ZWJ must not absorb
  that next cluster. Reflow/UIA/history preserve those authoritative spans.
- Saturation around Indic, Prepend, Hangul, RI and VT continuation cutoffs;
  interleaved DATA/stderr/local echo; independent source/grid/recording tails;
  REP of a capped spelling; identical Estimate/Execute classification.
- Normal Text/copy/history use the capped spelling while Raw, Terminal and
  alternate Text retain their respective original eligible input. No hidden
  whole-operation early return, prefix replay or recording-failure notification.

This keeps the module's depth and locality: ordinary callers still use C's
complete operations and owned observations. Neither a new public overflow
workflow nor a configurable Unicode-provider adapter is introduced.

### Candidate 6 decision frontier: closed

Q24 is accepted with the restored original Q26. Q25 is accepted with explicit
simplification authority, exercised by the bounded-retention adaptation above.
Q27 is accepted; the dependent Q28/Q29 branch is superseded. The user accepted
Q23's amended complete contract and verification plan on 2026-11-18, completing
candidate 6's design review. The earlier full-preservation proposal and
length-triggered receive failure are superseded;
unchanged portions of Q20-Q22 remain. No implementation, prototype or runtime/
conformance test execution is authorized.

### Q26 - Original shared-budget recommendation: restored and accepted

The user initially rejected this recommendation, then explicitly replaced their
Q26 answer with it on 2026-11-18: share the original once, process target
conversion/transmission within the existing 2 MiB allowance and retain the
30-second broadcast preparation deadline. The user then clarified that the
512 MiB per-paste setting and >1 KiB confirmation remain in force. This restored
accounting and the retained product rules together form the current policy;
only the independent Q28/Q29 aggregate guard is superseded.

**Accepted recommendation:** explicitly broaden the existing 4 GiB application accounting
envelope to cover terminal state plus admitted paste-owned storage/work. Keep
the 2 GiB per-session terminal-state sublimit and the 2 MiB/256-unit per-target
input-work allowance. This accounting adds no new arbitrary size constant;
the separately retained 512 MiB default source cap is user-configurable and is
not replaced by these budgets. Accounting is not a whole-process working-set guarantee.
The old 4 GiB scope was terminal state only; the broader scope is now an explicit
accepted contract change rather than an implicit consequence of Q24.

| Ownership/accounting scope | Accounting charge |
| --- | --- |
| Input operation | Exact immutable source, common analysis/index capacity, preview/capture storage, frozen target metadata, source compaction/copy overlap and operation work until actual release |
| Application 4 GiB envelope | Terminal state plus the operation storage above and actual private paste work/copies across targets; genuinely shared allocations count once, not once per target |
| Per-session 2 GiB terminal-state bound | Existing combined/receive-only models, retained disconnected output, cluster/provenance/index state and other existing terminal-state charges |
| Per-target 2 MiB input-work/256 queue units | Private conversion and preflight workspace, owned queue/in-flight send buffers, result metadata and the existing treatment of reserved codec/bracket closure capacity; the count limits concurrent owned units rather than all chunks in a paste |
| Other existing resource budgets | Receive queues, recording, AI/UIA and presentation keep their own limits/owners; this is not a new claim that every process allocation is covered by 4 GiB |

The application reservation is real ownership for the shared source; a target
reference does not create an uncharged input store. Conversely, do not charge
the entire shared source again against every target's 2 MiB working allowance,
which would reinstate a small total-paste limit by another name. A transfer
between accounting owners keeps the actual allocation charged without double
counting or a lifetime gap.

Validate capacity before proportional owned allocation/growth where possible,
including initial capture and native/managed overlap. Reject/defer new paste
work when the application envelope cannot admit it; never discard retained
terminal output, shorten saved retention or end an unrelated session to make
room. Holding a confirmed paste does not exempt subsequent terminal mutations
from their existing resource admission. Actual OS allocation failure keeps its
existing handling and is not promised recoverable by accounting alone.

Rust preflights the whole input through isolated encoder state for every frozen
target, including strict conversion and encoder finalization, but does not
retain a full encoded copy for every target. After successful preparation,
bounded rescanning of the same immutable source feeds each target's send owner.
No payload or bracket start marker is submitted until every required target
passes its complete preflight and reservation checks. Recheck generation,
authority and the relevant encoder/mode basis before commitment; do not hold
multiple session locks while awaiting capacity. Release/retry partial target
reservations under the original deadline instead of waiting in a cycle.

Preparing, sending and cancellation remain observable through the existing
operation interface. Work yields in the existing bounded scheduling slices;
Cancel remains available during capture/preview, preparation, capacity waits
and pacing. Stop, expiry, window movement or a cancelled awaiter does not imply
actual worker/buffer release. No automatic temporary-file spooling is added.

**Keep the existing 30-second broadcast preparation deadline.** This is an
existing bound, not a new universal deadline for every paste. Preparation or
reservation timeout before commitment leaves all targets not sent and releases
private work as it actually completes. Do not restart the deadline on retry.
Profile paste-write waits and user-selected pacing retain their existing rules.
Removing 1 MiB thus permits larger accepted input but promises neither unlimited
memory nor arbitrary-duration preparation. A future request for progress-based
deadline extension would require its own explicit contract change.

Future verification must include an admitted payload beyond 1 MiB, multiple
targets sharing one source, a failed last-target preflight, genuine aggregate
pressure, source-capture/compaction overlap, delayed cancellation/release,
original-deadline exhaustion and accounting without duplicated full encoded
copies. Verify actual supported workloads after implementation authorization;
the contract does not establish measured capacity or performance.

### Q27 - Exact input and responsive Stop: accepted

Q25's storage omission does not authorize altering outbound clipboard text.
The user accepted exact input with a safe Stop point inside a grapheme regardless
of its length. Use one Stop rule rather than
a special length threshold for an exceptionally long grapheme.

**Accepted:** remove the 256-scalar input rejection as well. Scan the
already owned immutable input with bounded continuation context and offsets;
do not collect each entire grapheme into a new temporary buffer. Preserve
malformed-Unicode, forbidden-control, exact-encoding, marker and capacity
validation. Normal successful sending transmits all original input scalars;
the display/REP prefix is never a send payload. This does not require another
unbounded terminal archive because Q24 already owns the complete input.

Segment encoding/transmission into bounded pieces at known complete Unicode
scalar/indivisible codec-unit and encoding-control boundaries, even within a
long grapheme. Preserve CRLF's existing indivisible input/timing treatment.
Normal character/line pacing still follows completed graphemes and newline
units: an internal chunk end introduces no extra pacing delay or character count.

**For explicit Stop only, do not wait for the whole current grapheme to end,
regardless of its length.**
Stop at the next known complete scalar or required larger indivisible codec/
control unit, discard the unsent input and perform the existing healthy encoder
and started-bracket closure. A stopped paste can therefore leave the remote end
with a prefix of its last grapheme; it does not leave a deliberately cut encoded
character/control sequence. Do not split CRLF or invent an extra newline. This
is an explicit exception to the old complete-grapheme stopping requirement,
not permission for ordinary successful paste to lose text.

Commit encoder progress and local echo only for definitely submitted complete
encoding units under the existing transport outcome contract. Bounded progress
can distinguish a submitted prefix from a completed grapheme; it must not count
an incomplete grapheme as a completed pacing unit merely because a buffer was
written. Unsent scalars have no echo. Local-echo storage independently applies
Q25, and its retained-prefix limit is not a transport truncation.

Stop/cancellation can interrupt a pacing wait or preparation promptly; it cannot
guarantee immediate completion of already issued OS/transport work. Partial or
uncertain submission keeps existing failure/no-replay behavior. Only a known
healthy current encoder/transport state permits normal closure; a broken send
does not fabricate successful cleanup. Additional interactive input and queued
automatic replies retain their existing ordering relative to paste cleanup.

Future verification includes a single grapheme exceeding 256 scalars and the
input-work chunk size, an unchanged exact successful payload, Stop within that
grapheme and within a short base-plus-mark or emoji sequence, chunk splits
inside multibyte encodings/stateful control units, CRLF,
unchanged pacing, partial/uncertain writes, definitely submitted echo and local
display omission independent of transmission. Invalid final-page input must
still cause whole-operation pre-send refusal.

### Current decision frontier: closed

Q27 is accepted and the original Q26 shared-budget recommendation is restored,
with the user-clarified source-size setting and size-confirmation rules retained
below. Q28/Q29 preserve superseded independent-budget history. The accepted
revised Q23 contract above reflects this combination. All six candidate design
reviews are complete. No implementation, prototype or runtime test is authorized.

### Q26 - Retained clipboard-size limit and confirmation

After restoring Q26's original resource-accounting recommendation, the user
explicitly clarified that the 512 MiB setting and >1 KiB confirmation remain.
This corrects the prior interpretation that the entire replacement answer had
been withdrawn. Only its exclusion from existing budgets and dependent Q28/Q29
aggregate guard are superseded.

The user specified these retained rules and explicitly confirmed that the
512 MiB setting is a limit for **one paste**, not the sum of simultaneous pastes:

- Share source/common storage once under the broadened existing 4 GiB application
  envelope; target work shares the existing 2 MiB/256-unit input allowance.
- The default maximum clipboard-text size per paste is 512 MiB (536,870,912
  bytes), and the user can change it in application settings.
- Show the same confirmation as a newline-containing paste whenever clipboard
  text exceeds 1 KiB (1,024 bytes), even if it contains no newline.
- Measure the text before converting it to a target session's encoding.

#### Exact size meaning and settings behavior

For this contract, clipboard-text size is the actual Unicode text selected for
paste, measured as two bytes per UTF-16 code unit. Exclude the terminating NUL,
allocation padding, clipboard metadata and other representations such as HTML,
RTF or images. Supplementary scalars use two UTF-16 code units. Preserve actual
CR/LF spelling and count it as part of the text; do not normalize before counting.
This is a defined text-data measure, not the size of every format simultaneously
offered by the clipboard and not the producer's original file size.

Windows' Unicode clipboard text is UTF-16, and its standard format has a NUL
terminator. A global memory object's allocated size can exceed its requested
size. These facts support using actual text length rather than `GlobalSize`
as the product size. Sources: [UnicodeText format](https://learn.microsoft.com/en-us/dotnet/api/system.windows.clipboard.settext?view=windowsdesktop-10.0),
[standard clipboard formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats),
[GlobalSize](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-globalsize).

Apply the same size measure to both the configurable maximum and the fixed
confirmation threshold. Size equal to the configured maximum is admissible
subject to normal validation and resource availability; greater size rejects
the whole paste. Do not trim it to fit. An encoded target payload may be larger
than the source limit after conversion; do not introduce a second 512 MiB
encoded-output rejection. Checked byte-count arithmetic and whole-input strict
encoding/finalization validation remain required for every target.

The setting is application-wide, persistent and independent of connection
profiles, unlike remembered paste delays. Use the existing settings Save/Cancel
and validated persistence behavior. Canonical consolidation names its optional
`settings.json` field `maxPasteTextBytes`. An absent field in an older supported
settings file resolves to 512 MiB. Persist a positive finite whole-byte count
with checked representation; zero/negative/overflowing values are invalid, not
an undocumented unlimited mode. UI unit presentation does not change that byte
value or silently round it downward.

A successful settings save affects subsequently started paste operations.
Each existing capture/preview/send retains the limit captured when that
operation started, just as it retains its immutable source and frozen targets.
Saving a lower limit cannot truncate or cancel an already confirmed paste;
Cancel or failed settings persistence changes no effective value.

#### Confirmation, preview and pacing

For otherwise valid text within the captured source maximum, use exactly one
required confirmation when either condition is true:

```text
contains CR or LF OR clipboard_text_bytes > 1024
```

Both conditions together still show one confirmation, with the same Send/Cancel
actions, Cancel default, read-only preview, counts, ending-newline indication,
pacing controls and remembered-delay rules. Broadcast shows the frozen recipient
list once. No persistent bypass is added. Invalid or over-limit input is refused
without sending any payload/start marker; a dialog cannot approve a different
text snapshot from the one later validated and sent.

| Text | Size-triggered confirmation | Overall outcome if otherwise valid |
| --- | --- | --- |
| No newline, 512 BMP code points / 1,024 UTF-16 bytes | No | Existing direct path |
| No newline, 513 BMP code points / 1,026 UTF-16 bytes | Yes | Same required confirmation as newline paste |
| No newline, 256 supplementary scalars / 1,024 UTF-16 bytes | No | Existing direct path; this is not a 256-scalar input limit |
| Any CR or LF, including a trailing newline | Independent of size | Required confirmation |
| Exactly the captured configured maximum | Apply the ordinary size criterion | Apply the ordinary confirmation predicate; full validation before sending |
| Greater than the captured configured maximum | Not a way to override refusal | Whole-operation rejection; no prefix send |

The old rule "no newline always means direct paste" is superseded. For a
size-confirmed paste without newlines, character pacing and Remember these
delays have the same Send/Cancel transitions as any other confirmed paste;
line delay simply has no events. A small direct paste still uses the remembered
character delay, if enabled, and does not change remembered values. Delay memory
remains application-run-only and is not persisted with the size setting.

The existing source feeds a complete string to one WPF TextBox and performs
clipboard retrieval/analysis synchronously before the first await
(`TerminalTab.cs:1028,1091-1110`, `TerminalPaste.cs:21`). Increasing its numeric
limit alone does not establish responsive 512 MiB preview behavior. Preserve
the same confirmation workflow and access to the whole immutable text, but
use bounded read-only preview pages/ranges rather than forcing all text into
one visual control. A huge line or single grapheme must not defeat the display
page bound. Presentation paging may show continuation fragments at Unicode
scalar boundaries without splitting a surrogate pair, clearly tied to their
source range; it does not insert content, apply Q25's terminal-storage
omission to the input, or turn the preview page into the send payload.

Keep analysis/counting and target preflight cancellable in bounded work slices.
The clipboard provider may require delayed rendering while data is retrieved;
do not promise its immediate cancellation or that its size is known before the
provider supplies the format. Retain bounded ownership of late results and
capture work, without repeated detached workers on cancellation.

#### Shared resources and practical limits

A 512 MiB UTF-16 text payload already requires approximately 512 MiB for one
uncompressed copy. Clipboard capture, duplicated text, preview/index storage,
native/managed overlap and per-target workspace can add to that. Multiple
operations in different windows can coexist. The per-paste text setting is
therefore neither a total process-memory limit nor a promise that every
machine can accept a paste at that size. No peak-memory multiplier or runtime
throughput has been measured in this documentation work.

Use restored Q26's existing 4 GiB application envelope for terminal state plus
shared paste source/common analysis/preview and actual private work/copies.
Each target's conversion/preflight/queued/in-flight work shares its existing
2 MiB/256-unit allowance with ordinary input; no second paste allowance is added.
Keep the existing reserved codec/bracket closure capacity and real peak
ownership until actual release, including cancellation and capture overlap.
The whole shared source/common plan is not charged again against each target's
2 MiB allowance. Do not retain a full encoded payload for each broadcast target.

Preserve one session writer and reserve remaining bounded send/Stop/closure
work before first submission. The independent Q28/Q29 aggregate setting and
available-memory percentage guard remain superseded. The source-size setting
does not enlarge the shared envelope or promise admission at its maximum.
Multiple windows, pending captures and late cleanup still share that envelope;
modal confirmation does not prove that only one paste's memory exists.

Keep the existing 30-second broadcast preparation deadline after confirmation;
when confirmation is not required, start it when preparation begins. Do not
count the user's time in the dialog as preparation time. Check expiry
and cancellation during work and immediately before the first-send commitment,
including after the final target's conversion. A 512 MiB configured source
limit is not proof that all targets can be validated within this deadline.
Timeout before commitment leaves every target not sent; retries do not restart
the deadline. Q27 continues to govern Stop after sending begins.

#### Verification additions

Future checks must cover source sizes below/equal/above 1 KiB and the configured
maximum; BMP and supplementary text; NUL/padding/other clipboard formats;
unchanged CR/LF; invalid UTF-16/control rejection; converted output larger than
the source limit; all confirmation/pacing/Remember transitions; old settings
default and Save/Cancel/failure; in-flight limit changes; bounded complete-text
preview including a huge line/grapheme; multiple windows and physically shared
broadcast input; capture/preview/cancellation overlap; and deadline expiry in
the final preflight work immediately before first submission. These are required
future observations, not executed tests.

### Q28 - Independent aggregate paste resources: superseded

The user previously accepted the independent aggregate guard on 2026-11-18.
It depended on excluding paste from existing budgets and is superseded by the
latest restoration of Q26. The following paragraphs record that prior branch.
Use a separately accounted cap across all windows so simultaneous maximum-size
operations cannot grow application-owned paste storage without a defined
ceiling. Refuse new work that cannot fit without terminating existing sessions
or truncating an accepted paste. This does not place paste inside the old
budgets or invent an undisclosed second per-paste cap.

Q28 did not itself settle the numeric/default/settings policy. Amended Q29
subsequently selected automatic admission and retained the complete reservation
policy below. At that stage, Q23's final review was still pending. The later Q26
restoration superseded this branch, and Q23 is now accepted under the restored
policy. This interview authorizes no implementation or runtime testing.

### Q29 - Original aggregate settings proposal: superseded

The user provisionally accepted this recommendation, while suggesting that the
aggregate could instead be automatic at 80% of currently available memory with
no user setting. The user then accepted the amended Q29 below. The following
saved-setting/default paragraphs record the superseded provisional baseline;
its ownership and pre-send reservation obligations remain under amended Q29.

**Original recommendation:** use a default aggregate paste accounting limit of 2 GiB
(2,147,483,648 bytes), independently user-configurable in application settings.
This default was introduced in Q29, not implied by Q28.

Two default-maximum 512 MiB source texts occupy 1 GiB before copies or work;
the remaining 1 GiB provides initial headroom for capture overlap, common
analysis, preview and target workspace. This is an engineering starting point,
not a measured peak, a two-paste concurrency guarantee or a whole-process
memory target. Real copies and the frozen target count determine admission.
The existing background-receive 1 GB optimization workload and terminal-state
budgets remain independent; neither supplies this proposed paste allowance.

#### Two independent saved settings

Persist the per-paste source limit and aggregate paste accounting limit as
separate application settings. Use the existing validated Save/Cancel flow;
older supported files without the aggregate field resolve to 2 GiB. Both values
are positive finite whole-byte counts with checked representation, no implicit
unlimited sentinel and no unit rounding that changes the stored value.

Do not automatically increase one when the other changes, or require
`aggregate >= source maximum` as a validity rule. Equality would not establish
room for workspace, and existing operations can already hold most of the
aggregate. Explain their different meanings in English/Japanese settings text:
one limits each clipboard text, while the other limits application-owned paste
storage and reserved work across all windows. A source within its individual
limit may still be refused for unavailable aggregate capacity.

The source limit keeps Q26's operation-start capture rule. A saved aggregate
change governs new or additional reservations immediately. It does not revoke
existing reservations: if current charges exceed a newly lowered limit, allow
them to drain and reject any increase until it fits. Consuming an already-held
reservation is not a new increase. Existing sends therefore keep their reserved
workspace; an operation still preparing may fail a new reservation with no
payload sent. Cancel or failed persistence changes neither effective setting.

#### Admission and resource lifetime

Use one application-wide paste ledger behind the existing operation interface.
Charge actual owned allocation capacities plus outstanding future reservations;
consuming a reservation replaces its reserved charge with the allocation's
charge rather than charging both. Make admission atomic across windows and
include transient old/new-copy overlap, owned pool capacity and reserved
closure work. Share source bytes only when ownership is physically shared;
equal text in separate allocations is not sharing.

Charge clipboard capture, immutable source, analysis/index/plan storage,
preview pages, private native/managed copies, conversion/framing/local-echo
intermediates, queued/in-flight chunks and cancelled-but-incomplete resources.
Resulting retained local-echo terminal state still belongs to its existing
terminal-state budget. Common transport buffers and ordinary input retain their
own domains; sharing one session writer does not merge the budgets.

Reserve controlled capacity before allocating/copying into it. Do not complete
an unreserved whole-text capture just to discover its size; the capture adapter
must determine bounds before that copy or acquire text incrementally within
reserved storage. External clipboard-provider rendering may happen before the
text size is available and cannot be promised immediately cancellable. Account
for owned late results until actual release and do not create detached workers
on repeated cancellation. This guard covers controlled application-owned paste
capacities, not every OS/provider, CLR, WPF or allocator-internal allocation;
it is not a strict process working-set ceiling or an OOM recovery guarantee.

During preview, retain the immutable source and bounded preview/analysis work.
After confirmation, validate the entire input for every frozen target using
bounded reusable work. Neither a full encoded copy per target nor a descriptor
per scalar/grapheme is required by the owned-plan interface. Before the first
payload or bracket-start marker, reserve all remaining bounded send, Stop and
healthy closure workspace for every target. The initial per-target working
sublimit is 2 MiB/256 concurrent queue units, with separately reserved closure
capacity; both belong to the aggregate. The source/plan is shared separately
and need not fit the per-target working sublimit.

If the aggregate cannot admit a new or increased reservation, reject that
not-yet-started work without sending a prefix/start marker or closing a session.
Do not add an automatic waiting queue, serialize all application pastes, evict
another paste's source, truncate input or spool it to a temporary file. Partial
preparation reservations release when their owned work actually finishes.
The confirmation's Send action does not guarantee resources or encodability;
first-send commitment requires every target's full validation and reservations.

After commitment, reuse the held workspace; another operation or a later
settings reduction cannot take it away. This protects against aggregate-budget
competition, not independent transport, terminal-state or OS allocation failure.
Stop follows Q27. Cancellation, window closure, a detached observer or an
expired operation does not return capacity while a worker, native result,
in-flight write or inert preview still owns it. Release exactly once at the
actual end of ownership, without attributing a late result to a replacement.

These clipboard settings do not relax unrelated external-request limits.
In particular, AI input retains its existing 256 KiB UTF-8 request limit and
authority checks even where it shares the common input-analysis module.

#### Additional future verification and final frontier

Add independent observations for concurrent admission at the aggregate boundary,
real sharing versus copies, capture/compaction peak overlap, failed reservations
after confirmation, pre-reserved send/closure under competing new pastes, saved
limit reduction below held reservations, growth of a still-preparing operation,
and release delayed by cancellation or detached previews. Exercise bounds
without allocating multi-GiB test data where controlled capacity adapters suffice;
retain actual peak-memory/large-input integration measurements after separately
authorized implementation. No such tests or measurements ran in this interview.

Q29 bundles the remaining numerical and settings policy rather than reopening
Q26's source limit or Q28's independent aggregate guard. The user's subsequent
automatic-management suggestion is considered below before candidate 6's
revised Q23 final review and canonical English/Japanese consolidation. Private
allocation layouts remain implementation details subject to the stated
capacity and complete-operation contracts.

### Superseded Q29 - Automatic aggregate admission

The user previously accepted this revised recommendation on 2026-11-18 after
suggesting 80% of currently available memory instead of another setting. The
latest restoration of original Q26 supersedes this dependent guard; no current
paste admission uses its OS snapshot or 80% formula. The rest of this section
records the former policy: automatic
aggregate admission without an aggregate settings field or an additional fixed
2 GiB cap. Keep Q26's independently configurable 512 MiB default per-paste source
limit, measurement basis and confirmation rule unchanged. This explicit
acceptance replaces the original Q29's provisional saved-aggregate-limit policy.

#### Available memory and the admission rule

On Windows, `GlobalMemoryStatusEx` reports a volatile snapshot: successive calls
need not agree. Its `ullAvailPhys` is immediately reusable physical memory,
including standby, free and zero lists; it is not just unused RAM.
`ullAvailPageFile` is the additional memory the current process can commit,
not simply free bytes in a page-file disk file. See Microsoft's
[GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)
and [MEMORYSTATUSEX](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)
definitions, reviewed 2026-11-18.

Use the smaller of available physical memory and available process commit
capacity as the observed headroom. Obtain a fresh successful snapshot when
admitting each new or increased paste reservation. Across all windows, admit
the increase only when the following checked calculation holds:

```text
headroom = min(available_physical_bytes, available_process_commit_bytes)
admission_threshold = floor(4 * headroom / 5)
owned_paste_capacity + outstanding_paste_reservations + requested_increase
    <= admission_threshold
```

Calculate the fraction without overflowing the intermediate product. All terms
use byte capacities, not character counts or per-target encoded length. Owned
paste capacity includes the capture, source, preview, copies, indexes, work,
pools and late-resource categories already specified in Q29. Outstanding
reservations are future capacity not already charged as owned allocation.
The requested increase must not already occur in either existing term.
Using an existing reservation converts its charge to owned allocation without
charging twice or requesting a new increase.

Serialize the observation/admission decision and ledger update against other
paste admission decisions so windows cannot independently reuse the same
unrecorded capacity. This serialization does not serialize paste operations,
perform clipboard rendering under the ledger lock or lock out OS allocations.
If the memory snapshot fails or values/arithmetic are unusable, refuse only
new/increased reservations with the ordinary resource-unavailable outcome;
keep already reserved work and cleanup. Do not guess a 2 GiB fallback or treat
failure as unlimited capacity.

#### Meaning, conservatism and existing work

This is a new-admission threshold, not a continuous invariant that already
owned paste memory must remain below 80% of the latest observed free memory.
TabTty's own allocations as well as unrelated processes can lower the next
snapshot. Counting existing paste capacity on the left and then-current
available capacity on the right is deliberately conservative. For example,
if observed headroom drops from 1 GiB to 0.5 GiB after an operation allocates
0.5 GiB, the next threshold is 0.4 GiB: new growth is refused while the existing
operation retains its reservation. Do not add owned bytes back to the OS
snapshot to reconstruct hypothetical free memory or infer exact resident
memory from accounting charges.

Conversely, treating 80% as an allowance for each additional request would
permit repeated consumption of the remaining free memory and would not retain
Q28's aggregate meaning. The formula above compares the whole charged total,
including outstanding reservations. It does not promise admission of 80% of
the memory available when TabTty started, a fixed number of maximum-size pastes,
or a guaranteed 20% system-memory reserve after admission.

Retain Q29's pre-first-send reservation of every target's bounded send, Stop
and healthy closure peak. Once reserved, using that capacity is not re-admitted
against a later smaller snapshot; new operations cannot reclaim it. A preview
or preparing operation that needs additional capacity can be refused before
first submission. A lower later snapshot does not trigger a session disconnect,
payload truncation or forced cancellation of a committed send. Independent
transport, terminal-state and allocation failures retain their existing policy.
Cancellation releases accounting only at actual resource release, as before.

The 80% ratio is the user's suggested initial engineering policy, not a measured
safe percentage. OS availability can change immediately after a snapshot;
neither the query nor an internal reservation reserves RAM/commit globally
against other applications. This policy therefore remains an accounting guard,
not a process-memory ceiling, an allocation-success guarantee or a replacement
for actual peak-memory and responsiveness measurements.

#### Specification and future verification delta

Remove the aggregate field/default/Save/Cancel
and saved-limit-reduction clauses from the final contract. Retain those rules
only for Q26's per-paste source setting, and replace aggregate-setting tests
with controlled memory-snapshot/admission tests. Do not introduce a user option
to select a percentage or automatically change the saved per-paste limit.

Future verification covers a limiting physical snapshot, a limiting commit
snapshot, threshold equality, checked fractional arithmetic, simultaneous
window admission, reserved-but-unallocated work, source-copy overlap, a lower
snapshot after self-allocation or unrelated activity, query failure, rejection
before submission, and completion/Stop/closure within existing reservations.
Changing the snapshot while an operation runs must not add a new payload cutoff.
These were proposed future observations for the now-superseded Q29 branch;
this review performed no implementation or runtime memory query. The accepted
revised Q23 contract and matrix use the restored original Q26 accounting with
the retained 512 MiB source setting and >1 KiB confirmation, rather than this
superseded automatic aggregate guard. No design decision remains open.

### Q23 - Final shared understanding and verification: accepted

The user's clarification retains `maxPasteTextBytes`, the configurable 512 MiB
default source cap and confirmation for text >1 KiB, together with the restored
original Q26 resource-accounting recommendation. Confirmation also remains
required for CR/LF. Q28/Q29's independent aggregate branch stays superseded.
Retain Q24's removal of the
old 1 MiB encoded cap, Q25's bounded stored spelling and Q27's exact input/Stop.
This is not a return to a 1 MiB product cap or a 2 MiB whole-source cap.

On 2026-11-18, the user explicitly accepted this combined contract and future
verification plan, finalized candidate 6 and completed the design review of all
six candidates. The revised complete contract and 39-case verification matrix
above are accepted; there is no remaining size-limit, settings, Unicode-policy
or final-confirmation decision. The accepted decisions are consolidated into
the English/Japanese terminal, native-core, session/recording, broadcast,
settings, resource and AI observation contracts.

All six candidates are ready for separately requested implementation. The
verification matrices specify required future evidence, not executed tests.
This review performed documentation checks only and created no implementation
or prototype. Implementation, prototyping and runtime tests require the user's
separate explicit request.

### Ticket publication after design completion

On 2026-11-18, the user approved the breakdown and explicitly requested
publication. The [implementation plan #40](https://github.com/TabTty/TabTty/issues/40)
and its 27 child issues record 26 implementation slices and one cross-owner
verification slice. The 134 cases across the six accepted verification matrices
are assigned to those slices. Read-back checks confirmed the submitted bodies,
labels, 27 native parent relationships and 34 native blocking relationships.
Publication does not authorize implementation, prototyping or runtime tests;
the user must separately request that work.
