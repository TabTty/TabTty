# Define the TabTty initial release

> The 2026-11-10 baseline was consolidated in [implementation readiness](../specification/implementation-readiness.md), which also indexes subsequent scope decisions. See [implementation status](../specification/implementation-status.md) for build and validation evidence.

Unpublished issue-body draft for the GitHub issue tracker. The 2026-11-10
baseline and readiness of later scope additions are tracked in
[implementation readiness](../specification/implementation-readiness.md).

## Confirmed product scope

- Q155 establishes settings timing: saved profile changes apply on the next
  connection; saved app settings apply immediately by default, except defaults
  resolved through a profile, which apply on the next connection. Keep explicit
  Save/Cancel, the existing shared-highlight live-edit behavior, and temporary
  session actions. Q156-Q157 use separate preference/highlight JSON files with
  offline manual-copy migration, no initial import/export UI, one previous
  validated backup, and explicit recovery without silently overwriting unreadable
  settings. See `application-settings-proposals.md`.
- Build TabTty in C# as a tabbed terminal for Windows 11.
- Use WPF on .NET 10 LTS with a TabTty-owned native terminal control
  (Q100 accepted). Select a supported servicing patch at implementation and
  release time; exact drawing APIs remain subject to the terminal design.
- Require no additional end-user prerequisite installation; bundle the needed
  runtime and application dependencies.
- Require native x64 support. ARM64 support is optional and must not block the
  initial release (Q101 accepted with this amendment). Officially support
  Windows 11 releases/editions supported by Microsoft and the selected runtime
  at release time; Windows 10 and 32-bit x86 are outside the initial matrix.
  Determine the minimum installable OS build from the required APIs separately.
- Distribute initial evaluation builds as self-contained ZIP folders, with
  manual replacement for updates. Use MSIX for eventual Microsoft Store
  distribution and Store-managed updates. An independent application updater
  is outside the initial scope (Q102 accepted). Preserve profile-file activation
  through Windows association selection/declaration, without taking over a
  default association. Exact storage, signing, and package identity remain open.
- Require creating and saving each connection profile as a UTF-8 JSON `.ttty`
  file, then selecting it to connect. Ad-hoc/temporary connections are excluded.
  Profile, logging, and reconnect behavior is specified in
  `connection-profiles-issue.md`.
- Double-clicking an associated profile file opens TabTty and starts connection
  using that profile, subject to profile validity and authentication/trust needs.
- Q158 bounds pre-connection profile loading to 30 seconds and the complete
  initial log-folder/file preparation stage to another 30 seconds. Keep each
  stage visible and cancellable, exclude local user-decision waiting, and do not
  start transport after cancellation/expiry or a late completion. Retry is manual;
  underlying OS cleanup retains separate bounded ownership. These limits do not
  replace transport or active-log/close-drain timing.
- Support multiple windows. Open file activations as new tabs in the last active
  surviving TabTty terminal window, creating a window if necessary. Allow tab
  reorder, detach into a new window, and transfer into another window's tab bar.
  Preserve the terminal session and logging through those moves without reconnect.
- Q152-Q154 exclude received clipboard access, remote title replacement and
  outer-window manipulation. Use profile-name tab labels. Keep visible URLs and
  OSC 8 labels as selectable text without link activation or hidden-target copy.
  BEL defaults to a coalesced visual tab indicator, with an application-wide Off
  option and no sound, terminal/taskbar flashing, toast, or forced focus. Preserve
  ordinary terminal controls and existing Raw/Text/Terminal recording boundaries;
  see `terminal-external-effects-proposals.md` for the accepted scope.
- Confirm closing connected/connecting tabs, with one combined confirmation
  when closing a window. Q192 closes a window when its final tab of any kind
  is removed, including automatic/bulk closure and committed transfer. Empty
  split groups do not keep it alive; settings/editor tabs do. The final window
  ends the app and its vault unlock state. Tab transfer establishes its
  destination first and does not trigger this exit.
- Persist the last active window's normal geometry and normal/maximized state;
  restore one window, never minimized, and recover unavailable-monitor bounds.
  Do not restore prior tabs or connections. Normal startup shows profile
  selection; a profile-file launch starts the selected connection workflow.
  Restore minimized activation targets and defer opens behind blocking dialogs.
  Share one pending vault-unlock interaction. Provide keyboard-accessible tab
  movement commands with the same live-session preservation as dragging.
  Q114-Q116 are detailed in `window-lifecycle-proposals.md`.
- Support SSH, Telnet, and serial COM connections. Implement the SSH client
  protocol and session layer in C#, using OpenSSL for cryptographic operations,
  as recorded in `../adr/0001-own-ssh-client-with-openssl.md`.
- Initial SSH support is SSH 2 with password, public-key, and keyboard-interactive
  authentication selected/ordered per profile; new profiles enable Password only.
  Keep Keyboard-interactive responses separate from saved passwords. Q133 limits
  retries and handles partial success without automatically updating saved secrets.
  Support Ed25519, ECDSA P-256/P-384/P-521, and RSA 2048-8192 with SHA-2 signatures.
  Exclude legacy SSH 1, DSA, SHA-1 signatures, SSH certificates, and FIDO keys.
  Confirm first-seen host keys and offer explicit
  replacement when a saved key changes, as specified in
  `authentication-issue.md`.
- Initial SSH sessions connect directly to an interactive shell, with independent
  connections per tab. Defer jump hosts, port forwarding, X11 forwarding,
  connection sharing between terminal tabs, authentication-agent integration,
  and agent forwarding.
- Include [SFTP v3 file transfer](../specification/file-transfer.md) from the
  header context menu of a connected SSH terminal tab. The two-pane window
  uploads and downloads files and folder trees and supports folder creation,
  rename, and deletion. The transfer uses the invoking tab's authenticated
  connection and its effective transmit encoding for remote names. Legacy SCP
  is deferred. See [Q1–Q36](file-transfer-issue.md) for the decisions.
- Q137-Q139 accept a fixed reviewed transport algorithm policy, no SSH compression,
  and automatic rekey at 1 GiB per direction or one hour, whichever comes first,
  with tighter cryptographic limits respected. Prefer ML-KEM/X25519 hybrid KEX
  with listed traditional alternatives, AES-GCM then AES-CTR/SHA-2 EtM. Enforce
  strict KEX when negotiated but permit non-supporting peers under the selected
  AES-only policy. No initial algorithm-list editor, weak compatibility mode,
  compression setting, or rekey-disable switch. Keep the tab/session/log parts
  through ordinary rekey without reauthentication; failure disconnects for manual
  reconnect. See `ssh-transport-policy-proposals.md` for the exact accepted matrix.
- Q140 requires successful authentication/channel/PTY/shell setup before SSH
  Connected/input readiness, with no prompt guessing, queued pre-ready input,
  or no-PTY fallback. Process valid early channel output. Q142 shows protocol
  messages in per-tab connection information outside the terminal/logs. Q143
  distinguishes exit notifications, EOF, and close; drains accepted output,
  retains the tab, and uses a two-second peer-close grace for explicit disconnect
  without sending exit commands. See `ssh-session-proposals.md`. Q141's handling
  of separately delivered stderr is also accepted: include both sources in the
  terminal/logs, preserve Raw payload byte order, and isolate each source's
  partial characters/control sequences before applying completed operations to
  shared terminal state. Raw does not preserve source labels or promise replay.
- Save authentication material only on explicit request in a portable encrypted
  `.tttykey` vault shared by all profiles and windows, using credential references in
  connection profiles. Store passwords and canonical PKCS#8 private-key material
  there; source-key passphrases are used only during import and then discarded.
  Key import preserves the source file; later use needs neither it nor its
  passphrase. The vault opens on
  another PC with its master password. Ask for that password on first use and
  retain the unlock state until app exit; connection-secret plaintext remains
  authentication-scoped. Window/tab movement preserves this shared unlock state.
  See `credential-vault-issue.md` for encryption/lifecycle details. Support
  the exact PEM/PKCS#8 and OpenSSH import/encryption matrix accepted in Q135;
  PPK/PFX and unlisted envelopes are outside the initial scope.
  The agreed bcrypt_pbkdf source exception
  and detailed credential handling are recorded in `authentication-issue.md`.
- Share host-key trust by hostname/IP and port within the current Windows user.
  Keep one approved key per public-key format per destination/port, with explicit
  approval for additional types and replacement of changed keys. Provide local
  record viewing/deletion; no initial known_hosts import/export or automatic
  key learning. Copying a profile/vault does not copy host trust (Q136).
  End temporary secret retention when authentication finishes; unsaved secrets
  require re-entry on reconnection. Follow the ongoing security-guidance policy
  in `authentication-issue.md` for authentication and credential handling.
- Q112 supersedes approval at import/new-PC activation: external profiles are
  trusted configuration after basic loading checks.
  Validate detailed settings on every editor open, during editing, and on save;
  no per-profile connection approval is required. Existing SSH host-key checks,
  first-use vault unlock, and credential-resolution errors still apply.
- Exclude built-in Telnet automatic login, superseding Q129-Q130 on 2026-11-08.
  Telnet becomes Connected/ready for input at transport establishment without
  waiting for remote textual login. No initial Telnet username/password fields,
  vault binding, prompt matcher, login-stage wait, or saved-password editor
  warning remains. Initial Telnet and serial login is manual; future macros can
  own automatic login. Connection readiness is not proof of remote account login.
- Q131-Q132 provide configurable connection/protocol waits (30 seconds by
  default), excluding user dialogs and SSH authentication completion waits.
  Offer optional authenticated SSH liveness checks, default Off, without terminal
  characters or log records. Retain manual reconnect and no input replay.
- Use a native terminal presentation; do not adopt xterm.js or WebView2.
- Q106 accepts normal-screen reflow, non-reflowing alternate-screen resize,
  and preserved selection/search/recording provenance. Keep the retained limit
  as 5,000 physical rows by default. A shrink can retire old content at that
  limit; flush eligible unrecorded Text before retirement without newlines or
  duplicated ranges. Raw/Terminal receive no synthetic resize records. See
  `resize-and-vault-proposals.md`.
- Q107-Q108 accept a narrow TabTty `.tttykey` vault container using Argon2id,
  a wrapped random data key, and whole-payload AES-256-GCM, with bounded encrypted
  JSON contents and transient plaintext handling. Accept the documented password
  rules, full key rotation on password change, one-generation encrypted backup,
  manual backup/restore, and conflict handling. Exact framing, nonce/key-use
  limits, numeric bounds, and security verification remain open; see
  `credential-vault-issue.md` and `resize-and-vault-proposals.md`.
- Resolve Q103 and subsequent reference-compatible behavior using Tera Term
  5.7.1 under the user's delegation, as documented in
  `teraterm-behavior-baseline.md`. Preserve earlier explicit TabTty decisions
  and the dependency/security boundaries rather than adopting the reference's
  complete feature set or source. Default the advertised terminal type to
  `xterm` and the identification response to VT100; these are distinct settings
  from the implemented terminal capabilities.
- Use Tera Term's serial defaults: 9600 bps, 8 data bits, no parity, 1 stop bit,
  no flow control. Offer None, XON/XOFF, RTS/CTS, and DSR/DTR flow control.
  Save an explicit COM name; keep manual reconnect and no automatic substitution.
- Keep eventual Microsoft Store publication in scope.
- Q125-Q128 fix encoding settings for each connection, omit automatic BOM
  emission, and suppress only a UTF-8 receive stream's initial signature from
  interpreted text while Raw preserves it. Literal search wraps at buffer ends,
  remembers queries only for a tab, and uses bounded result caching without
  restricting the searched retained buffer. Screen switches clear selection
  and suspend/resume normal-screen search without an automatic viewport jump.
  See `search-and-encoding-proposals.md`.
- Use Consolas 12 pt with installed fallback as the initial terminal font;
  offer app defaults/profile overrides and temporary tab-only zoom. Keep Unicode
  cell width separate from glyph measurement, preserve source text on fallback,
  and treat supported emoji sequences as two-cell clusters. Color emoji is not
  required initially. Use the fixed blinking-block cursor default with OS blink
  settings, unfocused outline, and received cursor controls; no user-selectable
  cursor settings initially. Include keyboard and Windows Narrator access to
  the UI and retained terminal text, without automatic per-line announcements.
  See accepted Q117-Q120 in `terminal-display-proposals.md` for details.
- Follow the repository language, initial UI localization, application theme,
  terminal default color, and dependency policies in `../../AGENTS.md`.
- Support basic interactive use of `less`, `top`, and `vim`, in addition to
  ordinary command-line operation. This includes basic full-screen behavior,
  scrolling, screen switching, and cursor movement. Terminal image rendering
  and advanced terminal extensions are outside the initial scope.
- Provide receive-only Raw and Text logging (Q90 restored), including remote
  echo but excluding local echo. Raw must preserve terminal-facing received
  bytes exactly, including controls and invalid/incomplete characters, as a
  headerless `.tttylog` stream. For SSH, capture terminal-channel payload after
  decryption, verification, and channel extraction, before terminal decoding.
  Protocol/authentication exchanges and local diagnostics are separate.
  Provide Terminal mode combining received output and local echo
  without original-byte fidelity. Revised Q94 is accepted: record ordered
  UTF-8 text and equivalent supported VT controls, including editing operations,
  without waiting for final-line snapshots. Q92's local Enter CRLF and Q93's
  no-heuristic-deduplication rule apply to this mode only.
  Q91's source-tagged Raw container is superseded, not accepted.
- Configure recording in the connection profile. Enabled recording starts with
  the session and supports pause/resume. A session with recording disabled cannot
  enable it midway. Offer independent Raw, Text, and Terminal selections,
  defaulting all off. Any combination is allowed; write separate files and
  share pause/resume/retry. Initial open failure prevents transport startup;
  any mid-session write failure stops all selected recordings, keeping the
  connection alive. Q95/Q96 in `receive-logging-issue.md` are accepted with the
  Terminal name and provisional shared `.tttylog` extension.
  Profile destination/authentication/logging
  edits take effect on the next connection; reconnect uses the latest saved settings.
- Normal-screen Text logging records line contents after received terminal editing,
  including cursor movement, overwrite, and erasure, without local-echo events.
  This receive-only state can differ from the combined visible display.
  Finalize on a received newline to avoid logging intermediate command edits.
  A received lone CR edits the current row without finalizing it; keep the
  latest visible progress value.
  Preserve genuine CRLF/LF line endings without duplicating a split CRLF.
  Do not add log newlines for local display wrapping, even at a coincident
  right-margin/received-newline boundary. Preserve received newlines even when
  the remote application generated them to fit the reported terminal width.
  Preserve received TABs while their final spans remain faithfully representable
  with ordinary eight-column stops; otherwise use final displayed text/spaces.
  Preserve explicit received spaces still present in the final text, including
  trailing spaces, but omit unused/erased trailing cells. Do not pad to screen width.
  Do not log unseen key input.
  Before a newline, record eligible, unrecorded rows just before they are evicted
  from the retained terminal buffer, which includes the active screen and
  scrollback. Merely leaving the active screen or browsing with the mouse does
  not trigger recording. Do not flush editable text at an arbitrary byte threshold
  or duplicate previously recorded rows/prefixes on eviction.
  Later edits do not rewrite recorded contents; append an updated version when
  finalized again, without duplicating unchanged redraws.
  Q149 applies destructive edits without pre-edit snapshots of removed text;
  a screen/history clear leaves already written logs intact. Q151 flushes eligible
  unrecorded content when output scrolling actually displaces it out of all
  retained Text state, without inventing LF or duplicating recorded content.
  Q150 keeps received LF/CRLF as the finalization boundary without cursor-movement
  triggers or shell-command inference. Accept imperfect Text layout/order during
  complex multiline editing, including backslash-escaped newlines; preserve
  ordinary terminal edit/wrap semantics. See `text-editing-proposals.md`.
  Store text as UTF-8 without a BOM, independently of connection encoding.
  File naming, pause/resume parts, incomplete-tail flushing, and recording-error
  behavior are confirmed in `receive-logging-issue.md`, with remaining details
  listed there. Local input does not itself finalize receive-only Text.
- For the new combined mode, locally echoed Enter creates a CRLF display/log
  boundary independently of its outgoing sequence (Q92). Echo-off input does
  not create a local boundary. Do not infer and remove duplicate local/remote
  echoes (Q93). Accepted Q94 records the combined operations, including edit
  history, rather than only resolved lines. Earlier combined-mode line-snapshot
  rules are superseded; Text retains its own edited-line policy. The new mode's
  control scope and interval behavior are confirmed in Q97/Q98: record supported
  display operations, exclude non-display effects/queries/unsupported fragments,
  and resume new eligible operations without line waiting or screen/state dumps.
  Receive units incomplete at resume remain ineligible after completion.
- Also record alternate-screen Text with terminal controls removed or replaced,
  accepting reduced readability and repeated redraw text. Append printable text
  sequentially without waiting for displayed-line finalization; preserve TAB and
  CRLF/LF, replace standalone CR with LF, and remove other terminal controls.
  This does not change normal-screen edited-display semantics or enable
  alternate-screen highlighting.
- Default retained normal-screen output to 5,000 rows including the active screen
  and scrollback. Each profile inherits the application setting or supplies an
  explicit override. Resolve the limit when connecting; setting changes apply
  on the next connection, leaving existing sessions' retained output intact.
  Q146 accepts integer values from 100 through 100,000, without unlimited mode
  or silent clamping. Separate cell/character/aggregate memory bounds remain.
- Q147-Q148 bound owned pending log payload to 8 MiB per session across selected
  formats, including staging and in-flight buffers. Admission failure or ten
  seconds without completed pending I/O progress stops recording with a visible
  incomplete status while the connection continues. Target roughly one second
  of normal application buffering; drain and request durable storage at eligible
  pause/EOF/disconnect/close boundaries, without a universal durability guarantee.
  After five seconds of unresolved close finalization, offer Continue waiting
  (default) or Close with unsaved log data. Preserve chosen waiting against the
  active-I/O stall timer and retain safe ownership until actual I/O completion.
  See `retention-and-log-io-proposals.md` for accepted failure/retry details.
- Normal-screen Text resume/retry excludes the in-progress mixed logical line,
  including new content before its terminating newline, then records the next
  new logical line. Alternate-screen Text and Raw resume immediately from new
  received data without replaying paused output or copying the existing screen.
- When entering the alternate screen, flush eligible normal-screen pending text
  once without adding a newline. On returning, finish eligible alternate-screen
  output and continue normal-screen handling without dumping restored content.
  Screen switches cannot backfill missed output or add duplicate text, synthetic
  labels, blank lines, or recording parts.
- Do not split recordings automatically by file size. The agreed new files on
  connection/reconnect, pause/resume, and error retry still apply. Logging paths
  may be absolute or relative to the directory containing the profile file.
- Default the recording folder to relative `Logs` and create it if needed before
  connecting. Initially name files using profile name, connection-start local
  timestamp with UTC offset, unique session suffix, and part number from `p001`.
  Use a common session/part stem with `.log` for Text, `.raw.tttylog` for Raw,
  and `.term.tttylog` for Terminal, without overwriting. `.term` is the shortened
  filename marker; `.tttylog` remains the provisional final extension.
  Keep naming separate from recording lifecycle so user-specified names, macro
  names, or explicit invocation naming arguments can be added later; those
  input mechanisms are not required in the initial release.
- Do not automatically delete recordings. Exclude an open-log-folder action
  and a dedicated log viewer/search UI from the initial scope.
- Use UTF-8 by default with manual encoding selection and no automatic encoding
  detection. Match the terminal communication encoding choices supported by
  Tera Term 5.7.1, using the inventory in `teraterm-encoding-baseline.md`.
  Design SSH for this same inventory from the start instead of basing its data
  path on a UTF-8-only console conversion boundary. UTF-16 LE remains outside
  the initial scope.
- Allow separate receive and transmit encodings. Link them by default with a
  same-encoding setting; the user can opt into different values.
- Replace invalid received character sequences visibly with U+FFFD and continue
  receiving. Reject outgoing input or a paste that cannot be encoded rather
  than silently substituting question marks or approximating characters;
  notify the user about the rejected send operation.
- Defer macro execution and macro UI beyond the initial release. Preserve the
  design constraints in `future-macro-design.md` for later integration, including
  access to session operations, received text, and credential references.
  Reserve `.tttym` for future macro files without selecting their language or
  content format or adding initial macro execution/file-activation support.
- Keep disconnected tabs and displayed output. Reconnect only on user action,
  with no automatic replay of input or commands. Future macros must stop on
  disconnect and must not resume automatically on reconnection.
- Provide regex-based highlighting as specified in `highlighting-issue.md`.
  Q121-Q124 confirm invariant-culture .NET matching, grapheme-safe match styling,
  explicit shared-rule saves, disabled invalid patterns, text-change scheduling,
  and bounded omission of highlighting on excessive inputs. Keep normal display,
  copying, and logging independent of regex overload/timeouts.
- Configure Enter and Backspace in connection profiles. Enter offers CR/LF/CRLF,
  defaulting to CR for SSH/serial and CRLF for Telnet; Backspace offers BS/DEL,
  defaulting to DEL for SSH/Telnet and BS for serial. Apply saved edits on the
  next connection. Keep Delete and clipboard newline handling separate.
- Support Japanese IME: show local composition/candidates, send valid committed
  text once, and do not also send Enter consumed by conversion. Reject a whole
  unencodable committed result. Cancel uncommitted composition on tab switch or
  disconnect; do not send or record it or replay it after reconnect.
- Keep SSH local echo off. Telnet defaults to Follow negotiation with manual
  Off available; serial defaults Off with On available. Store initial policy in
  the profile and permit temporary session changes without saving them; reconnect
  restores the profile. Eligible local echo belongs to the new combined mode,
  not Raw/Text. Do not include
  application-managed authentication values, unsent input, or framing/protocol
  traffic as local echo. Explain that manually entered secrets displayed locally
  may be recorded. See `terminal-interaction-issue.md`.
- Use Ctrl+Shift+C/V for terminal copy/paste, preserving Ctrl+C/V for terminal
  input. Default right-click to a Copy/Paste context menu; selection alone does
  not copy. Ordinary application text fields retain standard editing behavior.
- Support ordinary linear selection and Unicode plain-text copy, omitting
  terminal/style escape sequences. Join local soft wraps, preserve explicit
  boundaries as clipboard CRLF, and do not split wide characters or combining
  sequences. Preserve actual selected leading/interior/trailing spaces, omit
  unused right-side padding, and copy visible TAB spacing as spaces. Retain
  selections through unrelated output/scrolling; clear the whole selection if
  selected text changes or is partly evicted, without altering the clipboard
  or pausing reception/logging. Rectangular selection is outside the initial scope.
- While supported remote mouse reporting is active, route eligible unmodified
  mouse actions to the peer; Shift selects local selection/menu/available
  scrollback behavior. Keep each button gesture assigned to one recipient.
- Keep the viewport anchored while browsing retained output as new data arrives.
  Return to following live output at the bottom or when sending keyboard input/
  paste; local copy/search and cancelled paste do not jump. Reception/logging
  continue, and buffer eviction still enforces the retention limit.
- Confirm any paste containing CR or LF before sending it, including a single
  command with a trailing newline. Cancel sends none of the pending paste, and
  TabTty does not automatically append Enter/newline.
- Show a scrollable read-only confirmation preview with character/line counts
  and a trailing-newline indicator; make invisible controls identifiable.
  Default to Cancel and provide no permanent bypass for newline confirmation.
- Use bracketed-paste framing only when the current peer enables it. Keep
  newline confirmation regardless of that mode, add no newline, and do not
  carry enabled mode into a new connection. Remaining framing/mode-change
  details are tracked in `terminal-interaction-issue.md`.
- Reject pasted literal Unicode controls except TAB/CR/LF, without silently
  stripping/replacing them. Validate the full immutable payload before any
  sending and cap it at 1 MiB of encoded/finalized bytes, excluding bracket
  markers. Do not reject legitimate legacy-codec bytes or generated JIS shifts.
- Send paste payloads in order with progress and Stop. During sending, visibly
  reject additional user terminal input/pastes while reception, logging, and
  local copy/search continue. Stop discards unsent payload at a complete boundary
  and closes necessary encoding/framing on a healthy connection, without
  appending a newline or claiming to undo sent data. Never replay a disconnected
  paste on reconnect.
- Provide separate per-character and per-line delay controls in the newline-
  paste confirmation dialog, each accepting integer milliseconds from 0 through
  10,000. Every app start defaults both to zero with remembering off. Pace
  complete text elements and treat CRLF as one newline; before the next unit,
  add character delay plus line delay after a newline, without leading/trailing
  waits or inserted newlines.
  Show estimated extra waiting time and keep waits cancellable. Include a
  checkbox for remembering the delay pair application-wide across all profiles
  and windows only in memory until complete application exit. Do not persist
  the values or checkbox state in app settings or profiles. Checked Send updates
  the shared pair; unchecked Send uses the edited pair once and clears shared
  remembering. Cancel or failed validation leaves the shared state unchanged.
  No-newline paste uses the remembered character delay when enabled, otherwise
  zero. Open dialogs/running pastes retain their captured settings. See
  `terminal-interaction-issue.md` for accepted timing and dialog transitions.
- Provide literal search of retained normal-screen output with Ctrl+Shift+F,
  previous/next navigation, and a case-sensitive option (off by default). Keep
  Ctrl+F available for terminal input. Regex search, alternate-screen search,
  and on-disk log search are outside the initial scope. Match across soft wraps
  but not real line breaks. Refresh changed lines with bounded work around
  500 ms idle or every two seconds during continuous output. Preserve the current
  result while valid; losing it does not jump to another result automatically.
  Reject stale search results and keep background updates independent of viewport
  movement, selection, reception, and logging. See
  `terminal-interaction-issue.md` for confirmed behavior and remaining proposals.
- Prioritize selection colors over search-match colors, then regex highlighting,
  then terminal attributes. Use blue/white for selection and yellow/black for
  search, with an outline for the current result. Keep these styles independent
  of Light/Dark application themes and restore underlying attributes when an
  overlay is cleared; copied text and logs are unaffected.

## Encoding findings and open compatibility details

Microsoft's `System.Text.Encoding.CodePages` provider supplies many legacy
encodings; UTF-8 is available in the base .NET libraries. The provider alone
does not cover the accepted 48 identities: reviewed tables lack ISO-8859-10/-14/-16
and alias ISO-8859-11 to CP874. Q144 in `legacy-encoding-proposals.md` accepts
small TabTty-owned maps using published Unicode data for those ISO identities,
without another codec library. Preserve data provenance/notices when implemented.
Availability of a similarly named codec does not prove end-to-end terminal interoperability.

The concrete encoding inventory and remaining compatibility details are in
`teraterm-encoding-baseline.md`. Do not broaden the list merely because .NET
exposes additional code pages.

The accepted SSH architecture uses TabTty-owned protocol/session handling and
OpenSSL cryptography, preserving terminal-channel bytes for both legacy and
UTF-8 encodings. Responsibilities and remaining protocol decisions are in
`ssh-transport-issue.md`. RLogin is an architectural reference; its implementation
is not an adopted dependency. No package installation or prototype has occurred.

The Microsoft CP51932 EUC-JP codec does not support JIS X 0212. Static review
also found no JIS X 0212 Unicode mapping in the reference's EUC receive path.
Q145 accepts initial ordinary Japanese repertoire/half-width kana, shipped
B/B and 8-bit defaults, and standard kana wire forms instead of reproducing
source discrepancies. JIS X 0212/X 0213 are initially excluded. CP932 extension mapping
and malformed-sequence state tables still require technical comparison.

Retain decoder state across receive chunks and independently for each SSH
receive source under Q141. Q125 fixes settings per connection; Q126 prohibits
automatic transmit/log BOMs and recognizes only an actual leading UTF-8 signature,
independently per new receive source. These are accepted, not pending decisions.

Sources:

- [Microsoft CodePagesEncodingProvider](https://learn.microsoft.com/en-us/dotnet/api/system.text.codepagesencodingprovider?view=net-10.0)
- [Microsoft encoding list](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoding?view=net-10.0)
- [.NET EUC-JP implementation](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/EUCJPEncoding.cs.html)
- [Microsoft Decoder](https://learn.microsoft.com/en-us/dotnet/api/system.text.decoder?view=net-10.0)
- [Microsoft encoding fallback behavior](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding)

## Logging acceptance cases

The agreed receive-stream, newline, encoding, and profile-mode acceptance cases
are maintained in `receive-logging-issue.md`.

## Implementation contracts and verification obligations

The interview is complete. `specification-frontier.md` records accepted
Q100-Q102 (ARM64 optional), reference-based decisions for Q103-Q105, accepted
Q106-Q108 with the `.tttykey` vault extension, amended Q109-Q113 profile trust/
management, Q114-Q116 window behavior, and Q117-Q120 display/accessibility
(cursor choices excluded). `teraterm-behavior-baseline.md` distinguishes verified
reference behavior from TabTty adaptations. Unreviewed reference behavior is
not implicitly accepted and does not supersede earlier explicit requirements.

The following topic list preserves the original work decomposition. Its
previously open technical items are completed by the six contracts linked from
`../specification/implementation-readiness.md`; implementation/testing and later
release work remain. Do not treat historical wording here as unanswered questions.

- Concrete terminal compatibility checks, keyboard/IME behavior, Unicode width,
  alternate-screen control variants, mouse protocols/mode changes,
  paste framing details, cancellation races, and accessibility implementation
  contracts/acceptance cases. Q117-Q120 settle display and accessibility scope.
  Q106 resolves
  local resize/reflow, selection anchoring, and quota-induced Text retirement.
  Clipboard commands/content, newline confirmation/preview, peer-enabled bracketed paste,
  mouse ownership, scroll following, retained-output search, and display
  precedence, payload validation/limit, and ordered transmission/Stop are agreed
  in `terminal-interaction-issue.md`. Character/line pacing defaults, timing,
  10-second per-field caps, and application-wide remembering only for the current
  app run, including dialog transitions, are agreed. Copied whitespace, selection
  stability during output edits/eviction, and live search updates are agreed
  there as Q85-Q87. Enter/Backspace, Japanese IME, and local-echo policy are
  agreed in Q88-Q90. Raw/Text exclude local echo; a new combined mode inherits
  Q92/Q93's local newline and duplicate handling. Q91 is superseded; Q94's
  Terminal representation is accepted. Q95-Q98 are confirmed with the revised
  name/extension/marker. Q99 confirms shared basic output-control semantics and
  minimal local key mapping, without peer-editing emulation or guarded local
  deletion. Detailed serialization and event ordering remain open in the logging
  and terminal-interaction drafts. Keep receiver
  decoding/parsing separate from local echo independently of recording mode.
- Aggregate highlighting worker/cache limits, exact overlay styles, and later
  verification of accepted initial limits. Q121-Q124 settle cursor-addressed
  text-change scheduling, regex semantics, shared editing, and overload policy.
- Text-log event ordering, wrapped-row serialization, exact recording eligibility
  at boundaries, aggregate queue/cleanup bounds, and partial-write/shutdown
  transitions under accepted Q146-Q148. The 100-100,000 row range, 5,000-row default,
  application/profile inheritance, alternate-screen serialization/resume,
  screen-switch behavior, TAB/space representation, append-only row revisions,
  normal-screen mixed-line exclusion, initial file organization, extensible
  naming, and retention/UI scope are agreed.
- Transport protocol contracts/resource limits, character-encoding details, exact key validation
  and credential/host-identity schemas, and authentication protocol state/resource
  bounds. Q133-Q136 settle methods/retries, key/import scope, canonical vaulted
  keys, and local host records. Q131-Q132 settle connection deadlines,
  cancellation, and optional SSH liveness. Telnet/serial automatic login is excluded.
  Q137-Q139 settle transport algorithms, strict-KEX compatibility, compression,
  and rekey policy. Q140/Q142/Q143 settle session readiness/messages/closure;
  Q141's output-source composition is accepted, with detailed state/bounds and
  shutdown contracts still required.
- Exact profile schema/limits, registration/path identity, save failures,
  activation/close/transfer races, DPI/placement details, and exact app-settings
  schema/recovery transitions under Q155-Q157. Q109-Q116 settle profile management, trusted activation,
  validation timing, geometry policy, and tab-movement access. Automatic
  restoration of tabs/connections is outside the initial release.
- Portable vault exact framing/cryptographic limits, imported-key schema,
  file-failure transitions, security review/verification, and reference resolution
  on another PC. Q107-Q108 settle the container direction, password-change,
  backup/recovery, storage, and conflict policies.
- Confirm the limited architectural preparation for future macros without
  selecting or implementing a macro language/runtime for the initial release.
- The WPF/.NET 10 LTS foundation, required x64/optional ARM64, and ZIP/MSIX
  distribution/update policy are agreed. Exact drawing integration, OpenSSL
  integration and update maintenance, SSH protocol maintenance, runtime/native
  servicing versions and OS API minimum remain open for initial ZIP readiness.
  Preserve packaging compatibility; final Store identity/signing, ZIP-to-MSIX
  transfer, uninstall retention, and certification belong to later Store delivery
  under Q102 rather than implying immediate Store publication.
- Q159-Q160 in `capacity-and-overload-proposals.md` accept initial multi-session
  capacity/response targets and explicit overload behavior. Resource accounting
  and acceptance checks remain to complete; no measurement has been performed.
- `technical-contract-review.md` records the 2026-11-10 source review and
  remaining terminal, SSH, and vault contracts. Q161 in
  `key-import-passphrase-proposals.md` accepts UTF-8-only source-key passphrases
  initially, without an encoding selector, normalization, or alternate-encoding
  retry. It does not change vault-master-password rules or terminal encodings.

## Completion condition

The initial specification is consolidated and implementation preparation is
complete. Keep this issue draft unpublished until publication is authorized.
Wait for an explicit implementation request; runtime/security/release validation
remains governed by the consolidated acceptance plan.
