## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Verify the integrated ownership changes under controlled races that cross recording, SSH, clipboard paste, file publication and vault selection/closure. Each feature ticket remains responsible for its own passing acceptance checks; this ticket adds focused cross-owner evidence without replacing the existing deployment and sustained-workload gates.

Related existing work: [#15](https://github.com/TabTty/TabTty/issues/15), [#39](https://github.com/TabTty/TabTty/issues/39). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Record the exact integrated source/build, fixtures and applicable configured internal capacity values. Use ordinary application operations and real Rust/session/vault behavior with independent expected bytes and controlled peer, file, time and interaction adapters; preserve independent real-file, cryptographic and protocol coverage.
- [ ] Combine a zero-credit SSH send, a generated terminal reply and slow recording admission/completion. Prove permitted receive/control/rekey and SFTP work progress without reducer/consumer/credit cycles. In a separate controlled close case, preserve recording protection through Continue waiting and a later explicit loss choice without treating transport-only work as unsaved logs.
- [ ] Start a confirmed clipboard paste through the configured large-source path while SFTP/provider work and terminal retention are active. Stop or close at controlled commitment boundaries; verify shared source/private work accounting, no first payload before all-target preparation, exact definitely submitted units, no replay of uncertain input and healthy reserved closure where applicable.
- [ ] Close an editor or the last window during master-password publication or owner-dependent verification, and during a reserved selected-vault settings transition. Observe physical file facts, effective settings/root adoption and pending versus quiescent release separately; no new selected path may be visible with the previous credential owner, and no stale credential handoff is delivered.
- [ ] Deliver old-generation I/O, native/consumer completion and observation notifications after Stop, view closure, explicit reopen or a replacement generation. Only original ownership settles; live handles/buffers/roots/reservations remain charged until actual completion, without false release, resurrected bindings or duplicate prompts/publication.
- [ ] Keep the scenarios separately controlled so a failed invariant has an attributable cause. Run the relevant existing regression paths on the integrated candidate and report passes, failures, blocked checks and not-run external checks distinctly. Do not copy historical performance results or recreate the broader deployment/performance scope of the related acceptance issue.

## Blocked by

- Draft ticket 03: Preserve recording work throughout close waiting and later loss decisions.
- Draft ticket 18: Deliver SSH terminal startup and interaction through the shared transport and channel owners.
- Draft ticket 19: Preserve SFTP outcomes and bounded reopen through channel shutdown.
- Draft ticket 11: Change the vault master password through one owned operation.
- Draft ticket 15: Coordinate vault selection and closing with owned operations.
- Draft ticket 26: Configure large clipboard pastes with bounded full-text confirmation.
