# Specify session logging and file lifecycle

> The 2026-11-10 baseline was consolidated in the [session and recording contract](../specification/session-and-recording.md). See [implementation status](../specification/implementation-status.md) for validation evidence.

Working issue-body draft. Raw preserves received terminal bytes exactly; Text
records readable received output. Both exclude locally generated echo. The
accepted Terminal mode safely merges received output and local echo
as ordered UTF-8 text and VT controls; its remaining details are under design.
Confirmed requirements and remaining proposals are separated.
No implementation or tests are authorized.

The 2026-11-10 source review in `technical-contract-review.md` distinguishes
received LF/CRLF from other controls that move the cursor vertically. Display
movement alone is not a received newline or permission to add a Text-log LF.
Reference erase behavior must not silently override Q149's destructive-edit
policy.

## Confirmed resize policy (Q106 accepted)

See `resize-and-vault-proposals.md` for normal-screen reflow, traditional
alternate-screen resizing, retained-row accounting, and recording provenance.
The policy preserves the 5,000-physical-row meaning. Reflow alone creates no
log event; actual quota eviction can flush eligible unrecorded Text portions
without a newline. It does not change Raw/Terminal into resize-event recordings.
The user accepted this policy; detailed event ordering and writer bounds remain
part of the open implementation specification, not an implementation request.

## Confirmed recording scope and received Raw payload

- SFTP uses a separate channel on the authenticated SSH connection. Its file
  data, replies, and stderr stay outside Raw, Text, and Terminal recordings.
  See the [SFTP channel contract](../specification/file-transfer.md#ssh-and-sftp-channel).
- Q142-Q143 in `ssh-session-proposals.md` confirm that SSH authentication banners,
  protocol notices, disconnect reasons, and exit metadata stay outside session
  logs. Welcome/error text delivered as terminal payload remains eligible.
  Exit status alone never stops recording. On receive-direction EOF, process
  accepted payload and flush eligible incomplete Text once without a newline;
  later channel closure must not repeat that finalization. Retain part/session
  ownership until ordinary close or explicit pause. The two-second explicit
  disconnect grace bounds peer waiting, not a guarantee of complete disk drain.
  Q141 is also accepted: include normal channel DATA and extended stderr in
  eligible recordings. Raw concatenates original payloads in SSH message order
  without headers/source markers. For display/Text/Terminal, isolate character
  decoders and incomplete-control parsing per source, then apply complete
  operations to shared terminal state. Incomplete units do not consume another
  source's bytes or block its complete operations. Apply recording-interval
  eligibility and initial UTF-8-signature rules independently to each source.
  Raw preserves all eligible bytes but cannot identify the two sources later;
  do not promise that parsing Raw as one stream reproduces the actual screen.
- Restore Q90's original receive-only recommendation for the existing Raw and
  Text modes. Remote echo is received data; locally generated echo is excluded.
  The user's later instruction supersedes the intermediate decision to include
  local echo in Raw/Text. Q94 accepts a separate Terminal mode instead.
- Raw must preserve the original terminal-facing received bytes exactly and in
  order before character decoding, including control bytes, invalid sequences,
  and incomplete characters. Do not transcode, normalize line endings, replace
  invalid bytes, or inject local echo, metadata, or generated separators.
  For SSH this means verified, decrypted terminal-channel payload, not encrypted
  network packets or SSH authentication/protocol exchanges.
  Exactness applies to data at this terminal-payload boundary during active,
  successful recording, not transport packets or intervals excluded by pause/
  error. Keep the agreed failure reporting; do not silently drop bytes and
  describe a failed recording as complete.
- Do not inject local diagnostics into session logs. Highlight styling
  does not change recorded text. A hidden or moved tab continues to receive and
  record independently of UI visibility.
- Session logs can contain secrets supplied by the peer; Terminal mode
  can also contain secrets displayed through local echo.
  Diagnostic-log secret exclusions are not a promise of complete redaction; see
  `authentication-issue.md`.

### Confirmed sources for Terminal mode

- Provide a separate mode that safely combines received output and enabled
  local echo without promising original-byte fidelity. Revised Q94 is accepted:
  record ordered UTF-8 text and equivalent supported VT controls, without
  waiting for line finalization. Keep Q92's local Enter CRLF and Q93's rejection
  of heuristic echo deduplication; replace the earlier combined-mode snapshot
  rules with operation recording. Existing Raw and Text remain receive-only.
  Q95 is accepted with the user's Terminal name and provisional shared
  `.tttylog` extension; filename markers are `.raw` and `.term`. Q96-Q98 are
  accepted below. The earlier readable Display proposal is withdrawn.
- Preserve the profile's None/start/pause/resume/retry rules for both received
  output and local echo. Enabling local echo does not enable recording for a
  session whose profile selected None, and does not backfill earlier input.
- Eligible local echo comes from submitted ordinary typing, committed IME text,
  and the submitted portion of a paste. Uncommitted composition, validation-
  rejected input, unsent paste remainder, protocol/framing bytes, application-
  managed authentication values, and local diagnostics remain excluded.
- The new mode includes local-echo content under the Q92/Q93 rules below.
  Existing Raw and Text do not gain local-echo content or local finalization
  events. Do not treat echo as received wire bytes or inject it into the
  receiver's decoding/parsing state.
- Recording pause/error intervals also exclude local echo generated during
  those intervals. Do not dump old echoed screen contents on resume, reconnect,
  or a local-echo setting change. Text's mixed-line exclusion is not a rule for
  this operation stream; Q98 confirms its resume/partial-unit eligibility.
- Known application-managed secrets do not enter local echo or its recording.
  Ordinary manually typed secrets can be recorded when echoed locally; explain
  this at the local-echo setting. Do not claim automatic identification or
  redaction of arbitrary secret input.

## Confirmed normal-screen Text meaning and line finalization

- Record text as close as practicable to the terminal's displayed line contents
  after applying received terminal editing operations. Since Q90 returns Text
  to receive-only scope, this means the line state derived from received events;
  with local echo enabled, it can differ from the actual combined display.
  Reuse terminal semantics, not a filter that merely removes control characters
  or a screenshot/UI-text scrape. The proposed state separation is described
  below; do not accidentally copy locally echoed cells into Text.
- Apply Backspace, CR, cursor movement, overwrite, insertion, deletion, and erasure
  according to terminal semantics before recording the affected line. This
  corrects the earlier proposal to discard Backspace while retaining stale text.
- A received newline finalizes the current displayed contents, avoiding records
  of each intermediate edit before command entry is completed. Pressing a local
  Enter key does not itself finalize receive-only Text. Q92's local boundary
  applies to the new combined mode; it is not evidence of a received newline.
- A standalone CR moves the cursor within the current line and does not finalize
  it. Repeated CR-based progress updates retain their last displayed result when
  the line is later finalized or an eligible incomplete tail is flushed. This
  explicitly supersedes the earlier requirement to preserve every standalone CR.
- Preserve genuine received CRLF versus standalone LF at hard line boundaries.
  A CRLF split across input chunks still produces one boundary, never a doubled
  blank line. Retain parser/decoder state across chunks; do not serialize editing
  CR operations as extra log line endings.
- Do not record a local TAB keystroke in receive-only Text unless its result
  comes back as received data. Local-echo TAB handling belongs to the new mode.
  Preserve a received TAB only while its final span remains faithfully
  representable using ordinary eight-column tab stops (Q62 accepted). Otherwise
  emit final displayed text/spaces for the span, including when later edits or
  custom tab stops invalidate that representation. Do not infer TABs merely
  from an observed run of spaces. A stale TAB must not hide visible text.
- Omit unused/erased blank cells after the meaningful content at the end of a
  line (Q66 accepted). Preserve leading/interior spacing needed for its layout
  and explicit spaces/TABs from eligible received output still represented in
  the final text, including at the line end. Do not pad every
  line to the terminal width or blindly trim all trailing whitespace.
  Editing/erasure must not restore removed
  characters or turn unused cells into received spaces.
- Store Text as UTF-8 without a BOM regardless of the connection receive encoding.
  The same file encoding applies to alternate-screen Text.

### Display wrapping versus received line endings

- Do not insert log line endings for TabTty's automatic wrapping at the right
  margin. Track whether each boundary originated from received newline input or
  local wrapping while processing terminal state; do not guess from the final
  screen appearance, row length, or cursor position.
- Preserve an actual received CRLF/LF even when it arrives exactly at the right
  margin. Delayed-wrap state and an explicit newline are distinct events; an
  edge coincidence must neither lose the received boundary nor add a second one.
- Resizing or reflowing retained output must not turn display wraps into logged
  newlines, duplicate already-recorded text, or drop an explicit received
  boundary. Apply the accepted reflow/retirement policy in
  `resize-and-vault-proposals.md`.
- If the remote application itself emits CRLF/LF to format output for the
  reported width, that is still received newline input and is retained. Its
  purpose cannot be inferred reliably from identical received bytes. This
  requirement removes local display-wrap boundaries, not every line ending
  that may have been motivated by display width.
- Alternate-screen serialization likewise never adds local display wraps to
  the receive stream; its separate standalone-CR-to-LF rule still applies.

### Rows evicted from the retained terminal buffer before a newline

- The retained terminal buffer includes the active normal screen and its
  scrollback, not just the rows currently visible in the terminal viewport.
- Before a hard newline, subsequent output may fill this buffer. Record an
  eligible, unrecorded row's displayed contents immediately before it is evicted
  to enforce the retention limit. Moving from the active screen into retained
  scrollback does not itself finalize the row.
- Default the retained normal-screen buffer to 5,000 rows total across the active
  screen and scrollback (Q60 revised). The application setting supplies the
  default; each profile chooses inheritance or an explicit override (Q63
  accepted). Resolve the effective limit when connecting. Later application or
  profile edits apply on the next connection, without discarding output from an
  existing session. The earlier 10,000-row proposal is superseded.
- Q146 accepts integer limits from 100 through 100,000 rows, with no unlimited
  option. Validate editor/save values and fail clearly on unsupported runtime
  values without silently clamping. Preserve Q106's active-height limit. This
  row limit alone does not bound cell, character, or aggregate session memory.
- The rejected 64 KiB proposal is not a Text finalization trigger. Finalization
  follows terminal line/row events, not an arbitrary character/byte threshold.
- Manual mouse scrolling changes the viewed subset only; it does not finalize,
  duplicate, or otherwise change recorded content. Minimizing, tab hiding, or
  viewing another window likewise does not change recording.
- Preserve soft-wrap relationships. Ordinary wrapping is not a received hard
  newline; do not invent a hard newline solely for a wrapped continuation.
  Track already-recorded portions so finalizing the remaining tail does not
  duplicate a prefix recorded on buffer eviction. Evicting a row version already
  recorded on a newline also does not record it again.
- Resize/reflow-induced retirement follows accepted Q106. Non-linear cursor
  movement between partially finalized lines, partial scrolling regions, and
  remaining validation bounds still need explicit rules before implementation.

### Later edits to recorded rows (Q57 accepted)

- Append finalized snapshots; never rewrite previously stored log contents.
- If a previously finalized row is edited again, append its updated version only
  when that version is finalized again. An unchanged redraw alone does not
  duplicate a previously finalized row.
- This is tracking of an individual row's recorded versions, not global text
  deduplication. Distinct new output rows with identical contents still count
  as distinct received output.

## Confirmed alternate-screen Text scope (Q58 revised)

- Record alternate-screen received text in Text logs as well. The earlier
  normal-screen-only proposal is rejected.
- Remove or replace terminal control characters/sequences while retaining the
  received textual content. Reduced readability, repeated redraw text, and loss
  of the original screen layout are acceptable; do not omit the alternate-screen
  interval merely because it cannot be represented as normal finalized lines.
- Keep the normal-screen edited-display policy above. The alternate-screen
  fallback does not turn normal-screen Text back into a stripped input stream.
- Raw recording is unchanged. Alternate-screen highlighting remains disabled
  under the separate highlighting specification.
- Apply the confirmed screen-switch and recording-interval rules below.

### Alternate-screen serialization (Q61 accepted)

- Append decoded printable text in receive order without waiting for displayed
  line finalization. Repeated redraw text is retained.
- Remove complete terminal control sequences including their parameters/payloads.
  Retain TAB and genuine CRLF/LF; replace standalone CR with LF, and discard
  BS/other nonprinting controls. This deliberately does not reconstruct
  alternate-screen cursor editing.
- Preserve parser state across receive chunks so split controls or CRLF do not
  leak fragments or create duplicate line endings. Resolving a trailing CR or
  incomplete sequence may require subsequent input or an agreed flush boundary;
  sequential recording does not mean treating each receive chunk independently.
- Standalone-CR replacement is specific to this fallback. On the normal screen,
  CR continues to edit the displayed line without finalizing it.

### Screen switches (Q65 accepted)

- When entering the alternate screen, flush eligible pending normal-screen text
  once, without adding a newline, before recording alternate-screen output.
- On returning, finish eligible pending alternate-screen serialization and
  continue normal-screen handling without dumping the restored screen.
- Track earlier recordings and interval eligibility. Screen switches cannot
  backfill paused/error-period contents or duplicate unchanged restored text.
  A screen switch alone does not restore eligibility to a normal-screen logical
  line excluded by resume/retry.
- Do not add synthetic screen-switch labels or blank lines to either log.
  Switching screens does not itself start another file or recording part.

## Terminal semantics informing the design

BS moves the cursor; it does not by itself erase a cell. Thus `abc\bX\r\n`
displays `abX`, while a bare BS without later overwrite/erase does not remove the
existing character. Similarly, `abc\rX\r\n` leaves `Xbc` visible. TabTty must
record the actual resulting display, not assume BS means deleting a character
from a string or CR means clearing the row.

TAB moves to a tab stop and does not uniquely mark cells as a TAB. Preserving a
TAB therefore requires tracking its origin/range in addition to final cell text.
The accepted Q62 rule retains a TAB only while its representation remains
faithful to the final layout with ordinary eight-column stops; otherwise expand
the affected range to displayed text/spacing. The accepted Q66 rule separately
preserves explicit received whitespace while omitting unused trailing cells.

SSH PTY requests and window-change messages convey terminal dimensions separately
from session data. RFC 4254 section 6.2 explicitly treats dimensions as
informational; reporting a width does not instruct SSH to insert a newline every
that many columns. The remote application can use the dimensions to choose its
output. Consequently, TabTty can distinguish its own wrapping from received
newlines, but cannot infer a remote application's intent for a received newline.
The intent limitation is a design inference from the data available to TabTty.

Microsoft's terminal documentation describes delayed automatic wrapping, and
Microsoft Terminal's implementation tracks forced wrapping separately from
explicit line movement. These are references for retaining boundary provenance,
not a requirement to adopt the Windows Console API or its renderer. A received
LF boundary is also separate from the column at which subsequent text appears;
terminal line-feed modes still determine the latter.

## Confirmed profile and file organization

Q158 in `profile-file-preparation-proposals.md` confirms a fixed 30-second budget
for preparing the log directory and opening all selected initial files together,
separate from the profile-load and transport budgets. Show Cancel, prevent
connection startup on expiry/cancellation, and reject late completion results.
Existing active-write and close-drain policies remain unchanged. This deadline
does not guarantee immediate OS cancellation or deletion of already-created files.

- Select Raw, Text, and Terminal independently in the connection profile (Q96).
  Any combination is permitted, including all three. New profiles have all
  unchecked, meaning recording disabled. This replaces the fixed None/Raw/Text/
  Both selector; the former Both selection corresponds to Raw plus Text only.
- Enabled recording starts with the session. None cannot start recording midway.
  Profile logging edits apply on the next connection. Enabled recording permits
  pause/resume and exposes its current status.
- Choose the destination folder in the profile. Support absolute paths and
  relative paths resolved against the directory containing the `.ttty` file,
  never the application's current working directory.
- Default that folder to relative `Logs` (Q67 accepted). After profile validation,
  create the chosen folder when starting configured recording if it does not
  exist. If creation/opening fails, report the failure before transport startup.
- Initially generate names from the sanitized profile name, connection-start
  local timestamp including its UTC offset, unique session suffix, and part
  number starting at `p001`. Text uses `<stem>.log`; Raw uses
  `<stem>.raw.tttylog`; Terminal uses `<stem>.term.tttylog`. The final extension
  is provisionally `.tttylog` for both Raw and Terminal, while `.raw`/`.term`
  are filename markers, not different final extensions. Include the markers
  even when only one format is selected. Create without overwriting recordings.
  Concurrent sessions and manual reconnect use new files.
- Text remains UTF-8 without a BOM. Raw is a headerless `.tttylog` stream of
  the original received terminal bytes, without a TabTty-added BOM or records.
  An actual BOM supplied by the peer remains part of those bytes. Accepted Q126
  recognizes only an initial UTF-8 receive signature for interpreted text and
  omits it from display/Text/Terminal, with no effect on Raw. New read chunks,
  recording parts, and screen resets never create another stream-start check;
  midstream U+FEFF remains content. See `search-and-encoding-proposals.md`.
  Q91's proposed
  source-tagged container is superseded by the separate-mode direction below.
- This is the initial naming policy, not a permanent restriction to profile-based
  names. Preserve the extension path described below for later user-specified
  names and macro/invocation-derived names.
- Do not automatically split files by size. The proposed 100 MiB split threshold
  is rejected. This does not remove the agreed separate files for a new
  connection, pause/resume, or explicit retry after a write failure.
- Retain recordings until the user removes them (Q68 accepted with revision).
  Do not automatically delete recordings by age, count, or size.
- Do not provide an action to open the recording folder in Explorer; the user
  explicitly rejected that feature. A dedicated in-app log viewer/search UI is
  also outside the initial scope. Searching retained terminal output is a
  separate feature decision.

### Naming extensibility

- Keep filename composition localized within the logging module, separate from
  receive-data processing and file creation/write/pause/retry lifecycle. Pass
  the naming context needed by the current policy explicitly; do not assemble
  profile-derived filenames independently in transports, terminal rendering,
  activation handlers, or a future macro caller.
- Allow the name-selection/composition logic to evolve without changing the
  recording lifecycle. Future choices may include a user-specified name or
  template, the macro name, or an explicitly supplied startup/invocation naming
  argument. Those input mechanisms and their precedence are deferred decisions,
  not initial UI, command-line, or macro-runtime requirements.
- Keep destination resolution, filename validation, no-overwrite creation,
  collision handling, grouping of selected formats, and resume/retry part
  management in the logging module regardless of how a name is selected. A future naming input
  must not independently open files or bypass profile recording eligibility.
- Pass only explicit naming data; do not derive names by serializing all macro
  or process arguments or credential values. This preserves the existing secret
  handling rules without defining a future macro argument format.
- A localized name-composition function is sufficient for the initial single
  policy. Do not build a plugin framework, template engine, speculative adapters,
  or macro runtime solely for possible later naming options. See
  `future-macro-design.md` for the shared deferred-feature constraints.

## Confirmed pause, tail flushing, and retry

- Pausing stops recording, not reception, terminal-state updates, or display.
  Close the current files; resuming creates new numbered parts for that session.
- Do not retrospectively record data received while paused. Under the new Text
  definition, a row can contain both paused and active-period content. On
  normal-screen Text resume/retry, skip that in-progress logical line and begin
  with the next new logical line after a received hard newline (Q59 accepted).
  A local-echo newline does not end this exclusion for receive-only Text.
  Terminal mode does not wait for line snapshots; see Q94/Q98.
  This deliberately also omits new characters received on the excluded line
  before that boundary. Do not save the existing screen on resume.
- On alternate-screen Text resume/retry, record newly received text immediately
  (Q64 accepted), without replaying the current screen or output received during
  the gap. This is a scoped exception to the normal-screen logical-line rule:
  alternate-screen Text is a sanitized receive stream, not a displayed snapshot
  containing earlier text. Raw also resumes immediately.
  Neither Raw nor Text gains local echo on resume. Raw writes each new received
  byte without waiting for a complete decoded character or control sequence.
- For Text, retain decoder/parser state across recording intervals,
  but keep pre-resume partial text/control fragments ineligible. Completing an
  earlier unit after resume is not permission to record paused/error content.
  Raw instead applies eligibility to received bytes, independently of whether
  a decoded unit starts or finishes in another interval. The corresponding
  Terminal-mode interval policy is confirmed in Q98.
- For normal-screen Text, on pause, disconnect, or orderly exit, write eligible
  pending incomplete displayed text without adding a newline. Account for
  already-recorded prefixes;
  do not flush a history of intermediate edits, duplicate evicted rows, or flush
  a line excluded by the resume/retry rule.
- Preserve decoder/parser state while paused. Do not insert diagnostic pause
  markers into raw/text content.
- If configured files cannot be opened initially, report the failure and stop
  the connection attempt before transport startup. Do not silently connect
  without its configured recording.
- A write failure during a session stops recording, reports a persistent error,
  and keeps the connection/terminal usable. Stop all selected formats together
  under Q96; retry starts new parts for that selected set.
- Bound pending writes; do not accumulate unlimited log data while disk output
  is unavailable. This is a write-queue requirement, not permission to finalize
  still-editable Text contents at an arbitrary byte threshold.
- Explicit retry after correction starts new files under the active configuration
  without reconstructing missed output. None remains ineligible for mid-session
  logging start. Text retry follows the same rules as resume for the current
  screen: mixed-line exclusion on the normal screen and immediate recording of
  new received text on the alternate screen.
- Screen switching does not reset recording eligibility or reconstruct output
  from paused/error periods. Detailed per-unit provenance and event ordering
  remain design work, subject to these confirmed behavior requirements.

## Confirmed local behavior carried into Q94 (Q92/Q93)

- A locally echoed Enter creates a local CRLF display operation even if the
  profile sends only CR or LF. Record that CRLF in Terminal mode;
  it does not change outgoing bytes or create received data for Raw/Text.
  With local echo off, the key alone produces no local recording operation.
  Pasted CR/LF retain their meaning; they are not remapped as keyboard Enter.
- Keep receiver decoding/control parsing isolated from local echo. Record local
  echo as independent text/display operations in the combined display order.
- Literal basic display controls can remain controls in Terminal recording:
  a local BS operation records BS rather than only its resulting text. Revised
  Q99 in `terminal-interaction-issue.md` confirms ordinary submitted text/IME/
  paste echo, physical Backspace mapped locally to BS, Enter to local CRLF,
  and TAB to the shared TAB operation. Other special/control-key input does
  not generate local display operations; its resulting peer output still does.
  The earlier guarded local-erase proposal was not accepted and is withdrawn.
  Shared output semantics does not authorize all transmitted key encodings or
  non-display controls as new log sources beyond Q94/Q97.
- Treat eligible local echo as ordinary displayed text/control operations in
  Terminal recording, without added source-label text. Keep source identity
  internally: local events are not actual received data and must not enter
  Raw/Text or receive-only observers. Full imitation of peer echo/editing is
  not a goal. Prefer shared terminal semantics and the agreed minimal mapping;
  do not add source-owned deletion, peer-editing guesses, or history emulation.
- Do not suppress local/remote echoes by string matching or timing. If the
  combined operations produce `aa`, both contributing operations remain.
  If later operations overwrite or erase text, preserve those operations too.
  Do not apply Text's unchanged-row snapshot suppression to an operation stream.
- Revised Q94 supersedes the earlier combined-mode line finalization, TAB/space
  reconstruction, alternate-screen sanitization, and mixed-line resume rules.
  Those rules continue to apply to receive-only Text as specified above.
  Terminal recording retains the actual text and operations as they
  occur, with no newline added for automatic wrapping. Local behavior is
  confirmed in Q99; detailed cross-source event ordering remains open.

## Superseded Raw container proposal (Q91)

The source-tagged `.tttylog` container was proposed but never accepted. The
user instead requires exact receive-only Raw and asks for a separate combined
mode without original-byte fidelity. Withdraw the container proposal; Raw
does not need source tags because it contains only received bytes. This does
not authorize a replay format, viewer, or implementation.

## Confirmed Terminal recording (Q94 accepted)

Record the common ordered text/display operations before their effects are
reduced to final screen cells. Use UTF-8 text and equivalent supported VT controls,
without requiring original-byte fidelity or waiting for edited-line snapshots.
This replaces the earlier readable Display proposal. The selected name is
Terminal; Q95 records the user's name, provisional extension, and filename marker.

- Keep Raw's original-byte branch before decoding. The receive path owns its
  incremental character/charset decoder and terminal-control parser. Decoding
  to Unicode alone is insufficient: an unfinished CSI/OSC sequence must not
  absorb locally echoed characters either.
- Local echo supplies independently complete text/display operations. Merge
  those with parsed receive operations in the same serialized order used to
  update terminal state. One consumer updates the screen; another writes a
  UTF-8, BOM-free stream with equivalent supported VT controls. Logging failures
  must not prevent terminal state from being updated under the agreed lifecycle.
- Retain control functions such as CR, LF, TAB, Backspace, cursor movement,
  erasure, SGR color/style changes, and supported screen/mode changes. Serialize
  local display operations as text or equivalent controls as well. Do not add
  application highlighting, selection/search styling, UI diagnostics, protocol
  traffic, or a record of transmissions that were not locally echoed.
- Preserve semantic operation order, not original byte positions or control
  spellings. Use a defined canonical serialization for supported operations.
  Equivalent source spellings may become the same control sequence. Receive
  encoding shifts/charset selectors must be resolved to Unicode or equivalent
  normalized operations, not blindly copied into a UTF-8 stream. Invalid bytes
  follow the display replacement policy; Raw alone preserves their exact values.
  Parser-relative operations also need normalization where merging changes the
  context: for example, an RX repeat-character action already resolved to `AAA`
  must log that text, not a repeat escape that could instead repeat a locally
  echoed `X`. A canonical control is usable only when it represents the operation
  actually applied by the combined display path.
- Record operations as they become eligible, without waiting for a newline,
  row finalization, or buffer eviction; disk-write batching is independent.
  Backspace/overwrite history remains in the stream. For example, received
  `abc\bX` retains text plus a BS operation, whereas Text resolves it to `abX`
  at a later eligible line finalization. Do not replace the operations with only
  the final string. Alternate-screen operations use the same representation,
  without Text's control-removal/replacement fallback.
- Keep Q92's locally echoed Enter represented as CRLF regardless of its outgoing
  bytes, and Q93's rejection of heuristic echo deduplication. Preserve received
  CR/LF operations, including intermediate progress updates. Local soft wrapping
  does not itself add a newline. Record operations that actually occur even if
  the resulting screen cells happen not to change; row-snapshot deduplication
  is a Text rule, not an operation-stream rule.
- Do not emit an unfinished receive control prefix and then insert local text.
  For example, receive `ESC[31`, process local `X`, then receive `m`: the visible
  operation order is local `X`, then red-foreground selection. The log may be
  `X ESC[31m` (without the explanatory space), not `ESC[31Xm`.
  This follows the terminal's executed-operation order, not network arrival of
  individual bytes. Immediately executed controls inside a still-pending parser
  sequence need their own ordered operations; serializing the completed command
  later must not duplicate those controls.
- Do not claim every original escape sequence survives. Unsupported, malformed,
  incomplete, or non-display controls require explicit policy; they remain
  available as original bytes only in Raw. Define external side effects and
  query/reply handling separately from display-operation serialization.
  Merely retaining VT controls does not promise independent full-session replay:
  initial modes, dimensions, resize events, and prior paused-state dependencies
  can affect interpretation. A player or replay container is not selected.
- Keep receive-only Text separate from the combined state; do not remove local
  characters from an already merged string and call that received-only output.
  Its state must exclude local cursor changes and their consequences as well.
- Name/extension, profile combinations, control exclusions, and interval rules
  are confirmed in Q95-Q98. Exact operation-to-VT mappings, supported terminal
  features, and resource bounds still need specification. The accepted direction
  is not implementation or prototype authorization.

## Confirmed naming, selection, scope, and intervals (Q95-Q98)

### Q95: UI name and file extension (accepted with revision)

- Use the mode name Terminal. Localized explanatory text describes received
  output plus enabled local echo recorded as UTF-8 text and VT controls.
  The earlier VT/制御付きログ label and `.ttabvt` proposal are superseded.
- Provisionally use `.tttylog` for Terminal as explicitly requested by the user.
  Raw keeps `.tttylog` and Text keeps `.log`. This does not merge Raw and Terminal
  contents or change either representation. The selected mode determines file
  contents; the shared extension alone cannot distinguish the formats.
- Preserve the common session/part stem while adding `.raw` or `.term` before
  the shared final extension. The user's follow-up shortens `.terminal` to
  `.term` as the filename marker; the mode name remains Terminal and the final
  extension remains `.tttylog`. Thus `<stem>.raw.tttylog` and
  `<stem>.term.tttylog` can coexist without overwriting or ambiguous suffix-free
  names. Text remains `<stem>.log`. Use these markers for single-format sessions
  too. Keep this provisional naming policy localized for later adjustment.
- No log-file association, viewer, or replay feature is added by this choice.

### Q96: Format selection and shared lifecycle (accepted)

- Replace the fixed None/Raw/Text/Both selector with independently selectable
  Raw, Text, and Terminal. Allow any combination, including all three.
  All unchecked means disabled and remains the new-profile default.
  The earlier Both choice corresponds to Raw plus Text, not all three formats.
- Configure the set before connecting and keep it fixed for that session.
  Write a separate file per selected format. Share pause/resume/retry across
  the selected files; do not add individual per-format pause controls.
- Open every configured file before transport startup. If any fails, the
  connection attempt does not proceed. If a write fails during the session,
  stop the selected recordings together, retain the connection, and report
  the error. Explicit retry creates new parts for the selected set without
  reconstructing missing output, extending the existing Both failure rule.
  Multi-file recording is not a transaction: a failure can leave unequal tails.

### Q97: Terminal log scope (accepted)

- Include supported terminal display operations: text, CR/LF/TAB/BS, cursor
  movement, erasure, scrolling, colors/styles, alternate-screen switches, and
  display modes. Use the same operation order as the terminal model.
- Exclude non-display effects such as bells, window/title changes, clipboard
  operations, and terminal queries/replies. Input-only mode changes such as
  mouse reporting and bracketed-paste enablement are likewise outside this
  display-operation log. This does not decide which such terminal functions
  the application itself supports; it only selects log contents.
- Do not pass through unknown, malformed, or incomplete control sequences as
  original bytes, nor replace them with fabricated log messages. If terminal
  processing yields visible text/replacement characters or a supported display
  operation, record that result when eligible. Exact received bytes remain Raw's
  responsibility. Concrete parser limits and end-of-input recovery remain open.

### Q98: Pause/resume and partial-unit handling (accepted)

- Close the current part on pause and create a new part on resume/retry, using
  the shared lifecycle. Resume eligible new complete operations without waiting
  for a hard newline, on either screen. Do not dump earlier screen contents or
  synthesize display-state setup sequences or pause/resume markers.
- Continue receiver decoding/parsing and screen updates while paused. A text
  or control unit already incomplete at resume remains ineligible when it later
  completes; do not reset the live parser to establish a logging boundary.
  New complete local operations after resume remain eligible independently of
  an earlier incomplete receive sequence. Do not backfill echoed input from the
  gap. Unit ordering/provenance details still need a concrete design.
- Screen switches are ordinary supported operations and do not themselves
  split files or dump restored content. No row-eviction/final-line event copies
  old text into the operation stream. Resumed parts can depend on display state
  established before that part; standalone full-screen reconstruction is not
  guaranteed. Raw and Text retain their own agreed eligibility rules.

## Live display boundary, independent of log format

The Q92 separation applies to screen rendering as well as logging. Keep
incomplete received characters, encoding state, and terminal-control parsing
owned by the receive path. Do not insert local input bytes or local characters
into that unfinished stream. Local echo supplies complete text/display
operations independently; combine operations from both paths in a serialized
session terminal-state update path. Terminal mode records that operation stream
before the final screen cells discard editing history.
Receive-only Text must exclude local events and their state changes; Raw
branches before decoding.

For example, pending UTF-8 receive bytes `E3 81`, a local `X`, and later receive
byte `82` must not become the decoder input `E3 81 58 82`. The receive decoder
still sees `E3 81 82` and produces `あ`; the echo path supplies `X` independently.
If the complete local operation is processed before the received character
completes, an illustrative result is `Xあ`, not a broken multibyte character.
This example does not settle every cross-source scheduling policy. Received
cursor movement, overwriting, or genuine duplicate echo can still change the
visible arrangement; source separation prevents decoding/parser contamination,
not all possible overlapping terminal output.

## Representation facts and guidance review (2026-11-08)

Microsoft documents that a Decoder retains unfinished bytes between calls.
For example, UTF-8 receive chunks `E3 81` and `82` form one character, but
inserting an echoed `41` between them corrupts that sequence. Independent
stateful streams also carry separate character-set states. The conclusion that
unmarked byte concatenation cannot preserve these inputs is a design inference,
not a standard requiring a particular TabTty file format. The selected
direction preserves received bytes in Raw and uses a separate combined
mode; a structured Raw container is not needed for correct screen decoding.

Microsoft's VT documentation permits control sequences to span arbitrary input
chunks. Its terminal parser keeps incomplete CSI state while executing embedded
C0 controls, and dispatches OSC at its terminator. Its output engine exposes
text/control actions and resolves repeat-character input using parser-owned
preceding-character state. These facts support the revised Q94 inference:
serialize the operations actually executed, not decoded input concatenated at
arbitrary boundaries or every original escape spelling. ISO-2022-JP's encoding
selectors are another reason original control bytes cannot be copied blindly
after converting text to UTF-8. The accepted serialization direction is TabTty's
design, not a Microsoft-defined logging format.

The local-echo record represents display input, not all outgoing protocol bytes.
The existing application-managed-secret exclusions are retained, consistent
with OWASP's recommendation to exclude authentication secrets from logs. A
terminal transcript can still contain ordinary manually entered or peer-supplied
secret text; no general-purpose secret detector is claimed.

## Confirmed recording capacity and finalization (Q146-Q148)

`retention-and-log-io-proposals.md` records the accepted 100-100,000 retained-row
range, preserving the 5,000 default. The combined per-session pending-log payload
budget is 8 MiB, including queued, staged, and still-owned in-flight bytes across
all selected formats. Failed admission or ten seconds without completed pending
I/O progress stops recording with a persistent incomplete status; reception and
the connection continue. Never drop old queued records and claim completeness.

Use bounded background batches with an approximately one-second buffering target
under normal conditions, not a durability/loss guarantee. At eligible pause,
receive EOF, disconnect, and orderly-close boundaries, finalize Text once, drain,
and request a durable flush. Q143's EOF keeps the part/session ownership intact.
An unresolved close after five seconds offers Continue waiting (default) or
Close with unsaved log data. Waiting overrides automatic abandonment by the
active-I/O watchdog; actual I/O failures still report incomplete recording.

Preserve uncertain/partially written files, do not replay failed batches into
the same part, and isolate old I/O generations and still-owned buffers from
retry parts. Cancellation is a request, not proof of completion. Detailed
aggregate cleanup/resource contracts remain open. No implementation is authorized.

## Confirmed destructive edits and scroll displacement (Q149/Q151)

Apply erase/delete/overwrite and insertion truncation to unfinished Text state
without first recording the removed text. Preserve surviving content and the
accepted meaningful-space rules. Already written logs remain append-only; a
screen/history clear cannot erase the log or add a marker/new part. A received
newline can still finalize an empty line; erasure alone does not create one.

When output scrolling actually removes eligible, changed/unrecorded content from
all retained Text state, save it immediately before removal without an added LF.
This includes scrolling-region edges and reverse scrolling. Merely moving into
retained scrollback or browsing the viewport does not trigger recording. Preserve
recorded-range identity and interval eligibility; failure to admit a snapshot
uses the recording-failure policy. Explicit deletion/insertion truncation remains
an edit under Q149, not this displacement flush. See `text-editing-proposals.md`.

## Confirmed received-newline finalization (Q150)

Q150 in `text-editing-proposals.md` is accepted after clarification. Positioning
the cursor does not itself finalize Text or add line endings. A received LF/CRLF
finalizes the eligible logical line being left, with surviving soft-wrap content
and existing recorded-prefix/revision rules. Remaining eligible tails flush in
retained order at accepted boundaries without synthetic separators. Preserve
append order rather than rewriting earlier log contents into screen order.

The user accepts imperfect layout/order during complex multiline editing,
including backslash-escaped shell newlines. Do not parse shell syntax, identify
Enter/command acceptance, or repair such edits heuristically. A received LF can
be a redraw operation; it is still a logging boundary. This does not remove
received cursor/edit semantics from the Text projection or change it into a
stripped receive stream. Preserve CR/BS/erase behavior and local-wrap suppression.

## Remaining decisions

- The 5,000-row default with application/profile inheritance, alternate-screen
  serialization/resume, screen switches, TAB/space representation, append-only
  revisions, normal-screen mixed-line exclusion, initial file organization,
  naming extensibility, and retention/UI scope are agreed.
- Exact eligibility/event ordering at screen switches and resume boundaries,
  and control sanitization limits.
- Exact receive-only Raw, receive-only Text, and Q94's Terminal mode
  are confirmed. Q92's local Enter CRLF and Q93's no-heuristic-deduplication rule
  apply to that mode; earlier combined-mode line snapshots are superseded.
  Q95-Q98 are accepted, with the Terminal name, provisional shared `.tttylog`
  extension, and `.raw`/`.term` filename markers. Specify detailed operation-to-VT
  mappings and state/interval provenance. Q91 is superseded, not pending.
  Q99 confirms shared local rendering without peer-emulation heuristics. Event
  ordering, malformed/oversized controls, and incomplete final input still need
  detailed rules.
- Terminal-event ordering, wrap continuation, non-linear cursor edits,
  erase/clear/line deletion, scrolling regions, and final partial-screen flushing.
  Local resize/reflow and quota-induced retirement are agreed in Q106.
- Aggregate resource limits for terminal cells/characters, pending item counts,
  sessions, and failed-I/O cleanup; exact scheduling, partial-write accounting,
  and drain/cancellation races under accepted Q146-Q148.
- Exact timestamp/identifier serialization, collision handling, and path
  validation within the agreed file organization. Future naming UI, template
  syntax, macro arguments, and precedence are deferred and do not block initial
  release readiness. No Explorer-opening action is planned.

## Agreed acceptance cases

Q94 cases use ordered operation recording. Edited-line and sanitized alternate-
screen cases below belong to Text. Q95-Q98 cases reflect the accepted revisions.

- Raw contains exactly the received terminal-payload bytes in recording order,
  including invalid/incomplete characters, received BOMs, and controls. No
  injected header, local echo, generated newline, or encoding conversion occurs.
- Local echo never contributes bytes/content or finalization events to Raw/Text.
  Remote echo remains received data. In the new combined mode, enabled local
  echo is eligible; with echo off and no peer echo, input alone adds no entry.
- None, pause/error intervals, and resume eligibility apply to local echo too.
  A setting change or reconnect never backfills unrecorded local input.
  Uncommitted IME text, unsent paste tails, and application-managed credentials
  do not enter local echo records in the new mode.
- With the new combined mode and local echo active, typing an eligible `abc`
  records the characters without waiting for Enter. Enter appends local CRLF
  even when its outgoing sequence is CR only. Without received data, Raw/Text
  gain no content. With echo off, the key alone adds no local operation.
  Received/pasted standalone CR remains a CR operation, not a synthetic CRLF.
  Local soft wrapping adds no newline.
- If eligible local and remote echo visibly produce `aa`, the new mode preserves
  both contributions without timing/text matching. Later overwrites/erasures
  remain ordered operations, even when the final cells match an earlier state.
- For received `abc\bX\r\n`, the Terminal stream includes `abc`, BS,
  `X`, and CRLF; Text records the finalized `abX\r\n`. Progress updates retain
  their intermediate text and CR operations in the new mode.
- Received `ESC[31`, local `X`, then received `m` records local `X` followed by
  a complete red-foreground operation, without inserting `X` into a partial CSI.
  An embedded C0 operation executed before CSI completion is recorded once in
  execution order, not replayed again inside the serialized completed command.
- Terminal mode uses supported VT operations on both screens;
  alternate-screen controls are not replaced using Text's sanitization rules.
- A received multibyte character split around a local-echo operation is decoded
  solely from the receive bytes. Local echo cannot complete/corrupt that
  character or an unfinished received escape sequence. This screen requirement
  applies independently of the chosen recording mode. Raw receives the original
  `E3 81 82` bytes without the echoed `X`; no re-encoding is used to reconstruct it.
- In Text, editing a command before its received newline yields the edited display, not
  a concatenated transcript of intermediate keystrokes and editing sequences.
- Text for `abc\bX\r\n` yields `abX` at finalization; `abc\rX\r\n` yields
  `Xbc`. A bare BS alone does not erase a character still displayed.
- Text for `10%\r20%\r100%\r\n` records the final visible `100%`, not three progress
  records. A pending final progress value is flushed on eligible disconnect.
- CR and LF arriving in different chunks never generate two log line endings.
- With a five-column normal screen, ASCII, and automatic wrapping enabled,
  `ABCDEF\r\n` produces `ABCDEF\r\n` in Text, with no extra newline between
  `E` and `F`. At the exact margin, `ABCDE\r\nF\r\n` retains those two CRLF
  boundaries once each; `ABCDE\n` retains its one LF.
- At that same width, `ABCDE\rX\r\n` records `XBCDE\r\n`: a pending wrap
  does not turn the received editing CR into a logged newline.
- Local resizing/reflow of the first example does not change its logged newline
  boundaries. If the peer instead sends `ABCDE\r\nF\r\n` to fit the width,
  keep those received boundaries; do not try to reconstruct its preformatted
  source text by deleting them.
- A local TAB keystroke with local echo off and no incoming data is absent from
  session logs. With local echo on, its displayed effect is eligible only for
  the new combined mode, not receive-only Raw/Text.
  Raw retains received TAB bytes regardless of their visible effect. A received
  TAB survives in normal-screen Text when the final span remains faithfully
  representable with ordinary eight-column stops; edited spans or incompatible
  custom stops instead produce final displayed text/spaces.
- `abc\r\n` on an 80-column screen records `abc\r\n` without right-margin
  padding. `abc  \r\n` retains the two explicit trailing spaces if they have not
  been erased/overwritten. Leading/interior layout spacing is preserved.
- The default retained normal-screen buffer has 5,000 rows across the active
  screen and scrollback. A profile inherits the application setting or supplies
  its own override. Changing either setting leaves an existing session's buffer
  limit/output intact; the next connection resolves the updated setting.
- A not-yet-finalized wrapped row moving into scrollback is not recorded merely
  because it left the active screen. If it is later evicted at the retained
  buffer's capacity, record its eligible, unrecorded contents before disposal.
  Do not use arbitrary 64 KiB flushing, invent wrap newlines, or duplicate an
  already-recorded prefix when the remaining logical line later reaches LF.
- Browsing scrollback, hiding a tab, or moving it to another window does not
  duplicate or restart recording.
- Editing a previously recorded row leaves prior file contents intact. Its next
  finalization appends the changed version; unchanged redraws do not duplicate it.
  Separate new rows containing the same text are still recorded separately.
- Alternate-screen textual output is recorded with terminal controls removed or
  replaced; it is not silently excluded. Normal-screen progress/editing rules
  remain unchanged after returning to normal-screen recording.
- Alternate-screen `a\rb\r\n` produces `a\nb\r\n`, retaining the actual CRLF
  once. A split control sequence does not leak its parameters into Text. There
  is no need to wait for a received LF before recording ordinary alternate-screen
  text, subject to pending decoder/parser units and recording interval eligibility.
- On normal-screen Text resume/retry, omit the in-progress mixed logical line,
  including new characters up to its terminating newline. Record subsequent new
  logical lines; Raw resumes immediately. An excluded line is not flushed on a
  subsequent pause/disconnect before the boundary is reached.
- On alternate-screen Text resume/retry, record newly received printable text
  without waiting for LF. Do not copy the current screen, replay paused text, or
  emit pre-resume fragments merely because their decoding/parsing later completes.
- Entering the alternate screen flushes an eligible normal-screen tail once,
  without an added newline. Returning does not dump unchanged restored content.
  A screen switch while paused produces no recording; a screen switch after
  resume does not make excluded normal-screen text eligible again.
- A recording can exceed 100 MiB without size-triggered splitting. Resume/retry
  still uses new parts, and reconnect still uses new session files.
- Relative `Logs` resolves from the profile's directory, independently of where
  TabTty was launched, and is the initial default. A missing folder is created
  before connecting; failure prevents transport startup. Existing recordings
  are not overwritten.
- Selecting all three formats creates `<stem>.log`, `<stem>.raw.tttylog`, and
  `<stem>.term.tttylog` under the initial naming rule, with separate contents.
  The final extension does not identify Raw versus Terminal. Single-format
  sessions use the same format-specific filename rule. Resume/retry increments
  the common part number without profile-name parsing in the write lifecycle.
- No age/count/size condition automatically deletes recordings. There is no
  open-log-folder action or dedicated log-viewer UI in the initial release.
- Design review confirms that future name selection can change without editing
  receive processing or file-write/pause/retry behavior. No future naming input,
  template engine, or macro execution is required for initial acceptance.
- All unchecked produces no log files and cannot start recording mid-session.
  Each selected combination shares pause/resume/retry; any write failure stops
  its recordings together while the connection continues. Initial open failure
  prevents transport startup. Retry creates new parts without backfill.
- Terminal excludes non-display controls, queries/replies, and unsupported raw
  control fragments under Q97. Supported visible text/replacement output remains
  eligible; Raw preserves received bytes without adopting these exclusions.
- Terminal resumes new eligible operations without a newline wait on either
  screen. A receive unit incomplete at resume stays ineligible on completion,
  while independent new local operations remain eligible. No earlier screen,
  restore-state sequence, or pause marker is injected into the new part. Text
  keeps its mixed-line rule and Raw keeps byte-level eligibility.

## Primary references

- [Microsoft VT controls and split sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)
- [Microsoft terminal parser state transitions](https://github.com/microsoft/terminal/blob/main/src/terminal/parser/stateMachine.cpp)
- [Microsoft terminal output dispatch and repeat-character handling](https://github.com/microsoft/terminal/blob/main/src/terminal/parser/OutputStateMachineEngine.cpp)
- [Microsoft streaming Decoder state](https://learn.microsoft.com/en-us/dotnet/api/system.text.decoder?view=net-10.0)
- [Microsoft streaming Encoder state](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoder?view=net-10.0)
- [ISO-2022-JP state changes](https://www.rfc-editor.org/rfc/rfc1468.html)
- [OWASP data to exclude from logs](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html#data-to-exclude)
- [ECMA-48 control semantics](https://ecma-international.org/wp-content/uploads/ECMA-48_5th_edition_june_1991.pdf)
- [SSH PTY dimensions](https://www.rfc-editor.org/rfc/rfc4254.html#section-6.2)
- [SSH window-dimension changes](https://www.rfc-editor.org/rfc/rfc4254.html#section-6.7)
- [Microsoft delayed automatic wrapping](https://learn.microsoft.com/en-us/windows/console/setconsolemode)
- [Microsoft Terminal wrap-state handling](https://github.com/microsoft/terminal/blob/main/src/terminal/adapter/adaptDispatch.cpp)
- [Microsoft Terminal text-buffer reflow](https://github.com/microsoft/terminal/blob/main/src/buffer/out/textBuffer.cpp)
- [Microsoft terminal tab stops](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences#tabs)
- [Microsoft cursor positioning](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences#cursor-positioning)
- [Microsoft scrolling margins](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences#scrolling-margins)
- [xterm wrapping and saved-line behavior](https://invisible-island.net/xterm/manpage/xterm.html)
- [xterm alternate screen specification](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h2-The-Alternate-Screen-Buffer)

These terminal specifications are references, not adopted UI libraries.
