# Resource accounting, acceptance, and delivery gates

Specification baseline, 2026-11-10. These are initial engineering guards under
accepted Q124/Q146-Q148/Q159-Q160, not measured capacity claims. No benchmark,
runtime test, prototype, dependency installation, or release has occurred.

The accepted whole-codebase architecture Q20-Q22/Q24-Q27 decisions, including
Q26 shared-budget accounting together with its retained per-paste size and
confirmation requirements, amend grapheme retention and
paste admission below. The later standalone paste-budget proposals are
superseded. Candidate 6's final Q23 shared-understanding review was accepted on
2026-11-18. These are design contracts, not evidence of implementation or measured
capacity.

## Initial internal budgets

| Resource | Initial bound/accounting |
| --- | --- |
| Terminal grid | 2-1024 columns, 1-512 active rows, additionally active rows <= captured retained-row limit; excess viewport area is unused rather than increasing retention |
| Retained rows | Accepted 100-100000, default 5000, normal active plus scrollback per model; no early eviction to satisfy another budget |
| Terminal state and shared paste envelope | Terminal state remains 2 GiB per session. The existing 4 GiB application envelope includes disconnected retained tabs and both models, plus shared paste source/common work and actual paste-related target work counted once |
| State charge | At least 64 bytes per allocated cell slot, plus owned text UTF-16 capacity, row/cluster/provenance/index capacities, and native model buffers at their actual allocated sizes; shared immutable buffers counted once only with actual shared ownership |
| Stored cluster spelling | Retain the first 256 Unicode scalars; consume further continuations while advancing bounded complete-stream break state, without length-triggered session failure or an artificial cluster break; width uses the retained prefix |
| Receive queue | 2 MiB and 1024 items per session, 128 MiB application; include outstanding read buffers and source partials |
| CSI/ESC parser | 4096 bytes per sequence, 32 main parameters, 16 subparameters each; numeric arithmetic checked |
| String controls | Consume at most 1 MiB per OSC/DCS/SOS/PM/APC string, keeping only bounded recognized fields; overflow is explicit resource failure |
| Input | Existing 2 MiB owned queued/preflight/transmit work per session and 256 queue units, shared by ordinary input and target-specific paste work; retain existing separately reserved codec/bracket closure capacity |
| Shared paste source | One immutable original and common analysis/preview ownership in the 4 GiB application envelope; physical sharing counts once, real copies and overlap count separately |
| Clipboard paste admission | Configurable per-paste source maximum, default 512 MiB of actual UTF-16 text bytes before target encoding; exclude terminating NUL, allocation padding and other clipboard formats; no separate encoded-payload cap. Valid in-limit text confirms for CR/LF or source size >1,024 bytes |
| Automatic replies | 64 KiB and 128 queued replies per session, charged to input budget; no insertion inside an active paste |
| Log payload | Accepted 8 MiB per recording group across all formats, 4096 items, 256 MiB application; queued/staged/in-flight/failed-owned data remains charged |
| Log I/O | One outstanding write per file, at most 128 application-wide; at most one failed/cancelled prior part group per session, 32 application-wide; retry waits/fails visibly when unavailable |
| File preparation | Four concurrent preparations application-wide; at most eight cancelled-but-incomplete preparations; no repeated new worker per cancellation |
| Activation queue | 64 pending file opens; capacity error on new excess, no existing session closure |
| Background CPU work | At most four terminal-processing workers; at most two search/highlight workers; one running expensive KDF/import worker; no regex on UI/receive/log owner |
| Recomputable caches | 64 MiB application for search/highlight ranges and 128 MiB for glyph/layout snapshots; evict/recompute caches rather than terminal content |
| UIA snapshots | 64 MiB application-wide transient snapshot budget; reserve for the requested full result, or explicitly fail the provider call; no implicit GetText truncation/pagination |

Allocation charges are admission/accounting rules, not an exact process working-
set ceiling; CLR, WPF, OS, graphics, native crypto, and driver overhead differ.
Reserve before mutation/allocation where possible. On budget pressure first
drop recomputable caches and reject/defer new work. Existing hard-state failure
ends only the affected session with available output retained and an explicit
incomplete-capture status. Do not silently alter saved retention, encoding,
handshake, recording selections, or content. A process-level fatal OOM is not
promised recoverable. The reference 20-tab workload fits these accounting
budgets for its ordinary text; measurement must verify actual overhead.

TCP/SSH can stop admitting new payload while backpressure works; resume when
budget becomes available. Serial uses its selected handshake and explicit
overrun policy, not invented lossless backpressure. Unresolvable parser or
terminal-state allocation failure cannot wait forever
while claiming a healthy connection. Narrow failures remain narrow: log I/O
failure stops recording, regex timeout suspends the rule, and search/cache
pressure degrades only the recomputable operation.
Long grapheme continuations follow the bounded retention policy; reaching the
256-scalar stored prefix alone is not such a failure. Observations preserve the
authoritative stored spans rather than re-segmenting concatenated prefixes.

Except for the explicitly configurable per-paste source maximum, the table is
versioned internal policy, not new settings UI. Later measured
adjustments may tune engineering budgets while preserving accepted features and
failure semantics; reducing promised scope/retention or the acceptance target
requires an explicit specification decision.

## Shared paste storage and existing input work

The restored original Q26 policy broadens the existing 4 GiB application
accounting envelope to cover terminal state and owned paste data together.
The per-session 2 GiB terminal-state limit remains unchanged. Keep one immutable
paste original and common analysis/plan/index/preview ownership at application
scope; targets refer to that storage rather than each retaining a full copy.
Do not charge the whole original again to every target's input allowance.
Real copies, capture/compaction overlap, native/managed handoff, owned pools,
per-target work and delayed cleanup remain charged at actual owned capacity.
A shared allocation is counted once only while ownership is physically shared.

Each target's existing 2 MiB/256-unit input allowance bounds its queued,
preflight and transmit work together with ordinary input. Do not add another
2 MiB paste allowance. Actual paste-related target work also belongs to the
4 GiB application envelope; this is nested admission, not a second physical
allocation. Retain existing encoder/bracket closure reservations and count
those actual capacities in the application envelope. Local-echo terminal state
continues to use terminal-state admission. Other resource domains retain their
existing budgets and ownership.

Reserve controlled capacity before whole-text copies or other owned growth;
size capture before copying, or use bounded capture within reserved storage.
An external clipboard provider may render before the size is known and is not
promised immediately cancellable. Retain bounded ownership of late results;
repeated cancellation must not spawn detached workers. Preview provides access
to the entire immutable text through bounded scalar-safe pages, without one
full-text visual copy or an unbounded per-grapheme index. Replace the former
1 MiB encoded-payload rejection with the configurable clipboard-source maximum,
default 512 MiB (536,870,912 bytes). Measure actual UTF-16 text at two bytes per
code unit before target encoding, excluding terminating NUL, allocation padding
and other clipboard formats; count actual CR/LF without normalization. Capture
the saved maximum when the operation starts; later settings changes do not
truncate or cancel it. Source equality is admissible subject to validation and
capacity; greater size rejects the whole operation. No separate encoded-output
cap is added. See [settings persistence](persistence-and-windows.md). Actual peak
ownership still has to fit the shared envelope and existing work limits; the
source setting does not guarantee available memory or preparation duration.

Before the first payload or bracket-start marker, validate the complete input
for every frozen target and reserve its remaining bounded send, Stop and healthy
closure peak. Use reusable target workspace instead of retaining a full encoded
copy per target. Confirmation does not guarantee admission: failed preparation
or reservation sends nothing to any target. Preserve the existing 30-second
broadcast preparation deadline, beginning after required confirmation or when
preparation begins if no confirmation is needed. Dialog time is excluded;
retries do not restart the deadline. Check cancellation and expiry after the
last target's conversion and immediately before first-send commitment.

Use the existing bounded rejection/defer and capacity-wait behavior within
that original deadline. Do not hold multiple session locks while awaiting
capacity; release/retry partial reservations under the same deadline rather
than waiting in a cycle. Do not add a new waiting queue, serialize all pastes,
evict another paste's source, truncate input or spool it to a temporary file.
Independent terminal hard-state, transport and OS allocation failures retain
their existing outcomes; paste admission pressure alone does not close a
healthy session.

Keep actual allocated capacities and future reservations disjoint: consuming
a reservation replaces its charge without double counting. Committed sending
and Stop/closure reuse their held workspace; another operation cannot take it.
Cancellation, a closed window, a detached observer or an expired operation
releases charges only when its workers/results/writes actually release ownership.
The shared 4 GiB limit is an accounting envelope, not an exact process working-
set ceiling, an allocation-success guarantee or an OOM recovery promise. No
independent paste pool, available-memory percentage rule or aggregate-limit
setting is introduced.

Human clipboard confirmation follows [the input contract](session-and-recording.md):
for otherwise valid text within the captured source maximum, show the same
confirmation when it contains CR/LF or exceeds 1,024 source bytes. Both triggers
still show one dialog. At or below 1,024 bytes without CR/LF, neither the text
size nor equality with a configured maximum forces confirmation. Refuse invalid
or over-limit input without prefix transmission. AI-supplied
paste keeps its separate 256 KiB UTF-8 request limit and authorization/confirmation
contract in [AI collaboration](ai-collaboration.md). Shared analysis does not
remove those external limits or move ordinary AI request/transport buffers
into paste ownership.

## Scheduling and immutable results

Process one session sequentially, yielding between batches at approximately
5 ms or 1024 operations. This is a scheduling target, not a preemptive deadline
inside native work. Drain fairly across sessions. Coalesce UI invalidations to
the latest snapshot with a target of at most 60 frames/second per visible tab;
do not discard terminal operations or log data when skipping intermediate paints.
Hidden tabs and tabs in minimized windows continue receiving, updating terminal
state, and logging while periodic snapshot capture, painting, and highlighting
are suspended. When a tab is visible again, present its latest state.

Schedule highlighting after approximately 500 ms without text changes. When
switching tabs or scrolling, evaluate the visible area without waiting for that
idle period. Automatic scrolling that follows output uses the same idle timing.
One queued revision per line/set is sufficient; supersede old unstarted work, discard stale results
by session/content/set revision, and retain no history of superseded matches.
Keep accepted 100 rules, 4096 pattern units, 65536 line units, 4096 enumerated
matches, 50 ms search timeout, and approximately 20 ms cooperative work slice.
Overlong/dense lines receive no partial overlay. Timeout suspension is rule/tab
specific; ordinary unrelated saves do not reactivate it.

Search remains literal, ordinal case-insensitive by default, normal retained
content only, query <=1024 UTF-16 units. Cache at most 10000 matches but continue
bounded navigation through uncached content; reaching the cache cap is not the
end of search. Bound work by the same revision/cancellation and worker policy.
Terminal content, selection/copy, logging, and transport never wait for a match.

The [SFTP contract](file-transfer.md#file-management-deadlines-and-limits)
defines separate bounds for transfer batches, channel buffers, requests,
listing and plan metadata, and local provider calls. Bulk transfer work shares
the authenticated transport with the terminal channel and must preserve its
interactive response targets and the terminal and recording budgets above.

## Background receive efficiency objective, 2026-11-14

For the additional 1-6 receiving-tab workload, improve sustained receiving in
non-selected tabs and minimized windows while keeping whole-process memory
around 1 GB. Keep the configured 5000-row reference retention and all three
logs in the measurement. Record private memory and working-set peaks across
visible-history seeding, hidden reception, EOF/log drain and restoration.
Use decimal GB (1,000,000,000 bytes) and state whether peaks exceed that reference;
do not substitute terminal accounting charges or a headless core measurement.

This is an additional workload-specific optimization objective, not an enforced
process cap or a replacement for the internal admission budgets and the existing
20-session acceptance workload. Do not reduce retention or discard output to
meet it. The first C# measurements and remaining memory gap are recorded in
[implementation status](implementation-status.md#c-background-receive-optimization-2026-11-14).

## Deterministic performance workload

The accepted baseline is a recorded Windows 11 x64 PC with >=4 CPU cores,
16 GiB RAM, and local SSD; two windows, one selected tab in each; 20 connected
SSH sessions using aes128-gcm@openssh.com after connection setup, 120x40 grids,
5000 retained rows each, all three logs enabled. It is
a reference machine, not a new installation minimum or a hard tab-count limit.

Define a repeating 4096-line UTF-8 cycle. In the table, `dddd` is the zero-padded
four-digit decimal index 0000-4095, `F` is exactly 96 ASCII X characters, and
backslash escapes name actual CR/LF/ESC bytes (not literal backslashes).

| Index modulo 6 | Exact line construction |
| ---: | --- |
| 0 | `INFO status index=dddd F\n` |
| 1 | `WARN count=dddd F\r\n` |
| 2 | `ERROR code=dddd F\n` |
| 3 | `status index=dddd 接続状態を確認しています F\n` |
| 4 | `\x1B[31mERROR\x1B[0m status index=dddd F\n` |
| 5 | `progress count=dddd 10%\rprogress count=dddd 100% F\r\n` |

Replace only the named `dddd`/`F` placeholders; all other spaces/characters are
literal. Repeat without varying/random content. No delay is
inserted within a logical line, but transport chunk splits cycle 1/7/64/4096
bytes without changing the byte stream. The later fixture generator must save
the resulting payload digest and exact encoded cycle with the test report.

Use ten simple patterns per session: `INFO`, `WARN`, `ERROR`, `code=[0-9]+`,
`count=[0-9]+`, `[0-9]{4}`, `status`, `progress`, `接続状態`, and `X{16}`;
case-sensitive, all enabled, each an explicit fixed color. Produce a measured
aggregate 1,048,576 terminal-payload bytes/second. Run both equal distribution
and one busy producer (19 other sessions remain connected). After retention
fills, measure each workload for 30 minutes. Do not substitute transport bytes
or generated text characters for received payload bytes.

During each run schedule at least 600 local interactions at deterministic
three-second intervals, rotating tab selection, input acceptance, and opening/
closing a local menu. Measure from original input timestamp, including dispatcher
waiting, to valid current viewport or local queue/control acceptance. Remote
echo/RTT is excluded. Require p95 <=100 ms, and report max/tail stalls as well.
In overload cases measure Cancel/Disconnect acceptance p95 <=250 ms, without
claiming transport/file completion within that time. Record queue peaks,
resource refusals, missed output, log state, actual memory, CPU, and storage.
Missed targets are failures to resolve, not permission to drop output or relax
criteria silently. No results exist yet.

Exercise the same terminal response targets with concurrent SFTP upload and
download. Include transfer stalls and rekey on a shared connection. Check Raw
logs against the expected terminal-only digest and report transfer resource
usage separately. See [SFTP acceptance cases](file-transfer.md#acceptance-cases).

## Functional and failure acceptance matrix

The [desktop and device acceptance inventory](desktop-acceptance-inventory.md) tracks the outstanding product-wide checks from initial development onward. It maps each workflow to a pass observation, the required environment and any assistance needed from the user.

| Area | Required coverage after implementation authorization |
| --- | --- |
| Deployment | Clean supported Windows 11 x64 with no additional runtime installation; bundled native dependencies; absence of optional fonts/device drivers handled as specified |
| Localization/theme | English/Japanese/system language including fallback; live saved theme changes and OS app-preference changes; terminal fixed defaults and ANSI/highlight composition |
| Profiles/settings | All transport fields/defaults; basic activation versus editor validation; unknown optional preservation; version/corruption recovery; copied/moved/missing profiles; relative/UNC/case-sensitive/long paths; no secret fields |
| Windows | Double-click ordering, modal queue/cancel, shared unlock, last-tab detach/merge, closure racing activation, per-monitor DPI/removal, disconnected retained output |
| Terminal/encoding | Every supported control family and reset; Tera Term reference vectors and declared differences; every codec identity; all relevant byte splits; malformed prefixes; JIS/kana/extensions; combining/emoji/right-edge edits/reflow |
| Input | All key/mouse modes, Shift ownership, IME once-only commit, strict whole-unit rejects, preview/control filtering, pacing bounds/remember lifetime, Stop/rekey/disconnect, replies after paste |
| Shared grapheme contract | Pinned independent Unicode vectors; 255/256/257 and longer continuations with complete incoming break state, retained-prefix width and authoritative spans; original outgoing text versus bounded display; independent REP/grid/recording tails |
| Paste admission | One physically shared original; actual copies/capture overlap inside the shared 4 GiB envelope; existing target 2 MiB/256-unit competition with ordinary input; configured source maximum and 1,024-byte/CR-LF confirmation boundaries, in-flight setting capture and bounded full-text preview; reserved send/Stop/closure capacity and actual release; original 30-second broadcast deadline and final preflight expiry check before any send |
| Logging | Every nonempty format combination plus Off; exact Raw payload; edited receive-only Text; complete Terminal operations; wrap/CRLF/CR/BS/TAB; resume partial units; alternate screens; EOF/CLOSE; no UI-scroll effect |
| SFTP | [File transfer acceptance cases](file-transfer.md#acceptance-cases), including an independent OpenSSH server, inactive-tab targeting, cross-pane drag-and-drop, recursive file operations, safe overwrite and cancellation, and terminal/Raw isolation under load |
| Storage faults | Partial/late/failed writes, disk full/disconnected share, stalled flush, five-second close choice, retry without replay, initial-file timeout, maximum abandoned-I/O admission |
| Security | Known-answer crypto vectors; key validation/import envelopes and Q161; host-change rejection/replacement; MFA/banners; malformed protocol/KDF/container inputs; vault copy/restore/password-change/conflict; owned-secret cleanup |
| Accessibility | Keyboard-only create/edit/connect/search/copy/paste/move/close; Narrator retained text/selection/caret in EN/JA, no per-line forced announcement; native dialogs and focus recovery |
| Capacity | Baseline above; 100/5000/100000 rows in appropriate single-session cases, 2/120/1024 columns, bursts, inactive/disconnected tabs, many windows, continuous resize, slow disk, regex timeout, serial overrun, hard-limit failure |

Use independent expected byte/state results and external protocol peers; do
not verify a serializer solely by reading it with the same implementation.
Fuzz/inject failures at parsers, cancellation/ownership, and file commits.
Reference-source inspection is evidence for design, not a substitute for these
runtime checks or an independent security review.

## Implementation and release gates

The initial foundation is C#/WPF/.NET 10 LTS, self-contained x64 ZIP distribution,
with official Microsoft and OpenSSL libraries plus the scoped bcrypt_pbkdf
exception. Use OpenSSL 3.5 LTS at a supported security servicing version; pin
all native binaries/source/data hashes and license notices at implementation
start. Bundle required redistributable/native dependencies so a clean supported
PC needs no separate installation. Prefer native calls through a narrow owned
adapter; do not load crypto providers/libraries from the working directory or
an uncontrolled search path.

Use Windows 11 APIs available from build 22000 as the API baseline, and declare
that minimum in packaging where needed; actual supported editions/releases at
delivery must also be supported by Microsoft and the selected .NET version.
API availability and product support lifetime are different checks. ARM64 is
optional and requires its own matching native binaries/tests if shipped.

Implement in vertical stages after explicit authorization: pure session/codec/
terminal/recording contracts with deterministic tests; WPF profile/settings/tab
and accessibility integration; Telnet/serial; crypto/vault and independent key
vectors; SSH transport/auth/channel; then cross-feature fault/performance work.
The stages are sequencing guidance, not reductions of initial-release scope.
Freeze serializer constants against these versioned formats before writing user
files; discrepancies require contract correction, never silent incompatible v1.

Before initial ZIP release: pass the acceptance matrix, complete independent
review of custom SSH/vault/native-secret handling, verify clean-PC deployment,
record dependency/OS support versions, and prepare a reproducible package and
servicing process. ZIP updates are explicit manual folder replacement with
user data outside program files; refuse destructive downgrade of newer stores.
No application updater, automatic upload/telemetry, crash secret dump, or macro
runtime is introduced. Security advisories/dependency updates require reviewed
rebuilds and regression validation; bundled runtimes do not service themselves.
Before creating the ZIP, require the native build record to identify the pinned
OpenSSL source version and SHA-256. Compare its two output digests against the
packaged OpenSSL DLL and private-key import helper. A missing build record or
a mismatch stops packaging. The build record comes from the trusted build
environment and accompanies the ZIP contents.

Before later Store release: finalize publisher/package identity/signing,
association declarations, packaged data resolution, ZIP-to-MSIX transfer,
uninstall retention, and certification. Keep storage/activation behind logical
owners so packaging does not require changing sessions or file identities.
No Store publication is authorized by specification or initial ZIP readiness.

Implementation readiness means the product decisions, observable contracts,
initial budgets, and validation plan are recorded with no unanswered decision
blocking development. It does not mean code exists, benchmarks pass, independent
review has happened, or the product is release-ready. Starting development still
requires the user's explicit request.
