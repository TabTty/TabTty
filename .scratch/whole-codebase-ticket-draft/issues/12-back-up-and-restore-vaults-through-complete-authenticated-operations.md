## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Create verified encrypted backups and explicitly restore a chosen authenticated generation through the same management operation interface, including an allowed rescue copy after external-stale detection.

## Acceptance criteria

- [ ] Backup the exact authenticated encrypted generation to an explicitly selected non-overwriting destination and verify it without changing the source revision or implying a new password. An external-stale rescue copy may use only a retained authenticated image and must identify its generation/stale status without clearing source restrictions or retrieving credentials.
- [ ] Provide one Restore operation for locked and unlocked states. Independently authenticate/validate the backup, enforce an unlocked owner ID or structurally established original ID as specified, and preserve recognizable-newer-version/nonzero-flags refusal; invalid/missing/empty originals do not supply an invented ID.
- [ ] Preserve exact original target bytes under the existing recovery policy and adopt only after complete target/preservation proof. Keep the editor target path and distinct old-password generations; missing authenticated rescue evidence is an explicit unavailable result.
- [ ] Bind file pickers, master input and confirmation to the admitted target/image. Keep actual work and shared encrypted-image ownership through cancellation, and report target, preservation, restrictions and cleanup separately without blind retry or automatic initialization.
- [ ] Exercise real backup/restore operations with independent locked/unlocked fixtures, mismatched IDs, damaged originals, future-version refusal, destination collision, stale rescue, cancelled input and uncertain publication; verify actual encrypted bytes and preserved originals.

## Blocked by

- Draft ticket 09: Own shared vault unlock and credential handoff operations.
- Draft ticket 08: Publish encrypted vault revisions with verified preservation.
