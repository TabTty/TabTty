# Profiles, settings, paths, and window ownership

Specification baseline, 2026-11-10. Completes the internal contracts under
Q109-Q116/Q155-Q158. The [profile trust specification](profile-trust.md)
supersedes the original activation rules. It specifies a future implementation.
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
editor open, edit, and save. Apply the profile trust decision at connection
time, before preparing the log or starting the transport. A permitted
connection also checks port availability, supported limits, credentials and
file access.

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
| settings.json | `language`: `system` (`en/ja/system`), `theme`: `system` (`system/light/dark`), `bell`: `visual` (`visual/off`), `defaultTerminalFont`: family Consolas and sizePoints 12, `defaultRetainedRows`: 5000, optional `autoCloseDisconnectedTabs`: boolean, default `true`, optional `credentialVaultPath`: absolute `.tttykey` path or null for the default, optional `broadcastAcrossWindows`: boolean, default `false` (broadcast extension), optional `maxPasteTextBytes`: positive integer byte count, default `536870912` (512 MiB) |
| highlighting.json | `sets`: ordered array, initially empty; each has `setId`, `name`, `enabled`, `rules`; each rule has `ruleId`, `name`, `enabled`, `pattern`, `ignoreCase`, `style` |
| style | Nullable `foreground`/`background` as `#RRGGBB`; nullable booleans `bold` and `underline`; null inherits terminal attributes, never a lower-priority rule |
| state.json | `registrations`, `lastWindow`, legacy `selectedVaultPath` for settings that predate the vault path field. Contains application state only. |
| registration | Independent `registrationId`, canonical opening path plus display spelling, `favorite`, nullable UTC `lastUsed`; missing status is observed locally, not a profile rewrite |
| lastWindow | Normal bounds in DIPs, monitor identity/work-area/DPI observations, maximized flag; never restore minimized |
| host-keys.json | Versioned host records defined in the transport contract, independent of vault and portable settings |

Clarified whole-codebase architecture Q26 retains `maxPasteTextBytes` as an
application-wide user setting independent of connection profiles, alongside
shared immutable source storage in the existing resource budgets. It measures
one paste's actual selected Unicode text before target encoding at two bytes
per UTF-16 code unit, including unchanged CR/LF but excluding the terminating
NUL, allocation padding, metadata and other clipboard formats. Older supported
files without this optional field resolve to 536,870,912 bytes. Accept a positive
finite whole-byte integer with checked representation; zero, negative,
fractional or overflowing values are invalid, not an unlimited sentinel. Unit
presentation must not silently round the saved byte value. Use existing validated
Save/Cancel and persistence-failure behavior, with English/Japanese resources
explaining the source-size measure and per-operation scope.

A successful save changes subsequently started paste operations. Existing
capture, preview and send operations retain the limit captured at their start;
lowering it cannot truncate or cancel those operations. Cancel or failed save
changes no effective value. Text within the configured limit still requires
ordinary validation and [resource admission](resources-and-validation.md).
Q28-Q29's independent aggregate policy is superseded: add no aggregate-limit or
memory-percentage field/control. Paste delays/Remember remain application-run-only
as below. Candidate 6's final Q23 review was accepted on 2026-11-18; this
documentation change implements no file migration.

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
Closing the last terminal or settings/editor tab leaves its window open. This
applies to manual closure, automatic closure on disconnect, and the bulk close
command. A single-group window returns to its welcome screen. A window with
split groups retains its layout. The new-tab button remains available.
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

The profile editor explains that enabled recording opens the log destination
before the terminal connection starts. A UNC path, a mapped drive, or a relative
path beside a profile on a share can cause Windows to authenticate to that
share and store received output there. Explorer's Edit command opens the profile
for inspection without starting its connection or recording.

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

Successful new registration automatically trusts the registered revision. Manual
connection to an unregistered profile offers the three connection choices even
if the revision is already trusted. Opening an editor, saving, or registering
a profile does not prompt for trust. Unregistering removes all profile trust
for that opened location. See the [profile trust specification](profile-trust.md).

## Save and recovery

Keep an editor draft and original file identity/digest. Save validates, compares
the original snapshot, writes and flushes a same-directory candidate, replaces or
non-overwriting-creates, inspects actual outcome, then publishes the effective
revision. Preserve one previous validated `.bak` generation. Detected external
changes require reload/review, never merge/overwrite automatically. A failure
keeps the draft and last effective settings, reports actual outcome, and must
not falsely label Saved. File replacement is not a multi-file transaction.

If replacement reports an ambiguous outcome, compare target/candidate/backup
against expected bytes; freeze affected writes until the publication and required
preservation are resolved under the contract below. Never assume every error left
names/content unchanged. Corrupt files
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

### Verified file operations

One shared module owns the complete physical operation for profile, application
store and encrypted vault publication: same-directory staging, access rules,
writer exclusion where required, write/flush, original-basis recheck, publication,
target/candidate/backup inspection and owned-artifact cleanup. Its interface
accepts complete typed requests for non-overwriting creation, existing-file
update or explicitly authorized restore. Format owners retain serialization,
schema validation, unknown properties, allowed locations, preservation policy
and effective revision adoption; the vault owner retains all cryptography,
secret ownership and credential-use decisions. Streaming logs and SFTP file
operations retain their own contracts. A profile save and registration/settings
save remain separate outcomes, without a multi-file transaction.

Admission is synchronous and does not wait on filesystem I/O. It reserves bounded
capacity and returns an operation ID with a read-only operation view before a
caller can cancel observation. Rejection transfers no ownership and changes no
files. Identity resolution, verification and I/O run in owned execution after
admission; acceptance alone does not permit mutation. Bind each accepted
request to its original editing basis and immutable candidate bytes, with
format-owner evidence of rules, identity/revision, length and digest. Neither
later draft edits nor another save may change that basis. Required copies and
buffers count against the limits. Do not expose a caller-managed staging or
cleanup protocol or a second public per-file owner. An ordinary caller awaits
one initial observation; bounded current status, reconciliation and actual
resource release can be observed separately without a required monitoring loop.
Discarding the operation view does not end owned work or impose a cleanup duty
on the caller.

Coordinate the normalized opened location, parent namespace and final name
before mutation, retaining the reservation through replacement and unresolved
outcomes. Coordinate known aliases and observed physical identities without
merging registrations or changing opening-path semantics. Track old and
installed identities separately; replacement can change the file ID. If safe
coordination of potentially conflicting work cannot be established, refuse or
defer mutation. Keep existing directory case/short-name semantics and per-format
access, lock and location rules. This provides no atomic compare-and-swap
against arbitrary external writers.

Compare each artifact using one bounded acquired image for its digest and
validation; do not combine facts from different reads into a single observation.
The three files do not form an atomic snapshot. Detected changes invalidate
the affected evidence. Equality with a previously authenticated encrypted image
can establish that vault generation without another unlock; independently
selected restore content needs its own authentication and schema validation.
The common module receives no keys, master password or plaintext credentials.
Verification and domain result handling do not run under a mutex they may need
to reacquire; logical operation admission remains held.

Report target evidence, required preservation, overall outcome and remaining
resources/artifacts separately, with the operation ID and a monotonically
increasing observation revision. Owners ignore obsolete observations rather
than reverse newer effective state. Filesystem return codes alone do not decide
whether publication occurred.

| Evidence after relevant issued work settles | Outcome and adoption |
| --- | --- |
| Candidate verified at target; all required preservation verified | Complete publication; the format owner may adopt the effective revision. Cleanup may still be pending. |
| Candidate verified at target; required backup/recovery copy missing or different | Known partial publication; retain prior effective state/draft and freeze ordinary writes. Report that the target changed and preservation failed. |
| Candidate verified at target; required preservation unreadable/unverified | Target publication is known, preservation unresolved; retain the same adoption restriction without hiding the known target fact. |
| Original update/restore target verified intact, or create destination verified absent | No effective publication by this operation; distinguish an intact original from a new file that was never created. Report candidate/backup side effects separately. |
| Unexpected revision or detected external change | Conflict; preserve observed external data and required owned recovery material. No automatic merge or overwrite. |
| Target missing/damaged or evidence unavailable | Distinguish absence/damage from inability to inspect; retain freeze/recovery restrictions. No automatic restore or empty reset. |
| Only cleanup fails or remains pending | Keep the verified publication result; report retained artifacts and actual-resource lifetime. Do not describe it as unchanged or nothing saved. |

Ordinary backup preservation concerns the previous validated generation. An
explicit restore instead preserves the exact observed original bytes, even if
damaged, under a unique recovery name; that copy is not a validated backup.
Partial publication is neither ordinary success nor an unchanged file. Reinspect
unavailable evidence, but do not loop or recreate a known missing artifact.
Explicit recovery or the next application run must establish the next usable
state; ordinary reopening cannot clear a partial/unresolved restriction.
Recovery is a new authorized operation. Transfer the target reservation without
a gap only once earlier potentially mutating work ends and the domain owner
selects recovery with the required evidence/artifacts still owned. Failed
recovery cannot clear the prior restriction. A later run validates actual files
normally; it does not retroactively make the previous save successful.

Observation cancellation ends only that wait. Serialize cancellation of work
against issuing publication: cancellation alone proves no publication only
when issue was prevented. Once mutation may have been issued, retain actual
work and inspect its result. Seeing an old/absent target is not final while issued
work could still change it. Concurrent reconciliation joins the same inspection pass
and never republishes, rebases or restores implicitly. Later reconciliation may
admit another pass; release of an earlier pass's I/O and verification resources
is not proof of current quiescence. Only retirement ends inspection admission.

Keep operation/close guards while actual I/O or owner-dependent verification is
pending. Once both have ended, allow editor closure or application exit with
partial/unresolved facts and target/recovery paths visible in the existing close
decision. Serialize this quiescence check and owner termination against further
inspection/verification admission. Late callbacks cannot use a released owner.
Leaving does not mark the draft saved, delete recovery files or approve degraded
adoption. If the application continues, compact evidence and write restrictions
survive view closure; later inspection uses retained non-secret proof or an
ordinarily authorized live domain owner. On exit, domain secret lifetimes end
normally. Operation IDs are not durable, and no journal, automatic recovery,
hidden process lifetime or post-process cleanup guarantee is introduced. The
recording five-/ten-second rules do not apply to these file operations.

Set finite, versioned internal limits on operation records, owned bytes and
retained artifact count/bytes. Reserve capacity before admission/allocation;
reject or defer new work without effects when full. One conflicting target has
at most one potentially mutating/unresolved operation, apart from the explicit
recovery handover. Release large buffers after actual work ends when compact
evidence is sufficient. Never evict unresolved restrictions by TTL, cancellation
or memory pressure. Account for recovery ownership transfers explicitly; normal
user files and validated `.bak` files are distinct from transient/unresolved
artifacts. Delete only files whose ownership is established, not a replacement
at a reused candidate path. Retire only resolved, resource-free records; unknown
or retired IDs cannot replay publication and completed history stays bounded.
Existing preparation limits are not the entire commit/artifact budget. Fix exact
new limits in internal policy at implementation start and test at/beyond them;
no deadline can guarantee completion of unresponsive OS I/O.

The [accepted 15-case verification plan](../drafts/whole-codebase-architecture-deepening.md#candidate-3-verification-plan)
exercises the ordinary interface with real format/cryptographic validation,
controlled filesystem/time adapters and real-file integration, including UNC
only for formats supporting it. It is required future verification, not executed
evidence. Q7-Q11 of that review establish implementation readiness for this
design; implementation and prototyping still require a separate user request.

## Windows, activation, and modal order

One Windows-user application instance of `TabTty.exe` owns terminal and
standalone editor windows. Use an authenticated same-user local activation
channel with bounded operation kinds and paths for the profile and vault
actions, and generation-checked dispatch. Reject invalid operation/path
combinations. An invalid Edit request must never fall through to connection.
Before forwarding or accepting file paths or arguments, both processes verify
the installed TabTty peer and match its user, logon SID, Windows session,
elevation state and mandatory integrity SID. A mismatch or an unreadable token
ends forwarding before either process discloses paths or dispatches actions.

Admit file activations FIFO up to the [pending-open capacity](resources-and-validation.md).
While a blocking modal is active, queue without starting file-preparation
deadlines. Route profile connection requests to the last active surviving
terminal window, restoring it if minimized. Create a terminal window when none
exists and exit is uncommitted. Reserve tabs in the order received before
asynchronous loading. Apply the connection decision to each file and continue
preparation only for a permitted connection. Display its failure in its own tab. Q158's separate
profile and log 30-second deadlines begin when their respective stages run.
Standalone editors do not take part in last-active terminal routing.

On initial editor-only activation, display the requested editor windows. If
only editors are open, a normal launch without a file opens a terminal window
with profile selection. The application exits after the last terminal or
standalone editor window closes.
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

In the unpackaged Windows 11 distribution, register or refresh Explorer file
types for the current account only when the user selects **Register TabTty file
types with Windows** under **Maintenance** in the Settings tab.

Register distinct Open and Edit verbs for `.ttty` and `.tttykey`. Windows
controls the default handler for each extension. The verbs below apply when
TabTty is that handler.
The **Show more options** context menu is sufficient for Edit. For multi-select
use the items Explorer delivers under its desktop verb selection limits.

| File type | Open, double-click, or Enter | Edit |
| --- | --- | --- |
| `.ttty` | Apply the manual connection decision to each delivered file. Register its opened location if the user chooses registration. Start a connection only for a permitted file. | Open one profile editor window per delivered file. Support multi-selection. |
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
reference. Opening or saving the profile does not unlock a vault. Every credential
picker reports a missing/empty vault without creating it. Ordinary Unlock and
authentication retrieval also do not initialize. Creation or guarded empty-file
initialization requires an explicit vault-management operation under the
[complete vault contract](vault-format.md#complete-vault-management-operations).

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
Reuse the newly selected file's unlocked owner if its editor is open. Reject
the settings change as Busy before saving while affected work is active; a
later explicit save can apply it once that work has settled, as detailed below.
Show the target path for Restore and Archive confirmations. After an Archive
initiated in a standalone editor succeeds, show the archived path and close
the editor after acknowledgment. Restore and password changes keep the editor
open at the same path and refresh its state.

### Vault selection and operation hosting

Management tabs and standalone editors use the vault module's named complete
operations on a non-secret file-bound context. UI owners provide localized
forms, input, file pickers, required confirmation/impact display and window
hosting. They do not acquire an unlocked owner, wrap a mutation in serialization,
or call a separate refresh after saving. The shared vault owner publishes
committed metadata with its effective snapshot/root/revision; views can observe
current state after a notification failure without repeating the operation.
Contexts, subscriptions and operation views neither own roots nor prolong their
lifetime. A context is bound to its opening location and selected/editor role
epoch. Switching A-to-B-to-A does not revive the first A context; closing an
editor invalidates its context even if another role retains the file owner.

Keep the current Busy rejection before settings save when a requested selected-
path change affects active old/new vault work. Retain the settings draft and
effective selection; do not queue an automatic later settings apply. Once the
affected work has settled, reserve its role transition and validate the settings
revision before allowing the settings owner to publish. The reservation excludes
new conflicting work through completion. An unchanged selected path does not
create a role transition merely because other settings are saved.

The settings owner retains serialization, file outcomes and effective settings
adoption. Failure or partial settings publication keeps the prior effective
selected role and the settings store's restrictions. Verified settings adoption
and the reserved in-memory role/selection-epoch update form one ordered visibility
transition for new credential requests: no new effective path with an old
credential owner is observable. Once adoption occurs, UI closure or cancelled
observation cannot abandon the role transfer. Settings and vault files still
have separate physical outcomes; this is not a cross-file transaction.

Use the same coordination for settings restore/default recovery and later
adoption after settings reconciliation, not just the normal Save Settings action.
Initial startup loading is distinct from changing roles of existing owners.
Keep non-secret transition evidence if the settings outcome remains unresolved;
later adoption must reacquire valid ordinary roles rather than use an expired
owner. An admitted transition retains the old/new owner references needed by
actual work if an editor closes meanwhile. Promote a usable existing editor
owner on success; on failure, reevaluate release under the unchanged roles.
Quiescent unresolved metadata alone cannot extend an editor-only secret lifetime.
Preserve stale-location restrictions across switching away/back and keep every
standalone editor's original target.

Shared unlock participation belongs to admitted operations/connections, not
their observers. Cancelling an observation or disposing a subscription does not
remove that consumer. A consumer's explicit stop does not cancel other consumers.
The interaction coordinator may transfer a prompt to a surviving host or end it,
preserving the operation/target identity and invalidating old-host replies.
Shared prompt failure or cancellation ends its participants without a prompt
loop. Late/duplicate input replies have one cleanup owner and are never consent
for another target. Do not hold a reentrant physical mutex or occupy the expensive
KDF/import worker while waiting for UI input or confirmation.

The selected credential resolver checks exact references, role epoch and current
eligibility immediately before handoff. It clears a result invalidated before
handoff rather than retrying against a different vault. A valid handoff transfers
the credential to the existing authentication owner and lifetime; selection
change does not wait for all established terminal sessions to end or disconnect
them. Vault input and actual native-work cleanup remain under the
[vault operation contract](vault-format.md#complete-vault-management-operations).

Editor/application termination coordinates prompt hosting, operation-stop
requests, actual I/O/native/owner-dependent verification and new inspection
admission. Pending work retains required owners; quiescent partial/unresolved
outcomes may use the existing visible close decision, with paths and retained
artifacts reported. Context release alone never disposes a live root. While the
application continues, non-secret outcome/restriction records survive closed
views. Archive success ends the old usable owner after dependent work settles;
display failure after a verified archive does not undo it or authorize another
move. Keep the established acknowledgement/standalone-editor closure and normal
final-exit rules. No hidden lifetime or session-recording timeout is introduced.

### Editor acceptance cases

| Scenario | Expected behavior |
| --- | --- |
| Select two `.ttty` files and choose Open or press Enter | Evaluate each file independently. Connecting or registering one file does not decide the other. Use distinct tabs for permitted connections. |
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
