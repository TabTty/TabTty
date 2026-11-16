# Specify terminal interaction

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Working issue-body draft. Confirmed requirements and remaining proposals are
separated below. No implementation or prototyping is authorized.

## Confirmed resize policy (Q106 accepted)

`resize-and-vault-proposals.md` specifies normal-screen reflow with preserved
content/selection/search anchors, non-reflowing alternate screens, and delayed
reflow of a parked normal screen. Its explicit quota-eviction exception preserves
the existing Text logging rules. The user accepted this policy, including
preserving recording provenance and selection/search anchors through reflow.

## Confirmed external-effect and notification scope (Q152-Q154)

`terminal-external-effects-proposals.md` confirms no initial remote clipboard
read/write, title/icon-name replacement, or outer-window movement, resizing,
minimizing, maximizing, raising, or focus changes. Keep profile-name tab labels.
User-initiated Copy/Paste and window operations remain unchanged; ordinary
terminal cursor/edit operations and supported read-only replies are separate.

Render URLs and OSC 8 labels as ordinary selectable text, without automatic link
detection, click activation, an Open link action, or hidden-target copying.
BEL uses an application-wide persistent Visual (default) / Off setting. Visual
mode shows one coalesced tab indicator, briefly on the active tab or until the
inactive tab is acknowledged. Do not play sound, flash the terminal/taskbar,
show a toast, or steal focus. An OSC-terminating BEL is not a bell notification.
The detailed indicator lifetime and log boundaries are recorded in that document.

Raw still retains eligible received control bytes. Their presence in an exact
Raw log does not authorize their effects; Text/Terminal exclusions remain intact.

## Established constraints

Q117-Q120 are accepted in `terminal-display-proposals.md`: Consolas 12 pt with
installed fallback and app/profile settings, temporary tab zoom, explicit Unicode
cell-width/cluster policy, and initial keyboard/Narrator access. Q119 is amended:
only the fixed local blinking-block default, OS blink preferences, focus outline,
and received cursor controls are supported; no cursor choice UI or profile fields.
These preserve the existing logging semantics. Q114-Q116 window behavior is
accepted in `window-lifecycle-proposals.md`.

- Use a native terminal UI without xterm.js or WebView2. Support English and
  Japanese according to `../../AGENTS.md`.
- Keep reception, terminal-state updates, and logging independent of viewport
  browsing or tab/window movement. Highlighting may prioritize visible content
  as specified in `highlighting-issue.md`, without changing the received text.
- Retain normal-screen active rows and scrollback under the agreed 5,000-row
  application default/profile override. This retained output is distinct from
  on-disk logs and the subset currently visible.
- Do not add a dedicated on-disk log viewer/search UI or an action to open the
  log folder. Search within retained normal-screen output is supported below.
- The connection's transmit encoding and agreed rejection of unencodable input
  still apply to pasted text. Macros remain deferred.

## Reference-based terminal compatibility (Q103 onward)

The user delegated these choices to a Tera Term reference review. The selected
baseline and explicit adaptations are in `teraterm-behavior-baseline.md`:

- Advertise `xterm` by default for SSH/Telnet, with a profile override, and use
  VT100 primary identification for the initial terminal baseline. Neither is
  a claim of complete xterm or all-VT-model compatibility.
- Support the agreed ordinary full-screen applications with cursor/erase/edit,
  scrolling, DEC line graphics, alternate screens, and required modes/responses.
  Start with autowrap on, application cursor/keypad off, and mouse reporting off.
- Support 16/256 colors and actual 24-bit RGB. The RGB rendering is a TabTty
  extension over the reference's palette reduction. Preserve fixed defaults
  and the already agreed overlay precedence.
- Select X10/1000/1002/1003 mouse tracking with classic or SGR 1006 cell reports.
  Preserve the existing Shift/gesture rules. Other coordinate protocols and
  locator extensions are outside the initial scope.
- Default Unicode ambiguous width to one cell with a per-profile one/two choice,
  independent of UI language. Other Unicode/legacy-width and font rules need
  explicit acceptance cases rather than a generic equivalence claim.

Use accepted Q106 normal-screen reflow instead of the reference's width-truncating
resize behavior. Existing copy, paste, IME,
local echo, input ordering, and log semantics remain authoritative.

## Confirmed copy and paste commands (Q69 accepted)

- Use Ctrl+Shift+C and Ctrl+Shift+V for local copy and paste while the
  terminal has focus. Reserve Ctrl+C/Ctrl+V for terminal input rather than
  intercepting them as clipboard commands.
- By default, right-click opens a context menu containing Copy/Paste rather than
  pasting immediately. Selection alone does not update the clipboard.
- These bindings apply when the terminal has focus; ordinary application text
  fields keep their normal editing behavior. Apply the confirmed remote-mouse
  routing and Shift override below when reporting is active.

## Confirmed paste confirmation (Q70 accepted)

- Before sending clipboard text containing any CR or LF, show a paste
  confirmation and require an explicit decision to send. Include a single
  command with a trailing newline in this rule.
- Text without a newline pastes directly, subject to the existing encoding and
  connection requirements. Cancelling sends none of the pending paste. Never
  add an Enter key or newline automatically.
- Apply the same confirmation rule to keyboard and context-menu paste. The
  requirement to confirm CR/LF-containing content is independent of
  protocol-specific paste framing. Apply the confirmed payload validation,
  size limit, ordered transmission, and Stop behavior below.

### Confirmation preview (Q75 accepted)

- Show a scrollable, read-only preview of the complete accepted-size text, its
  character/line counts, and whether it ends in a newline. Make otherwise
  invisible controls identifiable without interpreting them as terminal input.
- Offer Send and Cancel, defaulting to Cancel. Do not offer a persistent bypass
  of the required newline confirmation in the initial release.
- A preview does not itself send input or update the clipboard. Detailed input
  validation and count/line-ending rules are resolved with the paste payload
  policy; the UI must not silently approve text different from what was shown.

### Bracketed paste (Q76 accepted)

- Support bracketed paste when the peer explicitly enables it for the current
  terminal session. Surround the pasted payload with the specified start/end
  markers; otherwise use the ordinary paste path without those markers.
- Keep the required CR/LF confirmation regardless of bracketed mode. Do not add
  a newline or assume framing prevents command execution.
- Keep bracketed mode session-local; a new connection must not inherit a prior
  connection's enabled mode. Detailed encoded-marker validation, mode changes
  during a paste, and Stop races/failures remain to be resolved within the
  confirmed payload/transmission requirements below.

### Payload validation and size (Q78 and Q79 accepted)

- Treat clipboard paste as text. Reject a paste containing literal Unicode C0
  controls other than TAB/CR/LF, DEL, or C1 controls before sending any of it.
  Identify the offending control rather than silently deleting/replacing it.
- Validate Unicode input, not raw encoded byte ranges: legitimate legacy-codec
  bytes and generated JIS state-change sequences remain permitted. Apply this
  rule to ordinary and bracketed paste, without changing direct keyboard input.
- Initially cap a single paste at 1 MiB (1,048,576 bytes) of fully encoded payload,
  including required encoder state-finalization output but excluding
  bracketed-paste markers. Keep the limit centralized for later adjustment; it
  is a product limit, not a measured throughput guarantee or an initial settings
  control for raising the limit.
- Validate the entire candidate's controls, strict encoding, finalization, and
  size before any payload or start marker is sent. Reject the whole paste on
  failure; never send only the prefix that fits.
- Use the same immutable text snapshot for validation, preview, and sending.
  Later clipboard changes cannot replace the approved text.

### Ordered transmission and Stop (Q80 accepted)

- Send each paste in ordered, bounded chunks with progress and an explicit Stop
  action. Character/line pacing may impose smaller emission units as specified
  below; chunking must not bypass configured delays.
- While transmitting, reject additional user terminal input and paste attempts
  visibly rather than interleaving or silently queueing them. Keep reception,
  logging, and local copy/search responsive.
- Stop discards unsent payload at a complete character/encoding-control boundary.
  On a healthy connection, send required encoding finalization and, if a start
  marker was sent, one bracketed-paste end marker before allowing new terminal
  input. Do not append a newline.
- Stop does not undo data already handed to the transport or remote execution.
  On disconnect/write failure, discard unsent remainder and do not replay it on
  reconnect; do not claim the closing marker arrived.

### Confirmed paste pacing (Q81-Q83 accepted with revisions)

- Provide independently editable per-character and per-line delay controls in
  the confirmation dialog for pasted text containing line endings, as explicitly
  requested by the user. This includes the already confirmed trailing-newline
  case; no extra confirmation is required just because the controls exist.
- Pacing changes scheduling, not clipboard text, receive-log contents, or the
  preflight payload-size rule. Keep the same validation and Stop workflow.
- On each application start, both delays default to 0 ms and remembering is off.
  These are application-wide paste controls; do not add per-profile delay
  defaults or last-used delay settings to connection profile files.
- Provide a checkbox controlling whether the selected delay values are remembered.
  Remembered values are application-wide, shared across profiles, tabs, and
  windows; do not keep a separate last-used pair for each connection profile.
  Hold the values and checkbox state only in memory for the current application
  run. Completely exiting TabTty discards them; the next start returns to 0 ms
  for both delays with remembering off. Never persist them to application
  settings or profile files. Closing one window while another remains does not
  end this lifetime. Confirmed Send/Cancel and checkbox transitions follow below.
- Accept integer milliseconds from 0 through 10,000 (10 seconds) for each delay,
  with zero disabling that extra wait.
- Pace by complete Unicode text elements (extended grapheme clusters), not
  UTF-16 code units or encoded bytes. Treat CRLF as one line-ending unit with no
  deliberate wait between CR and LF; standalone CR/LF each constitute one line
  ending for this outgoing paste timing policy. Before the next payload unit,
  apply character delay C, plus additional line delay L if the preceding unit
  was a line ending. Thus C=10 ms and L=100 ms yield 110 ms after a line ending.
  The individual 10-second caps allow a combined 20-second wait after a newline.
- Do not add a leading wait, a wait after the final payload unit, or any newline.
  Do not count generated bracket/encoding-control bytes as pasted characters or
  insert pacing waits inside their sequences. Preserve encoder state across
  pacing units; local display wrapping does not create line-delay events.
- Show estimated additional waiting time based on the selected delays and
  payload units, separately from transfer/remote processing time. Waiting remains
  cancellable; Stop must not wait for a long pacing timer to expire before
  discarding the remainder and performing necessary closure. Never delay
  reception/logging or apply paste pacing to ordinary typing, authentication
  exchanges, or protocol traffic. These are TabTty-side waits, not guaranteed
  peer arrival intervals or command-completion detection.
- No-newline paste retains its direct path without opening this confirmation.
  While remembering is enabled, use the shared character delay; otherwise use
  zero. Such a paste has no line-delay events and does not update the shared
  values or checkbox state.

### Remembered delay controls (Q84 accepted)

- Label the checkbox "Remember these delays" / "この遅延設定を保持する", with
  a localized explanation that it applies until the application exits.
- When a validated paste is accepted for sending with the checkbox checked,
  update the application-wide in-memory delay pair and enabled state.
  Subsequent dialogs in any profile/window start with that pair and a checked
  box. Apply the existing 0 ms and unchecked defaults on every application start.
- When Send is accepted with the checkbox unchecked, use the edited pair for
  this paste only, disable remembering, and discard the previous shared pair.
  Subsequent dialogs start at 0 ms for both fields with an unchecked box.
- Cancel or failed validation leaves the shared state unchanged. Stopping or
  failing an already accepted transmission does not undo the in-memory settings.
- Each dialog/running paste keeps its captured values if another window changes
  the shared settings. The latest explicitly accepted Send determines the shared
  values used for subsequently opened dialogs or direct pastes. Never update
  the timing of an already running paste from another dialog's changes.

## Confirmed retained-output search (Q71 accepted)

Q127-Q128 in `search-and-encoding-proposals.md` confirm ordinal literal search,
wrapping navigation, a 1,024-unit query limit and 10,000-range cache (without
excluding later results), tab-lifetime query memory, and screen-switch behavior.
Q125-Q126 confirm next-connection-only encoding edits and initial UTF-8 signature
suppression from interpreted text while preserving every Raw byte.

- Provide Ctrl+Shift+F search over the current normal-screen retained buffer,
  including scrollback, with previous/next result navigation.
- Use literal substring search initially, case-insensitive by default with a
  case-sensitive option. Initially exclude regex search, alternate-screen
  search, and on-disk log search.
- Keep Ctrl+F available for terminal input. Searching and navigating results do
  not send terminal input or alter reception, terminal contents, or logging.
- Copy/search can operate on retained output after disconnect; they do not
  require reconnecting or reopening saved log files.
- Use OrdinalIgnoreCase/Ordinal according to the case option, with no width,
  kana, diacritic, or normalization folding. Enter/Shift+Enter moves next/previous
  while search owns focus; Escape closes search without sending that key remotely.
  Wrap at buffer ends with a quiet indication and preserve viewport on close.
- Query and case option live only for the current tab. Limit a query to 1,024
  UTF-16 units and reject multiline/invalid/overlong input without truncation.
  Cache at most 10,000 result ranges, with a 10,000+ indicator and on-demand
  navigation through all retained text, including lines excluded from regex work.

### Search updates (Q87 accepted)

- While normal-screen search is open, refresh matches after displayed text
  changes, searching changed logical lines rather than repeatedly scanning the
  entire retained buffer. Use a 500 ms idle target and bounded progress about
  every two seconds during continuous output; query/options changes also
  schedule a bounded search.
- Join soft wraps for matching, but do not match across explicit line boundaries
  in this initial single-line literal search. Keep the current match anchored
  if it still exists. If it changes or is evicted, clear its current-match
  indication without automatically scrolling to another result; explicit
  Next/Previous selects a current result.
- Reject stale results computed for older text/query versions. Background
  result updates do not move the viewport or selection, stop reception/logging,
  or retain output beyond the configured buffer limit.

### Selection and search display (Q77 accepted)

- For foreground/background colors, use this precedence: selection, then search
  matches, then regex highlighting, then received terminal attributes/fixed
  defaults. This does not merge multiple regex rules or change their ordering.
- Use blue background/white text for selection and yellow background/black text
  for search matches. Add an outline to the current search result so it remains
  distinguishable from other matches, including when overlapping selection.
- Use the same overlay styles in Light and Dark application themes. Clearing
  an overlay restores the current underlying attributes; do not change the
  terminal's fixed default colors or the stored highlight rule definitions.
- Copied text, received data, and logs are unaffected. Exact color values and
  accessible focus/cursor appearance remain visual-design details. See
  `highlighting-issue.md` for the shared display precedence.

## Confirmed selection and copied content (Q72 accepted)

- Initially support ordinary linear text selection and plain-text copy, without
  terminal escape sequences, ANSI colors, or highlight styling. Defer rectangular
  selection.
- Join locally soft-wrapped portions of a logical line and preserve explicit
  line boundaries. Do not add a newline merely because the viewport is narrow.
- Serialize retained line boundaries as CRLF in Windows Unicode clipboard text,
  independently of the receive-log rule that preserves CRLF versus LF.
- Include whole displayed characters rather than splitting surrogate pairs,
  wide glyphs, or combining sequences at selection endpoints.

### Copied whitespace (Q85 accepted)

- Copy the selected displayed text, preserving selected leading/interior spaces
  and actual received trailing spaces that remain in the final contents. Omit
  unused or erased right-side padding rather than extending copied lines to the
  terminal width. Preserve interior layout gaps as spaces.
- Expand visible tab spacing to spaces for clipboard copy, including only
  selected cells when part of that spacing is selected. Do not trim genuine
  received whitespace or reconstruct a TAB from visually identical spaces.
- Keep the agreed joining of local soft wraps and CRLF serialization of selected
  real line boundaries. These clipboard choices do not change Text-log
  TAB/whitespace semantics.

### Selection during output updates (Q86 accepted)

- Keep a selection attached to its selected content through ordinary new output
  and scrolling, without pausing reception/logging or pinning unlimited history.
- If selected text is changed by overwrite or erasure, or is partly evicted,
  clear the whole selection so a subsequent Copy cannot silently copy
  replacement text or a shortened range. Unrelated output, unchanged redraws,
  and color/highlight-only changes do not clear it.
- Do not change the system clipboard when clearing a selection. Q106 preserves
  selection across reflow while its content survives, applying the existing
  clearing rules on edits/eviction. Q128 clears selection on every normal/
  alternate screen transition; never restore the old selection automatically.
- While alternate screen is active, suspend an open search's navigation and
  overlays, retaining its query/options and allowing close. Returning to normal
  recomputes matches if search remains open without selecting/jumping to a result.
  New selection/copy on the alternate screen remains available.

## Confirmed remote mouse routing (Q73 accepted)

- When the remote application enables supported mouse reporting, send
  unmodified terminal mouse interactions to it.
- Holding Shift instead performs local selection/context-menu actions and,
  where local scrollback is available, local scrolling. Without remote reporting,
  preserve the default local right-click menu.
- An interaction must not both act locally and be sent remotely. Choose its
  local/remote owner at button-down and keep that owner through the gesture,
  rather than switching halfway when Shift changes.
- Preserve required matching press/release reports while the connection and
  reporting mode remain valid; do not invent release reports for wheel or
  press-only protocols. Supported mouse protocols and mode-change/cancellation
  details remain to be specified.

## Confirmed scroll following (Q74 accepted)

- While browsing older retained normal-screen output, keep the viewed position
  anchored as new output arrives instead of jumping to the bottom. Reception
  and logging continue throughout browsing.
- Resume following output when the user returns to the bottom. Accepted keyboard
  input/paste that will be sent to the session returns the viewport to the live
  cursor; local copy/search/navigation and cancelled paste do not.
- If anchored rows are evicted, clamp to the oldest remaining content instead
  of retaining unlimited history.

Q184/Q195 extend this behavior to independent panes of one session. Input or
search navigation moves only the operated pane; broadcast delivery does not
move other panes browsing history. Each pane has local horizontal/vertical
scrollbars when needed. Query/case and temporary zoom remain shared by the tab.
See the [split-view contract](../specification/broadcast-and-split-views.md).

## Confirmed Enter and Backspace settings (Q88 accepted)

- Store Enter and Backspace behavior in each saved connection profile,
  capturing it when connecting; profile edits apply on the next connection.
  Enter offers CR, LF, and CRLF, defaulting to CR for SSH/serial and CRLF for
  Telnet. Backspace offers BS (0x08) and DEL (0x7F), defaulting to DEL for
  SSH/Telnet and BS for serial. These are product defaults, not guarantees of
  the remote application's editing behavior. Keep the physical Delete key
  separate; it is not reconfigured by the Backspace choice. For SSH, request an
  initial PTY VERASE matching the selected Backspace value, without claiming
  later application-controlled modes cannot change it. Enter selection generates
  terminal input before Telnet transport encoding: non-BINARY CR is sent as
  CR NUL, LF as LF, and CRLF as CRLF. With transmit-direction BINARY active,
  do not apply NVT CR conversion. Do not use the keyboard Enter setting to
  normalize clipboard newlines or modify received data/log line endings.

## Confirmed Japanese IME behavior (Q89 accepted)

- Support Windows Japanese IME in the initial release, displaying
  composition/candidates near the terminal caret without choosing a UI framework
  yet. Keep uncommitted composition local and outside session logs.
  Validate each committed result against the active transmit encoding before
  sending it once; reject an unencodable result as a whole and notify the user,
  without replacement characters or a partial send. Enter consumed to commit
  conversion must not also send terminal Enter; a later independent Enter uses
  the profile's selected sequence. IME cancellation sends nothing. Bind
  composition to its originating pane/session, cancel uncommitted composition on
  tab switching, disconnect, or focus movement to another terminal pane (even in
  the same tab, Q194), and never transfer it to another session or
  replay it on reconnect. Committed IME text follows ordinary keyboard input,
  not clipboard paste pacing or paste framing. Detailed event plumbing follows
  framework selection; composition-end alone is not evidence of a committed
  string.

## Confirmed local echo (Q90 restored; Terminal recording accepted)

- Define local echo as displaying submitted user input locally, separate
  from remote echo received from the peer. Keep it off for SSH in the initial
  release. For Telnet, offer "Follow negotiation" (default) and "Off": following
  negotiation suppresses local echo while the peer's ECHO option is enabled,
  and permits it while that option is disabled. This is TabTty's client policy,
  not a guarantee that the peer will echo every character. For serial, offer
  Off (default) and On. Store the initial policy in the profile and allow a
  temporary session change among that transport's supported choices without
  rewriting the profile; reconnect restores the saved policy. Show the current
  effective local-echo state. Never infer echo mode from missing/delayed replies.
  Local display is limited to submitted user typing, committed IME text, and
  the submitted portion of a paste; exclude uncommitted text, unsent paste data,
  protocol/framing bytes, and application-managed authentication values.
  When local echo is enabled, ordinary manually typed input may be visible even
  if the peer would hide it; explain this at the setting and allow turning it
  off before manual secret entry. The user restored Q90's original exclusion of
  local echo from Raw/Text and requested a separate combined logging mode.
  Raw must preserve received terminal bytes exactly. Actual remote echo remains
  received data. Manually entered secrets displayed through local echo may
  appear in the new combined mode; application-managed
  authentication values remain excluded from local echo and its recording.
  Q92/Q93 now apply only to the new mode. Q91's Raw container is superseded;
  Revised Q94 is accepted: record the combined operations as UTF-8 text and
  equivalent supported VT controls without waiting for final-line snapshots.
  Q95-Q98 of `receive-logging-issue.md` are accepted, with mode name Terminal,
  provisional `.tttylog` extension, and `.term` filename marker. Local rendering
  and the preference for shared terminal semantics are confirmed in Q99 below;
  detailed event ordering remains open.

## Confirmed submitted text echo (Q99 accepted)

The user accepts displaying the submitted portion of committed IME and pasted
text. Ordinary submitted typing remains covered by Q90. Uncommitted/rejected
text, unsent paste remainders, protocol/framing traffic, and application-managed
authentication values remain excluded. Printable text produced by keyboard
layouts is text regardless of the modifiers involved.

The user accepts treating local echo as ordinary displayed output in Terminal
recording, much as if the peer had echoed it. Fully imitating the peer's echo
or command-editing behavior is not a goal. Prefer shared terminal semantics
and the small key mapping below over additional local heuristics.

## Confirmed shared control semantics and key mapping (Q99 revised, accepted)

- Use the same terminal output operations for echoed BS, TAB, CR, and LF as
  for received controls. Record their equivalent control characters in Terminal
  mode in the common application order. Logging those controls does not require
  converting them to edited text or deleting their history.
- BS moves the cursor left under ordinary terminal bounds/modes; it does not
  erase a character. TAB moves to the next tab stop without erasure. CR/LF use
  the same output semantics as received CR/LF. For example, `abc`, BS leaves
  the text `abc` with the caret moved back; a subsequent `X` yields `abX`.
  The earlier source-owned erasable-span, whole-local-grapheme deletion, and
  suppression-after-receive proposals are withdrawn; do not add them as hidden
  local editing rules.
- For the physical Backspace key, generate a local BS display
  operation regardless of the selected transmitted BS/DEL value. Keep the
  outbound profile mapping unchanged. This is a product mapping, not a claim
  that received DEL is equivalent to BS or performs deletion.
- Keep the accepted physical Enter local CRLF behavior, independently of the
  outgoing CR/LF/CRLF setting. Pasted CR/LF keep their actual meaning under Q92.
  TAB and printable/committed text
  generate their corresponding local output operations.
- Generate no local display for navigation keys,
  physical Delete, function keys, Escape, or Ctrl/Alt-generated control-key
  sequences. Send them under the terminal input mapping; process any peer
  output normally. Printable layout text remains eligible. This key-to-echo
  mapping is an accepted product choice, not a protocol requirement. Input
  encodings such as normal-mode Up's `ESC[A` need not have their input meaning
  when interpreted as output; full literal key-code echo is not selected.
- Shared output semantics does not mean shared unfinished parsing state.
  Keep receive decoding and partial escape sequences isolated from local echo.
  Generate complete local operations for the accepted mapping; do not feed
  outgoing bytes into the receive parser.
- Merge received and local operations in their common terminal-processing
  order for display and Terminal recording. Do not add source-label text or
  pretend that locally generated events actually arrived from the peer.
  Keep source identity internally for receive-only Raw/Text, receive observers,
  and recording-interval eligibility. This is shared display/log processing,
  not injection into the actual receive-data stream.
- Terminal remains a display-operation log under Q94/Q97. Controls that are
  not echoed or do not produce an eligible display operation are not added just
  because they were transmitted. Recording every transmitted control, including
  non-display/unsupported controls, would require an explicit scope change.
  Raw and Text continue to exclude all locally generated echo.

Prefer the common terminal renderer and control semantics for later refinements.
Do not add inferred peer-editing behavior, source-owned deletion heuristics,
command-history emulation, or heuristic echo deduplication. The accepted mapping
does not add a local line editor, command buffering, transmit log, or Telnet
LINEMODE negotiation. A future behavior change requires an explicit specification
decision rather than an attempt to imitate an unknown remote application.

## Further decisions

- Copy/paste commands/content, newline confirmation/preview, use of peer-enabled
  bracketed paste, mouse ownership, scroll following, retained-output search,
  selection/search display precedence, payload validation/limit, and ordered
  transmission/Stop are agreed. Character/line pacing defaults, timing, the
  10-second cap per field, and application-wide remembering for the current run
  only, including Send/Cancel and checkbox transitions, are agreed.
- Copied whitespace, selection stability during editing/eviction, and live
  search updates are agreed in Q85-Q87. Initial mouse protocols are selected
  in the reference baseline; mode changes, coordinate bounds/release behavior,
  and scrollback input routing remain open.
- Enter/Backspace, Japanese IME, and local-echo policy are agreed in Q88-Q90.
  Raw/Text exclude local echo. Q94 accepts operation-stream recording, preserving
  Q92's local Enter CRLF and Q93's duplicate handling. Q95-Q98 are confirmed
  in `receive-logging-issue.md` with the Terminal name and shortened `.term`
  filename marker. Q99's submitted text echo, shared basic control semantics,
  minimal key mapping, and avoidance of peer-emulation heuristics are accepted.
  Other keyboard mappings, detailed Unicode/legacy-width cases, and exact
  local-echo event ordering remain open. Q117-Q120 settle font/zoom, Unicode
  width direction, fixed cursor behavior, and initial accessibility scope.
  Resize/reflow is accepted in Q106. The ambiguous-width default and profile
  override remain independent of UI language.
- Detailed paste framing/encoded-marker validation, count/line-ending rules,
  chunk/pacing boundaries, mode changes during a paste, asynchronous transport
  races, stop deadlines/failures, and ordering of automatic terminal replies.
  A local encoder reset alone cannot restore a peer's encoding state. Strict fallback
  does not by itself prove round-trip mapping fidelity; resolve JIS/half-width
  kana behavior against `teraterm-encoding-baseline.md`.
- Exact global search work/cache budgets and failure transitions under Q127-Q128's
  accepted navigation/bounds/screen-switch policy; exact overlay color values
  and cursor contrast/control coverage within accepted fixed
  cursor/focus behavior. No cursor customization is pending for initial release.
- Detailed UI Automation ranges/events and verification cases for the accepted
  keyboard/Narrator scope; remaining keyboard bindings.

## Agreed acceptance cases

- With local echo active and eligible recording, received `prompt> ` followed
  by locally echoed `help` and Enter yields `prompt> help\r\n` in Terminal,
  without local/remote source labels. Raw/Text do not gain the local input.
- A physical Backspace transmitted as either BS or DEL generates local BS,
  leaving existing text in place while moving the caret left under the same
  terminal rules as received BS. For `abc`, BS, `X`, Terminal retains the
  ordered text/BS/text operations and the displayed result is `abX`.
  No source-owned deletion or receive-interruption editing guard is applied.
- Navigation/Delete/function/Escape/control-key sequences are transmitted under
  their mappings without creating local display operations. Received output
  resulting from those inputs is processed normally. Ordinary printable text
  after an Escape key is not consumed by a pending local-output ESC parser.
- Screen and Terminal recording share completed operations, while receive-only
  observers and Raw/Text retain the actual-receive boundary. No inferred command
  history, remote editing, or string/time-based echo deduplication is introduced.
- New SSH/serial profiles default Enter to CR; Telnet defaults to CRLF.
  Backspace defaults to DEL for SSH/Telnet and BS for serial. Saved changes
  apply on the next connection. The Delete key and clipboard newlines are not
  remapped by those settings. Telnet non-BINARY and BINARY encoding remain
  separate from the selected terminal input sequence.
- Uncommitted IME text is neither sent nor recorded. A valid committed result
  is sent once; conversion Enter does not also send terminal Enter. A later
  independent Enter sends the profile sequence. An unencodable result sends
  nothing, and cancelled composition is not replayed into another tab or a
  reconnected session.
- SSH local echo is off. Telnet follows ECHO negotiation by default, with a
  manual Off option; serial defaults Off and supports On. A temporary session
  change does not rewrite the profile, and reconnect restores its saved policy.
- When enabled local echo contributes terminal output while recording is
  active, it is eligible for the new combined mode, never existing Raw/Text.
  Remote echo remains received data. Uncommitted input,
  unsent paste remainder, protocol/framing bytes, and application-managed
  authentication values do not become local-echo records. The new mode's
  local CRLF and duplicate handling follow accepted Q92/Q93. Q94's accepted
  operation stream replaces earlier line-snapshot rules: record eligible text
  and controls in operation order without waiting for a newline.
- Incomplete receive characters/control sequences remain isolated from local
  echo, which reaches terminal state as independent text/display operations.
  Correct screen behavior does not depend on choosing a structured Raw file.
- With the terminal focused, Ctrl+Shift+C/V invoke local clipboard commands;
  Ctrl+C/V remain terminal input. Ordinary settings text fields retain normal
  text-editing shortcuts.
- Without remote mouse reporting, right-click opens the local context menu and
  does not paste. Selecting text alone leaves the clipboard unchanged.
- Pasting `command\n` requires confirmation even though it contains only one
  command. A paste containing CR, LF, or CRLF asks before any of its text is sent;
  cancellation sends none. No-newline text takes the direct paste path.
- Keyboard and menu paste use the same confirmation/encoding rules. Do not
  append an Enter key or newline that was not supplied in the paste.
- The required confirmation shows the complete accepted-size text in a read-only
  scrolling preview, character/line counts, and a trailing-newline indication.
  Cancel is the default action. Previewing sends nothing and does not update
  the clipboard; there is no permanent bypass for newline confirmation.
- A peer-enabled bracketed paste sends one start/end pair around its payload;
  an ordinary paste adds neither marker. Both still require confirmation for
  CR/LF-containing content and neither adds a newline. A new connection starts
  without inheriting bracketed mode from the previous connection.
- A literal pasted ESC/NUL or another disallowed Unicode control rejects the
  whole paste without sending payload or a start marker. TAB/CR/LF remain
  permitted. Legitimate CP932 bytes and generated JIS shifts are not rejected
  by a raw-byte blacklist.
- A payload over 1 MiB after full encoding/finalization is rejected as a whole;
  bracket markers are excluded from that size. Unencodable text also sends
  nothing. The reviewed snapshot, rather than later clipboard contents, is sent.
- A running paste exposes progress and Stop. Additional user terminal input is
  visibly rejected, not mixed into the paste or silently replayed later; local
  copy/search, reception, and logging continue.
- Stopping a healthy bracketed paste discards unsent payload at a complete
  boundary and sends required encoding closure/end marker without a newline.
  No status claims to undo already handed-off data. Disconnect discards the
  remaining operation; reconnect does not resend its payload or closing marker.
- The newline-paste confirmation contains separate editable character-delay
  and line-delay controls, each accepting integers from 0 through 10,000 ms.
  Every application start uses 0 ms for both with remembering unchecked. A
  checkbox controls remembering; the in-memory pair is shared across profiles
  and windows, never per-profile and never persisted to disk. Closing one window
  while another remains preserves this shared state; complete application exit
  discards it and a new run starts with the defaults.
- With C=10 ms and L=100 ms, pasting `AB\r\nC` inserts waits of 10 ms between A
  and B, 10 ms between B and CRLF, and 110 ms between CRLF and C: 130 ms total
  estimated additional waiting. No wait splits CRLF, precedes A, or follows C.
  A combining sequence is one pacing unit; generated framing/encoding controls
  add no units. Soft wrapping does not trigger a line delay.
- Stop interrupts a pacing wait without waiting for its remaining duration;
  reception, logging, and local interaction remain responsive.
- Sending C=10 ms and L=100 ms with remembering checked makes that pair and an
  enabled checkbox available in a subsequently opened dialog in another profile
  or window. No-newline paste then uses C=10 ms without adding a confirmation.
- Editing the pair or checkbox and then cancelling, or failing validation,
  leaves the previous shared state unchanged. Sending with remembering unchecked
  uses that paste's edited values only; the next dialog starts at 0 ms with an
  unchecked box, and subsequent direct paste has zero configured pacing delay.
- Stopping an accepted paste does not roll back the remembered settings. Another
  window's accepted Send updates only future dialogs/direct pastes, not values
  already captured by an open dialog or a running paste. Nothing is written to
  settings/profile files, and a complete restart resets both values and the box.
- Ctrl+Shift+F finds literal matches within the retained normal-screen buffer,
  including off-screen history, and supports previous/next navigation. Case
  sensitivity can be changed. Ctrl+F remains available to the session.
- Searching or copying retained output after disconnect neither reconnects nor
  reads an on-disk log. Search navigation does not change recorded content.
- A normal-screen search can find a match split by local soft wrapping but
  cannot match across an actual line break. New/changed output refreshes results
  with bounded work; obsolete results for older text/query versions are not
  applied. Losing the current match clears its indication without a viewport
  jump; Next/Previous performs explicit result navigation.
- A cell covered by a regex rule, a search match, and selection uses the
  selection's foreground/background. Clearing selection reveals search colors;
  clearing search reveals the current regex/terminal colors. The current-result
  outline remains distinguishable when selection overlaps it. Theme changes
  do not change these styles or recorded/copied text.
- At a five-column width, selecting locally wrapped `ABCDEF` copies `ABCDEF`
  without a break between E and F. An actual selected line boundary is copied
  as CRLF. Terminal colors, regex highlight styles, and selection decorations
  do not enter the copied text.
- Selecting either half of a displayed wide character includes that character
  once, without splitting a surrogate pair or attached combining sequence.
- Copying selected `  abc  ` preserves those actual spaces but adds no padding
  out to the right margin. Selected tab-expanded gaps copy as spaces, including
  a partial selected gap; this does not change how Text logs preserve TAB.
- New output and ordinary scrolling preserve a selection whose text remains
  available and unchanged. Changing or evicting any selected text clears the
  entire selection without modifying the clipboard or pausing reception/logging.
  Unrelated changes and color-only redraws leave it selected.
- When mouse reporting is active, unmodified eligible interactions go to the
  peer and Shift interactions stay local. A gesture is never delivered to both.
  Changing Shift during a button gesture does not orphan a required remote
  release or send a remote release for a locally started gesture.
- Reading earlier retained output does not jump to the bottom when new output
  arrives. Returning to the bottom or sending accepted keyboard input/paste
  resumes the live view; local copy/search and cancelled paste do not.
- Retention eviction still enforces the configured buffer limit while browsing;
  the viewport falls back to the oldest remaining content if its anchor is lost.

These requirements do not settle the separate terminal compatibility profile.
Session logging follows the updated scope and the remaining format/behavior
decisions in `receive-logging-issue.md`.

## Reference facts and design distinctions

The keyboard/IME/echo references below were checked on 2026-11-08. They describe
protocol/platform facts separately from the accepted product choices above.

- RFC 1123 recommends CRLF as the User Telnet end-of-line default, with CRLF,
  CR NUL, and LF send support. In the negotiated BINARY direction it prohibits
  NVT character conversion while retaining IAC escaping. Keyboard-generated
  CRLF remains distinct from transport-layer conversion of an existing CR.
- Microsoft's VT input table distinguishes Backspace (DEL) from the Delete
  sequence. BS versus DEL defaults for TabTty profiles are product choices.
  RFC 4254 allows an initial PTY VERASE value; the server or remote application
  can still determine editing behavior. Initial PTY modes do not report all
  subsequent remote echo-state changes to the client.
- The Q99 review distinguishes terminal input from output interpretation:
  Microsoft's input Up/Delete sequences do not themselves prescribe a client
  echo operation. RFC 854's output BS moves left without erasing, while input
  editing has separate protocol concepts. RFC 857 leaves self-echo to each
  endpoint, and RFC 1184's negotiated line editing is a different feature.
  Revised Q99 therefore adopts shared basic output-control semantics rather
  than the withdrawn guarded local-erase policy. A local Backspace-to-BS mapping
  is still a TabTty choice independent of the outbound BS/DEL setting. DEL is
  not universally synonymous with deletion: Microsoft's output handling can
  ignore it or use a designated 96-character set's glyph mapping. TabTty's
  supported character-set details remain a separate compatibility decision.
  Neither literal key-code echo nor a local editing policy establishes the
  peer's editable command-buffer contents.
- RFC 857 leaves self/local echo to each endpoint; a client may use remote ECHO
  negotiation in its decision. RFC 1184's password example uses server WILL
  ECHO to suppress user-visible echo. Silence or missing character reflection
  is therefore not evidence that local echo should be enabled. Telnet ECHO
  support and a chosen local-display policy are distinct decisions.
- Microsoft distinguishes IME composition text from committed results and
  instructs applications not to process a key already consumed by a text
  service. Composition ending is not itself a committed text result. TabTty's
  no-double-send and no-extra-Enter behavior can be specified independently of
  the eventual WPF/WinUI/native text-input integration.
- SSH keyboard-interactive prompt echo flags belong to the authentication UI,
  not to the terminal local-echo setting. Authentication values remain excluded
  from local echo and its logging in the new combined mode; existing Raw/Text
  remain receive-only. See `authentication-issue.md`.

- A remote application enables supported mouse reporting through terminal
  control sequences. Do not infer the mode from a command name. Button tracking,
  coordinate encodings, and whether release events exist vary by protocol.
- Microsoft documents Shift selection while terminal mouse reporting is active.
  Extending the local override to context menus and available scrollback, and
  fixing gesture ownership at button-down, are accepted TabTty decisions rather
  than requirements imposed by the mouse-reporting protocol.
- Terminal wrap metadata can distinguish local soft wraps from received line
  endings; received line endings cannot reliably reveal a remote application's
  formatting intent. Copy must not guess which received line endings to remove.
- Windows CF_UNICODETEXT specifies CRLF line separators. TabTty's accepted
  plain-text copy omits terminal color/style sequences; that omission is a
  product choice, not a restriction on which characters the clipboard can hold.
  This clipboard representation does not change Text log serialization or
  require adopting another renderer.
- Microsoft terminal selection documentation and .NET Unicode guidance support
  avoiding splits within wide glyphs, surrogate pairs, and grapheme clusters.
  Exact terminal rendering/width compatibility remains a separate design topic.
- xterm defines bracketed-paste mode 2004: received `ESC [ ? 2004 h` enables it,
  `ESC [ ? 2004 l` disables it, and paste framing uses `ESC [ 200 ~` followed
  by the payload and `ESC [ 201 ~`. This is framing, not a length-delimited or
  escaped transfer format, input-queue isolation, or a nonexecution guarantee.
- Microsoft's terminal source also keeps a confirmation option independent of
  bracketed mode. Its conhost paste implementation illustrates removing ESC
  from bracketed payloads; that is one implementation policy, not a standardized
  escaping rule. TabTty instead rejects disallowed literal controls as a whole;
  remaining encoded-marker and cancellation-race details must still be resolved.
- Unicode control classification and encoded-byte values are different. For
  example, Microsoft's code-page documentation encodes a normal CP932 character
  using bytes in the 0x80-0x9F range, and ISO-2022-JP legitimately generates ESC
  sequences to change character sets. An encoded-byte blacklist would corrupt
  supported text. RFC 1468 describes an ISO-2022-JP mail format, not a terminal
  cancellation protocol or the full Tera Term JIS compatibility profile.
- .NET encoders preserve state and may have pending output after input is
  consumed. Finalization must be completed; Reset only clears local state.
  EncoderExceptionFallback rejects unmappable input, but built-in codec mapping
  (such as CP50220 half-width-to-full-width kana conversion) can occur without
  fallback, so strict fallback alone is not proof of exact round-trip fidelity.
- Transport send completion is not proof of remote receipt/execution, and an
  I/O cancellation request can race with successful completion. The accepted
  Stop action therefore applies to unsent payload, not a transactional undo.
- .NET char represents a UTF-16 code unit; a Unicode text element can span
  multiple code units or scalar values. Grapheme-boundary rules keep CRLF
  together. Using text elements for pacing and adding character/line waits are
  TabTty product choices, not delay semantics imposed by those standards.
- .NET/Windows waits are affected by timer resolution and scheduling; socket
  buffering also separates handing off data from peer arrival. Millisecond
  controls must not promise exact real-time spacing or remote processing gaps.
  A stateful encoder must remain continuous across pacing boundaries rather
  than resetting for each independently encoded substring.

## Primary references

- [Telnet end-of-line and BINARY requirements (RFC 1123)](https://www.rfc-editor.org/rfc/rfc1123.html#section-3.3.1)
- [Telnet NVT CR representation (RFC 854)](https://www.rfc-editor.org/rfc/rfc854.html)
- [Telnet ECHO option and local echo (RFC 857)](https://www.rfc-editor.org/rfc/rfc857.html#section-5)
- [Telnet password echo example (RFC 1184)](https://www.rfc-editor.org/rfc/rfc1184.html#section-5.10)
- [SSH PTY terminal modes (RFC 4254)](https://www.rfc-editor.org/rfc/rfc4254.html#section-8)
- [SSH authentication prompt echo flag (RFC 4256)](https://www.rfc-editor.org/rfc/rfc4256.html#section-3.3)
- [Microsoft VT input sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences#input-sequences)
- [Microsoft terminal BS/TAB/CR/LF and DEL output handling](https://github.com/microsoft/terminal/blob/main/src/terminal/parser/OutputStateMachineEngine.cpp)
- [Microsoft terminal persistent escape-sequence parsing](https://github.com/microsoft/terminal/blob/main/src/terminal/parser/stateMachine.cpp)
- [Microsoft IME composition and result events](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-composition)
- [Microsoft IME composition-end notification](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-endcomposition)
- [Microsoft text-service key consumption](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-keydown)
- [xterm mouse tracking](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h2-Mouse-Tracking)
- [Microsoft terminal mouse selection](https://learn.microsoft.com/en-us/windows/terminal/tips-and-tricks#virtual-terminal-and-wsl-mouse-support)
- [Microsoft copy action](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions#copy)
- [Windows standard clipboard formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats)
- [Microsoft terminal selection specification](https://github.com/microsoft/terminal/blob/main/doc/specs/%234993%20-%20Keyboard%20Selection/Keyboard-Selection.md#grapheme-clusters)
- [.NET grapheme clusters](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction#grapheme-clusters)
- [xterm bracketed-paste mode](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h2-Bracketed-Paste-Mode)
- [xterm paste-framing limitations](https://invisible-island.net/xterm/xterm-paste64.html)
- [Microsoft Terminal paste confirmation](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalApp/TerminalPage.cpp)
- [Microsoft conhost paste payload handling](https://github.com/microsoft/terminal/blob/main/src/interactivity/win32/Clipboard.cpp)
- [Microsoft Unicode control classification](https://learn.microsoft.com/en-us/dotnet/api/system.char.iscontrol?view=net-10.0)
- [Microsoft legacy code pages](https://learn.microsoft.com/en-us/globalization/encoding/code-pages)
- [ISO-2022-JP character-set switching](https://www.rfc-editor.org/rfc/rfc1468.html)
- [.NET encoding fallback defaults](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoding.getencoding?view=net-10.0)
- [.NET exception fallback](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoderexceptionfallback?view=net-10.0)
- [.NET stateful encoding and finalization](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoder.convert?view=net-10.0)
- [.NET encoder reset](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoder.reset?view=net-10.0)
- [Microsoft ISO-2022 encoding implementation](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/ISO2022Encoding.cs.html)
- [Windows socket send completion](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-send)
- [Windows I/O cancellation races](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
- [.NET text elements and Unicode characters](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction)
- [.NET grapheme enumeration](https://learn.microsoft.com/en-us/dotnet/core/compatibility/globalization/5.0/uax29-compliant-grapheme-enumeration)
- [Unicode CRLF grapheme boundary rule](https://www.unicode.org/reports/tr29/#GB3)
- [.NET timer delay behavior](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay)
- [Windows wait scheduling](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-sleep)
- [.NET socket buffering semantics](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.send?view=net-10.0)

These sources describe terminal/clipboard behavior, not selected dependencies.
