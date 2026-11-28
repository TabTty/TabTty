# Settings maintenance and ZIP backup

Accepted on 2026-11-28. [日本語](settings-maintenance-ja-jp.md)

## Maintenance tab

Place **Maintenance** alongside **Settings** in **Tools** in the hamburger menu. Open a separate **Maintenance** tab in the invoking window or select it if already open in that window. Remove the maintenance category from Settings and the SSH host-key management entry from the hamburger menu.

In the Maintenance tab, place each row's buttons side by side in the order shown. Use English and Japanese resources for the labels.

| Item | Buttons |
| --- | --- |
| Register TabTty file types with Windows | **Register**, **Unregister** |
| Manage connection profile trust | **Remove individually**, **Remove all** |
| Manage macro trust | **Remove individually**, **Remove all** |
| SSH host keys | **Manage** |
| Manage application settings | **Back up**, **Restore**, **Verify backup**, **Recreate with defaults** |

The macro row applies to macro-enabled builds. Remove the **Recover application files** tab and its standalone `.bak` and `.json` restore action. Use Windows file pickers and a separate window for selecting restore items, owned by the main window hosting the Maintenance tab. Maintenance reads saved settings without committing the Settings draft.

### Windows file types

In the unpackaged distribution, **Register** creates or refreshes the existing integration for the current Windows user. **Unregister** removes the TabTty-owned association candidates, verbs, icons and application entries covered by the existing uninstall helper. Preserve other applications' shared values and the Windows default-app choice. Notify the shell if registration changes. Both commands show completion, no change or failure in an owned dialog, including the extent of any partial change.

The association candidates are `.ttty` and `.tttykey`, plus `.tttym` in macro-enabled builds. Apply icon-only registration to `.tttylog`. Unregistration also covers any existing TabTty registration for macros.

[Explorer and editors](persistence-and-windows.md#file-explorer-and-editors) · [Windows file types](https://learn.microsoft.com/en-us/windows/win32/shell/fa-file-types) · [Windows app defaults](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)

### Individual and bulk trust removal

- For connection profiles, **Remove individually** opens a dialog styled like the macro trust dialog. The user selects a normalized file path and removes its remembered version. Include unregistered and missing profiles. Show an empty dialog when there are no entries. Keep registrations, favorites and usage history.
- For macros, **Remove individually** opens the existing decision selection based on fingerprints, constants versions and argument search. Remove one remembered allow or block. Always open the dialog, including when the list is empty.
- Each **Remove all** action affects its own trust category. Name its scope in a confirmation that defaults to **Cancel**. Admitted connections and running macros continue. Later decisions use the current trust records.

[Trust removal and pending operations](profile-trust.md#trust-removal-and-pending-operations)

### SSH host-key management

**Manage** opens a dialog owned by the main window hosting Maintenance. Show approved destinations, ports, key formats and fingerprints. Delete the selected key after confirmation. Existing sessions continue. Handle host-key approval during each connection according to the transport contract.

[Destination and host records](transports-and-authentication.md#destination-and-host-records)

## ZIP backup and verification

**Back up** opens a backup window. Use the Windows Save dialog to choose the destination for one ZIP containing all supported items. Explain in the backup window that the vault file is excluded and can be saved from **Manage vault** using **Create encrypted backup**.

| Item | Included data |
| --- | --- |
| Settings | Saved application preferences, including the profile folder and vault path settings. A default location resolves to the destination PC's default. |
| Highlight sets | Shared sets and rules with their reference identifiers. |
| Connection profile files | `.ttty` files directly in the configured profile folder, including unregistered files. |
| Connection profile registrations | Committed registrations corresponding to included files, with favorites and last-used times. Record the count of registrations whose files are outside the scope or missing. |

Exclude profile trust, macro trust, SSH host keys, the vault file, all `state.json` data, logs, prior `.bak` files and outstanding work artifacts. Record a store that has never been created as having no data. An existing corrupt or unreadable input fails the backup. Identify the affected item and avoid publishing an incomplete archive as a completed ZIP. Show the destination after verifying the saved ZIP.

Use a versioned manifest that records item availability, entry names, lengths, digests and the mapping from registrations to archived profiles. Apply the existing per-format validation limits plus bounded archive member counts and expanded sizes. Reject unexpected entry names, including absolute and parent-relative names and duplicate destinations. The application determines actual restore paths.

**Verify backup** selects a ZIP and checks the manifest, entry integrity, supported versions and file readability without writing application data. Show the outcome in a dialog. **Restore** runs the same validation before writing.

## Restore from ZIP

Support restoration on the same PC and on another PC. After selecting a ZIP, open the restore window with the included and available items. Start with all checkboxes clear. Selecting **Connection profile registrations** also selects **Connection profile files** and prevents clearing it for as long as registrations are selected. Before **Run**, let the user review the selected items, destinations, replacements and Recycle Bin movements.

### Destinations

- When Settings is selected, use the archived path preferences. Otherwise use the current preferences. Use an existing configured profile folder. Before applying or using a missing non-default folder, show the path and offer another folder or cancellation. Create the default profile folder when needed for profile files.
- When restoring the vault setting, apply the path if its parent exists, even if the vault file is absent. For a non-default path with a missing parent, offer another folder or cancellation. Keep the configured vault file name when another parent is chosen. No vault file is restored or initialized by this workflow.
- Distinguish a missing folder from an access or I/O error. With Settings selected, adopt the confirmed paths as settings. Without Settings selected, an alternative profile folder is a destination for this restore only. Report the destination and explain how to select that folder in Settings to see its registrations.

### Replacements and outcomes

- Validate and stage a profile before placement. Count an identical existing file as restored without rewriting it. For a different file with the same name, recheck that the original still matches what the user reviewed, move it to the Windows Recycle Bin and place the archived version. A failed or uncertain move stops the restore with no replacement or permanent-delete fallback. If placement fails after a confirmed move, stop and report the move and placement outcomes and how to recover the old file from the Recycle Bin. List every file moved to the Recycle Bin in the result dialog.
- For selected registrations, rebind metadata to the confirmed restored files. Update a registration already at the same destination without duplication and preserve destination registrations outside the restore. Report the count of archived registrations skipped because their files were outside the archive. Restoring profile files alone adds no registrations. Restore grants no trust and starts no connection.
- Validate all selected inputs and destinations before modifying them. Apply the existing conflict and publication rules if targets change. On a write failure, stop and show the completed, failed and unstarted items and the verified file changes.

## Separate stores and local recovery

Store registration data in `profile-catalog.json`. Keep the vault selection path in `settings.json`. The pre-release formats and development macro-trust records in Windows Credential Manager require no migration.

`state.json` contains the common format fields `schemaVersion` and `requiredFeatures` and the following local state.

| Field | Contents |
| --- | --- |
| `lastWindow` | Last saved main-window normal position and size, monitor ID, work area, DPI and maximized state. Read during normal startup. |
| `recordingMappings` | Optional digests of paths and remembered drive mappings used to detect changes to a log destination on this PC. |

Read and save `state.json` as part of ordinary use on this PC. Exclude it from ZIP and individual-file backup and restore. After a verified save or recreation, keep no prior generation as a backup. Retain temporary artifacts only as long as needed to verify publication or recheck an uncertain outcome.

Use three independent application-managed files for profile trust, macro trust and SSH host keys. Protect each with Windows DPAPI-NG `LOCAL=user` for the current Windows user and PC. Share protection, validation and publication machinery across stores. Include the format version and trust category in the protected data so the reader can reject a file from another category. An unreadable, invalid or unresolved trust record stops new admissions based on that record and reports the affected store. Distinguish a genuinely absent record from a store failure. A previous copy must not be adopted automatically or revive revoked trust.

For ordinary new profile registration, treat catalog registration and automatic trust as one reported action. Report success and admit a requested connection only after both writes are verified. If one fails, compensate for the changes made by that action and verify the compensation. Resolve uncertain writes against the observed stores before compensating. Preserve preexisting records and independent, later changes. If compensation fails, show the registration, trust and compensation outcomes and the changes that remain in an owned dialog. Do not start the requested connection, and keep trust from an incomplete operation unusable after restart. [Registration and trust consistency](profile-trust.md#trust-removal-and-pending-operations)

For profile unregistration, remove trust before removing the catalog entry. If only trust removal succeeds, keep it revoked, report both outcomes and offer **Retry unregister**. [Unregistration rules](profile-trust.md#trust-removal-and-pending-operations)

Expose **Recheck last save** from the report for the affected unresolved save. Rechecking observes the effects of that save. **Verify backup** reads a selected ZIP. **Recreate with defaults** names the selected file and effects and requires explicit confirmation. It supports settings, highlights, the profile catalog, local state and SSH host keys as separate targets. Recreating the catalog clears registration data. Recreating local state clears placement and remembered log drive mappings. Recreating SSH host keys returns future connections to ordinary host-key review. A damaged profile or macro trust store may be recreated empty through the affected category's **Remove all**, with the affected scope shown before confirmation.

[Common save and recovery rules](persistence-and-windows.md#save-and-recovery) · [Microsoft user and PC protection](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-dpapi-constants)

## Acceptance cases

1. In English and Japanese, open **Maintenance** from **Tools**, then select it again in the same window to activate the existing tab. Check that Settings has no maintenance category and that the host-key list and individual deletion are accessible from Maintenance. Check each row's buttons. Repeat Windows registration and removal, verify unrelated registrations and default-app settings, and check the result dialogs.
2. Remove one profile decision from a set that includes registered, unregistered and moved profiles. Open an empty macro trust dialog, then remove allow and block records individually and in bulk. Keep unrelated trust and registrations.
3. Back up a missing first-use item and a corrupt existing input in separate runs. Verify readable, corrupt and unsupported ZIPs. Restore selected items on another PC and verify that verification alone writes nothing.
4. Cover default folders, existing and absent custom folders and a vault file whose parent exists but whose file is absent.
5. Cover identical and differing profile collisions, failed recycle and failed placement after successful recycle. Verify that the result names actual effects and every recycled path.
6. Restore files alone and registrations with their files. Verify catalog rebinding and excluded registrations. Verify that neither `state.json` nor its data is in the ZIP and that restore leaves `state.json` and local trust stores unchanged. Verify that a successful `state.json` save or recreation leaves no prior-generation backup.
7. Copy a protected file to another PC or corrupt one of the files. Verify that the other trust categories remain available and recreation is scoped. Inject failures into each registration write and its compensation, including across restart, and verify that no incomplete new trust authorizes a connection.
