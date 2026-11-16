# Confirmed SSH session startup, output, messages, and closure

> Baseline decisions were consolidated on 2026-11-10 in the [transport and authentication contract](../specification/transports-and-authentication.md). The initial release also includes an on-demand [SFTP subsystem channel](../specification/file-transfer.md#ssh-and-sftp-channel) within the same authenticated connection.

Status: Q140, Q142, and Q143 accepted as recommended on 2026-11-08.
Q141 subsequently accepted as recommended after clarification on 2026-11-08.
Q137-Q139 remain accepted. See [implementation status](../specification/implementation-status.md)
for build and validation evidence.

Read with `ssh-transport-issue.md`, `connection-profiles-issue.md`,
`authentication-issue.md`, `receive-logging-issue.md`, and
`terminal-interaction-issue.md`. The accepted algorithm policy, saved-profile
workflow, logging modes, and manual Telnet/serial login scope are preserved.

## Q140: Interactive SSH readiness and PTY setup (accepted)

Open the SSH terminal channel after complete user authentication,
request a PTY with a reply, then request a shell with a reply. Mark the
session Connected/ready for terminal input only after those steps succeed.
Do not infer readiness from a guessed shell prompt or wait for output/newlines.
A successful shell request does not prove that shell initialization or a
remote application login is complete. Authentication alone is not terminal
readiness, and a shell that starts and immediately exits can briefly be ready.

Require PTY and shell success initially. On refusal or the accepted Q131 setup
deadline, fail the attempt and retain the tab/output; do not silently fall back
to a no-PTY shell, exec command, or subsystem. Initial scope remains one
interactive shell and one independent connection per tab. Disable terminal
key/committed-IME/paste transmission while connecting and do not queue those
inputs for later automatic sending. Local copy/search/cancellation still work.

Process legitimate channel output as soon as it arrives, including output
before the shell-success reply. It remains eligible for configured logging;
do not buffer it behind an assumed prompt or discard it on startup failure.
Protocol messages and authentication banners are not channel payload.

Use the accepted profile terminal type (`xterm` by default), the actual current
row/column dimensions, and initial VERASE matching the selected Backspace byte.
Coalesce size changes while startup is pending and send the latest dimensions
through normal window-change handling when permitted. Preserve Q106's screen
reflow/logging rules independently of remote resize notifications.

Request IUTF8=1 only when the effective transmit encoding is UTF-8, otherwise 0.
It concerns remote input-line character handling, not conversion of channel
bytes. Do not derive it from UI language or the receive codec alone. Leave
other unspecified terminal modes to server defaults; do not configure arbitrary
stty-like options or force newline translation to alter received log content.
TERM uses the PTY field. Do not automatically send LANG, LC_*, Windows environment
variables, startup commands, or directory-changing commands. No initial custom
environment/startup-command editor is introduced by this decision. Remote apps
can change terminal modes; these initial requests do not guarantee remote editing.

## Q141: Normal channel output and extended stderr (accepted)

Include separately delivered stderr in the same terminal/logging outputs, and
isolate incomplete decoding/parsing units before merging display operations.
Here stderr is output of the remote program, for example a missing-file
diagnostic; it is distinct from TabTty's own connection error.

Programs can write ordinary results to stdout and diagnostic text to stderr.
SSH can distinguish normal channel DATA from extended stderr. In a usual
OpenSSH PTY session, stdout/stderr have already been combined at the server and
arrive through normal DATA; there is nothing for TabTty to separate or infer.
Q141 addresses a server that actually sends extended stderr separately. It does
not propose classifying text by words such as "error" or extracting stderr
from a stream the server has already combined.

Retain both delivered sources while preserving their
individual partial characters/control sequences until complete. Raw remains the
exact ordered concatenation of received payload bytes. The display can differ
in operation-completion order, and the headerless Raw file does not retain
source identities. This is not a proposal to discard, transcode, or reorder Raw
bytes. It also does not claim to repair a stream already corrupted/interleaved
at the server. The detailed requirements and byte example follow.

Display and record both normal channel DATA and standard
extended-data type 1 (stderr) for the selected terminal channel. Do not label
normal DATA simply stdout: a remote PTY commonly already combines stdout and
stderr there. No separate stderr pane, automatic red coloring, source prefix,
extra newline, or duplicate echo suppression is introduced.

Raw concatenates the exact payload bytes from both supported received data
message types in SSH message-reception order before decoding. Preserve byte
order within every payload. Exclude channel/extended-type headers and do not
add source markers. This preserves the accepted terminal-payload boundary,
not encrypted packet capture or separable reconstruction of the two sources.

For display, Text, and Terminal, maintain independent incremental character
decoder states and incomplete terminal-control parsing states for normal DATA
and extended stderr, using the same selected receive encoding. A partial
multibyte character or control sequence from one source must not consume bytes
from the other. Process messages in receive order and emit each complete text
or control operation when it becomes available; an incomplete source does not
block complete operations from the other source. Screen/cursor/attributes and
other applied terminal state are shared, not separate virtual screens.

For example, UTF-8 normal DATA `E3 81`, extended stderr `21`, then normal DATA
`82` produces Raw bytes `E3 81 21 82`. The safe display operations are `!` then
`あ`. Raw therefore is not promised to recreate the actual screen if reparsed
as one stream. Nor can SSH arrival order recover the original wall-clock order
of writes in remote processes. This is a deliberate TabTty rule consistent
with its source-isolated receive/local-echo design, not a claim of identical
Tera Term or OpenSSH rendering.

Both sources are received data eligible for Text's received-state projection
and Terminal's operation stream. SSH local echo remains off. Preserve existing
normal/alternate-screen semantics, recording interval eligibility, and no-backfill
rules per source. A source's incomplete unit at resume does not become eligible
merely because another source emits output. UTF-8 leading-signature handling
is clarified for Q126 to apply independently at byte offset zero of each new
source stream, with Raw still preserving the bytes; it does not restart on log
parts, reflow, or rekey. This Q126 clarification is accepted with Q141.

Only the selected terminal channel's normal DATA and defined stderr are terminal
payload. Do not reinterpret unknown extended-data types or unrelated channels
as display text. Unknown-type handling, flow-credit accounting, parser bounds,
source finalization order, and reply ordering remain explicit protocol/pipeline
contracts to complete; no unbounded source queues or claims of lossless logging
after a reported transport/log failure are permitted.

## Q142: Server messages outside terminal output (accepted)

Present authentication banners and applicable connection messages
as plain text in a per-tab connection-information area, separate from the terminal
grid. Show authentication banners during connection without adding a consent
button; retain their bounded content for inspection while the tab exists.
Surface failures beside connection status and preserve the relevant reason
after disconnect. Distinguish server-provided text from TabTty's own localized
status and from a verified host-identity statement.

Interpret protocol text under its defined UTF-8/ASCII rules, independently of
terminal encoding. Make control characters inert or visibly escaped; never feed
these strings through terminal VT interpretation, hyperlinks/actions, or commands.
Normal textual line breaks may be shown. Invalid text remains safely representable
without switching to the terminal codec. Server language tags do not authorize
translated commands or change the app's selected language.

Show SSH DEBUG with always_display=true as a server message; ordinary protocol
debug messages are not shown in normal operation. Do not automatically persist
arbitrary server message bodies in diagnostics. This bounded, tab-lifetime
display is not a new diagnostic-history/export feature. Authentication prompts
remain in their existing interaction UI, and user answers or key/password
material never enter this message area.

Authentication banners, disconnect explanations, exit-status metadata, and local
connection errors remain excluded from Raw/Text/Terminal session logs. A welcome
message/MOTD or an error actually transmitted as terminal channel payload remains
ordinary terminal output and is recorded under the selected modes; do not classify
it by wording or strip it because it resembles a banner. No connection-time
profile approval or replacement Telnet warning is introduced.

Complete bounded message sizes/counts, truncation indication, pre-authentication
server-text labeling, and accessible notification behavior in the resource/UI
contract. Overlong server text must not exhaust memory or be mistaken for local UI.

## Q143: Remote completion and explicit local disconnect (accepted)

Show an available remote exit status or signal in connection
information, without appending it to terminal text/logs. Missing status remains
unknown rather than assumed zero. Status/signal notification alone does not end
reception, close recording, or discard later valid channel output.

EOF ends the peer's data direction, not the whole channel. Process already
accepted payload before finalizing that receive direction. Flush eligible
incomplete Text at this receive completion without adding a newline and mark
it finalized so later channel closure does not duplicate it. Keep the recording
part/session ownership until ordinary close or explicit pause. Distinguish
receive completion in connection state and continue permitted opposite-direction
input until channel closure or an explicit disconnect. EOF alone does not start an invented idle
disconnect timer. A valid channel CLOSE is handled even without a preceding EOF;
reply as required and drain already accepted terminal operations/log work before
finishing the session. Do not wait forever for an optional exit status.

On actual closure/failure retain the tab and its output, with the disconnect
reason. Flush eligible tails under the existing recording policy without adding
newlines, fabricated end markers, or new log parts. Reconnection is explicit and
never replays buffered input. Remote transport shutdown can leave remote output
unreceived; do not claim that closing captures every byte a remote process might
have produced.

For an explicit local Disconnect action, stop accepting new input, cancel paste,
discard unsent input, and make a best-effort protocol close. Do not send `exit`,
Ctrl-D, or another terminal command. Already handed-off input cannot be recalled.
Allow up to two seconds for peer-close/protocol cleanup, then close the underlying
transport. This fixed close-grace budget is distinct from Q131's configurable
setup/rekey waits; it is a product responsiveness choice, not an RFC deadline.
Do not add another confirmation to explicit Disconnect or to an already-confirmed
tab/window close. Preserve the existing confirmation for closing live tabs/windows.

Process valid output received before closure during the grace period. Stop
accepting session payload after SSH DISCONNECT/transport termination; do not
pretend the socket can be drained as SSH traffic after protocol disconnect.
The close grace bounds peer waiting, not a guaranteed total disk-flush time.
Do not abandon already accepted logging work to claim a two-second complete-save
guarantee. File-drain/error handling needs the separate bounded logging-shutdown
contract. Closing multiple sessions must not multiply peer waits serially across
all tabs; use independently bounded operations and keep the UI responsive.

## Primary evidence reviewed on 2026-11-08

RFC 4254 separates PTY/shell requests, normal and extended data, directional EOF,
channel CLOSE, and exit notifications. Tera Term 5.7.1 provides a reference for
PTY parameters and routes shell extended data through the terminal payload path.
OpenSSH's PTY server connects stdout/stderr to one PTY; its client keeps normal
and extended output in separate file-descriptor buffers without terminal parsing.
The safe source-parser composition, readiness rule, native message area, and
two-second local-close grace are TabTty product choices.

RFC 8160 supplies IUTF8 for remote input-line character handling. Tera Term's
reviewed PTY request does not send it, so the encoding-aware request above is
an explicit TabTty adaptation. Other terminal mode values are not copied
blindly from a reference's newline or serial-speed configuration. OpenSSH's
SendEnv default does not send arbitrary environment variables.

RFC 4252 recommends displaying authentication banners and filtering control
characters. RFC 4253 distinguishes displayable DEBUG from ordinary debugging
and specifies protocol-text encodings and disconnect handling. These do not
require injecting protocol messages into terminal logs or adding consent UI.

- [RFC 4254 SSH connection protocol](https://www.rfc-editor.org/rfc/rfc4254.html)
- [RFC 8160 IUTF8](https://www.rfc-editor.org/rfc/rfc8160.html)
- [RFC 4252 authentication banners](https://www.rfc-editor.org/rfc/rfc4252.html#section-5.4)
- [RFC 4253 DEBUG and DISCONNECT](https://www.rfc-editor.org/rfc/rfc4253.html#section-11)
- [Tera Term 5.7.1 PTY request](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/ttssh2/ttxssh/ssh.c#L8603)
- [Tera Term 5.7.1 extended data](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/ttssh2/ttxssh/ssh.c#L10184)
- [OpenSSH session requests](https://github.com/openssh/openssh-portable/blob/master/clientloop.c#L2575)
- [OpenSSH PTY execution and exit handling](https://github.com/openssh/openssh-portable/blob/master/session.c)
- [OpenSSH channel data handling](https://github.com/openssh/openssh-portable/blob/master/channels.c)
- [OpenSSH SendEnv](https://man.openbsd.org/ssh_config#SendEnv)

Exact state-transition tables, accepted-message sequences, channel/window and
parser budgets, startup-resize/input races, safe receive-source completion,
message-area limits, and shutdown/drain failure contracts remain technical work.
No runtime observations or interoperability tests have been performed in this
specification phase. Initial-release implementation readiness is not yet reached.
