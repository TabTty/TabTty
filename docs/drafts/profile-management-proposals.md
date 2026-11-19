# Profile management and credential references

> The [profile trust specification](../specification/profile-trust.md) supersedes the historical Q112 activation decision and the registration behavior described below.

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q109-Q113 accepted with user amendments on 2026-11-08. The reply's
"119" and "120" refer to Q109 and Q110 in the immediately preceding round.
Q106-Q108 remain accepted, including the `.tttykey` extension. This round does
not authorize implementation, prototypes, tests, installs, or publication.

These are TabTty-specific product choices within the accepted file-backed
profile and portable-vault model. Tera Term's explicit setup load/save behavior
is a reference; its INI format and configuration-directory defaults are not
silently substituted for TabTty's `.ttty` requirements.

## Q109: Default location and profile list

Use a suggested profile save directory of `TabTty\Profiles` beneath
the current Windows Documents known folder. Resolve that folder through the
Windows known-folder mechanism, including redirection, rather than constructing
`%USERPROFILE%\Documents`. Create the destination when the user first saves
there. If resolution/access/creation fails, retain the editor and ask for another
save location; do not silently select an unrelated fallback folder.

Permit `.ttty` files elsewhere. Open them in place; opening a file does not
copy it to the default directory or rewrite it. The suggested save folder is
a convenience, not a restriction on profile location.

Provide a searchable profile list with favorites and recent-use ordering. It
registers files created or explicitly opened/imported through the app, using
their paths; it is not an automatic scan of Documents, the whole disk, or network
shares. Allow explicit registration without connecting. Display profile name,
connection method, destination, and a path when needed to distinguish entries.
Do not put secret values in the list. Missing files remain identifiable and
can be removed from the list. An externally moved file is registered as a new
profile at its new location, not reattached to the old registration (Q110).

Keep app settings, list registrations, favorites, and recent-use information in
per-user local app storage. The later removal of Telnet automatic login also
removes initial Telnet saved-password warning acknowledgements.
There is no profile-action approval store or connection-time approval gate.
The already accepted
default vault location remains under LocalAppData, with explicit file selection.
Do not put saved secrets in Documents profiles or automatically copy a vault
there. ZIP distribution alone does not imply settings beside the executable.

Q155-Q157 in `application-settings-proposals.md` confirm Save/Cancel for app
preferences, immediate application except profile-resolved defaults, separate
portable preference/highlight JSON files, offline manual-copy migration without
an initial import/export UI, and previous-valid-version backups with explicit
recovery. Profile registrations, recent paths, window state, selected vault path,
and host trust are not part of those transferable settings files.

MSIX storage virtualization can change physical AppData paths. Define explicit
ZIP-to-MSIX migration and uninstall-retention acceptance cases in the packaging
branch instead of assuming both distributions automatically share a physical
directory. Profile files remain user-selected files independent of that state.

## Q110: Identity, duplication, renaming, and deletion

Keep profile identity, file location, and display name separate:

| Operation | Confirmed behavior |
| --- | --- |
| Edit display name | Preserve profile ID and file path; duplicate display names are allowed. |
| External rename/move | Remove the old registration and register the new location as a new profile; do not preserve registration identity or offer relocation reconciliation. |
| Duplicate / Save as new profile | Write a new file with a new profile ID. Copy settings and credential references, not secret values. |
| Open an external copy at a different location | Register a separate profile; an embedded matching ID does not merge or redirect existing registrations. |
| Remove from list | Remove registration/favorite/recent-list entries only; keep the file, vault, and logs. |
| Delete profile file | Confirm the target and request recycling through Windows; do not silently fall back to permanent deletion. |

Treat an external move as deletion of the old profile registration followed by
new registration, not continuity of the old profile. Do not transfer favorites,
recent-use history, or editor warning acknowledgement to the new registration.
Removal refers to app registration, never deletion of an unrelated file at the
old path. A copied file whose original still exists leaves the original
registration intact. No relocation-versus-duplicate confirmation is required
before opening the new path. Missing old registrations can be removed when
discovered; no disk scan, move tracking, or automatic search is introduced.

An external copy/move can retain the serialized profile ID. Keep local
registration identity distinct so that this does not merge the two locations.
Opening a file alone does not rewrite it. When the new registration is explicitly
saved in the editor, give its new profile a fresh serialized ID as needed.
Path normalization must avoid spurious duplicates for alternate spellings of
the same location; exact path/identity representation remains technical work.
Vault/entry references are independent and survive the new profile registration.

In-app duplication opens the new profile for edit/save and retains selected
credential references for convenience. Display names and serialized IDs are not
connection-time authorization records; Q112 defines the trusted-file boundary.

Deleting/removing a profile never deletes vault entries, logs, or related
directories. Remove that profile's local registration and editor warning state
too; reopening it creates a new registration and uses basic loading checks,
without mandatory setup review. Active sessions keep their captured settings
and continue; show
this consequence when deleting a profile used by an active session. Reconnecting
requires a valid saved profile again. If recycling is unavailable, the initial
app reports that the file was not deleted and offers cancellation/list removal;
permanent deletion can be performed separately by the user in Windows.

The already accepted ability to open multiple sessions from one saved profile
remains, subject to transport limits such as an occupied COM port. This round
does not introduce session sharing or automatic reconnection.

## Q111: Shared credential entries and migration

Use stable `(vault ID, entry ID)` references with an expected credential
kind. Entry names are human-readable labels and may be duplicated. Do not resolve
by label, file ordering, or the first available entry.

Allow several profiles to reference the same saved entry. Editing that entry's
secret is an explicit shared update: future authentication through all matching
references uses the updated value. Before a destructive or secret-changing
operation, show the affected registered profiles the app knows about; never
claim to enumerate references in unknown files. An edit intended for only one
profile creates a separate entry and changes that profile's reference. Changing
the entry's kind creates a new entry rather than silently repurposing its ID.

Deleting a vault entry does not delete or silently rewrite profile files. Keep
their references unresolved and require explicit correction before those saved
credentials are used again; never attach them automatically to a same-named
entry. Active authenticated sessions remain connected. A profile configured for
unsaved credentials continues to use the existing input-prompt workflow.

For migration, copy the desired `.ttty` files and the encrypted `.tttykey` file
separately. On the other PC, explicitly select that vault, unlock it with its
master password, and resolve exact IDs/kinds. A missing or different vault is
an actionable configuration error, not permission to substitute a secret. The
selected path is PC-local; stable references do not require the original path.
There is no first-use profile approval on the new PC. Ordinary vault unlock,
exact reference resolution, and SSH host-key checks still apply. The editor
performs its normal settings validation whenever the file is opened there.

Do not introduce a combined profile-and-secret export archive, plaintext export,
or automatic cross-vault merge in this round. Files and encrypted backup provide
the initial migration mechanism. Schema/key-import details remain separate.

## Q112: Trusted profile activation and editor validation

The user rejected the proposed PC-local action-approval mechanism. Treat a
profile explicitly opened for connection, including first-use double-clicks and
externally edited files, as trusted configuration after the basic checks in
Q113. Do not require setup review, destination/credential approval, log-path
approval, or a comparison with previously approved settings. Registration or
moving to another PC does not introduce such a gate. No approval flags or
semantic approval digests belong in the file or in local connection policy.

Perform detailed settings validation and consistency checks whenever a file is
opened in the settings editor, whether or not it was externally edited, during
settings changes, and before saving. Show actionable field errors in that UI.
An explicit connection action does not automatically open the editor merely
because it encounters a new or changed profile. Merely opening the editor
does not connect, create logs, or retrieve secret values to inspect settings.

The user's later 2026-11-08 scope change removes Telnet saved-password/automatic
login settings and their editor warning from the initial release. The earlier
editor-only warning rule therefore has no initial feature to apply to; revisit
credential-use warnings when future macros are specified. Do not replace it with
a connection-time prompt. Q112's rejection of setup/load consent for imported
profiles, a new PC, or external destination changes remains in force.

The accepted SSH host-key trust and cryptographic checks, first-use vault
unlock, exact vault/entry/kind lookup, and handling of unavailable credentials
remain runtime requirements. A trusted profile can use the selected saved
entry after those requirements are met; no separate per-profile allowlist is
introduced. Missing references fail clearly rather than selecting another
entry. Trust in a profile does not assert that a network peer is authentic.

Read and interpret one settings snapshot for a connection attempt. Resolve
relative log paths from that opened profile's directory. A moved or externally
edited profile may therefore select a different destination, saved credential,
or log folder without an extra review dialog. Existing sessions retain their
captured settings. This trusted-configuration boundary is the user's deliberate
product choice, not an assurance that arbitrary external files cannot direct
credential use or log writes. Parser/resource bounds and protocol checks still
apply; no protection against malicious trusted configuration is claimed.

The 2026-11-08 guidance recheck distinguishes parser safety, semantic validation,
and authorization from consent UI. Microsoft recommends validating activated
files; OWASP recommends bounded parsing and both syntactic and semantic checks
before use. Neither mandates the rejected local-approval UI. Confining the full
semantic validator to the editor is TabTty's explicit trusted-configuration
choice, not a claim of fully applying that broader external-input recommendation.

## Q113: Explicit saves and external changes

Use an editor with explicit Save/Cancel. Validate on editor open, while editing,
and before saving; starting
a connection uses a saved profile, never an unsaved editor-only configuration.
Save and Connect may combine the two ordered actions, with connection starting
only after successful save and any applicable editor-time warning handling.

Capture source-file identity/location and an original-content digest when
editing. Before saving, read/compare the actual file again. On a detected edit,
deletion, or replacement, do not silently overwrite: offer reload, save as a new
profile, or return to the editor. Reload explicitly discards unsaved edits only
after the user chooses it. Save as new follows the new-ID rule. Never claim that
a pre-save hash comparison alone eliminates races with non-cooperating writers.

Write the complete validated JSON to a temporary file in the same directory,
flush, and replace with a previous-version backup where supported. Keep one
previous profile version as `.ttty.bak`; it contains no saved secret values.
Do not treat backup files as ordinary double-click connection profiles. Report
failed saves and preserve unsaved editor contents and recoverable file candidates.
Replacement API failure behavior must be handled explicitly; do not promise that
every failure leaves the original path unchanged or that power loss is harmless.

Read the actual saved file on each explicit connect/reconnect and perform only
basic format/interpretation checks: readable bounded input, valid UTF-8/JSON,
supported format version, required fields/types, and unambiguous values that
can be interpreted. Reject malformed or ambiguous duplicate required fields,
unsupported required semantics, and resource-limit violations. These are
loading safeguards, not a second execution of the full settings editor's
semantic/consistency validator or a security-change comparison. Do not add
permission confirmations for a parseable external profile.

Runtime operations still report actual errors, such as an unavailable COM port,
unresolvable credential, invalid transport parameter, or unwritable log path.
Do not silently repair settings or substitute credentials/destinations. Initial
log-open failures still prevent transport startup under the accepted logging
policy. Treat filesystem notifications as optional hints, not an authoritative
change detector. Preserve unknown optional fields when saving, or explain that
the file cannot safely be round-tripped; do not silently discard behavior.
Exact schema and numeric loading bounds remain technical specification work.

External edits, renames, or deletion do not mutate or reconnect active sessions.
Their captured log destination remains fixed, including paths originally
resolved from a relative `Logs`. If the source profile is missing, keep the
session usable but require locating/restoring/saving a valid profile before
reconnecting; do not replay the old captured settings as an ad-hoc connection.

## Acceptance examples to carry into the detailed specification

| Scenario | Confirmed outcome |
| --- | --- |
| Open a `.ttty` from another folder | Perform basic loading checks, register/open it in place, and connect using its settings; no setup review or automatic copy. |
| Duplicate a profile that uses a saved password | New profile ID/file, same selected entry reference if retained, editor validation, no duplicate secret value. |
| Open an Explorer copy with the same serialized ID | Register a separate profile without merging or redirecting the original; no ID-rewrite side effect merely from opening. |
| Move a profile externally | Remove the old registration and register the new location as a new profile; no relocation-continuity choice. |
| Rename a profile display name or reformat its JSON | Same registered profile at the same location; no connection approval mechanism is involved. |
| Change the destination externally while its vault is unlocked | Use the newly read settings after basic checks, ordinary credential lookup, and required SSH host-key checks; no profile review. |
| Move a profile whose log destination is relative `Logs` | New connections resolve the new location without log-path approval. Existing sessions keep their old destination. |
| Delete one of several profiles using an entry | Only that profile file/list item changes; vault entry and other profiles remain. |
| Delete a shared vault entry | Existing sessions remain; affected saved references fail resolution rather than selecting a different entry. |
| Open copied profile/vault files on a second PC | Select/unlock the vault, resolve IDs, then follow ordinary connection/host-key handling; no local profile approval or original-PC dependency. |
| External editor saves while the TabTty editor is open | Detect the conflicting version and offer explicit resolution; do not silently overwrite. |
| Open any profile in the settings editor | Run detailed settings and consistency validation regardless of origin or whether content changed. |

## Remaining boundaries

These decisions cover profile management, trusted activation, and validation
timing. They do not
complete the exact JSON schema, path/identity normalization, credential kind and
key-import schema, distributed/cloud editing, exact settings recovery, MSIX
migration/uninstall behavior, or authentication method/protocol details. They
do not change the accepted log/echo/paste semantics or introduce macros.

## Primary references reviewed on 2026-11-08

- [Tera Term 5.7.1 setup handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/setup/teraterm.html)
- [Tera Term 5.7.1 setup save](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/menu/setup-save.html)
- [Tera Term 5.7.1 configuration locations](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/commandline/teraterm.html)
- [Windows known folder paths](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)
- [Windows known-folder flags](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ne-shlobj_core-known_folder_flag)
- [Packaged desktop file virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [MSIX flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
- [Microsoft file activation input validation](https://learn.microsoft.com/en-us/windows/apps/develop/launch/handle-file-activation#remarks)
- [OWASP input validation and safe parsing](https://cheatsheetseries.owasp.org/cheatsheets/Input_Validation_Cheat_Sheet.html)
- [OWASP reference identifiers and authorization](https://cheatsheetseries.owasp.org/cheatsheets/Insecure_Direct_Object_Reference_Prevention_Cheat_Sheet.html#identifier-complexity)
- [OWASP authorization checks](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html)
- [OWASP secret access control](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#23-access-control)
- [Microsoft File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0)
- [Windows replacement failure states](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
- [Windows recycle operation flags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)
- [Windows file-operation cancellation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-getanyoperationsaborted)
- [FileSystemWatcher limitations](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)
