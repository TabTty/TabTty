## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Change the master password from management or an editor with one complete operation that adopts a new root only after verified publication and accurately reports partial on-disk changes.

## Acceptance criteria

- [ ] Use the complete operation and bound interaction interface with existing new-password confirmation and normalization rules. Generate fresh root/wrapper/payload encryption under the existing vault format; ordinary views do not own the proposed root or commit choreography.
- [ ] Reserve outcome, metadata, root-transfer and actual cryptographic-work capacity before publication. Keep old/candidate roots only for their established owner and active-work lifetimes, clearing temporary master/KEK and working copies after actual use.
- [ ] Only complete verified target and preservation evidence adopts the root/snapshot/revision and shared metadata. A candidate on disk with missing/unreadable preservation retains prior effective state and blocks writes/retrieval while identifying the observed candidate generation; existing copies retain their original passwords.
- [ ] Observation cancellation, UI loss or metadata delivery failure never repeats encryption/publication. Reconciliation inspects existing work and cannot clear independent external-stale restrictions or release roots still used by native/I/O verification.
- [ ] Verify independent cryptographic fixtures, RNG/input failures, native cancellation, verified success after a reported filesystem error, partial preservation and owner-close races through the real management/editor operation and next credential lookup.

## Blocked by

- Draft ticket 09: Own shared vault unlock and credential handoff operations.
- Draft ticket 08: Publish encrypted vault revisions with verified preservation.
