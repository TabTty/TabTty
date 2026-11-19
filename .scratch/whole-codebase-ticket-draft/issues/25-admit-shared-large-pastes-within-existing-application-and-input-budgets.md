## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Prepare and send an admitted large paste to one or more frozen targets using one shared original source, bounded per-target work and the existing all-target-before-send guarantee, without adding a separate paste memory pool.

Related existing work: [#12](https://github.com/TabTty/TabTty/issues/12), [#18](https://github.com/TabTty/TabTty/issues/18), [#19](https://github.com/TabTty/TabTty/issues/19). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Broaden the existing 4 GiB application envelope to terminal state plus source/common analysis/preview/capture and actual private paste storage/work. Physically shared source/plan allocations count once; real copies and capture/compaction overlaps count separately. Preserve the 2 GiB per-session terminal-state sublimit.
- [ ] Charge each target conversion/preflight/queued/in-flight work to its existing 2 MiB/256-unit input allowance together with ordinary input and replies. Do not charge the full shared source again per target, create another 2 MiB pool or retain every target complete encoded payload.
- [ ] Remove the former 1 MiB finalized encoded-payload cap and add no replacement encoded-output cap. Source-size eligibility and confirmation do not prove resource availability. Exercise a valid paste beyond the old cap through real session/broadcast paths.
- [ ] All frozen targets complete strict whole-source encoding/finalization checks and required reservation admission before any target payload or bracket-start marker. A final-page encoding error or one target reservation failure leaves every target not sent.
- [ ] Keep the 30-second broadcast preparation deadline, starting after confirmation or at preparation start when no dialog is needed. Dialog time is excluded; waits/retries do not reset it. Check cancellation/expiry after the last target conversion and immediately before first-send commitment.
- [ ] Revalidate source/target generation, encoder/mode, authority and capacity basis without double charging held reservations or redirecting stale work. Before first submission reserve remaining bounded send/Stop/closure peaks; competing new work cannot consume them.
- [ ] Count capacities/reservations until actual release, including late capture results, cancellation, old-generation work and source/native/managed overlaps. Consuming a reservation replaces its future charge rather than adding a duplicate charge. Subsequent terminal mutations still undergo admission.
- [ ] Use existing reject/defer/backpressure behavior and the original deadline for capacity waits. Do not evict retained output, terminate unrelated sessions, serialize all pastes, spool to disk or introduce an independent aggregate setting or 80% memory rule.
- [ ] Verify competing windows, shared versus copied source, terminal growth, ordinary input/replies, delayed cleanup, reserved closure and full-target failure through the production path. Report attributable actual memory/latency measurements without treating accounting as a whole-process RAM guarantee.

## Blocked by

- Draft ticket 24: Send exact long-grapheme input with scalar-safe Stop.
