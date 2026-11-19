## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Apply settings, highlight catalogs, local state and host-key records only after verified publication, and make explicit JSON recovery preserve the observed original while reporting unresolved outcomes accurately.

## Acceptance criteria

- [ ] Migrate every application JSON-store publication and authorized recovery caller to the shared operation interface, preserving format validation, unknown properties, schema/version refusal, location policy and existing recovery choices. Remove the duplicated publication recipe after all such callers use the owner.
- [ ] Only verified complete publication makes settings, highlights, local state or approved host trust effective. Failure, partial preservation or unavailable evidence retains prior effective state and applicable write restrictions; a display failure or cleanup error does not undo a known commit or trigger replay.
- [ ] Explicit restore/default recovery independently validates its candidate and preserves exact observed original bytes under the existing unique recovery policy, even when damaged. Report that recovery copy separately from an ordinary validated backup; never silently restore, merge, empty-reset or overwrite.
- [ ] Coordinate recovery from a quiescent unresolved operation with a gap-free target reservation handover. Failed recovery retains earlier restrictions, and ordinary reopen does not bypass them. Later reconciliation uses current operation revisions without reissuing publication.
- [ ] Integrate pending versus quiescent unresolved outcomes into existing localized save/recovery/close presentation. Retain compact non-secret evidence and artifact locations after view closure, without a hidden process lifetime or recording-specific wait timers.
- [ ] Verify complete settings/highlight/host-trust application paths with real validators and controlled original-preservation failure, external changes, cancellation, late completion, recovery and capacity faults; keep real-file checks for all supported locations.

## Blocked by

- Draft ticket 06: Save connection profiles through owned verified file operations.
