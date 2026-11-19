## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Create and update connection profiles through one owned verified-publication operation, so the editor reports the actual target and backup outcome even after cancellation, replacement errors, or delayed filesystem completion.

## Acceptance criteria

- [ ] Introduce complete typed publication requests and read-only operation observations through the working profile create/update path. Admission reserves finite versioned record, byte and artifact capacity synchronously and returns an operation identity; rejected admission changes nothing and transfers no input ownership.
- [ ] Keep bounded immutable candidate bytes and the original editing basis with format-owner validation evidence. Coordinate target names, missing destinations, observed identities and known aliases before mutation, including replacement identity changes; refuse conflicting or unsafe coordination without claiming an atomic compare-and-swap against arbitrary writers.
- [ ] Perform same-directory candidate creation, required access policy, write/flush, baseline recheck and non-overwriting create or replacement. Inspect target, candidate and required backup using individually acquired bounded images; adopt the profile revision only after complete target and preservation proof.
- [ ] Distinguish complete publication, intact original or absent create, conflict, partial preservation, unavailable evidence and cleanup failure. Keep the prior effective revision and draft and freeze affected writes for partial/unresolved outcomes. Reconciliation observes the same operation without publishing again, rebasing or restoring automatically.
- [ ] Separate observation cancellation, prevented publication, issued mutation, per-inspection resource release and final retirement. Retain actual I/O and owned artifacts until completion or verified handoff; preserve restrictions across view close/reopen, allow visible quiescent unresolved closure, and bound completed history without evicting unresolved facts.
- [ ] Exercise the real profile editor-to-file path with controlled post-replacement errors, missing/unreadable backups, stale bases, alias collisions, candidate mutation, cancellation, late inspection, reused candidate names and capacity exhaustion; retain real supported-filesystem checks and exact file-content assertions. Keep profile registration publication a separate outcome.

## Blocked by

None (can start immediately).
