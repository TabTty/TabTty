# Architecture deepening interview

Status: design review complete, 2026-11-14. Q1-Q27 are accepted. This records decisions
for documentation and later implementation preparation. It does not authorize
implementation or prototyping. Question numbers are local to this interview.

The [terminal architecture follow-up](terminal-architecture-deepening.md),
started on 2026-11-16, discusses three new candidates separately from these
accepted decisions.

## Review sequence

The user selected all three candidates, in this order:

1. Concentrate file-operation policy behind the SFTP operation seam.
2. Deepen retained-terminal-buffer observation for AI output sharing.
3. Hide broadcast-input operation lifetime inside its existing module.

Work through each candidate before opening the next. Confirm the resulting
shared understanding before declaring its design ready for implementation.

## 1. File operations

The [file-transfer contract](../specification/file-transfer.md) remains the
behavioral authority. [ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md)
preserves use of the terminal tab's authenticated SSH connection. The accepted
SSH/OpenSSL allocation in ADR 0001 is unchanged.

### Confirmed decisions

**Q1 — Responsibility scope: accepted.** One file-operation module owns the
policy for copying, deleting, renaming, and creating folders. The transfer
window owns selection, presenting confirmations, and displaying progress and
results. The module owns preflight, execution, cancellation, and outcomes.
Copy-specific and deletion-specific rules remain distinct inside its
implementation; shared ownership does not require one generic tree algorithm.
The glossary distinguishes this broader file operation from a transfer batch.

**Q2 — Test surface: accepted.** Tests exercise the same operation interface as
the transfer window without starting that window. Observable results include
completed, failed, and unfinished entries after a child deletion failure, and
an unknown result when a file commit may have succeeded but its reply is lost.
Preserve the existing SSH/SFTP integration tests and separate Windows recycling
and confirmation-view checks. These are required future checks, not tests run
or passed during this architecture review.

**Q3 — Admission and lifetime: accepted.** The file-operation module owns
whether an operation is active, admission of subsequent operations, and
invalidation when its connection ends. The transfer window derives its controls
from that state. Bind the module to its connection; use a new module after
reconnect and prevent old completions from affecting it. Application-wide
transfer-batch and provider-I/O limits remain separately shared resources.
Keep their existing scopes: the four-batch limit is for transfers, not an
additional global limit on create, rename, or delete.

**Q4 — Confirmation ownership: accepted.** The module decides when confirmation
is required; the window presents the question and returns the user's answer.
This includes overwrite and permanent-delete fallback after recycling is
unavailable. Cancellation or disconnect invalidates a pending answer, so a
late approval cannot cause execution. The module also owns target rechecks
after confirmation. Preserve the existing confirmation choices and behavior.

**Q5 — Internal I/O seam: accepted.** Introduce an internal seam between
file-operation policy and actual I/O with production and test adapters. The
test adapter must control child-deletion failures, target changes during
confirmation, lost replies, and completion after cancellation in a specified
order. Exercise the production operation module through its ordinary interface,
not a replacement policy implementation. Keep real Windows and SSH/SFTP
integration tests for behavior the adapter cannot establish.

**Q6 — Interface shape: accepted.** Select alternative C: named copy, delete,
rename, and create operations with module-owned operation identity and
read-only state for progress, results, and pending confirmations. The same
interface accepts cancellation and confirmation answers. The module completes
operation management internally; callers do not have to call `End` or dispose
an operation handle to release admission. Exact type and method names remain
implementation choices.

**Q7 — Dependency placement: accepted.** Put file-operation policy in
`TabTty.Core` without a WPF dependency. Keep confirmation views, presentation,
and Windows-specific execution in adapters outside that policy module. Tests
can directly exercise the production policy implementation without a window.
Keep shared request/result values independent of application UI types.

**Q8 — Outstanding I/O after cancellation: accepted.** While cancelled work
still has outstanding I/O, the affected transfer window cannot start another
mutation. Browsing and closing remain available. Window close keeps the
existing bounded waiting behavior; tracking outstanding I/O does not depend
on the window remaining alive. Logical cancellation alone does not establish
that external effects have finished or release provider ownership.

**Q9 — Result and retry retention: accepted.** Retain the latest operation's
results and retry metadata together until the next operation is accepted or
the transfer window closes. A rejected start preserves both. A disconnected
window that remains open retains its results; closing it does not create a
restorable operation history. Preserve the existing retry rule: recheck failed
items and retry on the original live connection. Display retention does not
control outstanding-I/O ownership.

**Q10 — I/O ownership after view closure: accepted.** Application-wide
management retains outstanding I/O independently of the transfer window,
attributed to its original connection and operation. Keep provider slots and
required serialization until actual completion. Reopening a transfer window
for the same connection inherits the pending-I/O admission restriction.
Discarding displayed results does not discard this ownership information.
Application exit continues to follow the existing bounded-wait policy.

**Q11 — Final shared understanding: accepted.** The user confirmed the
[consolidated file-operation design](../specification/file-operations.md) and
requested moving to candidate 2. Candidate 1 is ready for implementation as a
design; implementation and prototyping still require a separate explicit
request. The architecture review has changed documentation only.

### Checks implied by Q1-Q10 and the existing contract

These are future verification cases, not executed results. Use the production
file-operation module with the controlled I/O adapter and the same interface
used by the transfer window.

| Situation | Observable requirement |
| --- | --- |
| A selected child cannot be deleted | Preserve completed effects, stop that top-level tree, distinguish failed and unprocessed entries, and continue other selected entries where permitted. |
| The target changes during overwrite confirmation | Recheck and request a fresh decision for the observed change; the earlier approval does not silently cover it. |
| Cancellation or disconnect occurs while confirmation is pending | A late approval causes no subsequent destructive request. |
| A sent mutation loses its reply | Report unknown outcome, exclude automatic replay, and require the contract's refresh before further mutations. |
| Local provider work completes after logical cancellation | Keep its resource ownership until it actually ends; do not attribute its completion to a later operation. |
| The retained terminal tab reconnects | An explicitly reopened transfer uses the new connection; old answers, progress, results, and retry state cannot be attached to it. |
| Equivalent input is submitted by the UI and a test | Both reach the same admission, confirmation, cancellation, and outcome rules. |
| A new operation is rejected or accepted | Rejection preserves the latest results and retry metadata; acceptance replaces them together. |
| The transfer window closes and reopens while I/O is pending | Display history is discarded, but pending I/O retains ownership, resources, and the same connection's admission restriction. |

Keep Windows recycling/no-follow behavior, real SSH/SFTP reply handling, and
terminal responsiveness under transfer load in their integration/acceptance
checks. A controlled adapter does not prove those behaviors.

### Evidence motivating the candidate

- `src/TabTty.App/FileTransferWindow.cs` owns deletion planning, recursive
  removal, source rechecks, and unfinished-entry reporting inside the window.
- `src/TabTty.App/TransferBatch.cs` owns analogous operation-wide decisions
  for copying, including staging, replacement, retry, and cancellation results.
- `src/TabTty.App/TransferFiles.cs` already concentrates local/SFTP I/O
  differences. Removing it would spread that knowledge back to callers.
- `tests/TabTty.App.Tests/Program.cs` exercises recursive copies through
  `TransferBatch` with real local files and the SSH/SFTP peer fixture; deletion
  policy does not currently have the same non-window test surface.

### Contract discrepancy to preserve in implementation preparation

The existing contract requires an explicit transfer open after reconnect to
replace the old disconnected view with the new connection generation.
`TerminalTab.OpenFileTransfer` currently activates any existing transfer window
without checking its session, while `TerminalTab.Start` starts the new connection
without replacing that window. `FileTransferWindow` retains its original session.
For a retained tab with an old transfer window, this path does not establish the
contracted replacement. This is a source-review finding, not an executed
reproduction. The existing contract remains authoritative; address this path
and test old-work isolation during later authorized implementation.

The source review also found that `TransferFiles.LocalCommit` is released by
the outer rename/commit call when `LocalAsync` stops waiting, even if the local
provider is still running. The provider-call slot itself remains charged until
the provider returns. Preserving a provider slot is not the same as preserving
mutation serialization. The selected design must keep required serialization
owned for the actual I/O lifetime, in accordance with the existing same-resolved-
destination contract. This observation does not establish general exclusion
against external filesystem writers or remote path aliases.

Display results and retry metadata currently have different lifetimes:
`FileTransferWindow.StartOperation` clears displayed results before the copy
module attempts global admission, while `TransferBatch` clears its failed list
only after obtaining a batch slot. Create/rename/delete also clear the display
without replacing the previous copy's retry metadata. The current contract
does not promise multiple-operation history or restoring results after the
transfer window is closed. Q9 now aligns the selected result set with its retry
metadata for the proposed implementation.

### Readiness

Q1-Q11 are accepted and consolidated in the
[file-operation module design](../specification/file-operations.md).
The final shared-understanding review is complete. No design branch remains
open for candidate 1; code changes and runtime verification remain future work.

### Interface alternatives reviewed after Q5

Three independent design explorations compared the following shapes. Q6
selected C; A and B remain unselected alternatives. Illustrative type and
method names are not final implementation requirements.

| Shape | Caller knowledge | Depth and trade-off |
| --- | --- | --- |
| A: typed request execution plus state observation | Submit a copy/delete/rename/create request, observe snapshots, and supply an asynchronous confirmation adapter at construction. | Few entry points hide substantial policy, but request variants, construction, and separate observation lifetime remain part of the interface. |
| B: commands plus revisioned state | Submit begin/answer/cancel/retry commands, observe state revisions, and page bounded results. | Flexible composition concentrates policy, but operation/question identifiers and command ordering add knowledge for the window. |
| C: named operations plus an opaque operation handle | Invoke copy/delete/rename/create directly; observe read-only state, return a confirmation answer, or request cancellation. | The current transfer window has direct operations without assembling generic commands; a shared presentation path handles snapshots and questions. |

The selected C fits the existing transfer window. The module owns admission and
operation completion; callers do not have to call an `End` method or dispose a
handle to unlock it. Confirmation requests carry module-owned identity so late
or duplicate answers can be rejected. Internal I/O, copy/delete algorithms,
retry selection, and completion validation stay behind the seam. This aims for
depth through less caller knowledge, not through counting public methods.

All three designs keep the Q5 production/test I/O adapters internal and
distinguish stopped operation execution from outstanding provider work. Q7
settles dependency placement and Q8 settles the affected window's admission
while I/O remains. Cleanup ownership across view replacement and final
interface contract consolidation are settled by Q9-Q10 and the linked design;
Q11 confirms the resulting shared understanding.

## 2. Retained terminal buffer observation

In progress after candidate 1's Q11 confirmation. Preserve the AI sharing/control
distinction and [ADR 0005](../adr/0005-expose-existing-terminal-sessions-over-local-mcp.md).
The [AI collaboration contract](../specification/ai-collaboration.md) and
[validation plan](../specification/ai-collaboration-validation.md) remain the
behavioral and test requirements. Architecture question numbering continues
at Q12; these numbers are distinct from the original feature interview.

### Confirmed decisions

**Q12 — Terminal-side scope: accepted.** Concentrate retained-history reading,
search, continuation, and pre-return consistency verification in the terminal
module. Keep wrap handling, range identity, and change/eviction detection
inside its implementation rather than requiring AI collaboration callers to
manipulate terminal rows.

**Q13 — AI collaboration responsibilities: accepted.** Keep counterpart-specific
sharing permissions, externally visible identifiers and their expiration, and
MCP response formatting in the AI collaboration module. The terminal module
owns content consistency; the AI collaboration module owns whether the result
may be disclosed to the counterpart. Both conditions must hold at disclosure.
Preserve existing permissions and public protocol behavior.

**Q14 — Interface shape: accepted.** Select alternative C with module-owned
preparation, publication, and cleanup, preserving the Q12/Q13 responsibility
split. Both ordinary history and search use final content and authorization
validation. Tool handlers do not coordinate a separate manual commit/end/cleanup
sequence, and prepared terminal data cannot bypass publication checks.

**Q15 — Publication and responsiveness: accepted.** Prepare the serialized
response outside the terminal mutation gate. At publication, perform bounded
content validation and authorized nonblocking queue admission while terminal
mutation is excluded. Never wait for the dispatcher, network, or queue space
under that gate. Gate contention allows only bounded rescheduling outside the
gate and dispatcher; exhaustion returns the existing `busy` result without
advancing continuation progress. Queue acceptance is the disclosure cutoff.

**Q16 — Test surface: accepted.** Exercise the same complete observation and
publication interface as production using the real terminal model. Use a
controlled publication adapter to order edits, cancellation, revocation, and
queue rejection; assert the disclosed result and continuation outcome. Preserve
real private-channel integration checks and screen/wait/input regression checks.
Preparation-only tests do not establish publication correctness. These are
future verification requirements, not tests run during this review.

**Q17 — Continuation publication: accepted.** Preserve reusable immutable
history/search checkpoints. Preparation stages successor IDs and their registry
effects privately; only successful queue admission makes them usable. Failed
publication discards the staged successors and does not consume the source
checkpoint or evict older live handles solely to register unpublished ones.
Independent expiry, quota retirement from other accepted work, or revocation
still applies. Reusing a valid source checkpoint revalidates its content; it
does not promise replay of an earlier response. A new successor receives its
own fixed 60-second lifetime when activated; the source lifetime is unchanged.
Reserve the bounded resources needed for a nonthrowing activation at admission.

**Q18 — Response sizing: accepted.** The AI collaboration module supplies
conservative content/fragment allowances to terminal preparation and verifies
the actual combined serialized result, including its text mirror, before
queue admission. If necessary, request a bounded smaller page from the terminal
module; that module owns cluster-safe pagination, scan coverage, and successor
selection. Never clip JSON or omit matches while advancing past them. Keep the
bridge's defensive size checks. Implementation capacity must fit a maximum
supported cluster, but an explicit caller text limit too small for the next
complete cluster returns `resource_limit` without progress; do not exceed the
requested limit or produce an endless empty continuation.

**Q19 — Bounded consistency metadata: accepted.** The terminal module maintains
bounded aggregate span-revision/retention information alongside mutations and
uses it for final verification. Do not traverse all retained rows or rebuild an
index under the publication gate. Detect intervening edits even when the text
is subsequently restored; preserve unchanged prefixes on append and anchors
through reflow. Charge intrinsic span/index state to existing terminal-state
accounting and additional AI-only proof/cache/work ownership to the AI budget,
without double charging shared ownership or creating an unbounded pool. Optional
AI metadata pressure expires that metadata or rejects AI work with the existing
errors; it must not evict terminal content or stall reception. Missing proof
metadata never silently downgrades verification. Exact index structure and
internal work caps are implementation choices constrained by these bounds and
the existing responsiveness validation targets.

### Starting evidence

`TerminalSession.ObserveAiAsync` and `CommitAiObservationAsync` expose
`TerminalOutput` to callbacks. `AiCollaboration.Observations` consequently
owns row normalization, logical-line anchors, range proofs, search overlap,
continuation construction, and publication-time revalidation. The existing
changed-search test verifies rejection when captured content changes before
commit, but the application still needs internal terminal-row knowledge.

The improvement target is depth at the terminal observation interface, with
the existing real terminal model as its implementation.

Additional source review found that search currently normalizes, enumerates,
and scans rows while holding the session semaphore through `ObserveAiAsync`.
This is not the separate immutable-window scan described by the existing
contract. `CommitAiObservationAsync` revalidates search proofs while holding
that semaphore and then waits for a dispatcher commit. The private-channel
enqueue is the current disclosure cutoff, not socket-write completion.

Only search currently attaches a publication-time content validator to a
reply. Ordinary history pages have coherent capture and reused-range validation
but do not use that final validator. Screen capture and change wait also use
the generic observation helper; changing history must preserve those callers
and the existing input-observation rules.

The changed-search assertion exercises capture followed by edit and commit.
Most other AI test calls return `CallAsync` results without the private-channel
commit path. Existing snapshot/reflow tests do not by themselves establish AI
search correctness or publication ordering. These are source-review facts,
not new runtime results or measured latency claims.

Further source review after Q16 established the following:

- `HistoryCursor` and `SearchCursor` are immutable records. Resolving a history
  or search handle does not consume it or update its position; successful
  preparation stores new successor handles. `AiCollaboration.Store` registers
  those handles before publication, with a new fixed 60-second lifetime, and
  may immediately evict an older handle. No per-request rollback removes those
  effects after final validation, cancellation, or later size rejection.
- The application's history/search page builders use estimated mirrored-text
  costs. The strict 1 MiB combined structured/text-result check currently occurs
  in `McpStdio.ToolResult`, after application queue admission. Oversize is
  replaced with `resource_limit`; there is no coordinated repagination path
  before publication. The private frame has its separate 2 MiB check.
- The maximum supported cluster is 256 Unicode scalars, at most 1024 UTF-8
  bytes. It fits the normal page and result limits. However, `max_text_bytes`
  accepts values as small as 1; the current implementation returns
  `resource_limit` if the first complete cluster cannot fit that request.
  The contract's maximum-cluster guarantee needs to distinguish implementation
  capacity from an explicitly smaller caller limit.
- `TerminalTextFingerprint` is a fixed-size composable content fingerprint,
  not a record of intervening mutations. `Find` scans rows and `Proof` combines
  the requested rows; a fixed-size stored proof does not make verification
  independent of the requested range length. Replacing text and restoring it
  can reproduce the same fingerprint. The selected design must satisfy the
  contract's span-revision semantics as well as its final-work bound.

### Interface alternatives reviewed after Q13

Three independent design explorations compared the following shapes. Q14
selected C; A and B remain unselected alternatives. These describe the proposed
implementation, not current behavior. Exact type and method names are
illustrative.

| Shape | Caller knowledge | Depth and trade-off |
| --- | --- | --- |
| A: typed preparation request plus explicit commit | Prepare a read/search request, serialize its result, and invoke a synchronous commit with an admission callback. | Few entry points hide terminal algorithms, but callers still coordinate preparation, reservations, publication, and abandonment. |
| B: explicit reader with page proposals and checkpoints | Open or resume a reader, prepare one page, commit it, and dispose abandoned proposals/readers. | Flexible page scheduling and independent resumptions, but a larger temporal interface exposes reader and proposal lifetimes. |
| C: named read/search operations with owned preparation and publication | Request history or search through the ordinary AI collaboration path; its implementation owns preparation, publication, and cleanup. | Less caller knowledge and a shared publication path for ordinary history and search; requires careful ownership inside the existing modules. |

Q14 selects C. The terminal module owns named read/search
preparation and opaque consistency metadata. The existing AI collaboration
module combines those results with its permissions, handle registry, formatter,
and output queue. Do not add a pass-through facade merely to shorten a call
site. Cleanup must not depend on a tool handler remembering a separate commit,
end, or disposal sequence. A prepared result cannot be disclosed by bypassing
the publication path.

This gives depth by hiding terminal-row knowledge and publication ordering,
locality by keeping history algorithms with the real terminal model, and
leverage by using the same flow for production callers and tests. The terminal
model is an in-process dependency, not a replaceable fake-model adapter. The
publication seam can have production queue admission and controlled test
adapters; the actual private-channel integration still needs its own checks.

All alternatives propose bounded capture under the terminal mutation gate,
followed by scanning immutable copied windows outside that gate. They also
propose pre-serializing the response before the final publication attempt,
nonblocking acquisition of that gate on the
application's permission-ordering dispatcher, and bounded synchronous content
validation followed by nonblocking queue admission. No terminal gate is held
while awaiting the dispatcher, network I/O, or queue space. A failed attempt
does not advance continuation progress or expose staged successor handles.
These are proposed implementation requirements, not established properties of
the present generic observation callbacks.

### Inherited constraints for the remaining design

The following are already required by the
[AI collaboration contract](../specification/ai-collaboration.md) and are not
new questions about product scope:

- Store bounded continuation metadata, not a frozen transcript or references
  that pin terminal rows. Keep external handles counterpart/session/source
  bound, with a fixed 60-second lifetime and the shared 256-per-counterpart /
  4096-application registry limits. Resolving a handle does not renew it.
- End sharing, counterpart loss, tab close, or reconnect invalidates the
  affected identifiers and cancels pending work. Ordinary disconnect preserves
  authorized retained-history access when the existing close policy retains
  the shared tab. Returning input control alone does not revoke reading.
- Keep the existing 256 KiB immutable scan windows with cluster-rounded query
  overlap, 20 ms cooperative slices, and 100 ms search-work budget. Every
  published page validates negative coverage, overlap, and context as well as
  matches. A continuation also validates its remaining requested range.
- Charge copied text, metadata, reservations, serialized responses, and queued
  work to their existing budgets for their actual ownership lifetimes. Transfer
  of ownership to a queue does not release the charge; cancelled work releases
  it when its underlying ownership ends. The 8 MiB counterpart transport budget
  is a subset of the 64 MiB application AI transient-work budget.
- Sharing revocation remains ordered with final disclosure. Unauthorized errors
  do not reveal terminal metadata. Screen capture, change wait, input observation
  watermarks, and their existing handle lifetimes keep their current contracts;
  history deepening does not replace those designs.

### Consolidated design and final review

Q12-Q20 are accepted and consolidated in the
[retained-history observation design](../specification/ai-history-observation.md).
The consolidation preserves the existing screen/wait/input contracts and the
full-publication test surface. No implementation or runtime validation was
performed for this architecture follow-up.

**Q20 — Final shared understanding: accepted.** The user confirmed the
consolidated responsibility split, complete publication path, immutable
checkpoint lifetime, bounded sizing/consistency work, and verification
requirements. Candidate 2 is ready for implementation as a design. The user
also confirmed C:\Users\kaosun\source\repos\TabTty as the working repository,
authorized saving the five consolidated documents there, and requested moving
to candidate 3, broadcast-input operation lifetime. This review does not start
implementation.

## 3. Broadcast input operation lifetime

In progress after candidate 2's Q20 confirmation. The
[broadcast/split contract](../specification/broadcast-and-split-views.md) remains
the behavioral authority, including the accepted AI-sharing exclusions in the
[AI collaboration contract](../specification/ai-collaboration.md). Preserve
ADR 0002's one terminal session per tab and one delivery per broadcast target
regardless of pane count. Q21-Q27 are accepted, including final shared
understanding; the consolidated design is ready for implementation.

### Confirmed decisions

**Q21 — Operation lifetime ownership: accepted.** Deepen the existing
`BroadcastInput` module to own paste and mouse-gesture preparation, recipient
freezing, preview validity, execution, cancellation, cleanup tracking, and
completion. UI callers provide semantic input, native capture/handoff facts,
and confirmation answers, and display read-only progress/results. They do not
manage a matching `EndPaste` call or mutate/read internal mode/operation state
to keep lifetime correct. Native UI execution and per-session input ownership
remain in their existing implementations. Exact completion/admission states
and interface shapes are subsequent decisions, not assumptions in this choice.

**Q22 — Complete-operation test surface: accepted.** Production callers and
tests use the same operation interface from preview/preparation through
cancellation or completion. Use the real session input owner and existing
controlled wire seam, with controlled confirmation/capture-event adapters to
order late answers, cancellation, failure, and cleanup. Assert observable
admission, results, and emitted bytes rather than constructing a private
lifecycle manually. Preserve WPF integration and native Windows capture/IME
checks for behavior the controlled adapters cannot establish.

**Q23 — Completion after effects have started: accepted.** Distinguish Stop
acknowledgement from operation completion. Full Stop immediately ends the mode
and prevents further user-input admission for that operation; Stop Paste cancels
that paste while leaving the mode active. Keep an explicit cleanup-pending state until
admitted sends, session-owned encoder/bracket closure, and native capture/gesture
cleanup have completed or concluded with a recorded failure and released their ownership.
A deadline or cancellation request alone does not establish completion.
Display pending cleanup and the reason a conflicting Start or AI grant remains
unavailable. Preserve the existing scope-specific Start and whole-operation AI
grant gates, ordinary per-session input admission after full Stop, and allowed
movement; do not prolong the active local-scope move barrier merely for draining.
Keep cleanup attributed to the original operation and sessions through allowed
movement or view closure, and ignore stale callbacks for replacement operations.

**Q24 — Preview-only cancellation: accepted.** If no send or terminal mouse
capture/gesture has started, cancellation immediately invalidates the pending
confirmation and any operation-related preference/progress updates, requests
the preview's closure through the UI adapter, and releases that operation's
broadcast admission/selection ownership. Do not wait for the user to dismiss an
obsolete dialog or for an inert late callback before marking this operation
complete. Late answers cannot send input, update shared paste pacing defaults,
or affect a replacement operation. Keep any still-owned UI/callback resources
accounted and safely owned until their actual release; their inert lifetime
does not retain permission to act. Other session/native-input admission checks
continue to apply independently.

**Q25 — Interface shape: accepted.** Select C: named paste, native-input, and
complete move intentions, with module-owned orchestration and read-only
state/results. Preserve existing named mode/selection/input commands. Keep
operation/question/capture identity and callback validation behind the seam,
and require no caller-owned End/disposal sequence to finish an operation.
The native and placement adapters provide observations and execution facts;
window-layout policy and session input ownership retain their existing roles.

**Q26 — Dependency placement: accepted.** Keep the deepened BroadcastInput
module in TabTty.App, sharing the existing ordered coordination of window scope,
selection, and AI permissions. Put WPF presentation, native capture, and layout
execution behind the justified production/test adapter seams. Keep request and
state values independent of WPF event arguments and visual-tree details. Reuse
the existing Core session input implementation and controlled wire seam. Do not
introduce a general input framework or duplicate authority in another module.

### Starting evidence

- `BroadcastInput` already owns effective scope, remembered selection,
  recipient snapshots, mode serialization, preflight/dispatch, failure policy,
  draining, and mouse press-recipient bookkeeping. Its useful implementation
  should remain concentrated. Its interface exposes mutable `Mode` and
  `Operation` state, cancellation sources, and completion ordering to callers.
- `TerminalTab.PasteClipboardAsync` calls `BeginPaste` before preview, reads
  `operation.Targets` and `operation.Mode.Paste`, builds progress/timer state,
  invokes `PasteAsync`, and must call `EndPaste` in its outer `finally`.
  Preview presentation and pacing controls belong with the UI; operation
  completion and stale callback validation currently cross the module's seam.
- `Stop(mode)` removes the active mode immediately, requests cancellation, and
  enters draining while waiting for gesture cleanup and the mode queue.
  `EndPaste` separately clears paste/progress and disposes the operation token.
  During preview no dispatch owns that queue, so queue draining can finish
  before the caller releases its preview operation. This is observed source
  ordering, not a reproduced runtime defect.
- Per-recipient `PreparedInput` already owns once-only submission and reserved
  input capacity. The existing session input owner performs encoding, ordered
  writes, unsent-tail cancellation, and valid encoder/bracket closure. Session
  paste returns after closure and clears its own paste-active state. Deepening
  broadcast lifetime must not introduce another writer or duplicate this policy.
- `TerminalSurface` owns native capture and source classification;
  `TerminalTab` remembers the effective broadcast mode across a gesture.
  `BroadcastInput` owns frozen recipients, held buttons, delivered presses,
  reporting generations, pending releases, and cleanup. `MainWindow` also
  reads `Active(...).Mouse.Source` when moving/activating a tab. Native host
  movement has its own begin/end capture-handoff protocol.
- AI grant eligibility consults unfinished operations for sources as well as
  recipients, including an unchecked source and draining modes. Sharing,
  selection changes, and tab movement retain their ordered GUI coordination.

Existing WPF tests in `BroadcastAndSplitViews` exercise scope/selection, codecs,
global paste, movement of an unchecked nonrecipient during paste, Stop Paste,
and zero-send conversion failure. They invoke `BeginPaste` and `EndPaste`
directly, so they do not establish clipboard-preview/progress lifetime behavior.
Core tests cover single-session paste closure and mouse encoding; AI tests cover
basic broadcast exclusion. The source review found no corresponding broadcast
integration coverage for preview cancellation/Stop, overlapping buttons/capture
loss, local frozen-operation move barriers, source movement, or AI grant during
source-only unfinished operations and draining. Existing native Windows capture,
DPI, IME, accessibility, and performance checks remain separate release work.
No builds or tests were run for this review.

### Completion and admission evidence after Q22

Focused source review distinguishes these gates; operation ownership must not
silently broaden them:

| Gate | Existing behavior to preserve |
| --- | --- |
| Selection and additional input during paste | The active mode is locked through preview/paste; additional broadcast input is rejected. |
| Input during a button gesture | Recipients stay frozen; another paste is rejected, but ordinary text is still allowed through existing mode ordering. |
| Stop Paste | Cancels the operation while leaving broadcast mode active. This differs from full Stop. |
| Full Stop | Removes the active mode immediately, hides checks, and tracks outstanding work in draining. |
| New Start | Waits for conflicting draining scopes: the same local window, a global drain, or any drain before global Start. Unrelated local scopes remain available. |
| Ordinary input after full Stop | Routes directly to its session under existing session admission/ownership checks; there is no whole-old-scope input lock. |
| Tab movement after full Stop | Existing completion/Stop and capture-handoff rules apply; draining alone does not prolong the active local-scope move barrier. |
| AI grant | Source and all frozen targets remain associated with the unfinished operation, including an unchecked source. Existing session/native-input checks also apply; there is no new per-target early grant. |

During preview, the current modal dialog has no operation-cancellation
registration. Its later acceptance can update the application's PasteDefaults
before submitting the already-cancelled operation. The cancelled mode token
prevents that old payload from dispatching, but does not itself invalidate every
UI side effect. Native capture-loss notifications also identify a pane rather
than a particular operation; a stale notification is not inherently isolated
from a newer gesture on that pane. These are source observations, not executed
race reproductions.

Cancellation/deadline expiry is not actual completion. Prepared requests retain
reservations until awaited sending and disposal finish; session paste completes
its closure attempt before clearing paste-active state. Mouse cleanup requests
cancellation after its deadline and still awaits the release operation. Serial
I/O retains its buffer until actual overlapped completion. None of these
dependencies should be replaced by a broadcaster-owned writer or a premature
resource release.

### Existing constraints

- Preserve scope selection, first-use/reset timing, and AI-shared-tab exclusion.
  Ordinary keys/wheel/unbuttoned motion take discrete snapshots. Paste and
  button gestures freeze recipients for their required operation lifetime.
- Keep all-recipient preflight before any payload, existing per-session budgets
  and deadlines, per-recipient results, and the failure policy that stops the
  affected mode. Do not add replay or rollback promises.
- Keep session-owned encoding, input ordering, and required frame/release
  cleanup. Logical Stop does not prove that transport effects have ended.
- Preserve allowed global cross-window/local same-window movement and frozen
  local-scope barriers. Native capture handoff must be verified; failed handoff
  causes explicit cancellation, not a fabricated physical release.
- Retain source identity independently of recipient membership and pane hosts.
  An unchecked source may send broadcast input, but its unfinished operation
  still matters for AI control eligibility.

### Interface alternatives reviewed after Q24

Three independent explorations compared the following shapes while preserving
Q21-Q24. Q25 selected C; A and B remain unselected alternatives. These are
designs for the proposed restructuring, not descriptions of current behavior.
Exact C# names and internal type decomposition are illustrative.

| Shape | Caller knowledge | Depth and trade-off |
| --- | --- | --- |
| A: one typed request entry point plus snapshot/notification | Submit mode, selection, input, or move request variants; interpret admission, completion, and immutable state. | Few members hide operation choreography, but the request families, receipts, and adapter contracts still form a substantial interface. Avoid growing a generic application command bus. |
| B: typed operation endpoints with revisioned observations | Observe an opaque paste/gesture/move endpoint, answer an operation-specific prompt, and route native facts through its event inlet. | Flexible for multiple independently interacting views; callers and adapters learn more endpoint, observation, and reply-capability concepts. No endpoint disposal is required for completion. |
| C: named intentions with module-owned orchestration | Request paste, forward classified native mouse facts, or request a complete terminal-tab move; render read-only state/results. | Common WPF handlers learn the least operation machinery. More named members preserve direct intent, while identities, confirmation validity, and handoff sequencing stay inside the implementation. |

Q25 selects C. A small number of members alone does not
establish depth: A still exposes a command vocabulary and acknowledgement versus
completion. B gives useful leverage for complex independent views, but current
callers can receive a shared read-only projection without managing operation
endpoints. C concentrates ownership and stale-work protection for locality while
keeping the existing module's substantial dispatch implementation.

An illustrative common-caller shape is:

```csharp
await broadcast.PasteAsync(sourcePane, immutableClipboardText);
broadcast.ReleaseRemoteButton(observedNativeRelease);
await broadcast.MoveTerminalTabAsync(tab, chosenDestination);
```

The module retains the existing concrete Start/Stop, selection, and ordinary
input operations. The native adapter provides classified input and correlation
facts. The UI captures clipboard text and chooses the layout destination; those
tasks do not move into the broadcast policy. No caller assembles an operation
with Begin/End calls, holds its cancellation source, or reads mutable Mode or
Operation fields. Adapter construction and binding remain part of the overall
interface cost; the sketch does not imply they are free.

Q26 confirms all three explorations' recommendation to keep the module in TabTty.App. Scope,
selection, source identity, shared paste defaults, and AI permission coordination
are in-process application dependencies. Keeping these decisions in their
existing GUI ordering domain avoids another copy of that authority. The real
TerminalSession remains authoritative for preparation, encoding, reservations,
ordered sending, and encoder/bracket closure.

The existing session/wire seam is local-substitutable in tests. Confirmation,
native capture, and tab placement have production WPF/Windows adapters and
controlled test adapters. Keep request/state values free of visual-tree traversal
and WPF event arguments; rendering and native execution belong in those adapters.
Moving the policy into Core would remove the App assembly dependency, but would
require exporting window/group topology, sharing eligibility, and GUI coordination
through additional interfaces. That trade-off does not improve this candidate's
locality. Core's existing session implementation is reused directly.

### Constraints on the selected interface and its adapters

These constraints follow from Q21-Q26 and the existing behavioral contracts;
they apply to the selected interface:

- Admission, permission changes, selection, Stop, preview answers, and movement
  decisions use the existing ordered application coordination. A displayed
  snapshot is advisory; each command rechecks authoritative eligibility.
- Preview answers and progress carry private operation/question identity.
  Accepting a valid answer and updating remembered pacing defaults is one
  ordered transition. Obsolete answers and detached observers cannot release
  admission or change replacement state. Cancelling an observer only detaches
  that view; the module continues to own the operation.
- Stop acknowledgement and actual completion remain separate. A busy Start is
  rejected under existing scope rules, not queued to start automatically later.
  Completion after effects start follows actual work and required cleanup;
  preview-only completion follows Q24 while inert UI resources remain owned.
- A result saying broadcast was inactive can route the initial input through
  the existing single-session path. An active mode with zero recipients,
  rejection, cancellation, or failure never falls back to sending to the source.
  Preserve the gesture's initial routing decision across mode changes.
- Keep source/target/session generation identity independent of pane placement.
  The unchecked source remains relevant to whole-operation AI-grant eligibility.
  Per-button delivered presses and reporting generations remain internal.
- Native callbacks identify the operation and capture/host attachment they
  belong to. A callback for a replaced operation cannot affect its successor.
  During a verified host move, however, a queued genuine release from the old
  host can still belong to the same gesture and must be incorporated once.
  Do not discard every old-host notification or use current button state to
  invent the time/position of an unobserved physical release.
- A complete move intent lets the module coordinate input admission, native
  handoff, and the placement adapter's execution/finalization without a caller
  BeginHostMove/EndHostMove protocol. Layout policy still chooses/validates the
  destination, performs insertion/restoration and focus work, and owns the
  existing empty-window closure rule. Keep the old host alive until verified
  handoff or explicit cancellation is decided.
- Placement success and gesture continuity are separate outcomes. Placement
  failure restores the original placement. Uncertain capture continuity causes
  explicit gesture cancellation and valid matching-release cleanup, not a claim
  of uninterrupted transfer. Outstanding wire cleanup does not create a new
  move barrier after full Stop. Actual Windows validation remains necessary;
  identity/sequence bookkeeping alone cannot prove native capture continuity.

### Consolidated design and final review

Q21-Q27 are accepted and consolidated in the
[broadcast-input operation module design](../specification/broadcast-input-operations.md).
The consolidation preserves the existing input, selection, AI coordination,
movement, and native-verification requirements. Independent source/document
consistency review found no material branch beyond final confirmation. The
design and cases B02/B11 now explicitly retire a cancelled broadcast preview's
legacy busy contribution while preserving independent native/session checks.
This clarifies Q24 and requires no new decision. No implementation or runtime
validation was performed for this architecture follow-up.

**Q27 — Final shared understanding: accepted.** The user confirmed the
consolidated App module, named operation interface, Stop/completion distinction,
preview-only cancellation, existing admission gates, native handoff, resource
ownership, and complete-operation verification requirements. All three selected
candidates are ready for implementation as designs, with candidates 1 and 2
already confirmed through Q11 and Q20. The user then requested ticket preparation
using to-tickets; its proposed breakdown is reviewed before tracker publication.
Implementation and runtime verification remain separate work from this review.
