# Session, input, and recording contract

Implementation allocation update, 2026-11-16: the
[complete Rust terminal design](native-terminal-core.md) places terminal
semantics and both required projections in Rust without handoff. C# retains
session ordering, transports, recording group/file ownership and actual I/O.
The ordering and recording behavior below remain binding; the target is not
implemented. The Begin/Cutoff/Abort recording-interface design, including
activation failure, repeated operations, cancellation, and stale-owner rules,
is confirmed through Q5. Parser rule sharing is confirmed through Q8. The
[closure design](tab-and-window-closure.md) records accepted Q9-Q11 and Q13,
including automatic closure's alignment with explicit closure. Q12a accepts
ordinary same-target transfer completion without restarting a close confirmation;
Q12b accepts a later window-close request joining an existing tab closure and
handling the remaining tabs with required confirmation, without duplicate work
or a user retry. Q14 accepts the exceptional changed-target outcome and confirms
candidate 3's final shared understanding. All three designs are ready for
separately requested implementation; this review performed no implementation,
prototyping, or runtime validation.

Specification baseline, 2026-11-10. This document completes internal ordering
under the accepted interview decisions; it does not authorize implementation.
Read with `../drafts/connection-profiles-issue.md` and
`../drafts/receive-logging-issue.md`. Their user-visible policies remain binding.

## Ownership and lifecycle

One application coordinator owns windows, registrations, the selected vault,
and admission budgets. One session owns its immutable connection snapshot,
transport, receive sources, terminal models, input queue, and recording group.
A terminal tab represents that owner through one or more same-session panes,
under accepted Q165/Q170/Q172 (2026-11-13). Additional panes do not create new
transports, receive pipelines, input owners, or recording groups. Broadcast
input addresses a selected tab's session once, irrespective of its pane count.
Moving the tab never transfers transport or file ownership. The remaining
split-view interactions are consolidated in
[broadcast and split views](broadcast-and-split-views.md), confirmed in Q199.
The decision history is tracked in `../drafts/broadcast-and-split-issue.md`.
Every connection attempt has a unique generation; reconnect
creates a fresh generation, terminal state, and files. Late results carry their
original generation and can only release original resources.

An SSH session's generation also owns its on-demand
[SFTP channel](file-transfer.md#ssh-and-sftp-channel). Moving the terminal tab
preserves that channel and its transfer work. Disconnect invalidates the
generation and stops file work. Explicitly opening transfer after reconnect
uses the new generation.

The session states are PreparingProfile, PreparingLogs, Connecting,
Authenticating (SSH only), StartingTerminal (SSH only), Connected,
Disconnecting, and Disconnected. Local dialogs suspend only the applicable
deadline, not the remote peer. Cancellation invalidates the attempt before
cleanup. No transition replays input or implicitly reconnects. Telnet becomes
Connected after TCP connection, serial after successful open/configuration,
SSH after successful session-channel, PTY, and shell replies. Early SSH terminal
payload is processed while startup is pending.

Use one ordered state reducer per session, without a requirement for one OS
thread per session. Transport chunks, completed input/echo actions, resize,
recording boundaries, EOF, and close actions enter this reducer. Assign an
increasing event ordinal at admission. UI rendering consumes immutable snapshots;
receive processing never waits for a rendered frame, search, or highlighting.
Independent sessions are scheduled fairly.

## Receive and display operations

Raw admission occurs at the accepted terminal-payload boundary before character
decoding. DATA and stderr from the SSH terminal channel preserve their
receive-message order. Telnet IAC/options/NVT conversion happens first, and
serial supplies received bytes. Authentication, protocol messages, and SFTP
channel data and stderr stay outside the terminal parser and all session logs.

Each SSH source owns a decoder, VT parser, charset state, last printable
character, and initial UTF-8-signature state. Telnet/serial have one source.
Retain partial units across chunks. Resolve a completed source operation before
merging it with local echo: for example REP becomes the repeated source text.
Embedded executable C0 operations receive their own execution ordinal and are
removed from the eventual completed-sequence representation.

Apply receive operations to both the combined display model and a receive-only
model when Text recording is configured. Apply local echo only to the combined
model. The two models may have different cursor positions and text, but use the
same editing engine, connection settings, and accepted resize rules. Maintain
the receive-only model through pauses so resume does not invent state. No local
echo stripping from an already merged string is permitted.

A text/control unit is eligible for Terminal or alternate Text recording only
if all its source bytes belonged to the current uninterrupted recording
interval. Unit provenance survives chunk boundaries. A C0 executed inside an
incomplete CSI is its own unit and can be eligible independently. Local echo is
already a complete operation. Raw uses byte eligibility and can therefore
include a new suffix of a character excluded from the other formats.

## Normal Text identities and versions

Retained rows carry stable content anchors and soft-wrap links. Reflow changes
row layout, not content identity. Each surviving span records its text revision,
recording-interval provenance, explicit-space/tab origin, and whether that
revision has already been submitted. Metadata is charged to the session budget.
Keep this information only while its content remains retained; do not keep an
unbounded set of every line ever seen.

Received LF finalizes the eligible logical line being left, including surviving
soft-wrap continuations. An immediately preceding CR from the same receive
source gives a CRLF boundary; another operation from that source breaks that
pair, while local echo and another source cannot complete it. Chunk boundaries
do not break a pair. CR itself edits the cursor without finalizing. VT, FF,
IND, NEL, RI, cursor addressing, local wrapping, and UI scrolling add no log LF.

Serialize cells in logical content order, joining soft wraps without separators.
Keep explicit received spaces, including trailing ones; omit unused/erased tail
cells. Emit a tracked TAB only if its final span still equals ordinary eight-
column tab expansion from that serialized column and hides no surviving text.
Otherwise emit its final spaces/text. Never infer TAB from unrelated spaces.
Keep clusters whole. A genuine received empty-line boundary records its newline;
an erasure alone does not create a blank record.

After submission, mark exactly those content revisions and that boundary as
recorded. A new LF on a distinct new empty line is distinct output. Revisiting
unchanged finalized content does not resubmit its old boundary. A later actual
text edit creates an unrecorded revision; its next finalization appends the
updated surviving line, never edits a previous file region. Rewriting identical
cells without changing content does not create another text revision.

Before quota/scroll retirement, serialize eligible unrecorded spans about to
leave all retention, without LF, then mark/remove those spans. Later finalization
submits only the remaining unrecorded continuation plus its genuine boundary.
No retirement record is made merely when a row enters scrollback. Explicit
erase/delete/overwrite/insertion truncation removes unfinished content without
a pre-edit snapshot. This includes ED2/ED3 adaptations. A replacement in a
surviving already-recorded portion can produce a later revised snapshot; no
attempt is made to reconstruct a shell command or rewrite the earlier prefix.

On a non-newline flush, visit eligible dirty retained content top to bottom,
without synthetic separators or already-recorded versions. Cursor addressing
can thus produce an imperfect transcript, as accepted in Q150. A logger does
not interpret prompts, shell escapes, cursor movement, or local Enter as
command acceptance.

## Recording state and boundaries

The group states are Disabled, Opening, Recording, Pausing, Paused, Failed,
Finalizing, and Closed. Formats are fixed by the connection snapshot. Any
format's admission or I/O failure makes the whole group Failed. Connections
continue unless a separate transport/terminal resource error requires closure.

| Boundary | Required ordering |
| --- | --- |
| Initial connection | Open all selected new files successfully before starting transport; no files for Disabled |
| Pause | Place a reducer cutoff, flush eligible Text tails without LF, finish accepted work, durable-flush and close parts; new output still updates models |
| Resume/retry | Create the whole new part set, then establish a new interval at a reducer event; no backfill if opening takes time |
| Normal Text resume | Mark every retained logical line containing pre-interval content ineligible, including its future continuation; the first received LF ending the current mixed line is omitted, and its next new line is eligible |
| Alternate Text resume | Admit only new complete eligible received units immediately; no screen dump |
| Terminal resume | Admit new complete eligible operations immediately, including independent local echo; no restore-state prefix |
| Enter alternate screen | Flush eligible normal tails once without LF before switching; park normal model/provenance |
| Leave alternate screen | Resolve pending alternate CR, return without dumping normal content; reflow parked normal content once at final dimensions |
| Receive EOF | Finalize source decoders/parsers, then eligible Text tails once; flush accepted work but retain part/session ownership until close/pause |
| Close | Establish final receive cutoff, cancel unsent input, finalize once, then perform bounded transport and log cleanup independently |

For alternate Text, hold one pending received CR to distinguish CRLF. Emit
CRLF for its next adjacent LF, otherwise emit LF for that CR before the next
operation; an interval/screen/EOF boundary resolves it without mixing intervals.
Record printable text and TAB, preserve LF, and omit other nonprinting controls.

For Terminal, encode complete display operations as UTF-8 text and canonical
seven-bit VT controls. Normalize aliases to supported forms, include the full
parameters of each completed display operation, and turn parser-relative REP
into its resolved text. Include cursor/edit/mode/SGR/screen changes that affect
display; exclude queries/replies, BEL, external OSC effects, unsupported or
incomplete fragments, transport commands, and diagnostics. A display operation
is recorded even when it happens not to change cells. Preserve source CR/LF;
local echoed Enter is CRLF. Never add local wrapping, resizing, or replay setup.

## Writers and failures

One owner serializes each part in order. Buffers may be split into bounded I/O
chunks without becoming separate logical records. Account queued, staged,
in-flight, and failed-but-owned buffers until ownership actually ends; admission
is not a durability claim. Preserve the 8 MiB group cap, approximately one-second
batching target, ten-second no-progress failure, and five-second close choice.

On failure, stop new admissions and best-effort finish the accepted prefix.
Never replay a partly written batch into the same file or guess how much failed
I/O reached storage. Explicit retry uses fresh part numbers and new owners. Late
completions cannot affect a newer owner. Resume/retry waits or fails visibly if
old I/O consumes the cleanup allowance. Close-with-loss abandons unissued work
and requests cancellation of issued work; only actual completion releases its
buffers. Continue-waiting on close overrides automatic stall abandonment.

Opening a multi-format part is not a filesystem transaction. On a partial open
failure, close successful handles, report failure, and leave any created empty
files in place. Do not start transport or silently record only a subset.

## Input ordering and future callers

Preflight a whole input unit using a cloned encoder state; unencodable input
does not mutate the live state or send a prefix. Serialize user input, paste,
terminal replies, and future callers through one send owner. Transport control
packets remain separate. Whole-paste preflight does not delay all echo until
paste completion: echo each successfully submitted complete character/grapheme
or mapped key operation as it is sent. Echo no partial encoded character;
partial transport failure can leave uncertain remote receipt and is never
replayed. Unsent units have no local echo. The session's accepted minimal echo
mapping remains authoritative for Enter/BS/TAB/navigation.

Paced paste captures an immutable validated payload and bracket mode immediately
before first send. Reserve codec closure/end-marker capacity before starting.
Stop acts at the next complete encoded/grapheme boundary; a healthy connection
finishes necessary codec closure and one started bracket wrapper, without LF.
Queue automatic terminal replies after this closure. Later peer mode changes
apply to the next paste. Further interactive terminal input is rejected while
paste owns sending; Cancel/Disconnect remain usable.

Expose these lifecycle/send/receive operations independently of WPF views.
Future observers must not consume data away from the terminal/logger or depend
on rendered frames. Preserve credential references and a replaceable naming
policy. Do not add a macro runtime, speculative API, or replay store now.

## Required verification after authorization

Exercise every byte split of representative UTF-8/CP932/JIS/control sequences,
interleaved stderr/local echo, CRLF across chunks, and recording cutoffs inside
each unit. Include width-edge LF, reflow with retirement, edited recorded rows,
custom TAB stops, alternate transitions, pause during file opening, EOF followed
by CLOSE, partial writes, stale completions, Stop/rekey/disconnect races, and tab
transfer while receiving. Assert resulting bytes and event order, not UI frames.
No such implementation tests have run.
