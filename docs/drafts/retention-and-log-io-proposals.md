# Confirmed retained output and log I/O policy

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q146-Q148 recommendations accepted on 2026-11-08. Specification only;
no implementation, runtime testing,
performance measurement, installation, or publication is authorized.

Read with `connection-profiles-issue.md`, `receive-logging-issue.md`, Q106 in
`resize-and-vault-proposals.md`, and Q143 in `ssh-session-proposals.md`.
The numbers below are accepted initial product limits, not measured capacity
or operating-system guarantees. They do not change the three logging formats.

## Q146: Configurable retained-row range

Use an integer range of 100 through 100,000 retained rows, keeping the
accepted default of 5,000. Application settings supply the common default and
profiles choose inheritance or an explicit value. Validate this range in the
editor/save path and apply changes only to the next connection. A runtime value
outside the supported resource range fails clearly without silent clamping or
substitution; this is not a full semantic profile-approval gate.

Continue counting active normal-screen rows plus scrollback at the current
width. The cap is physical rows, not logical lines or only the currently visible
viewport. Preserve Q106's oldest-content retirement/reflow policy and Text's
eligible pre-retirement flush without added newlines. Scrolling the viewport
does not retire content or produce log records. Keep active-screen height at
or below the selected cap; do not increase the cap automatically for a large window.

An increasing saved value does not restore discarded history. Reconnection
creates a new session under its captured settings rather than resizing old
history retroactively. The UI does not offer an unlimited value.

This row range alone is not a complete memory bound. Maximum columns, cell/text/
combining-sequence storage, alternate-screen and receive-only Text projection,
aggregate session memory, and cache/worker budgets remain technical contracts
to complete before implementation. Do not claim that 100,000 arbitrarily wide
or large-text rows fit a particular memory size or promise throughput without
later measurement. Do not silently introduce earlier memory-based eviction as
though it were already authorized by this row-count decision.

## Q147: Bound queued log data and stop recording on storage failure

Use an 8 MiB (8,388,608 bytes) per-session budget for owned pending log
payload across all selected Raw/Text/Terminal formats together. Include bytes
queued, staged for serialization, and still owned by an in-flight write; moving
a buffer between stages must not make it disappear from accounting. Distinguish
this from a FileStream's internal buffer and from the editable terminal state.
Bound queue item/metadata counts and aggregate sessions separately. Shared storage
can be counted once only if ownership and lifetime actually justify that count.

Serialize/write in bounded units and preserve each file's ordering. A batch
may be split for I/O without changing logical content, log parts, or Text
finalization rules. Do not force an incomplete editable Text line to disk just
because the I/O queue is approaching its budget. A retained-content retirement
still submits its eligible snapshot or reports a recording failure before
discarding the terminal backing state under Q106.

If a record cannot be admitted within the budget, stop accepting records for
all selected formats, mark recording failed/incomplete, and report a persistent
status while reception, terminal processing, and the connection continue.
Do not drop an old queue item to accept a new one while claiming a complete
recording. Do not stop serial reads or change handshake settings merely to let
the disk catch up. This extends the accepted mid-write-failure policy, not an
automatic logging pause that can silently resume later.

Also diagnose storage failure when a pending write/flush has no completed
progress for ten seconds. Count completed I/O progress, not new enqueues or
received terminal data. An idle session with nothing to write is not stalled.
This watchdog is an application decision; it does not guarantee that the OS
has cancelled the I/O by ten seconds. Clear distinction between active recording
and explicit close-drain waiting is required under Q148.

Once failed, stop new recording admission, preserve the bounded already-accepted
prefix for best-effort completion where viable, and close/flush as appropriate.
An actual I/O failure may leave a partially written suffix and unsaved work.
Report that uncertainty instead of treating an accepted queue item as durably
saved. Do not rewrite/replay the same failed batch into the same part, truncate
it back on a guess, or invent missing-byte counts when the OS did not report them.
Existing parts remain on disk and are not automatically deleted.

Explicit retry uses new parts for all selected formats, without backfilling
missed data and with the accepted Text resume eligibility. Keep a separate
generation/owner for failed I/O; late completions cannot write to a replacement
handle or report that a new part succeeded. Release/reuse buffers only after
the OS operation actually finishes. Bound outstanding failed/cancelled work;
when cleanup capacity is unavailable, report that retry cannot start yet rather
than accumulating writers indefinitely. Detailed counts and worker ownership
remain technical resource contracts.

## Q148: Normal flushing and an explicit choice for stalled closure

Write in background batches while targeting at most about one second
of application-side buffering under normal scheduling/storage conditions. This
is a scheduling target, not a promise of one-second disk durability or a license
to split files. Do not call a durable flush on every received byte or character.

At pause, receive EOF, disconnect, and orderly close, establish the appropriate
eligibility boundary, finish eligible Text exactly once, drain accepted writes,
then request durable storage with Flush(true) or the equivalent Windows primitive.
At EOF keep recording-part/session ownership under Q143; a later close must not
duplicate Text. At pause/close release the files after completion. Serialize
resume/retry and old-part finalization so unresolved writers do not grow without
bound. An error uses the common recording-failure status rather than claiming
a completed pause/save.

Distinguish application buffering, OS-accepted writes, and a successful explicit
flush request. FileStream FlushAsync alone does not flush the OS's intermediate
file buffers. Even the selected durability request is not a guarantee against
every power, storage-controller, remote-server, or forced-process failure.
Normal running logs may lose buffered data on a crash; do not state a maximum
one-second loss bound. Raw byte fidelity describes successfully recorded payload,
not atomic all-or-nothing files across crashes.

For a requested tab/window/application close, attempt the ordinary drain/flush
without another prompt when it completes normally. If pending log finalization
has not finished after five seconds, keep a visible closing/progress state and
offer Continue waiting or Close with unsaved log data. Default to waiting; do
not automatically discard queued data because the timer expired. Show this
choice once for the affected closing set rather than repeatedly or once per file.
After Continue waiting, retain an accessible explicit close-with-loss action
without opening a fresh dialog every five seconds.

During this explicit close-drain state, the ten-second active-recording watchdog
may diagnose a stall but must not override the user's choice to keep waiting or
silently destroy the accepted queue. No new output is admitted after the final
receive/recording cutoff, so the pending set stays bounded. An actual completed
I/O error is reported as incomplete recording; do not offer a false promise that
waiting can recover a write that has already failed.

Choosing Close with unsaved log data abandons not-yet-issued work and requests
cancellation/cleanup for in-flight I/O, with an explicit incomplete result. It
does not roll back bytes already written, guarantee immediate driver cancellation,
or allow reuse of still-owned buffers. Define safe late-completion cleanup and
aggregate abandoned-I/O limits before implementation. Closing one window must
not terminate unrelated windows/sessions to cancel a stuck writer. During final
application close, retain visible progress until the selected close path; do
not leave an unannounced hidden/tray application alive to save indefinitely.

This saving decision is separate from the existing connected-tab/window close
confirmation and Q143's two-second SSH peer-close grace. It only arises when
there is actual unresolved recording work. Disconnect can leave its retained
tab showing saving/error status; it does not introduce a close confirmation or
silently replay input. Finalizing multiple logs/sessions must use bounded
concurrency and responsive UI, not sequential unbounded waits on the UI thread.

## Evidence reviewed on 2026-11-08

Tera Term's documented shipped settings use 10,000 scrollback rows and a 500,000
maximum; setting-absent fallback values differ. Its implementation also bounds
cells at 40,000,000, so a row count alone is not its memory policy either.
TabTty's accepted 5,000 default and 100-100,000 range are deliberate
product choices. No claim is made that 100 is Tera Term's verified UI minimum.

Microsoft documents separate managed-stream and OS-buffer flushing, and that
CancelIoEx requests cancellation without waiting for completion. Completion may
still be normal, cancelled, or another error; buffers stay owned until completion.
TCP/SSH can apply protocol flow control, while serial safety depends on the
selected handshake and remote device. Windows reports serial overrun/data loss;
pausing reads is not a general substitute for flow control.

- [Tera Term settings/defaults](https://teratermproject.github.io/manual/5/en/setup/teraterm-ini.html)
- [Tera Term 5.7.1 buffer limits](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L492)
- [Tera Term 5.7.1 settings fallback](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/ttpset/ttset.c#L741)
- [FileStream FlushAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flushasync?view=net-10.0)
- [FileStream Flush](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0)
- [Windows buffered file flushing](https://learn.microsoft.com/en-us/windows/win32/fileio/flushing-system-buffered-i-o-data-to-disk)
- [CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
- [I/O cancellation limitations](https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations)
- [WriteFile buffer ownership](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile)
- [SSH channel flow control](https://www.rfc-editor.org/rfc/rfc4254.html#section-5)
- [TCP receive-window flow control](https://www.rfc-editor.org/rfc/rfc9293.html#section-3.8.6)
- [Windows serial handshake](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-dcb)
- [Windows serial overrun reporting](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-clearcommerror)

## Remaining technical contracts

Complete aggregate memory/item/worker limits, terminal cell/column/cluster bounds,
nonlinear Text provenance, event cutoffs and failed-part accounting, bounded
serializer/writer ownership, pause/resume/close races, initial-file-open deadlines,
and driver-cancellation cleanup. Preserve the no-profile-approval trust boundary
while applying required resource guards. Later authorized performance/failure
validation must include slow/full/disconnected storage, partial/cancelled writes,
serial overruns, and many simultaneous tabs. No such tests or measurements have
been performed, and initial implementation readiness has not yet been reached.
