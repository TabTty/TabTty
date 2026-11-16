# Profiles, settings, paths, and window ownership

Specification baseline, 2026-11-10. Completes the internal contracts under
Q109-Q116/Q155-Q158. The trusted profile-activation boundary and saved-setting
timing rules remain unchanged. No implementation or file migration is authorized.
The File Explorer and editor decisions accepted on 2026-11-16 supersede the
earlier profile-editor tab and terminal-only activation descriptions.

## Common JSON rules

Use UTF-8 objects, `schemaVersion: 1`, case-sensitive English property names,
and canonical lowercase hyphenated UUID strings for IDs. New writes have no
BOM; accept one initial UTF-8 BOM on these user-editable JSON files. Reject
invalid UTF-8, duplicate properties, duplicate IDs where uniqueness is required,
comments, trailing non-whitespace, unsupported schema versions/required features,
wrong types, missing required fields, and unknown enum values. No automatic
load-time repair with new-profile defaults. Nullable fields explicitly contain
null; inheritance is explicit rather than inferred from omission.

Each root also has `requiredFeatures`, an array of unique strings, empty for
v1. Reject unknown required features. Preserve unknown optional properties at
each object level as bounded JSON when saving the same supported version;
never execute them or downgrade a newer schema. Field order is not significant;
array order is significant. Pretty-printing need not preserve whitespace.

Structural checks and resource/type bounds apply on every load. Detailed
settings consistency, regex validity, and configuration guidance apply on every
editor open, edit, and save. Connection still performs ordinary runtime checks
such as port availability, supported limits, credentials, and file access;
none introduces a profile approval screen.

## Profile v1 fields

All fields in this table are emitted by the editor and required unless stated
otherwise. Defaults describe newly created profiles, not missing-field repair.

| Object | Fields and interpretation |
| --- | --- |
| Root | `schemaVersion`, `requiredFeatures`, `profileId`, `displayName`, `connection`, `terminal`, `logging` |
| connection | `type`: `ssh`, `telnet`, or `serial`; `waitSeconds`: 30, allowed 1-300; exactly the applicable transport fields below |
| SSH | `host`, `port`: 22, `username`, `authentication`, `liveness` |
| authentication | `methods`: ordered unique subset of `publicKey`, `password`, `keyboardInteractive`, initially `[password]`; `privateKey`: null or credential reference; `password`: null or credential reference; unavailable unsaved input is requested at authentication |
| Credential reference | `vaultId`, `entryId`, `kind`: `privateKey` or `password`; no secret/path/source passphrase |
| liveness | `enabled`: false; `intervalSeconds`: 30, range 1-3600; `unansweredWindows`: 3, fixed in v1 |
| Telnet | `host`, `port`: 23, `initiateBinary`: false; no username, credential, or login-matching fields |
| Serial | `portName`, `baudRate`: 9600, `dataBits`: 8 (7/8), `parity`: `none` (`odd/even/mark/space`), `stopBits`: 1 (1/2), `flowControl`: `none` (`xonXoff/rtsCts/dsrDtr`), `dtr`: true, `rts`: true |
| terminal | `advertisedType`: `xterm`; `receiveEncoding`: `utf-8`; `sameEncoding`: true; `transmitEncoding`: `utf-8`; `jis`; `ambiguousWidth`: 1 (1/2); `enter`; `backspace`; `localEcho`; `font`; `retainedRows`; `highlightSetId`: null or UUID |
| jis | `kanjiIn`: `B` (`@/B`); `kanjiOut`: `B` (`B/J`); both are retained in existing files as inert fields. `receiveKana`: `eightBit` (`eightBit/soSi`); `transmitKana`: `eightBit` (`eightBit/soSi`); applicability is fixed by the codec contract |
| font | `mode`: `inherit` or `override`; `family`: null when inherited, otherwise requested installed-font name; `sizePoints`: null when inherited, otherwise 6-72 |
| retainedRows | `mode`: `inherit` or `override`; `value`: null when inherited, otherwise integer 100-100000 |
| logging | `raw`: false, `text`: false, `terminal`: false, `directory`: `Logs` |

`enter` is `cr` (SSH/serial default), `lf`, or `crlf` (Telnet default).
`backspace` is `del` (SSH/Telnet default) or `bs` (serial default), as accepted
in `../drafts/terminal-interaction-issue.md`. `localEcho` is `off` for SSH,
`followRemote` or `off` for Telnet (default followRemote), and `off` or `on` for
serial (default off). Inapplicable stored terminal fields remain inert and
editable only when relevant. With sameEncoding=true the effective transmit
encoding equals receiveEncoding; keep the explicit saved transmit selection for
later use rather than interpreting it as a live override.

Encoding IDs: `utf-8`, `cp932`, `euc-jp`, `jis`, `cp949`, `cp936`, `cp950`,
`koi8-r`, lowercase `cp` plus the accepted OEM/Windows number, or `iso-8859-`
plus one of 1-11, 13-15. These are the 47 supported identities.
Profile IDs are content identifiers, distinct from local registration identity.

## Application stores

| File | Required payload besides common version fields |
| --- | --- |
| settings.json | `language`: `system` (`en/ja/system`), `theme`: `system` (`system/light/dark`), `bell`: `visual` (`visual/off`), `defaultTerminalFont`: family Consolas and sizePoints 12, `defaultRetainedRows`: 5000, optional `autoCloseDisconnectedTabs`: boolean, default `true`, optional `credentialVaultPath`: absolute `.tttykey` path or null for the default, optional `broadcastAcrossWindows`: boolean, default `false` (broadcast extension) |
| highlighting.json | `sets`: ordered array, initially empty; each has `setId`, `name`, `enabled`, `rules`; each rule has `ruleId`, `name`, `enabled`, `pattern`, `ignoreCase`, `style` |
| style | Nullable `foreground`/`background` as `#RRGGBB`; nullable booleans `bold` and `underline`; null inherits terminal attributes, never a lower-priority rule |
| state.json | `registrations`, `lastWindow`, legacy `selectedVaultPath` for settings that predate the vault path field. Contains application state only. |
| registration | Independent `registrationId`, canonical opening path plus display spelling, `favorite`, nullable UTC `lastUsed`; missing status is observed locally, not a profile rewrite |
| lastWindow | Normal bounds in DIPs, monitor identity/work-area/DPI observations, maximized flag; never restore minimized |
| host-keys.json | Versioned host records defined in the transport contract, independent of vault and portable settings |

Language/theme/bell and saved rule content apply live after successful save.
The auto-close preference applies to subsequent disconnections after saving.
Broadcast scope is captured at Start under Q181, while Q187's broadened
ordinary Stop command follows the saved global preference immediately. Q197
resets selection to all-selected when the scope mode changes; its next-Start
timing is confirmed in Q199. Never persist active
broadcast modes, target checks, per-window first-use/pending-reset state, or
split layouts. See [broadcast and split views](broadcast-and-split-views.md).
Retained disconnected tabs show a broken-link icon before the connection name.
The tooltip and accessible tab name include the localized disconnected status.
Close disconnected tabs in the current window from the tab bar context menu.
Accepted architecture Q10, amended on 2026-11-16, makes both this user-invoked
bulk command and automatic disconnected-tab removal close the affected tabs'
associated transfer windows under the same cleanup/reporting policy as explicit
tab/window closure. Already disconnected targets have no connection warning;
existing necessary cleanup, reporting, and wait/loss choices still apply.
Disconnect or peer loss retains transfer results while the terminal tab is
retained. See the [closure design](tab-and-window-closure.md); these bulk and
automatic-removal behavior changes are not yet implemented.
Q192 (2026-11-14) supersedes the earlier automatic/bulk-closure exception: close
the window when its final tab is removed, counting terminal and settings/editor
tabs together. Empty split groups do not count as tabs. A transfer commits the
destination before closing an empty source window; initial window construction
is not a final-tab removal.
Font/retention defaults and profile-resolved values are captured at next
connection. Store selected values such as `system`, not only resolved OS values.
Keep paste delays/remember checkbox, temporary zoom/echo, unlock state, and tab
contents out of all persistent stores. Translations are locale resources with
stable identifiers, initially English/Japanese; system language uses Windows
display-language preference order with English fallback. Theme follows the
Windows app preference live and affects only surrounding UI.

Limits: profile 1 MiB; settings 64 KiB; highlighting/state/host records 16 MiB
each; JSON depth 16; display names 256 scalars, host 253 ASCII bytes after IDNA,
paths 32760 UTF-16 units, advertised terminal type 64 printable ASCII bytes;
1000 highlight sets, 100 rules/set, 4096 pattern UTF-16 units; 10000 profile
registrations/host destinations; 32 required features of at most 64 ASCII bytes.
Unknown-property content counts toward the same size/depth limits. These are
internal bounded-loading limits, not promises of usable performance at all maxima.

## Opening locations and registration

Resolve local app data through Windows; unpackaged stores live under its
`TabTty` directory. Suggest Documents known-folder `TabTty\Profiles` for new
profiles, but open external profiles in place. Resolve log directory relative
to the profile's opening directory. Accept ordinary absolute DOS/UNC paths and
ordinary relative log paths, normalizing separators and dot components. Reject
drive-relative `C:Logs`, root-relative `\Logs`, device namespaces, alternate
data streams, and embedded NUL. Do not expand environment variables or a tilde.
Long-path handling may use an internal extended prefix; it is not a new path
syntax exposed to users.

Registration identifies the normalized opened location, not the final link
target, file ID, or serialized profile UUID. Preserve case-sensitive-directory
distinctions; compare names using the owning directory's Windows semantics,
not unconditional lowercasing. Resolve equivalent short/long spellings when
the filesystem supplies them without merging distinct link paths. If location
equivalence cannot be established, keep separate registrations rather than
silently join histories. Relative logs continue to use the opened location.

External copies/new locations get new registration IDs with no inherited
favorites/history. Do not infer a move from a duplicate profileId. An explicit
known move is old-registration removal and new registration; no relocation
continuity. Without move tracking, an old inaccessible path can remain visibly
missing until removed; it does not transfer identity to another path. A file
handle ID/digest is useful for edit-conflict detection, not registration merging.

## Save and recovery

Keep an editor draft and original file identity/digest. Save validates, compares
the original snapshot, writes and flushes a same-directory candidate, replaces or
non-overwriting-creates, inspects actual outcome, then publishes the effective
revision. Preserve one previous validated `.bak` generation. Detected external
changes require reload/review, never merge/overwrite automatically. A failure
keeps the draft and last effective settings, reports actual outcome, and must
not falsely label Saved. File replacement is not a multi-file transaction.

If replacement reports an ambiguous outcome, compare target/candidate/backup
against expected bytes; freeze affected writes until the effective file is
known. Never assume every error left names/content unchanged. Corrupt files
stay intact for explicit backup restore, temporary defaults, or exit. Temporary
defaults keep that store read-only until deliberate recovery/recreation after
preserving the original under a noncolliding name. Newer schema is not silent
corruption recovery. Missing first-use files use defaults. Highlight failure
remains nonfatal; local-state save errors are coalesced and do not disconnect.

Temporary defaults and first-use defaults apply only to application settings,
highlighting, and local-state stores. A missing, corrupt, or unsupported profile
cannot start or reconnect a session; restore or explicitly save a valid profile
first. Host-store failures follow explicit recovery and must not silently turn
known hosts into unrecognized hosts eligible for fresh first-use approval.

## Windows, activation, and modal order

One Windows-user application instance of `TabTty.exe` owns terminal and
standalone editor windows. Use an authenticated same-user local activation
channel with bounded operation kinds and paths for the profile and vault
actions, and generation-checked dispatch. Reject invalid operation/path
combinations. An invalid Edit request must never fall through to connection.

Admit file activations FIFO up to the [pending-open capacity](resources-and-validation.md).
While a blocking modal is active, queue without starting file-preparation
deadlines. Route profile connection requests to the last active surviving
terminal window, restoring it if minimized. Create a terminal window when none
exists and exit is uncommitted. Reserve tabs in the order received before
asynchronous loading. Each connection action runs its own ordinary preparation
and connection workflow. Display its failure in its own tab. Q158's separate
profile and log 30-second deadlines begin when their respective stages run.
Standalone editors do not take part in last-active terminal routing.

On initial editor-only activation, display the requested editor windows. If
only editors are open, a normal launch without a file opens a terminal window
with profile selection. The final-tab rule closes its containing terminal
window while other editor windows remain open and keep their drafts. The
application exits after the last terminal or standalone editor window closes.
Only terminal and standalone editor windows count toward this lifetime.

Share one pending unlock interaction per vault file. All waiters for that file
observe its result. Cancellation or failure ends those attempts without prompt
loops. If the dialog owner closes, resolve or transfer the interaction to a
surviving window through the same coordinator. See
[vault ownership and cleanup](vault-format.md#secret-owners-and-cleanup).

Tab transfer retains source ownership until destination insertion can commit.
Commit changes only visual hosting; then remove an empty source window. Failure
or cancelled drag leaves the source intact. A transfer guard prevents last-window
exit between hosts. File activation/close/transfer commits run in coordinator
order; each session has one containing terminal tab, and that tab belongs to one
tab group/window except while protected by a transfer guard. Multiple panes
inside the same tab are views of that session, not additional tab/group owners
(accepted Q165/Q170/Q172, 2026-11-13). Close confirmation cancellation resumes
deferred opens. Committed final
exit rejects new opens and cancels pending requests, without a hidden tray run.

The [transfer window](file-transfer.md#transfer-window-and-operation-ownership)
is associated with its SSH terminal tab and current connection generation.
Detaching or moving that tab preserves its transfer window and work. Include
active file work in the owning tab or terminal window's close confirmation.
Disconnect or peer loss retains the transfer result while the terminal tab
is retained. Actual tab removal, including automatic removal, closes its
associated transfer window under the closure policy above.

Use per-monitor DPI-aware WPF windows. Keep normal bounds in DIPs and map to the
target monitor's current scale/work area, clamping on-screen. Initial normal
size is 1000x700 DIPs, minimum 480x320 where the monitor permits; reduce to the
work area if needed. Restore maximized separately. Detached windows use source
normal size near the drop. Coalesce layout notifications to the final grid per
layout transaction; changed grids go through the session resize contract.

## File Explorer and editors

Register `.ttty` and `.tttykey` with distinct Open and Edit verbs in the
existing unpackaged Windows 11 distribution. Windows controls the default
handler for each extension. The verbs below apply when TabTty is that handler.
The **Show more options** context menu is sufficient for Edit. For multi-select
use the items Explorer delivers under its desktop verb selection limits.

| File type | Open, double-click, or Enter | Edit |
| --- | --- | --- |
| `.ttty` | Register in place after basic loading checks and start one connection attempt in a new terminal tab per delivered file. Open and Enter on a multi-selection use this behavior for every delivered file. | Open one profile editor window per delivered file. Support multi-selection. |
| `.tttykey` | Open the vault editor for a single existing file. | Open the same vault editor for a single existing file. |

Process all admitted profile files independently. A file that fails to load
reports a file-specific error and does not cancel the other files' editor opens
or connection attempts. Preserve order as received, including when Explorer
starts separate invocations. Apply the single-file rule to each vault invocation.

TabTty's profile creation and editing use top-level windows. Edit from Explorer
uses the same profile form, layout, validation, and explicit save workflow as
TabTty's existing-file editor. An Explorer edit saves in place and leaves
profile registration and connection startup to Open. Selecting a saved
credential explicitly may unlock the existing selected vault and select a
reference. Opening or saving the profile does not unlock a vault. A missing or
empty vault is reported by the Explorer editor's picker. Initialization is
available through vault management.

Use one standalone editor window per normalized opened location within each
file type. Repeat requests restore and activate that window, preserving its
draft. Profile edits from Explorer and the profile catalog use the same
location rules as profile registration. Copies at different locations can
open separate editors even when their serialized IDs match. Keep each window
bound to its opening location.

The vault management tab and standalone vault editor share one form and
management operations. The tab uses the selected vault. An Explorer-launched
window opens the requested file with its master password when needed. If the
file is the selected vault, both views share its unlock and committed revisions
with connections. A different file uses its own unlock and operations, leaving
the selected vault setting intact. Report missing, empty, malformed, and
unsupported files without initializing them on Explorer launch. Valid encrypted
manual backups with the `.tttykey` extension open as ordinary files.

When Settings changes the selected vault, any open vault editor keeps its
target; the management tab and subsequent connections use the new selection.
Reuse the newly selected file's unlocked owner if its editor is open. Apply
the change after any active save or unlock for the affected files has settled.
Show the target path for Restore and Archive confirmations. After an Archive
initiated in a standalone editor succeeds, show the archived path and close
the editor after acknowledgment. Restore and password changes keep the editor
open at the same path and refresh its state.

### Editor acceptance cases

| Scenario | Expected behavior |
| --- | --- |
| Select two `.ttty` files and choose Open or press Enter | Two ordinary connection attempts in distinct tabs, with location-based registrations. With no terminal window, create one to host the tabs. |
| Select two `.ttty` files and choose Edit | Two editable windows and independent saves. With no running TabTty, the first launch shows only editors. Editing creates neither a registration nor a connection. |
| Reopen a profile under edit from the other entry point | Restore and activate the existing editor with its draft. A copy at a different location opens separately. |
| One profile in a multi-selection fails to load | Report that path's error. The other admitted file actions proceed. |
| Open or edit one `.tttykey`, or select multiple vault files | Single-file verbs open one editor. A vault invocation carrying multiple paths is rejected before opening or unlocking a file. |
| Change a selected vault entry in a standalone window | The in-app management view and the next credential lookup see the same committed revision without another unlock. |
| Edit a copy of the selected vault at another path | Save to the copy. Connections keep using the selected file. Closing the copy's editor releases its unlock. |
| Switch the selected vault from A to B while both are open | A and B keep their targets. The tab and subsequent connections use B's existing unlock. |
| Close the last terminal window with a profile editor open | Preserve the editor and its draft. A later normal launch opens profile selection. Exiting after the last editor closes ends the shared unlock. |
| Open an empty or missing vault via Explorer, or archive a valid vault in its editor | The empty or missing target produces an error. Successful archive shows the new location and closes its editor after acknowledgment. |

Verify later: all save/recovery outcomes, unknown fields, duplicate IDs/keys,
same-ID copies, missing/removable profiles, relative paths through links, case-
sensitive folders, modal activation, last-tab transfer/close races, minimized
activation, and monitor/DPI removal. No such runtime validation has run.

Primary basis: [Windows paths](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats),
[case-sensitive directories](https://learn.microsoft.com/en-us/windows/wsl/case-sensitivity),
[final-path limitations](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew),
[replacement outcomes](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew),
[Explorer verb selection model](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-employ-the-verb-selection-model),
[Windows default apps](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform).
