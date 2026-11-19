## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Apply vault selection changes consistently with saved settings and allow editors/windows to close only under the accepted actual-work and quiescent-outcome rules.

## Acceptance criteria

- [ ] Keep Busy rejection before saving settings when old/new vault work is active; preserve the draft/current selection with no automatic later apply. Once quiescent, reserve the affected role transition and settings basis so no conflicting operation starts between validation, publication and adoption.
- [ ] Make verified settings adoption and in-memory selected role/epoch update one ordered visibility transition for new credential requests. Failed/partial settings keep the old role and settings restrictions; use the same coordination for restore/default recovery and later reconciliation adoption.
- [ ] Preserve opened editor paths and reuse an eligible unlocked editor owner on promotion. An A-to-B-to-A switch never revives old contexts or erases stale-location restrictions; an admitted transition retains necessary owner references if an editor closes, while quiescent unresolved metadata does not keep an otherwise unused root alive.
- [ ] Coordinate prompt hosting, operation stop, native/I/O/owner-dependent verification and further inspection admission during closure. Pending work retains its actual owners; quiescent unresolved outcomes may close with visible facts/paths, retaining non-secret restrictions while the app lives and applying ordinary final-exit secret cleanup.
- [ ] All migrating callers use the same owner-role and busy admission authority; no settings/recovery caller bypass remains. Context/subscription release cannot dispose roots, and no hidden lifetime or recording wait timer is introduced.
- [ ] Verify real settings save/recovery and editor/window flows with Busy, partial publication, late reconciliation, destination-editor closure, credential-handoff races, stale switch-back, prompt transfer, delayed owner release and bounded metadata delivery.

## Blocked by

- Draft ticket 09: Own shared vault unlock and credential handoff operations.
- Draft ticket 07: Save and recover application stores through verified operations.
