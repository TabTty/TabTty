# Confirmed application settings, portability, and recovery

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

> The 2026-11-28 [settings maintenance specification](../specification/settings-maintenance.md) supersedes Q156's archive exclusion and Q157's standalone backup restore entry point. It also specifies the split local stores and protected trust-file recovery.

Status: Q155-Q157 accepted on 2026-11-09. Q155 is clarified: profile changes
apply on the next connection; application settings normally apply immediately,
except application defaults resolved through a profile, which apply on the next
connection. Specification only; no implementation, prototype,
installation, runtime test, or publication is authorized.

Read with `profile-management-proposals.md`, `connection-profiles-issue.md`,
`highlighting-details-proposals.md`, `terminal-external-effects-proposals.md`,
and `specification-frontier.md`. Preserve the existing separate authentication,
host-trust, and `.tttykey` vault contracts. This round does not alter them.

## Existing storage decisions, not new questions

Q109 already places application settings, profile registrations, favorites, and
recent-use information in per-user local application storage. ZIP deployment
does not imply portable settings beside the executable. Profiles remain external
`.ttty` files opened in place; the selected portable vault remains a separate
`.tttykey` file. Resolve the local application-data location through Windows,
not a hard-coded user-directory string. No cloud synchronization is introduced.

For unpackaged deployment, use the TabTty subdirectory of that local data root.
Keep packaged storage behind the same logical ownership boundary, with the
actual location resolved for the selected packaging configuration. Exact
ZIP-to-MSIX migration and uninstall-retention behavior remain packaging work;
do not assume that all MSIX packages virtualize storage identically or share
the unpackaged application's physical files automatically.

## Q155: Explicit Save/Cancel for application preferences

Use an application-settings editor with Save and Cancel. Edits remain a
draft until validation and persistence succeed. Closing/cancelling the editor
leaves effective settings unchanged. Do not add a separate Apply button or live
preview of unsaved language/theme/bell choices initially.

Apply the user's general timing rule to every setting, rather than only to the
examples below. Saved connection-profile changes take effect on the next
connection. Saved application settings take effect immediately by default.
An application setting inherited/resolved through a connection profile is
captured for the next connection instead of changing existing sessions.
Here immediate means after a successful explicit save, not unsaved preview;
the accepted Save/Cancel and persistence-failure behavior remains in force.

Q181's broadcast-scope setting is an explicit exception added on 2026-11-13:
capture it when broadcast Start occurs. Saving a changed scope does not alter
an active mode; after that mode stops, the next Start uses the saved value.
When already stopped, the next Start uses the saved value directly. See
[broadcast input and split views](broadcast-and-split-issue.md).

Q187 (2026-11-14) separately defines the Stop command: after saving all-window
scope, ordinary Stop Broadcast ends broadcasts in all windows, including
currently active window-local modes. This does not change their input scope
before Stop/Start and does not add a separate Stop All command.

Q197 (2026-11-14) supersedes selection preservation across scope changes:
the new scope starts with all terminal tabs selected. The
[consolidated contract](../specification/broadcast-and-split-views.md) presents
the timing integration accepted in Q199: a successful scope-changing save
invalidates remembered selections in all windows, and each affected scope's
next Start applies its all-selected reset. Active modes/frozen operations keep
their current recipients until stopped. Unchanged-scope Stop/Start preserves
checks; the setting does not retain separate selections for local/global modes.

After a successful save, apply UI language, application theme, and bell mode to
all windows without restarting TabTty. Keep language values `en`/`ja`/`system`
and theme values `system`/`light`/`dark`; system mode follows its accepted OS
resolution rules. An OS theme change while already using system mode still
updates live without opening/saving the editor. Relocalization must not dismiss
dialogs, lose drafts, reconnect sessions, or change internal identifiers.

Default terminal font and retained-row limits are examples of application
defaults resolved through a profile. Existing sessions retain their captured
values; saving either the profile or its inherited app default does not change
them. Mark the scope in the editor. Shared highlight sets have their own
[management and editor save workflow](../specification/persistence-and-windows.md#highlight-set-management)
and apply saved revisions live. Temporary zoom and paste-delay memory
retain their own accepted lifetimes and must not be persisted by this editor.
Changing a profile's selected highlight-set reference remains a next-connection
change; editing the contents of an already selected shared set remains live.
Explicit temporary session actions, such as zoom or the supported local-echo
toggle, retain their existing behavior and do not rewrite the saved profile.

Window geometry, profile-list registrations/favorites, and recent use continue
to be saved automatically as local state. Their automatic saving does not make
unsaved preference drafts effective. Do not require opening Settings to record
a new profile registration or window geometry.

## Q156: Separate portable settings files without an initial import/export UI

Use versioned UTF-8 JSON files for preferences and shared highlight sets:
`settings.json` and `highlighting.json`. Keep machine-local state in a distinct
store, rather than mixing paths and session history into the transferable
preferences. Show/copy the resolved settings directory path in Settings so users
can locate the files; this does not add an Explorer-opening action for logs.

Initial scope has no dedicated application-settings export/import wizard,
combined archive, automatic merge, cloud sync, or executable-directory portable
mode. Document an offline manual-copy procedure: completely exit both TabTty
instances, back up destination files, then copy the desired preferences and/or
highlight file. Read them on the next launch. This replaces the corresponding
file as a whole, not a merge of selected fields or rule sets. Do not hot-reload
externally edited preference/highlight files during a running application.

Preserve highlight set/rule identifiers in a copied file so separately copied
profiles can resolve those same IDs. Replacement can remove destination-only
sets; preserve the existing nonfatal unresolved-reference behavior. Copying a
profile by itself still does not embed/copy a shared set or substitute a same-name
set. App preference copying does not rewrite any `.ttty` file.

Transferable settings contain language/theme/bell/default font/default retention
and highlight definitions, not profile registrations/recent-use paths, window
geometry, selected vault location, host-key trust, passwords, private keys, or
unlock state. Keep trust/secrets in their existing separate stores. Copying
settings never connects automatically or imports trust. Vault/profile migration
continues through their own files and established workflows.

JSON is not encrypted; names and regex patterns can themselves be private user
content. Do not describe these files as anonymized or safe for public sharing,
and do not embed authentication secrets in them. Exact schema, bounded fields,
version transitions, and ZIP/MSIX physical-location instructions remain technical
contracts. This decision does not add an initial settings file association.

## Q157: Keep a previous valid version and recover explicitly

Keep one previous validated backup per settings/highlight file. Prepare
and validate a replacement before committing it; never intentionally replace
the only good backup with a known-corrupt input. Keep this separate from the
existing encrypted-vault and `.ttty` backup policies. First save has no prior
version to back up. There is no transaction promise across the two JSON files.

If an explicit save fails, keep the editor draft, retain the last successfully
applied in-memory settings, and report the failure instead of saying Saved.
Do not silently switch to another directory or apply an unsaved preference as
though it were persisted. Handle uncertain file-replacement outcomes by
checking the actual files before retry; do not assume an exception proves that
every file name still refers to its pre-save contents. External changes found
before saving require reloading/review rather than an automatic overwrite or
merge. Detailed conflict/retry and disk-flush contracts remain to specify.

When a settings file is corrupt or otherwise cannot be interpreted, retain it
and offer explicit recovery: restore a validated compatible backup when available,
start this run with defaults without overwriting the file, or exit. Present the
affected file and the available choices, rather than silently resetting settings.
If highlighting alone fails, retain the existing nonfatal behavior: disable the
unavailable definitions with a visible status and recovery access, without
blocking trusted-profile connections or disabling unrelated preferences.

Starting with temporary defaults keeps the affected store read-only for that run
until the user deliberately restores it or recreates it after preserving the
original under a noncolliding recovery name. Do not keep invalid files writable
through an unrelated later autosave. If the original cannot be preserved, do
not overwrite it. Other unaffected stores and existing sessions can continue.
Recreating preferences must not delete profiles, logs, the vault, or host trust.

Treat a recognized but newer unsupported schema separately from corruption:
explain that a newer TabTty is needed, preserve the file, and permit only
temporary defaults or exit unless the user explicitly selects a compatible
backup/recreation workflow. Never silently downgrade or strip unknown-version
data by loading only familiar fields and saving them back. Missing files on
first use use defaults and are not corruption; missing referenced highlight sets
retain their established nonfatal indication. Missing/unreadable local-state
stores need similarly explicit status without silently erasing recoverable data.

Automatic local-state save failures should not disconnect sessions or repeatedly
open dialogs for window movement. Coalesce a visible persistence-error status;
do not claim that failed profile-list/geometry updates will survive restart.
These policies do not promise recovery after every crash, power loss, storage
failure, or manual deletion.

## Evidence reviewed on 2026-11-09

Microsoft defines local application data as per-user, non-roaming storage.
Its MSIX documentation distinguishes package/runtime/trust configurations and
limits AppData-virtualization statements to virtualized applications. Packaging
and uninstall behavior therefore need an explicit TabTty contract, not an
assumption based only on the `.msix` extension.

File.Replace supports a replacement and backup but requires application handling
of first-save and failure cases. Windows documents partial failure states;
per-file replacement is not a multi-file transaction or a universal power-loss
guarantee. Application validation determines whether a backup is usable.

System.Text.Json provides depth controls but does not by itself establish all
byte/token/count limits or version compatibility; unknown-property defaults are
not a safe replacement for a deliberate schema-version policy.

- [Windows known folders](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)
- [.NET special-folder definitions](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder?view=net-10.0)
- [Packaged desktop application behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [MSIX flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
- [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0)
- [Windows replacement failure semantics](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
- [JSON depth limits](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.maxdepth?view=net-10.0)
- [UTF-8 JSON reading](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/use-utf8jsonreader)
- [JSON deserialization behavior](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/deserialization)

No files described by these accepted product formats have been created as actual
TabTty settings. No save, restore, corruption, relocalization, or migration test
has run. Initial implementation readiness has not yet been reached.
