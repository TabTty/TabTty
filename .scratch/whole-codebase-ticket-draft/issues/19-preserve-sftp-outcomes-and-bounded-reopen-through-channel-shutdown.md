## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Keep the terminal responsive while an SFTP transfer stalls, is cancelled, or explicitly reopens, retaining old channel and file-operation ownership until their actual work settles.

Related existing work: [#5](https://github.com/TabTty/TabTty/issues/5), [#6](https://github.com/TabTty/TabTty/issues/6), [#7](https://github.com/TabTty/TabTty/issues/7), [#8](https://github.com/TabTty/TabTty/issues/8). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Integrate complete-frame stopping and truthful shared-channel send outcomes with existing file-operation cancellation/recovery. Preserve known completed effects and possible UnknownOutcome, never append a frame after an abandoned prefix and never infer non-execution from a missing whole-write success.
- [ ] Retain exactly one opening/live/closing SFTP slot per current connection generation until both identity retirement and channel-owned actual release. Explicit reopen uses the existing bounded connection-wait budget; repeated requests neither reset it nor add unbounded waiters, and ended waits never open later.
- [ ] An unresolved OPEN/CLOSE reports truthful pending/busy/timeout while a healthy terminal continues, without extra closing slots, forced terminal disconnect, ID reuse or automatic reconnect/replay. A live explicit waiter may start only after safe retirement; an explicitly reconnected generation has a fresh slot subject to shared limits.
- [ ] Keep separately transferred parser storage and local provider I/O charged after channel closure. Preserve the already implemented pending-mutation and refresh/recheck guards across view reopen/reconnect; old completions settle only old owners and cannot change new credit, matching, readiness or results.
- [ ] Complete bounded cross-channel service under tiny frames, eight outstanding file requests, blocked local I/O, zero credit and rekey. Allowed control and terminal traffic progress without consumer/reducer/credit cycles or starvation, with mandatory close/control capacity retained.
- [ ] Verify real transfer-window-to-file-operation paths against delayed/uncertain mutation packets, never-arriving OPEN/CLOSE, repeated explicit reopen, old completion after reconnect, parser/provider buffers surviving channel release and threshold exhaustion. Retain real OpenSSH alongside terminal output and existing file-operation regression evidence.

## Blocked by

- Draft ticket 17: Run SFTP through shared SSH channel operations.
