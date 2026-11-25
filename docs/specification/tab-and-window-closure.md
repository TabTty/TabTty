# Tab and window closure design

Scope and bulk/automatic closure behavior accepted on 2026-11-16 in Q9-Q10 of the
[architecture interview](../drafts/terminal-architecture-deepening.md).
This is target design, not implemented behavior or new validation evidence.
Q11 accepts named complete operations; Q13 accepts failure results and continued
ownership after an abandoned wait. Q12a-Q12b accept ordinary progress during
confirmation and joining an existing tab close when closing its window.
Q14 accepts the exceptional changed-target outcome and confirms the candidate's
final shared understanding. This and the recording/parser designs are ready
for separately requested implementation. Implementation and prototyping require
a separate request.

## Ownership scope

A C# application module owns one complete explicit tab/window closure workflow:
identify and review affected targets, obtain the existing confirmation decision,
validate target/generation ownership, coordinate termination through the existing
owners, handle waiting/failures and permitted cleanup adoption, and request
visual removal through existing layout/application coordination. User-invoked
bulk closure and automatic tab closure use this workflow with the scopes
specified below. Automatic lifecycle notifications enter the same complete
closure coordination; they do not directly remove the view and bypass cleanup.
The UI supplies intent, presents required decisions, and observes an outcome;
it does not combine Disconnect, Completion, transfer close, abandonment, and
removal into its own cleanup recipe.

| Responsibility | Owner |
| --- | --- |
| Complete closure orchestration, review validity, duplicate-request coordination, waiting and outcome | C# closure module |
| Receive cutoff, unsent input cancellation, transport close, recording finalization and actual file I/O ownership | Existing session/input/recording modules |
| SFTP operation/channel cleanup, provider completion, temporary-path and unknown-outcome reporting | Existing file-operation and transfer owners |
| Broadcast gesture/paste completion and complete tab-move intent | Existing BroadcastInput module |
| Placement, empty-window policy, move guards, activation order and final exit | Existing layout/application coordinator |
| Sharing invalidation, external handles and pending observation ownership | Existing AI module |
| Terminal meaning, EOF/recording semantics, retained state and final native release | Rust terminal module through its owning session/adapter |
| Localized confirmation, pending-cleanup choice and visual presentation | WPF presentation adapters |

The closure module delegates resource operations; it does not duplicate the
accepted [recording/parser design](native-terminal-core.md),
[file-operation module](file-operations.md), or
[broadcast-input module](broadcast-input-operations.md). It adds no authentication, vault,
settings-save, or macro-runtime policy.

## Intent-specific behavior

| Intent or event | Transfer-window behavior |
| --- | --- |
| Explicit single terminal-tab closure | Close windows associated with the targeted tab using the existing cleanup/reporting policy |
| Explicit terminal-window closure | Close transfer windows associated with its targeted terminal tabs; combine required connection/file-work confirmation |
| User-invoked **Close all disconnected tabs** in the current window | Close the transfer windows of each targeted terminal tab under the explicit-closure policy |
| Automatic disconnected-tab closure under the saved setting (amended Q10) | Close windows associated with the removed tab using the same cleanup/reporting policy as explicit closure |
| Disconnect/peer loss while the terminal tab remains open | Preserve disconnected transfer-result windows while the application remains running |
| Tab move, pane removal, or layout-only group change | Preserve the existing session/transfer lifetime; use the accepted move/layout contract |
| Final application exit | Apply the existing exit rule; transfer-result windows do not keep a background-only run alive |

**Close all disconnected tabs** acts across all tab groups in the invoking window.
It includes retained tabs with a broken-link icon (an orderly disconnect or
a user-cancelled reconnect) and retained tabs with a warning icon (a failed or
timed-out connection attempt, or a communication error that ended an established
connection). Either group alone enables the command, regardless of the saved
automatic-close setting. Connected tabs, active attempts including authentication
and other user-input waits, settings and editors stay open. A recording error
that leaves a connection running does not make its tab eligible.

An orderly end of an established connection and a user-cancelled reconnect
follow the automatic-close setting. A user-cancelled initial connection
closes its tab with either setting. A failed or timed-out attempt and an
established connection ended by a communication error retain their tabs
even with automatic closure enabled. The reason and retained output remain
available until the user chooses a manual closure. See the
[tab outcome contract](persistence-and-windows.md).

Bulk and automatic closure follow Q10's transfer cleanup for each target.
Closed connections and completed attempts need no connection-ending
warning. Existing active-file-work confirmation and known-unresolved-cleanup
reporting still apply where required; no cancellation starts before its
required approval. Automatic closure is not permission to skip a required
report or accept unsaved log data. A peer disconnect itself is an external
event, not a close-induced effect that rejecting a subsequent closure can undo.

## Accepted complete-operation interface (Q11)

Use named complete operations for the three ordinary UI commands:

```csharp
await closure.CloseTabAsync(tabId);
await closure.CloseWindowAsync(windowId);
await closure.CloseDisconnectedTabsAsync(windowId);
```

These are illustrative names, not implemented types. The caller supplies a
stable tab/window identity. The module captures current session/transfer
ownership and placement, presents required decisions through internal adapters,
and owns the complete operation and outcome. It exposes no caller-owned review
ticket, Begin/End sequence, or cleanup/disposal duty. Automatic closure uses
the same workflow for its affected tab through existing lifecycle coordination;
it does not add a fourth user command or a second closure recipe.

The named operations have the same depth as a single typed-intent entry point;
their advantage is discoverability and arguments matching existing commands.
The interface seam lies between UI/lifecycle intent and complete application
closure. Resource operations and layout policy remain with their existing
owners. The independently accepted Q12 rules below determine confirmation
validity and coordination of overlapping targets.

## Confirmation and overlapping closure (Q12a-Q12b)

### Ordinary progress does not restart confirmation

Capture stable tab identities, their connection generations, the relevant
transfer ownership, placement and effects requiring approval. Validate those
facts again in existing application-coordinator order before starting new
close-induced effects. Approval applies to those targets and effects, never
to a replacement connection or an additional target merely because it occupies
the same position or has the same display name.

If a transfer on the same reviewed target finishes while confirmation is open,
continue with the existing answer. Normal progress or completion that only
reduces the reviewed work does not require another confirmation. A peer
disconnect is not a reason to send a new disconnect warning. When it triggers
automatic closure, coordinate that existing closure as described below.

An independently completed removal is an observed target result, not a reason
to remove it again. Validate the remaining scope and existing final-window
policy before any window-removal commit. Ordinary progress does not authorize
a new generation, new affected work, or unreviewed target expansion. If the
approval is no longer valid for the intended new effects, start none of those
effects and report the changed target; a fresh valid decision is required.
This validation uses current ownership, not a lock held across a modal dialog.

Accepted Q14: finish such an invalid request with
a changed-target explanation and no new effects, rather than automatically
recapturing targets and repeating confirmation. A new request can obtain a
fresh valid decision. Existing independent closures continue. This exceptional
case does not change Q12a's ordinary-progress or Q12b's no-retry behavior.

### Join existing work; retain each request's approval scope

For the accepted A/B example, A's close has already been approved and is
waiting for log cleanup. A subsequent request to close the window reuses A's
existing execution, obtains any required combined confirmation for the
remaining affected targets such as B, and closes the window when its targets
meet the existing closure requirements. The user need not click close again
because A was already closing. Do not repeat A's confirmation, termination,
recording finalization, transfer-window closure, or native release.

Apply the same ownership rule to exact duplicates, bulk closure, and automatic
closure. A broader intent does not expand an earlier approval. An exact repeat
observes the same in-progress execution/result rather than returning a false
completed result or starting another cleanup. New affected targets still need
their own applicable approval. If reviews overlap before either starts effects,
revalidate which targets now have committed closure and coordinate through the
same target records; do not treat the later intent itself as permission.

Keep a per-target closure execution separate from the intents observing it.
Targets are identified by lifetime/generation, not only a reusable UI location.
An intent waits for target executions, not for another whole-window intent
which might in turn wait for it. One target owns its cleanup decisions and
timers: joining does not restart the five-second wait, duplicate its loss
dialog, or replace a prior Continue waiting choice. Existing modal ordering
and bounded cleanup admission still apply.

For a terminal tab, its per-target closure execution alone records when closure
has been accepted. Set that state after review and revalidation, before the
first closure effect. Tab movement, terminal input and reconnect read the same
state. Cancelling during review leaves those operations subject to their usual
conditions. If cleanup leaves the tab open, end the accepted state once. The
user can read the retained output and request closure again. Input and reconnect
then follow the actual connection state, including any completed disconnect.

Keep shared execution/results observable after the tab leaves the live UI
collection. Joining does not duplicate cleanup-capacity charges or transfer
one target's loss permission to another. Any combined cleanup choice applies
only to the owners actually presented and approved.

Lifecycle and native-window callbacks must not create a cycle with the work
being awaited. A session-disconnect notification cannot await the complete
closure which itself awaits session completion. Final-tab removal and native
window-close reentry acknowledge or reevaluate the existing visual commit;
they do not start and await the same whole-window intent again. Keep coordinator
transitions short and release ordering locks before dialogs or asynchronous
cleanup waits. Repeated notifications of the same disconnect must not create
an automatic confirmation loop after its closure decision was declined.

Declining the later window confirmation leaves B and other newly affected
targets unchanged by that request. It does not cancel or roll back A's earlier
accepted closure, nor an independently triggered automatic closure. Outcomes
distinguish new effects from joined or independently completed work so that
"declined" is not mistaken for "nothing anywhere changed". The same applies
when the later request cannot be applied because its approval became invalid.

After approval, existing owners retain issued work until actual completion;
joining cannot shorten their deadlines, bypass required loss decisions, or
claim resources were released because the window was removed. If a target
cannot yet be removed under the existing policy, keep the window's closure
pending or report its actual failure. Do not silently report whole-window
success with an unresolved required target removal. Revalidate placement and
all tab kinds through the existing coordinator for the final visual commit.

## Required lifetime and completion distinctions

Follow the [session/recording contract](session-and-recording.md) and
[transfer contract](file-transfer.md). A closure decision, visual removal,
bounded cleanup adoption, and actual I/O completion are different events.
Keep issued work and memory with their actual owners until completion, even
when the view has closed under the existing loss/cleanup policy. Report known
unresolved outcomes before dismissal where the existing contract requires it.
Do not call early visual closure a successful durable save.

The existing five-second recording-cleanup choice defaults to Continue waiting;
that choice overrides automatic stall abandonment. Close with unsaved data
abandons unissued work and requests cancellation of issued work, retaining
actual ownership. Transport and log cleanup have independent completion rules.
Do not introduce unbounded background cleanup or bypass its capacity admission.

EOF/disconnect does not end retained Rust terminal ownership. A retained
disconnected tab keeps its terminal state. Final tab removal releases the
applicable terminal ownership deterministically through its owner, without
turning WPF Unloaded or tab movement into terminal disposal. Independently
owned immutable results and issued I/O keep their own lifetime and charge.
Explicit, bulk and automatic final removal all apply the associated
transfer-window closure policy above. A failed visual removal must not leave
a still-visible tab with already destroyed terminal state.

## Accepted failure and observation contract (Q13)

Before effects begin, declined/cancelled/invalid confirmation or failure to
present it produces a no-new-effects result. This describes effects of the
closure request, not a rollback of independently completed work or peer loss.
The Q12 rules above distinguish ordinary progress from invalid approval.

After effects begin, the module keeps ownership and observes cleanup for every
target even if a caller stops waiting or a view disappears. Expected failures
produce immutable, bounded results containing actual visual dispositions,
known failures/loss or unresolved outcomes, and remaining owned cleanup.
Failure of one owner does not lose observation of the others or imply that
already started effects were undone. The module also retains and observes
operation tasks independently of WPF event handlers.

Distinguish visual removal, permitted cleanup adoption, and actual release.
Eventual-release observation is read-only and adds no caller cleanup or
disposal obligation. Release completion is not proof that every external
effect succeeded, and no cleanup survival after final application exit is
promised. Expected failures must not become unobserved task faults.

Failure to display the log-loss choice grants no loss permission. Failure to
deliver a required unresolved-transfer report prevents silent dismissal and
is reported as a failure. Preserve state needed by any view whose final removal
failed. These rules also apply to Q10's automatic closure.

The [window contract](persistence-and-windows.md) still orders activation,
closure and move commits. A tab close leaves its window open even after the last
tab closes. Preserve destination-before-source removal for moves, settings/editor
Save/Cancel behavior, and the final-exit rule.

## Verification and implementation readiness

Use the production closure interface with real Rust/session/resource owners.
Controlled confirmation, clock, I/O and layout adapters should make duplicate,
stale and late-completion cases observable; retain actual WPF, file, transport,
and provider integration coverage. Assert resulting connection/recording/view
state and ownership, not private helper counts.

Cover confirmation refusal without close-induced effects, one combined window
review, Q10 bulk/automatic transfer-window closure versus retained-tab disconnect,
once-only receive/tail finalization, cleanup failures and wait/loss choices,
old generation completions, final Rust release with held immutable snapshots,
move/activation/close order, and final-window exit. The current historical tests
do not establish these target guarantees and have not been rerun by this review.

| Complete-operation case | Required observable outcome |
| --- | --- |
| With automatic closure on, fail an initial connection, time out a reconnect before Connected, and interrupt an established connection with a communication error. Use a window with only those three tabs. Inspect it in light and dark themes and in English and Japanese, then run **Close all disconnected tabs**. | All three stay open until the command. Each shows the reason and available output. Its only connection-status icon is a dark exclamation mark on yellow before the name, including a previous connection name. The tooltip and accessible tab name convey failure in the selected language. The command closes all three, leaves the window open and becomes disabled. |
| Cancel the initial connection in a new tab before Connected, with close-on-disconnect off and on. | Both times the affected tab closes without another disconnect confirmation. Other sessions continue. Closing the last tab leaves the window open. |
| In an existing tab, cancel a reconnect before Connected. Repeat with close-on-disconnect off and on, including a tab whose first attempt failed. | Off retains the tab and available output with the broken-link icon. On closes the tab through the usual procedure. |
| With automatic closure off, place an orderly disconnect, a user-cancelled reconnect and a failed attempt in different tab groups of one window. Also open settings, an editor, a tab waiting for authentication, and a connected tab whose recording alone has failed. Leave a disconnected tab in another window. Run **Close all disconnected tabs**. | The command closes the three completed targets in its own window with the usual transfer cleanup. All other tabs remain. The authentication attempt can continue and the connected tab still accepts input. The command is now disabled in this window. |
| End an established connection by local disconnect or orderly peer closure. Exercise the saved automatic-close setting both ways. | With automatic closure on, the affected tab enters the usual close procedure. With it off, the retained tab shows the broken-link icon and is eligible for bulk closure in its own window. |
| The bulk command selects a failed tab. Start a reconnect in that tab before closure commits, and hold it at authentication. | Revalidation detects the new attempt. The command cannot remove the tab or cancel its reconnect, and reports the changed target under Q14. |
| Reconnect from a failed or disconnected tab, then deliver a delayed completion from the previous attempt. | Starting the attempt clears the old icon. The delayed result cannot restore it or close the tab. |
| A reviewed transfer finishes before the answer | The same answer closes the same targets without restarting confirmation |
| Close A, then close its A/B window while A is draining | A's existing execution is joined; B receives applicable confirmation; window closure needs no second click |
| Decline that later window confirmation | No new close-induced effects on B; A's independently accepted close continues and remains visible in the outcome |
| Repeat tab/window/bulk requests, including an automatic-close notification | Shared targets finalize and release once; pending work is not reported as already complete |
| Answer becomes stale for a replacement generation or added effects | No old approval is reused for those new effects; target identity and surviving state remain correct |
| Join while a five-second log choice is pending or Continue waiting was selected | One target decision remains authoritative; joining neither resets its timer nor permits automatic loss |
| A shared target faults, cleanup capacity is full, or visual removal fails | Every owner remains observed; results identify pending/failed targets and actual ownership rather than claiming whole-window success |
| Disconnect, final-tab removal, and native window closure reenter coordination | Existing execution/commit is observed without a cyclic wait, duplicate disposal, or repeated confirmation loop |

These cases use the same production interface and lifecycle entry path; include
real Rust retained state, controlled delays/faults, and existing real integration
checks after implementation is authorized. They are a verification plan, not
newly executed evidence. Q9-Q14 are accepted, including the exceptional
changed-target outcome and final shared understanding. The recording, parser,
and closure designs are all ready for separately requested implementation.
This design review added no implementation, prototype, or runtime validation.
