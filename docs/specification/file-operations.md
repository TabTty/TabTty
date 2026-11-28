# File-operation module design

Status: design accepted and ready for implementation, 2026-11-14. Architecture
interview Q1-Q11, including the final shared-understanding review, are accepted.
Implementation and prototyping require a separate explicit user request.
No implementation tests were run for this consolidation.

The file-transfer design review on 2026-11-28 settled how the module retains
retry results, tracks temporary file creation and cleanup, verifies remote
reads and reports progress, and resumes mutations after both panes refresh.
All four changes are ready for implementation.

The [architecture interview](../drafts/architecture-deepening.md) records the
decisions and alternatives. The [SFTP file-transfer contract](file-transfer.md)
continues to define file behavior, confirmation choices, path handling, limits,
and transport requirements. This document defines responsibility and lifetime
for those behaviors, including Q8-Q10's admission and retention decisions.

## Responsibility and seams

The file-operation module in `TabTty.Core` owns copy/upload/download, deletion,
rename, and folder creation. Its implementation is independent of WPF. The
transfer window and tests use the same interface. Copy and deletion retain
their distinct algorithms inside the module.

```mermaid
flowchart TB
    W[Transfer window] -->|operation interface| F[File-operation module / Core]
    T[Policy tests] -->|same interface| F
    F -->|internal I/O seam| P[Production adapter]
    F -->|internal I/O seam| D[Controlled test adapter]
    P --> L[Windows file operations]
    P --> S[Existing SFTP client]
    P --> O[Application-wide I/O ownership]
```

Production and test adapters are alternatives at the internal seam. The diagram
does not imply that both perform an operation together.

The production I/O adapter uses one remote read completion check for downloads,
remote-to-remote copies and transfer-history hashes. For a copy it also supplies
the size used by the progress display. See [Reading a remote file](file-transfer.md#reading-a-remote-file).

| Responsibility | Owner |
| --- | --- |
| Browsing, selection, path/name input, drag validity, dialogs, localized display | Transfer window and presentation adapter |
| Admission, captured request, preflight, confirmation validity, execution, cancellation, results, retry selection, transfer history | File-operation module |
| Windows operations, source/destination attributes and identity checks, file data, native recycling, low-level completion facts | Production I/O adapter, reusing the useful `TransferFiles` knowledge |
| SFTP framing, request replies, channel identity, transport flow control and close | Existing SFTP/SSH implementation |
| Outstanding-I/O ownership and shared resource/serialization lifetime across windows | Application-wide I/O management, without references to UI controls |
| Scripted I/O completion, failure, target mutation, and delayed completion | Controlled test adapter |

The external seam exposes file operations and observable results rather than
recursive algorithms or mutable execution state. This gives callers leverage
through a deep interface and concentrates policy changes and verification for
locality. Deleting the existing I/O knowledge would spread complexity back to
callers; it remains behind the internal seam.

## Operation interface

Use named copy, move, delete, rename, and folder-creation operations with opaque,
module-owned operation identity. Exact C# names and internal decomposition are
implementation choices. The interface also supports observing read-only state,
answering confirmation requests, requesting cancellation, retrying eligible
failed transfer items, Undo, Redo, and closing the operation context after a
close decision. A deletion request specifies recycling with confirmed fallback
or direct permanent deletion, as selected by the command and pane.

An operation request captures its selected entries and destination/name as
bounded, stable values. Preserve remote raw-name bytes and the selected path
information; subsequent browsing does not retarget the operation. Captured
attributes do not replace the required live rechecks.

Cross-pane move uses the same staged copy, final commit, and conflict prompts
as an ordinary transfer. After a confirmed file commit, verify that the
destination still matches the staged file and recheck the source before issuing
source removal. Keep the source when a copy is skipped, fails, cannot be
verified, or cannot preserve its metadata. For directories, remove only empty
source folders after their admitted children have been processed. Leave any
uncopied or newly appearing contents in place. Windows source removal uses the
checked file handle. SFTP v3 uses a metadata check followed by REMOVE or RMDIR,
which are separate server operations. Unknown outcomes use the existing refresh
and close-report rules. Repeating Paste can attempt an eligible, verified source
still in the cut clipboard. **Retry failed items** remains a copy operation and
cannot remove a source.

Same-side Paste uses the same captured paths, name checks, and staged-copy
policy as other copies. A same-side move uses a non-replacing rename where
supported. A move that needs a copy, such as a move between local volumes,
uses staged copy and verified source removal with the same non-replacement
policy. Apply download-only requirements to files received from the endpoint
into Windows. Determine whether two paths name the same directory or lie in
the same tree from provider facts before admitting a same-directory duplicate
or a directory paste. Reject a directory paste into itself or a descendant.

The module decides admission. A disabled button is a presentation of that
decision, not its enforcement. Reject a start when the connection is unavailable,
another operation is active, Q8/Q10 block mutations, or required capacity is
unavailable. Rejected starts do not clear the latest results. Preserve the
existing immediate-admission model without adding a queue of future operations.

Read-only state identifies the owning module and operation and describes
admission, progress, pending confirmation, per-item results, availability of
retry, Undo, and Redo, and outstanding I/O or cleanup. Observation begins with
current state. Updates may coalesce progress while pending questions and
retained results remain observable. Presentation dispatch discards callbacks for
a detached view or an old owner. Detaching observation does not itself request
cancellation.

Ordinary rejection and file-operation failure produce observable outcomes; a
caller must not infer success from acceptance or infer no effects from
cancellation. The module completes execution management itself. No caller-side
`End` call or disposal of an operation handle is required to release admission.
Logical operation completion and actual I/O completion remain distinct.

## Confirmation and cancellation

The module determines when to confirm deletion, replacement, permanent-delete
fallback, and a transfer that cannot be undone. It supplies the question's
facts and permitted choices. The presentation adapter owns the dialog and
locale resources. Names and internal identifiers do not depend on translated
text.

Each pending question belongs to one operation on one connection. Only its
current answer can authorize the next step. Cancellation, disconnect, or an
accepted close invalidates that question; delayed or duplicate answers cannot
authorize work. Waiting for a dialog must not prevent invalidation, and waiting
for local user input remains excluded from the existing I/O deadlines.

After a valid answer, the module rechecks the target as required by the transfer
contract. An observed target change requires a fresh overwrite decision. A
failed or missing response is never treated as approval. Recycling failure
allows the specified permanent-delete question only when fallback is valid;
an unknown recycling outcome does not authorize a second destructive attempt.

Cancellation stops new operation work while preserving confirmed effects and
required cleanup. Already-issued I/O retains its original owner. Keep per-item
completed, skipped, failed, unfinished, and unknown outcomes distinct. A lost
mutation reply can produce unknown outcome; do not replay an uncertain commit
or remove its final path as cleanup. Retain unresolved temporary paths when
cleanup cannot be confirmed.

While cancelled work has outstanding I/O, the affected connection's transfer
window cannot admit another mutation. Browsing and closing remain available;
available browsing controls do not guarantee a stalled provider will answer.
Once I/O has retired, apply the contract's required refresh/rechecks before
admitting further mutations. A UI refresh alone does not retire pending I/O.

After an unknown outcome, the connection's file-operation owner decides
when the listing requirement is satisfied. The transfer window reports when
each pane starts reading and which result it actually displays. The owner
checks the current window's two displayed listings against the latest unknown
outcome and the actual completion of outstanding I/O. A newer unknown outcome
requires fresh reads on both sides. If a pane starts a replacement read while
recovery is pending, only its new, successfully displayed result can qualify.
Closing the window discards its listing evidence. On the same connection the
admission restriction survives, and the new window supplies its own pair.

## Results and retry

Retain one latest operation's bounded result set together with its eligible
retry metadata. Replace both only after the next operation is accepted. A
rejected start, including capacity rejection, preserves both. Closing the
transfer window discards display results and retry metadata; reopening does not
restore a history of operations. A disconnected window that remains open keeps
its results.

The file-operation module owns this switch at admission. The transfer batch
reports which copies failed and qualify for retry during that execution. For
example, if a copy of A fails and the next Paste is accepted but skips every
source during verification, the window shows the Paste results and offers no
retry of A. A Paste rejected at admission preserves A's result and retry.

Retry uses the module's retained failed transfer items on the original live
connection, with current source, destination, and collision checks from the
beginning. Unknown outcomes are not ordinary failed items and must not become
automatic replay. Preserve existing retry scope; create, rename, or delete do
not become replayable merely because they share the module.

When **Retry failed items** is accepted, remove the previous failure rows for
the items being retried. Keep the other retained rows visible in their existing
order, including any unresolved cleanup paths. Show the current item in the
progress display. As each new outcome occurs, append it before any omission
notice. The existing retention limits apply throughout the retry. If the user
cancels, report items the retry had yet to reach as unfinished. A subsequent
retry selects only eligible copies that failed during this execution.

Per-item outcome, reason, path facts, and metadata/cleanup warnings are
independent of rendered strings. Plan, result, and retry storage share the
existing entry/path/memory bounds. Report capacity or omitted detail explicitly
rather than maintaining an unbounded event history. Display retention never
determines when outstanding-I/O ownership can be released.

For the latest accepted operation, retain unresolved outcomes and their known
paths ahead of ordinary result details. Omit ordinary entries such as successes
first and indicate the omission. Before creating a destination temporary file,
ensure its path can be retained within the existing limits if cleanup cannot
be confirmed. If capacity is still insufficient, report the capacity failure
before starting the write for that file. Keep earlier unresolved details.

For each file, the file-operation module tracks the temporary path and intended
destination from exclusive creation through final rename and cleanup. For each
SFTP request, the SFTP adapter reports whether the request could have reached
the server and which reply, if any, confirmed its outcome. The module uses
those facts to distinguish an uncertain temporary creation from an uncertain
final rename.

If the temporary creation request could have reached the server and its outcome
is unknown, show one unresolved entry in both the result list and the close
report. Identify the intended file by name, explain that temporary creation
could not be confirmed, and show the full temporary path. The final rename was
never issued, so the attempted transfer made no change to the final name.
Clean up a discovered temporary file only when the module can establish that
this operation created it. A matching filename alone does not establish that.

When folder creation, rename, or deletion has an unknown outcome, the
file-operation module records the operation kind and the full paths once
per item. The results list and close report show the full path of the requested
folder for creation, the old and new paths together for rename, and the path
of the item for deletion. Both views count a rename as one unresolved item.
A successful refresh of both panes leaves that record available for the
remainder of the latest operation's result lifetime.

During an operation, the transfer window lists the currently retained entries
in the order their outcomes occurred. At the next display update, remove any
ordinary entries displaced to make room and show newly retained entries in
that same order. Once any detail has been omitted, keep one localized notice
at the end of the list for that operation ("Some results were omitted.").
The notice gives no count. Later retained entries appear before it.

The file-operation module exposes one observable, read-only result list for
its lifetime. The transfer window binds to that list and localizes each row's
reason for display. Core owns the order, retention limits and omission notice.
The window uses the same list through operation completion and the next
accepted start. An ordinary append updates the affected rows without copying
the full list. The close report selects unresolved entries from the same
retained results.

## Transfer history

The file-operation module owns Undo and Redo. Keep the window's single history
entry independent of the displayed results, the cut/copy clipboard, and the
failed-copy retry selection. A new accepted attempt can replace displayed
results without invalidating history if it is confirmed to have made no changes.
[Undo and Redo contract](file-transfer.md#undo-and-redo)

Build the history entry from confirmed effects. Retain the exact source and
destination paths, including raw remote name bytes and the final duplicate
name. Keep the verification facts needed to distinguish newly created objects
and their directory contents from existing objects, plus confirmed source
removal for moves. Account for these facts under the existing operation
metadata bounds. Keep a complete record or report that history is unavailable.
Use execution outcomes to tell new creation, replacement, existing-directory
merge, skip, and unresolved changes apart. Rendered result labels and success
counts cannot establish Undo eligibility.

Run Undo and Redo through regular admission, cancellation, confirmation,
verification, and provider ownership. Revalidate the full entry before the
first mutation, then recheck each effect at the mutation boundary. A stale or
unverifiable preflight identifies the affected path and leaves the history
entry available. Follow the existing rules for external writers and unknown
outcomes. Undo and Redo preserve only the effects confirmed to have happened,
and a late result cannot re-enable history after disconnection or closure.

## Connection, window, and resource lifetime

Bind each operation context to its terminal session's current connection.
Tab movement changes visual ownership without reconnecting or replacing it.
After reconnect, an explicit transfer open uses a new module and the new
connection; old answers, progress, results, and retry state cannot attach to it.

After the existing close confirmation, invalidate questions and stop work.
Use the existing bounded close policy and expose known unresolved paths or
outcomes before dismissal. Closing a view releases its presentation bindings,
not the ownership of I/O that still exists.

The close report shows the count for each kind of unresolved outcome and
initially displays the first ten unresolved entries in total. The user can
inspect every remaining entry and its known path in the same report before
dismissing the window. Use the latest operation's unresolved outcomes for
both direct window closure and closure with the terminal tab.

Application-wide management retains that I/O's original connection/operation,
completion tracking, provider reservations, and required mutation serialization
until actual completion. Reopening a view on the same connection consults this
ownership and inherits the admission restriction. A new connection does not
release old I/O reservations or local serialization even though its operation
module is new. Late completion may retire original work; it cannot complete a
different operation or re-enable the old connection.

Keep the shared four-transfer-batch limit separate from the eight-local-provider-
call limit. Create, rename, and delete do not acquire an extra application-wide
four-batch restriction. A local provider call remains charged until it actually
returns. Required local mutation serialization follows the actual I/O lifetime,
not the lifetime of the task waiting for it, and still covers the contract's
same-resolved-destination mutations across tabs.

This ownership does not lock external writers or provide an SFTP filesystem
snapshot. Invalidate old remote request identities according to the existing
channel-close rules. Final application exit retains the existing bounded-wait
and no-hidden-background-application policy; this design adds no persistent
cleanup process, operation restoration, or promise that exit proves all external
effects completed.

## Internal I/O contract and verification

The internal seam supplies actual attributes, identity, data, and execution
outcomes to the production policy module. The adapter must distinguish work
not issued, confirmed completion/failure, and work with an unconfirmed effect;
it must also expose when outstanding provider ownership can actually retire.
Cancellation of a waiter cannot manufacture a confirmed failure or release
resources still owned by the provider. Keep approval and retry decisions in
the policy module rather than implementing them again in each adapter.

Use the controlled adapter to schedule attribute changes, failures, lost replies,
and delayed completions. Where deadlines are involved, control time so the test
does not depend on wall-clock races. Exercise the real module through the same
named operations, observation, answers, and cancellation used by the window.

| Required check | Observable result |
| --- | --- |
| Child deletion fails | Completed effects remain; the affected top-level tree stops; failed and unfinished entries are distinct; other selected trees continue where permitted. |
| Target changes during confirmation | Recheck detects it and obtains the required fresh decision. |
| Cancellation/disconnect wins before an answer is used | A late approval causes no new mutation. |
| A mutation may succeed but its reply is lost | Unknown outcome is retained; no automatic replay or final-path cleanup occurs. |
| An upload of `report.csv` loses the reply to exclusive temporary creation. The final rename was never issued. | The result list and close report each count one unresolved temporary creation and show `report.csv` with the full temporary path. Discovering that name alone does not trigger deletion. |
| The temporary creation request could not have reached the server, or the server explicitly refused it. | Report the transfer failure or cancellation as applicable. There is no unresolved temporary creation for this request. |
| A folder creation, rename, or deletion is issued once, its reply is lost, both panes refresh, and the user closes the window | The latest results and close report each show one unknown outcome for the affected item with the same operation kind and full path information. A rename shows the old and new full paths on one row. The mutation was issued only once. |
| Ordinary results fill the display before a later temporary file's cleanup is unconfirmed | The close report still gives the temporary path. If space for another temporary path cannot be retained, that file's write never starts and the capacity failure is visible. |
| During a transfer, an ordinary result is displaced and an unconfirmed cleanup is recorded | At the next display update, the list shows the retained entries in occurrence order, including the temporary path, with one omission notice at the end. Subsequent results appear above that notice. |
| Reserving space for a temporary path displaces an ordinary entry and the write then waits | On the next display update the displaced row is gone and the omission notice is visible, even while the next file outcome is still pending. |
| One transfer batch reports twelve unconfirmed temporary-file cleanups and one unknown final rename | Closing by either route shows counts of twelve and one, ten entries initially, and access to all thirteen results before dismissal. |
| Provider finishes after cancellation | Q8 admission remains blocked and resources remain owned until actual retirement. |
| A local read starts before outstanding I/O retires and displays afterward. The remote read starts after retirement and displays successfully. | Further mutations remain blocked until a qualifying local read is displayed. The remote result can count while it remains current in this window. |
| After I/O retires, the local pane displays its listing and the remote read fails. The user refreshes the remote pane or corrects its path and presses Enter. | While the remote read is pending, the local listing and selection stay visible and mutations remain blocked. Displaying the qualifying remote result satisfies the listing requirement without rereading the local pane. |
| A newer unknown outcome occurs between two successful pane reads, or the user closes and reopens the transfer window between them. | The two results cannot satisfy admission together. Both current listings must qualify for the latest unknown outcome in the open window. |
| View closes/reopens during outstanding work | Results are discarded, but I/O ownership and same-connection admission restrictions survive. |
| Reconnect occurs before old completion | A new owner is used; old results/answers cannot affect it and old resources are not released early. |
| A start is rejected, then another is accepted | Rejection preserves the latest result/retry set; acceptance replaces it together. |
| Copy A fails. A Paste of B is first rejected, then accepted after B has changed. | After rejection, A is still displayed and eligible for retry. After the accepted Paste skips B, its result explains the skip and A is no longer eligible. |
| A, B, and C are copied in order. Only B fails. Retry is accepted on the original live connection. | Only B is rechecked and copied. While B is running, the result list shows the successes for A and C in order, and progress identifies B. When B succeeds, the list shows A, C, B. |

Retain real SSH/SFTP tests for channel separation, request draining, bytes,
rekey, refusal, and terminal responsiveness; retain Windows tests for recycling,
identity/no-follow behavior, replacement, and dialog integration. The test
adapter establishes policy behavior, not those platform/protocol guarantees.

During later implementation, move existing policy assertions onto this shared
interface and preserve their observable guarantees. Keep integration checks that
cover different behavior; avoid duplicating tests solely for private helper
methods. Recheck the existing resource and release gates before delivery.

## Readiness

The [interview's source-review findings](../drafts/architecture-deepening.md#contract-discrepancy-to-preserve-in-implementation-preparation)
identify the old-window reconnect path, premature mutation-serialization release,
and display/retry lifetime mismatch that later implementation must address.
They are not executed reproductions or fixes in this documentation work.

Q11 confirms this consolidated design and establishes implementation readiness
for this architecture candidate. The user requested continuing to candidate 2,
retained-terminal-buffer observation. Implementation still requires an explicit
user request. ADR 0001 and ADR 0003 remain unchanged; no macro
runtime, new SSH connection strategy, or additional dependency is introduced.
