# Specify connection profiles and session lifecycle

> The [profile trust specification](../specification/profile-trust.md) supersedes the historical rules below for trusting a profile on activation and registering it on open.

> The 2026-11-10 baseline was consolidated in the [persistence and windows contract](../specification/persistence-and-windows.md). See [implementation status](../specification/implementation-status.md) for validation evidence.
> The current [tab closure rule](../specification/persistence-and-windows.md) supersedes Q192 for manual, automatic, and bulk tab closure.

Working issue-body draft. Confirmed requirements are agreed; remaining choices
are listed explicitly. This document does not authorize implementation.

## Confirmed profile management (Q109-Q113, amended)

`profile-management-proposals.md` records the accepted Documents-based save
location, registered-file list, duplicate/delete behavior, shared credentials,
and explicit saves. External moves remove the old registration and create a
new profile registration. The user rejected PC-local profile-action approval:
activation trusts the saved configuration after basic loading checks; detailed
settings validation occurs on every editor open, during editing, and on save.

## Confirmed connection workflow

- Create and save a connection profile before opening a connection, then select
  that profile to connect. Apply this workflow to SSH, Telnet, and serial COM.
- Exclude quick/ad-hoc/temporary connections that bypass profile creation.
- Persist each connection profile as its own file, using the format below.
- Support double-clicking a profile file to launch TabTty and connect using
  that profile when Windows associates the file type with TabTty. The app must
  handle file activation as well as opening profiles from inside its UI.
- Keep the destination, connection method, authentication choices, terminal
  options, and session-logging settings in the profile. Refer to saved secrets
  by credential reference; never embed their values in profile data.
- Telnet profiles have no built-in username/password login settings or saved
  credential binding initially. Connection readiness does not wait for remote
  account login; manual input handles it until future macros are available.
- Connection execution follows the saved profile. Existing SSH host-key trust
  decisions and input for unsaved/unavailable credentials still apply; selecting
  a profile does not bypass them.

## Reference-based transport settings (Q103-Q105)

The user delegated these choices to a Tera Term-based review. Use the selected
settings in `teraterm-behavior-baseline.md`, rather than the superseded proposals
in the earlier interview round:

- Default SSH/Telnet advertised terminal type to `xterm`, editable per profile.
  Keep the advertised name distinct from the VT100 identification baseline and
  the actual supported controls. Serial does not negotiate this name.
- Serial defaults to 9600/8/None/1/None, with 7/8 data bits, 1/2 stop bits,
  five parity choices, and None/XON-XOFF/RTS-CTS/DSR-DTR flow control. Use a
  saved COM name, with friendly-name display and absent-port preservation;
  do not silently replace the destination or enable automatic reconnection.
- Save DTR/RTS On/Off outside handshake ownership, defaulting On. Temporary
  explicit line operations do not rewrite the profile; reconnect restores
  saved values. BREAK is an explicit session operation (1000 ms for serial).
- Telnet port defaults to 23. Initiate normal negotiation on every configured
  Telnet port. `Initiate BINARY negotiation` defaults Off but still permits
  accepting a peer's BINARY request in each direction. Retain the selected
  encoding and eight-bit data without requiring BINARY negotiation to succeed.
- Unicode ambiguous width offers one/two cells, default one. Resolve it at
  connection start independently of UI language; detailed legacy/emoji width
  handling remains terminal compatibility work.

Saved settings follow the existing next-connection rule. Do not adopt the
reference's persistent serial transmit delays, local line-input buffering,
automatic protocol detection, or credential/storage model. Preserve all prior
TabTty logging, echo, paste, and authentication decisions.

## Confirmed profile file format

Use one UTF-8 JSON file per profile, with the dedicated `.ttty` extension.
Include a schema version and profile identifier independent of the translated
display name. Do not use that serialized ID to preserve registration identity
after external moves or to merge copies at different locations (Q110).
Register this dedicated extension for
profile activation rather than registering generic JSON files as profiles.
The selected extension does not change the JSON content format.
Keep saved authentication material in the portable `.tttykey` credential vault
described in `credential-vault-issue.md`; a profile contains references, never
secret values.
Saved public-key credentials contain the private-key material imported into the
vault, so they do not depend on the continued presence of the source key file.

The suggested location is `TabTty\Profiles` beneath the Windows Documents
known folder; files elsewhere open in place. In-app duplication creates a new
file/ID. External moves are old-registration removal plus new registration;
an external copy is a separate registration even if its serialized ID matches.
Opening alone does not rewrite the file. Detailed ID representation is recorded
in `profile-management-proposals.md` and remains technical schema work.
A credential reference alone does not make its secret available on another computer; the vault
must also be available and unlocked there. Paths used to select an import source
are not runtime private-key dependencies after import.

The later removal of built-in Telnet login also removes its saved-password
editor warning/acknowledgement from initial scope. Q112 still supersedes earlier
setup/load approval on imported files, another PC, or externally changed
destinations. Do not introduce a general profile approval store.

## Confirmed file activation and window routing

- Register the dedicated file type as a supported type using the appropriate
  packaged/unpackaged Windows mechanism. Windows/user choice controls the default
  association; the app must not silently replace it.
- Support multiple TabTty windows in one application run. Route a profile-file
  activation to a new tab in the most recently active surviving TabTty terminal
  window. Track TabTty window activation, not activation of Explorer or another
  application. If no terminal window exists, create one.
- A double-click opens a new terminal session/tab rather than transferring an
  already open tab using the same profile. The new session follows the profile's
  settings, validation, and any transport-resource constraints.
- Read the actual file and perform basic bounded format/interpretation checks
  before connecting. Trust its configured destination, paths, and credential
  references without a setup/review gate, including new or externally edited
  files. Full settings/consistency validation belongs to every editor open,
  editing, and saving. Runtime transport, credential, and log failures still
  produce errors; do not silently repair or substitute profile values.
- If the selected connection needs a locked vault, obtain the master password
  on first use, then continue. Later uses in the same application run do not
  request the master password again. Existing SSH host-key checks remain in force.

## Confirmed tab movement between windows

- Allow reordering tabs by dragging within a window's tab bar.
- Dragging a tab out creates a new window containing that tab. Dropping it into
  another TabTty window's tab bar moves it there at the selected position.
  This is the agreed window separation/combination interaction.
- Move the existing terminal session, preserving its connection, output and
  scrollback, terminal state, selected profile/settings, logging destinations,
  and recording/pause state. Do not reconnect, reauthenticate, restart logging,
  or replay input because a tab changes windows.
- Reception, terminal-state processing, and enabled recording continue during
  dragging. Cancelling a drag leaves the original session intact in its window.
- A changed terminal viewport is handled through normal resize behavior, not a
  new session. Apply accepted Q106 reflow/retirement rules in
  `resize-and-vault-proposals.md`, preserving recording and content anchors.
- Detaching or merging the final tab must not end the application or relock the
  vault while that session is being transferred to its destination window.
- Share the selected vault and its unlock state across every window in the
  application run. Opening, moving, or closing an individual window does not
  require unlocking the vault again while the application continues running.

Recommended implementation direction: one application instance owns the shared
session/vault state and creates multiple top-level windows. Keep connection and
logging ownership independent of a tab's visual host, reusing the session module
interfaces described in `future-macro-design.md`. This is design documentation,
not authorization to implement window movement or an activation broker.

## Confirmed closing and application lifetime

- Before closing a terminal tab that is connected or still connecting, request
  confirmation that its connection will end. A disconnected tab closes without
  this connection warning.
- Closing a window with connected/connecting tabs presents one combined
  confirmation for the affected connections, rather than one prompt per tab.
  Cancelling starts no new close-induced effects for that request. It does not
  reverse a separately accepted tab closure or independently occurring peer
  loss; architecture Q12b joins such existing closures without restarting them.
- The existing close confirmation includes active SFTP work for affected tabs.
  An accepted close cancels that work and closes the associated transfer window.
  An explicit disconnect stops file work and retains its results while the
  terminal tab is retained. Moving a tab
  preserves the transfer. See [transfer window ownership](../specification/file-transfer.md#transfer-window-and-operation-ownership).
- Architecture Q10, amended on 2026-11-16, makes the user-invoked Close
  disconnected tabs command and automatic disconnected-tab removal close the
  affected tabs' transfer windows under the same cleanup/reporting policy as
  explicit tab/window closure. Already disconnected targets have no connection
  warning; existing necessary cleanup, reporting, and wait/loss choices still
  apply. Disconnect or peer loss retains transfer results when the terminal
  tab is retained. This changes the current bulk and automatic-removal
  implementation; see the [closure design](../specification/tab-and-window-closure.md).
- Q192 (2026-11-14) clarifies that a window closes when all of its tabs are gone,
  counting terminal and settings/editor tabs together. Empty split groups do
  not keep it alive. This applies to explicit, automatic, bulk, and transfer
  removal; transfer still has the protection below. Closing the final
  application window ends the application run and discards the vault unlock state.
- Keep tab transfer distinct from closing a session. Moving the only tab into a
  new or existing window must not end the application, disconnect the session,
  display a disconnect confirmation, or discard the vault unlock state.
- Do not keep a hidden background/tray application running after the final
  window closes in the initial release.

## Confirmed initial startup behavior

- Persist the last active terminal window's usable normal position/size,
  monitor association, and normal/maximized state. Restore one window, never
  minimized. Do not restore previous windows, terminal tabs, output, or connections.
- Starting without a profile-file argument displays profile selection; it does
  not create an ad-hoc connection or reconnect the previous session.
- Starting by opening a profile file starts that profile's connection workflow,
  subject to the agreed validation, vault unlock, and SSH trust checks. Do not
  restore or reconnect unrelated previous tabs.
- File activation while the app is already running continues to target the
  most recently active surviving TabTty window.

Q114-Q116 are accepted in `window-lifecycle-proposals.md`:

- Recover unavailable-monitor/off-screen geometry into an available work area;
  use the target monitor's scaling. Detached windows appear near the drop using
  the source's normal size. Exact minimum/default dimensions remain layout work.
- Restore minimized activation targets. Defer file opens behind a blocking
  modal interaction, preserving arrival order without dismissing that dialog.
  Do not keep a hidden app alive for queued requests after final exit.
- Share one in-progress vault-unlock interaction among waiting connections.
  Cancellation/failure ends those attempts without automatic repeat prompts.
- Provide keyboard-accessible tab-menu movement to new/existing windows.
  Preserve the live session and logging; failed moves leave the source intact.

Detailed DPI API handling, modal ownership, queue bounds, and close/activation/
transfer ordering remain technical work, subject to these accepted behaviors.

## Confirmed edits and connection settings

Q155 confirms the general rule: saved connection-profile changes apply from
the next connection. Saved application settings apply immediately by default,
except application defaults inherited/resolved through a profile, which apply
from the next connection as well. Existing sessions keep their captured values.
See `application-settings-proposals.md` for explicit Save/Cancel and recovery.
Manual reconnect reads the latest saved profile with the same basic
loading checks and no profile approval gate. This does not change the agreed live
reevaluation when a shared highlight rule's contents are edited.

Q125 also fixes receive/transmit encoding and JIS/kana settings for each
connection. Initial scope has no live encoding switch or reinterpretation of
retained output/logs. Expose effective session encodings separately from saved
next-connection values when they differ.

Q144-Q145 in `legacy-encoding-proposals.md` retain the 48 encoding identities and
add relevant Japanese compatibility details: JIS transmit IN @/B and OUT B/J,
default B/B, and separate receive/transmit kana choices defaulting to 8-bit with
optional 7-bit SO/SI. Exclude outgoing ESC ( H; EUC kana uses standard SS2.
Store the selected choices with the profile and apply them only on the next
connection. Do not expose internal .NET code-page implementation numbers as
additional JIS choices or silently substitute a different encoding identity.

The retained normal-screen buffer defaults to 5,000 rows including the active
screen and scrollback. Store the common default in application settings; each
profile selects inheritance or an explicit override. Resolve the limit when
connecting. Edits to either setting apply on the next connection and do not
discard output from an existing session. See `receive-logging-issue.md`.

Q146-Q148 in `retention-and-log-io-proposals.md` confirm the integer range of
100-100,000 retained rows, the 8 MiB combined per-session pending-log budget,
and a ten-second active-I/O stall policy. After five seconds of unresolved log
finalization during a requested close, offer Continue waiting (default) or
Close with unsaved log data. Waiting must not be overridden by the stall timer.
These recording limits do not replace the remaining aggregate resource bounds.

Character/line delay controls are required in the newline-paste confirmation.
Each delay accepts integer milliseconds from 0 through 10,000. Each application
start uses 0 ms for both with remembering off. The confirmation permits editing
the current paste's delays and includes a checkbox for remembering them only
during the current application run. The last-used pair and checkbox state are
application-wide across all profiles, tabs, and windows, held only in memory.
Complete application exit discards them; closing one window while another
remains does not. Do not add per-profile delay defaults or last-used values,
and never save these controls to `.ttty` files or persistent app settings.
The accepted Send/Cancel and checkbox transitions are specified in
`terminal-interaction-issue.md`: checked Send updates the shared in-memory pair,
unchecked Send clears it after selecting values for that paste, and Cancel or
failed validation leaves it unchanged. No-newline paste uses the remembered
character delay when enabled, otherwise zero. Already captured dialog/paste
values do not change when another window updates the shared controls.

Enter/Backspace defaults and the initial local-echo policy are confirmed in
Q88/Q90 of `terminal-interaction-issue.md`. Enter offers CR/LF/CRLF, defaulting
to CR for SSH/serial and CRLF for Telnet; Backspace offers BS/DEL, defaulting
to DEL for SSH/Telnet and BS for serial. Capture keyboard mappings on connection;
saved edits apply on the next connection. SSH local echo is off. Telnet offers
Follow negotiation (default) and Off; serial offers Off (default) and On.
Permit a temporary local-echo policy change within that session, without
rewriting its saved profile. Reconnection uses the saved profile values.

## Confirmed Telnet readiness and manual login

The user's 2026-11-08 answer accepting Q133-Q136 supersedes Q129-Q130's automatic
login helper. Telnet setup completes when TCP is connected and terminal transport
is available; display Connected and enable terminal interaction. Ongoing option
negotiation is not an all-options-complete barrier. Do not wait for login/password
prompts or remote account authentication, and do not label transport readiness
as verified remote login.

Remove initial Telnet username/password settings and saved-credential references,
request strings/case choices, login-stage waits, automatic submissions, and
helper-specific Stop/manual-takeover state. Connecting Telnet never requires
vault access. Login prompts, rejection messages, and account logout are terminal
output, not application authentication events; transport closure still disconnects.
Initial Telnet and serial login is manual. Future macros own automated login
and its application-level success/timeouts; no macro runtime is introduced now.
See `connection-attempt-proposals.md` and `future-macro-design.md`.

## Confirmed SSH profile authentication (Q133-Q136)

Profiles select allowed Password/Public key/Keyboard-interactive methods and
their priority order; new profiles enable Password only. Public key selects one
vault key, and Password selects explicit input or a saved reference. Do not scan
other keys or infer saved-password answers for Keyboard-interactive. Retries,
partial authentication, key compatibility/import, and local host-key management
follow `ssh-authentication-proposals.md`. Host trust is not embedded in `.ttty`.

Q140 in `ssh-session-proposals.md` confirms SSH Connected/input readiness only
after successful authentication/channel/PTY/shell setup. Do not wait for prompt
text or queue user input before readiness. Preserve legitimate early output.
No automatic environment/startup commands or no-PTY fallback is introduced.

## Confirmed connection waits and SSH liveness (Q131-Q132)

Q158 in `profile-file-preparation-proposals.md` adds separate fixed 30-second
deadlines for loading the profile and preparing the directory plus all selected
initial log files. Show each stage with Cancel. Expiry/cancellation prevents
transport startup and invalidates late results; retry is explicit. These budgets
are separate from transport setup and do not count waiting for local user input.
They do not guarantee that an underlying OS/provider operation has stopped.

Use a profile connection-wait budget of 30 seconds (range 1-300) for initial
network/SSH setup and later non-authentication protocol waits. Exclude local
host-key/vault/credential dialogs and do not apply this universal deadline to
SSH authentication response waits. Server deadlines still apply. Connection
cancellation invalidates pending work and leaves the tab without automatic
reconnect or input replay.

SSH liveness checking is optional and defaults Off. When enabled, use authenticated
response-requesting protocol checks at a default 30-second interval (range
1-3600) and three unanswered response windows, after an initial idle interval.
Do not promise an exact 90-second cutoff or inject terminal characters/log data.
On failure disconnect and retain the tab. Do not automatically probe Telnet/serial.

## Confirmed session-logging workflow

- Decide whether and how to record terminal output in the profile before
  connecting. Recording configured as enabled starts as part of the session.
- Raw and Text are receive-only, including remote echo but excluding local
  echo (Q90 restored). Raw must preserve original terminal-payload bytes exactly.
  Terminal mode safely merges received output and enabled
  local echo without original-byte fidelity (Q94 accepted). It records ordered
  UTF-8 text and equivalent supported VT controls, keeping Q92's local Enter
  CRLF and Q93's no-heuristic-deduplication rule. Q91's structured Raw container
  and the earlier combined-mode line snapshots are superseded.
- Offer independent Raw, Text, and Terminal checkboxes, permitting any
  combination including all three (Q96 accepted). New profiles have all
  unchecked, meaning disabled. The former Both choice corresponds to Raw plus
  Text. Use separate files and shared pause/resume/retry for the selected set.
- Allow pause and resume during a session with recording enabled.
- A session created with recording disabled cannot start recording midway.
- Keep recording status visible. Pausing logging must not pause reception,
  terminal state updates, display, or highlighting.
- Preserve the mode boundaries in `receive-logging-issue.md`: Raw is a headerless
  exact received-byte stream. Normal-screen Text uses line contents after
  received editing operations, without local-echo content or cursor changes,
  and does not finalize on a lone CR or locally generated Enter.
  Q92's local CRLF behavior belongs only to the new combined mode. Q94 confirms
  recording its operations without waiting for edited-line snapshots. Its
  interval policy is confirmed in Q98: resume new eligible complete operations
  without a newline wait, omit units already incomplete at resume, and inject
  no earlier screen contents or restore-state setup. Q97's display-operation
  scope excludes non-display effects, queries/replies, and unsupported fragments.
  Alternate-screen Text records received text with controls removed or replaced,
  without waiting for displayed-line finalization: preserve TAB/CRLF/LF,
  turn standalone CR into LF, and remove other terminal controls. Logging is
  independent of tab visibility and scrollback browsing.

The profile can specify the log folder, filename template, and overwrite
permission. The [recording contract](receive-logging-issue.md#log-filename-template-redesign)
defines application defaults, macro overrides, and startup failures. Use `<stem>.log`
for Text, `<stem>.raw.tttylog` for Raw,
and `<stem>.term.tttylog` for Terminal. Q95 is accepted with the user's Terminal
name and provisional `.tttylog` extension; the follow-up shortens the filename
marker from `.terminal` to `.term`, not the final extension. Use markers even
for single-format recordings. Raw and Terminal share the extension but have
different contents; select serialization by mode, not by extension.
Keep this naming policy replaceable for user/macro/invocation naming without changing recording
lifecycle; see `receive-logging-issue.md` and `future-macro-design.md`.
A pause closes the current files; resuming creates a new
numbered part without backfilling paused output. On pause, disconnect, and orderly
exit, flush eligible incomplete normal-screen Text without adding a newline.
This means the current edited contents eligible for that recording interval,
not the history of keystrokes or received editing operations.
Normal-screen Text resume/retry skips the in-progress mixed logical line,
including newly received characters before its terminating newline, and starts
with the next new logical line. Alternate-screen Text and Raw resume immediately
from new received data, without replaying the existing screen or paused output.
For Text, keep decoder/parser state without making earlier partial units
eligible to log. Terminal likewise keeps receiver state live, excludes receive
units incomplete at resume, and accepts independent new local operations without
waiting for the pending receive unit. Its new part contains new eligible
operations only and does not reconstruct the screen/state from the gap.

Entering the alternate screen flushes eligible pending normal-screen text once,
without adding a newline. Returning finishes eligible alternate-screen output
and resumes normal-screen handling without dumping the restored screen.
Switching screens cannot backfill missed output or duplicate unchanged text;
it does not add diagnostic markers, blank lines, or new recording parts.

Do not automatically split by file size. This does not change the agreed new
files at connection/reconnect, pause/resume, or error retry.
Do not automatically delete recordings. An action to open the log folder in
Explorer is not required and is excluded; a dedicated log viewer/search UI is
also outside the initial scope.

Any [recording start failure](receive-logging-issue.md#log-filename-template-redesign)
fails the connection, including on resume or retry. A later write failure stops
all selected recordings, reports a persistent error, and keeps the connection
usable. Explicit retry opens new parts for all selected formats.
Accepted Q147-Q148 require bounded background writes, a roughly one-second
normal buffering target, and explicit durable flush requests at eligible pause,
EOF, disconnect, and orderly-close boundaries. Neither queue admission nor that
target guarantees disk durability. Keep failed I/O ownership separate from new
parts and report incomplete recording rather than silently discarding it.
Detailed screen-derived text semantics and remaining bounds are tracked in
`receive-logging-issue.md`.

## Confirmed disconnect and reconnect behavior

- Q142-Q143 in `ssh-session-proposals.md` keep protocol notices/exit status in
  connection information, not terminal output/logs. Exit status alone does not
  stop reception; directional EOF and channel/transport closure are distinct.
  Explicit SSH disconnect cancels unsent input and allows up to two seconds for
  peer-close cleanup without sending an exit command or adding confirmation.
  The existing live-tab/window-close confirmation remains. Disk/log draining
  follows its own contract; two seconds is not a complete-save guarantee.
- Close disconnected tabs by default. With automatic closure disabled, retain
  the tab and its displayed output after disconnect.
- Reconnect on explicit user action; do not automatically reconnect in the
  initial release.
- Do not replay buffered input or commands on a new connection.
- Retrieve explicitly saved credentials again when needed; unsaved credentials
  require new input under the agreed authentication-lifetime policy.
- General macros are outside the initial release. When introduced later, stop
  a running macro on disconnect and require explicit restart rather than
  automatically resuming it after reconnect.

## Remaining decisions

- Exact profile fields/schema, bounded loading, local registration/serialized-ID
  representation, path normalization, and save-failure handling. Storage,
  external-move semantics, CRUD, sharing, and validation timing are accepted
  in Q109-Q113; transport defaults are in `teraterm-behavior-baseline.md`.
- Technical modal ownership and races between window closure, dragging, and
  file activation; minimized/modal behavior is accepted in Q115. No profile
  approval/credential-binding gate is pending.
- Telnet protocol transitions and validation bounds. Built-in Telnet/serial
  login is excluded; no helper matcher or login-stage schema remains to design.
- Logging lifecycle details in `receive-logging-issue.md`.
- Detailed geometry persistence/monitor-DPI handling, session-history lifetime,
  exact import/export schema, and missing referenced resources. Geometry policy
  and keyboard-accessible tab movement are accepted in Q114/Q116. Automatic
  tab/connection restoration is outside the initial scope.

## Acceptance cases for later implementation

- Every connection is associated with a saved profile, including serial sessions.
- With the file association selected, double-clicking a readable valid profile
  opens TabTty and starts its connection workflow. A first required vault unlock
  asks for the master password; subsequent uses in the same run do not.
- With several windows open, file activation opens a new tab in the last active
  surviving TabTty terminal window. An external app becoming active does not
  change that selection; if no terminal window exists, create one.
- Dragging to another window or detaching into a new one preserves the existing
  connection, output, and log/pause state without an authentication prompt.
- Detaching the only tab, merging it into another window, and cancelling a drag
  do not lose the session or clear the shared vault unlock state.
- Closing a connected/connecting tab asks once; closing a window asks once for
  all its affected connections. Cancellation introduces no new closure effects
  and does not undo an earlier independent close; disconnected tabs
  do not trigger a disconnect warning.
- Removing the final tab of any kind closes its window, including automatic/
  bulk closure and committed transfer (Q192). Settings/editor tabs count;
  empty split groups do not. Closing the final window ends the app run and its
  vault unlock state. Transfer commits a surviving destination before source
  closure and never triggers this exit path.
- A normal new launch shows profile selection with restored window geometry,
  without restoring tabs or connections. A file-based launch connects only the
  selected profile, following its ordinary connection workflow.
- Opening a Telnet profile creates a usable connected session without vault
  unlock, saved-password sending, prompt recognition, or login confirmation.
  The editor contains no initial Telnet login settings or related acknowledgement.
- External moves are removal of the old registration followed by registration
  as a new profile; no relocation-continuity prompt or file rewrite on open.
- Every file opened in the editor receives settings/consistency validation;
  connect/reconnect runs only basic loading checks and ordinary runtime checks.
- A recording-disabled session cannot start logging partway through output.
- New profiles have all recording formats unchecked. Raw, Text, and Terminal
  may be selected independently, with separate files and common pause/retry.
- Raw and Terminal can both use `.tttylog` without collision because their
  filename markers are `.raw` and `.term`; their serialized contents stay distinct.
- Profile destination/authentication/logging edits leave a running connection
  unchanged and take effect when a new connection or manual reconnect starts.
- Telnet login prompts and rejection messages are displayed/logged as received
  without automatic sending or a change to connection readiness. Manual input
  remains available; there is no general macro runtime dependency.
- Pausing an enabled recording leaves terminal reception and display working;
  resuming is available without reconnecting.
- A retained disconnected tab keeps its display. Manual reconnect does not resend old
  input or commands and does not reuse unsaved authentication secrets.
- Deferred macros are not an initial-release acceptance prerequisite.

## Primary references for file activation

- [Windows default-app association policy](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)
- [Microsoft Path.GetExtension](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getextension?view=net-10.0)
- [App activation](https://learn.microsoft.com/en-us/windows/apps/develop/launch/activate-an-app)
- [Windows App SDK rich activation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-rich-activation)
- [Instance routing](https://learn.microsoft.com/en-us/windows/apps/develop/launch/multi-instance-apps)
- [File activation validation](https://learn.microsoft.com/en-us/windows/apps/develop/launch/handle-file-activation#remarks)
