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

### Terminal-payload transport ownership

Q4-Q6 of the
[whole-codebase architecture interview](../drafts/whole-codebase-architecture-deepening.md#2-terminal-payload-transport-ownership)
confirm named complete Start, Send prepared input, Apply committed geometry and
Disconnect operations. Bind one typed, awaited receive consumer and access to
current committed geometry before transport I/O. The transport does not construct
or depend on a concrete `TerminalSession`, and callers do not perform a public
read/acknowledge recipe. Operation names remain illustrative.

The C# transport owns protocol framing/negotiation, startup, wire representation
of geometry, and readiness, negotiated-echo, receive-EOF and closure facts. The
session owns input permission/order, delivery to Rust and recording, local-echo
policy and chosen geometry. Rust owns character encoding, VT interpretation,
terminal state, reflow and recorded content. Telnet IAC/NVT conversion is framing;
negotiated remote echo is an ordered fact combined with session local-echo policy.
SFTP and transport-specific actions retain dedicated operations. Existing
authentication, host-trust and secret owners remain unchanged.

Prepare the session and selected recordings before startup. Process legitimate
early SSH output before PTY/shell readiness, preserving source and receive order;
readiness cannot overtake preceding consumption. Startup refusal, timeout or
cancellation preserves earlier output and drains already-admitted delivery in
its original generation before the final closed fact, subject to the failure
rules below. One transport owns one startup attempt; overlapping requests join
it and failure never restarts it. Telnet and serial retain their existing
readiness definitions.

The consumer receives one ordered, nonrecursive payload/fact callback at a time.
Payload completion follows Rust processing, separately accounted recording
admission or its narrow failure, bounded admission of generated replies, and
release of the received allocation. Only then is consumption credit eligible;
copying into another pending receive queue is insufficient. It does not wait for
reply transmission, disk flush or painting. Payload memory cannot outlive the
callback without separately admitted ownership. Protocol control must progress
independently under bounded accounting for outstanding reads, advertised credit,
payload, source metadata and control work, including many tiny messages. Use
recoverable backpressure or the existing affected-session resource-failure
policy, never unbounded queues or silent loss. Hold no transport lock across
consumer code, and do not occupy the session reducer across remote-credit,
rekey, startup or I/O waits. A callback cannot await work whose completion needs
that callback or a later delivery; EOF/closed callbacks commit their ordered
facts without awaiting aggregate lifecycle completion.

Send accepts session-authorized encoded input and complete-unit boundaries.
Transport framing/segmentation does not take over encoding, paste pacing or
local echo. Results distinguish rejection, admitted but unsent work, definitely
submitted complete units and a partial/uncertain suffix. Complete-unit results
arrive promptly for echo and pacing; local submission does not prove remote
receipt, and uncertain data is never replayed. Geometry comes from the current
session commitment: PTY startup must not use a stale factory snapshot after a
long dialog. Coalesce startup changes and notify the latest permitted dimensions
without holding the reducer until readiness. Supersession, serial's lack of a
wire notification and failure remain distinguishable; notification does not
prove remote rendering.

Receive EOF finalizes only its direction once and preserves permitted reverse
input and recording-part ownership. Closure supplies the final receive cutoff
if EOF did not arrive. Cancelling observation leaves accepted work running.
Stop Paste or explicit send cancellation follows the scalar/codec-safe stopping
rule below and healthy codec/bracket cleanup without invalidating the generation. Attempt
cancellation or Disconnect invalidates the attempt, prevents new input/readiness
and joins one shutdown, cancelling unsent work. SSH permits up to two seconds of
peer-close grace: continue processing valid output until channel closure or protocol/transport
termination. The local request alone is not the receive cutoff, and late socket
bytes after protocol termination are not terminal payload.

Bounded immutable outcomes/status distinguish operation completion, pending
delivery and actual release. Issued I/O, buffers, handles and charges remain
owned until actual completion; pending delivery ownership must also settle.
Late callbacks/results stay with their original generation and cannot activate
or mutate its replacement. Consumer failure after partial application stops
ordinary delivery without replay or atomic-rollback claims. Independent status
reports the failure even if no closed callback can be accepted; preserve safe
output, report incomplete capture and settle pending ownership without false
consumption credit. A recording-only failure preserves valid terminal effects,
replies and the healthy transport. Candidate 1's recording owner independently
retains finalization, drain, continued-waiting and explicit-loss decisions;
transport release does not claim recording or retained-output release.

Inside this transport seam, the accepted
[shared SSH channel contract](transports-and-authentication.md#shared-ssh-channel-ownership)
concentrates channel state, credit, request replies and cleanup for terminal and
SFTP. Terminal startup combines protocol success with successful prior-delivery
settlement and the current unstopped generation before publishing readiness.
Protocol progress remains independent of that consumer. Channel release does
not change this section's session/Rust/recording ownership or terminal close
grace, and does not release upper-owner resources by implication.

This design is ready for separately requested implementation, under the accepted
[complete-operation contract](../drafts/whole-codebase-architecture-deepening.md#complete-operation-contract-accepted-in-q6)
and [verification plan](../drafts/whole-codebase-architecture-deepening.md#candidate-2-verification-plan).
Verify the ordinary interface with real session ordering/Rust and controlled
I/O/time adapters, retaining independent protocol vectors and real peer/device
integration. The review performed no implementation, prototyping or runtime
validation; the verification plan is required future work.

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

The shared grapheme contract retains the first 256 Unicode scalars of each
stored spelling and continues processing longer graphemes. Normal-screen Text
therefore records the receive-only projection's retained edited spelling;
omitted continuations are absent. Alternate-screen Text and Terminal continue
to receive their original eligible scalar/operation streams, and Raw retains
eligible original payload bytes. One source or grid tail reaching its storage
guard must not skip another projection, recording eligibility or source break
state. REP uses its source-local retained spelling. Normal bounded omission
does not fail the session/recording or set an incomplete status; actual I/O and
resource failures retain their existing policies. See the
[shared grapheme rules](terminal-and-encodings.md).

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
Finalizing, and Closed. Formats and application log-name choices are fixed by the connection snapshot. Any
format's admission or I/O failure makes the whole group Failed. Connections
continue unless a separate transport/terminal resource error requires closure.

| Boundary | Required ordering |
| --- | --- |
| Initial connection | Open all selected files successfully before starting transport; no files for Disabled |
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

Q1-Q3 of the
[whole-codebase architecture interview](../drafts/whole-codebase-architecture-deepening.md#1-session-log-finalization-and-waiting-ownership)
confirm one C# recording module owning complete named operations for Pause,
receive completion, disconnect completion and explicit close, plus existing
resume/retry behavior. Rust owns content and terminal cutoffs. Closure presents
the five-second choice and forwards decisions to the same recording owner.
Callers receive bounded results/status and read-only actual-release observation,
without an operation lease or cleanup recipe. Names in the interview are
illustrative rather than finalized types.

An accepted explicit close protects its pending recording work before any
long-running drain or prompt wait; Continue waiting keeps that protection.
Only unresolved recording work triggers the log-loss choice, and the later
explicit loss action remains available. Distinguish completed save, incomplete
recording, cleanup adoption and actual release, including when transport alone
is pending. Overlapping intents share admitted work and once-only finalization;
observation cancellation does not stop owned work. EOF retains its distinct
directional meaning and part/session ownership. Existing resource admission and
cleanup adoption remain with their established owners.

This refinement is design-ready with the interview's complete-operation
verification matrix. Tests must exercise the ordinary interface using actual
Rust semantics and controlled file/time adapters, preserving real-file, WPF
and transport integration checks. The interview executed no new runtime tests
and authorizes no implementation or prototyping.

Opening a multi-format part is not a filesystem transaction. On a partial open
failure, close successful handles, report failure, and leave any created empty
files in place. Do not start transport or silently record only a subset.

## Clipboard paste

Accepted whole-codebase architecture Q24-Q27, clarified on 2026-11-18, combine
original Q26's shared-budget ownership with the retained configurable 512 MiB
source maximum and confirmation above 1 KiB. The old 1 MiB encoded-payload cap
and Q28-Q29's independent aggregate policy are superseded.
These are specification decisions; candidate 6's final Q23 review was accepted
on 2026-11-18. This consolidation does not authorize implementation or runtime testing.

Capture one immutable Unicode clipboard text and the application-wide
`maxPasteTextBytes` setting when the operation starts; share the actual source
storage across targets. The default maximum is 512 MiB (536,870,912 bytes) per
paste, measured before target encoding as two bytes per UTF-16 code unit of the
actual selected text. Include unchanged CR/LF; exclude the terminating NUL,
allocation padding, metadata and other clipboard formats. Supplementary scalars
occupy two code units. Use checked length/capacity arithmetic. Text at the captured
maximum remains subject to normal validation and resource admission; larger
text is rejected whole, never normalized or trimmed to fit. There is no separate
encoded-output cap. Existing captures/previews/sends keep their captured maximum
when the saved setting changes. See [application settings](persistence-and-windows.md).

Choose the single-session or broadcast destination before requesting clipboard
data. A change of scope cannot redirect a pending paste. Discard a single-session
paste if broadcast is active when the clipboard result or confirmation arrives.

Reject malformed Unicode, literal DEL/C1 controls and C0 controls except TAB/CR/LF
without replacing or deleting them. This is Unicode validation,
not filtering of valid target-codec bytes or generated encoding controls.
Validate the entire text, strict target encoding, encoder finalization and
required framing before any payload or start marker. A failure near the end
still rejects the whole not-yet-started paste. Later clipboard changes cannot
replace the approved immutable text. Never add Enter, normalize newlines or use
the terminal's retained 256-scalar display prefix as the outgoing payload.

For otherwise valid text within the captured maximum, keyboard and context-menu
paste require exactly one confirmation when the text contains CR or LF,
including a trailing newline, or its source size is greater than 1,024 bytes.
Exactly 1,024 bytes without a newline uses the direct path. Both conditions
together still show one dialog; equality with the source maximum alone does
not require confirmation. Over-limit text is rejected rather than approved.
Use the same Send/Cancel actions, Cancel default, read-only complete-text preview,
grapheme/line counts, ending-newline indication, identifiable controls and pacing
fields; there is no persistent bypass. Bracketed mode does not bypass confirmation.
Broadcast adds the frozen target list to this one dialog. Previewing sends
nothing and does not change the clipboard.

Show TAB, CR, LF, Unicode format characters (including directional overrides)
and whitespace other than ordinary space as visible code-point markers in the
read-only preview and its accessible text. Escape a literal marker opener too,
so the user can distinguish a marker from clipboard text that spells one.
Lay out one visual line break per source CR, LF or CRLF and keep a CRLF pair on
the same preview page. For each frozen recipient, show its transport and endpoint (including the SSH
account), followed by its tab name. Escape controls and marker openers in both
fields. Provide the same destination in the target checkbox tooltip and accessible name. Sending uses the captured source text
and the frozen target identities. Unicode describes the review risks of hidden
line breaks and directional formatting in [UTS #55, §1.2](https://www.unicode.org/reports/tr55/).

Keep all text accessible through bounded read-only preview pages/ranges. A huge
line or single grapheme must not force a whole-text visual-control copy. Visible
continuation fragments use scalar boundaries, never split a surrogate pair and
remain tied to their source ranges; they do not insert text, omit input or become
the send payload. Complete analysis uses bounded continuation context and offsets,
not an array of large descriptors or a new whole-grapheme buffer. Analysis and
preflight yield in bounded work slices and remain cancellable. Clipboard-provider
rendering cannot be promised immediate cancellation; retain late resources until
actual release rather than detaching new workers on repeated cancellation.

Character and additional line delays accept integer 0-10000 ms values. Pace at
completed input graphemes/newline units, treating CRLF as one indivisible newline.
Before the next payload unit wait the character delay, plus line delay if the
previous unit was a newline. Do not add leading/final waits, waits for internal
chunk ends or generated codec/bracket bytes, or line events for display wrapping.
Show the estimated extra wait separately from transfer/remote processing time;
pacing waits remain promptly cancellable.

The shared delay pair and Remember these delays checkbox live only for the
application run, initially 0/0 ms and unchecked. Accepted Send with Remember
checked updates that pair/state; accepted Send with it unchecked uses the edited
pair for that paste and clears remembered values. Cancel or a stale answer changes
nothing. Size-confirmed newline-free text uses these same transitions and has
no line-delay events. A direct small paste uses remembered character delay if
enabled, otherwise zero, and changes no remembered values. The source-size
setting is persistent; these delays are not.

The existing application-wide 4 GiB [accounting envelope](resources-and-validation.md)
covers terminal state plus paste-owned source, common analysis/indexes, capture,
preview, target metadata and actual private work/copies, including allocation
overlap. Keep the existing 2 GiB per-session terminal-state bound. Count truly
shared storage once and real copies separately, including native/managed copies
and late resources. This is not an independent paste pool or a whole-process
working-set guarantee.

Each target's private conversion, preflight, queued/in-flight buffers and result
metadata share its existing 2 MiB/256 concurrent-unit input-work allowance with
ordinary input; keep the existing reserved codec/bracket closure treatment.
Do not charge the complete shared source/common plan again to each target's
2 MiB allowance. Its application reservation remains real ownership until actual
release; changing accounting owners must not create a gap or double charge.
Before first-send commitment, reserve the remaining bounded send/Stop/closure
peak for every target. Confirmation does not guarantee capacity or encodability;
failed preflight/reservation sends no payload/start marker. Reject/defer new work
under the existing capacity rules and original deadline, without adding a new
waiting mechanism. Release/retry partial target reservations without holding
multiple session locks in a cycle. Do not evict retained terminal output, shorten
saved retention or close an unrelated session to make room. Keep held send/closure
reservations; later work cannot consume them. No full encoded copy per target,
automatic spooling, blanket serialization of application pastes, available-memory
percentage or separate aggregate setting is added. Source and late/cancelled
resources stay charged until actual release. Resulting local-echo state and
later terminal mutations still require their existing terminal-state admission.

## Input ordering and future callers

Preflight a whole input unit using a cloned encoder state; unencodable input
does not mutate the live state or send a prefix. Serialize user input, paste,
terminal replies, and future callers through one send owner. Transport control
packets remain separate. Whole-paste preflight does not delay all echo until
paste completion: echo each definitely submitted complete scalar/indivisible
codec unit or mapped key operation as it is sent. A transmission chunk may end
inside a grapheme without completing its pacing/count unit. Echo no partial
encoded character;
partial transport failure can leave uncertain remote receipt and is never
replayed. Unsent units have no local echo. The session's accepted minimal echo
mapping remains authoritative for Enter/BS/TAB/navigation.

Paced paste uses its captured immutable text and validates the current target
encoder/configuration, authority and bracket mode at the ordered preparation
transition before first send. Reserve remaining bounded work and codec closure/
end-marker capacity before starting. Normal successful sending transmits every
input scalar, including graphemes longer than 256 scalars, through bounded chunks.
Explicit Stop acts at the next complete scalar or required larger indivisible
codec/control unit, even inside a short or long grapheme; it never splits CRLF.
Stop may leave a prefix of the final grapheme, but does not deliberately cut an
encoded character/control sequence. A healthy connection finishes necessary
codec closure and one started bracket wrapper, without LF.
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
Include valid input beyond the former 1 MiB cap, source-maximum and 1 KiB
confirmation boundaries, settings changes during preview,
huge-line/grapheme preview and exact send, Stop inside short/long graphemes,
storage omission independent of Raw/Terminal/alternate Text and outbound text,
shared versus copied paste storage, application/target capacity pressure, reserved closure
under pressure and delayed actual release. Full broadcast preflight must reject
a final-target failure or deadline expiry without sending any start marker.
No such implementation tests have run.
