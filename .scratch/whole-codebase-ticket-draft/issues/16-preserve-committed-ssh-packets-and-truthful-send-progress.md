## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Stop terminal or SFTP output without corrupting the shared encrypted stream, while reporting definitely submitted bytes and uncertain effects accurately through existing terminal and file-operation paths.

Related existing work: [#5](https://github.com/TabTty/TabTty/issues/5), [#6](https://github.com/TabTty/TabTty/issues/6), [#7](https://github.com/TabTty/TabTty/issues/7), [#8](https://github.com/TabTty/TabTty/issues/8). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Prefactor the existing shared writer through both real consumers so packet commitment precedes the first irreversible outgoing cipher/nonce/sequence change. Only definitively uncommitted work may be dropped and refunded; operation cancellation cannot enter a committed packet encryption/write token.
- [ ] Serialize reservations and reply correlation before a packet can cause a peer response. Preserve rekey restrictions, per-channel send order and bounded fair arbitration; no cipher rollback, skipped committed packet or automatic replay is allowed.
- [ ] Expose monotonic definite local-submission prefixes and distinguish uncommitted, committed/pending and uncertain ranges. Terminal mapping emits only newly confirmed complete units once for echo/pacing, never a partial encoded character or a claim of peer receipt.
- [ ] Close may abandon uncommitted application suffixes and schedule after the current packet settles without waiting for a zero-credit frame. Incomplete SFTP frames cannot be followed by new DATA frames; possible complete delivery of an uncertain final mutation packet propagates UnknownOutcome under the existing file-operation policy.
- [ ] Retain actual writer resources and bounded outcome records after stop/observer loss until real settlement. Preserve existing terminal/session cleanup and SFTP mutation/refresh semantics rather than introducing another send-state authority.
- [ ] Verify the real terminal/SFTP send paths with independent packet/cryptographic vectors and controlled precommit, cipher-advance, pending-write, uncertain-final-packet, zero-credit and rekey races; retain real-peer encrypted-stream checks.

## Blocked by

None (can start immediately).
