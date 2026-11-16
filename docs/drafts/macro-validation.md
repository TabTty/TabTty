# Macro budgets and acceptance cases: accepted contract

Drafted under Q69 and confirmed in final review on 2026-11-14. Read the
[behavior](macro-specification.md) and [language/API](macro-language.md) contracts.
Values below are accepted initial engineering
guards, not measured capacity, completed tests or implementation authorization.
Q70-Q77 were accepted in full on 2026-11-14; their cases are now required.
The existing terminal, input, log, SFTP and AI budgets remain separate
and binding; macro admission cannot borrow their reserved capacity silently.

## Confirmed primary testing seam

Use one primary feature seam: starting and controlling a macro run through the
same application-facing run entry point used by Run Macro/file activation.
Supply source/profile files and ordinary arguments, drive received payload and
user Pause/Resume/Stop/dialog responses, and observe outgoing terminal data,
run state/results, dialog/window requests, profile-registration effects and
recording files. Assert observable outcomes and ordering, not parser node shapes,
interpreter instruction names, private fields or internal mock call counts.

Reuse the existing terminal-session transport seam and real session/decoder/
recording behavior. Prior art includes saved-profile loopback connection/data
tests, exact Raw payload checks, recording-resume tests that reject backfill,
and the existing paused-send wire used for queue admission/continuation tests.
Use the application's existing shared-unlock and window/input tests for focused
UI integration. Keep parser/type/regex work exercised by scripts through the run
entry point where possible, instead of adding independent test-only APIs to
every language component.

At that single macro-host boundary, provide controllable time, transport outcomes
and user-interaction completions for deterministic timeout/pause/failure races.
Reuse existing services behind it; do not introduce a parallel fake implementation
of terminal decoding or vault semantics. Add a small set of real-peer and WPF
integration checks for protocol/OS behavior that cannot be established by the
controlled host. These supplement the primary seam rather than replacing the
feature contract with private-unit assertions.

The user confirmed this seam plan with the complete specification on 2026-11-14,
satisfying the invoked to-spec skill. It does not authorize implementation or testing code. The 50-case
matrix below describes the observable contract to verify once implementation is
explicitly requested.

## Admission and accounting

| Resource | Accepted initial bound |
| --- | --- |
| Source file | 1 MiB encoded bytes; 1,048,576 decoded UTF-16 units; reject oversized source before unbounded allocation |
| Parser/checker | 100,000 syntax nodes; lexical/expression/block nesting <=128; at most 256 functions, 32 parameters per function, 1,024 locals per frame; no call-graph cycle |
| Types/call depth | List type nesting <=4; statically computed longest helper call chain <=64, including unused functions; reject deeper graphs before execution |
| Active runs | 16 application-wide, including paused/dialog-waiting runs; reject new excess runs without stopping old ones; existing activation queue <=64 still applies |
| Interpreter state | 32 MiB accounting per run; 256 MiB application total, including retained result state, source, AST, frames, lists, strings, pattern caches and history |
| Admission reserve | Reserve 8 MiB per admitted run: at most 7 MiB for history plus 1 MiB exclusively for coordinator/error/Stop progress; both count inside 32 MiB/256 MiB totals |
| String/list | One string <=1,048,576 UTF-16 units; one list <=65,536 elements; shallow copies and list capacity count separately |
| Invocation | <=64 arguments; each <=4,096 UTF-16 units; total <=16,384, excluding source path; reject invalid Unicode and embedded NUL at invocation |
| Receive text | <=1,048,576 retained UTF-16 units and <=65,536 line records; actual history allocation <=7 MiB including metadata/prefix context/gaps; cannot consume the 1 MiB emergency reserve |
| One receive line | <=65,536 UTF-16 units, including retained consumed prefix required as regex context; exceeding this enters history-loss recovery, not terminal failure |
| Gap metadata | Coalesce adjacent losses; at most one unresolved incident, with cutoff/counters and bounded interval metadata; discard obsolete intervals once no retained range can refer to them |
| Pattern | <=4,096 UTF-16 units of pattern/literal text; <=32 simultaneously live patterns per run; no unbounded global pattern cache |
| Alternative wait | 1-16 candidates; immutable copy at start; each candidate counted within pattern ownership |
| Regex search | 50 ms approximate timeout per engine search; no user option to remove it; check cancellation/yield between searches |
| Matching pass | <=65,536 candidate-line search attempts and <=1,000 ms accumulated active matching time per initial/history-revision/deadline-cutoff pass; excess reports MatchWorkLimit without consuming history; worker-queue/pause time excluded |
| Regex results | <=128 groups including group 0; <=4,096 captured values total; <=262,144 UTF-16 units of materialized match/group/capture text per result; exceeded result is CaptureLimit without consumption |
| Search snapshots | At most one in-progress search and one replaceable pending revision per run; bounded per-line copies, no full-history copy for each receive event |
| Send | <=1 MiB encoded payload including appended Enter, within the existing session's 2 MiB input accounting; reserve required codec closure separately before commitment |
| Dialog | At most one per run; title <=256 UTF-16 units, message <=16,384, input/initial text <=4,096; no secret-result exemption from caps |
| Local delay/finite wait | Milliseconds in 0..long.MaxValue; maintain remaining budget without overflowing absolute deadline arithmetic; zero is specified in the API |
| Worker concurrency | At most two macro evaluation/search workers application-wide; no one-thread-per-run requirement; do not occupy receive/UI/log owners |
| Scheduling slice | Yield/check pause/Stop at about 5 ms or 1,024 evaluated instructions, whichever first; a bounded host call has its own checks/bounds |
| Result diagnostics | Bounded source position, error code and message; no automatic variable/argument/send/capture dump, execution trace or unbounded result history |

These are accounting limits, not a hard process-working-set guarantee. Charge
owned allocated capacities, not just logical lengths: at least 64 bytes per
syntax node, 32 bytes per value/list/string/pattern wrapper, 16 bytes per list or
frame slot, plus string UTF-16 storage, line/gap metadata and explicitly owned
buffers. Count actually shared immutable storage once; charge clones separately.
VM metadata and capture/result copies are not free because their underlying
source has been consumed. Host .NET regex, CLR, WPF, native/driver and OS overhead
requires measurement and cannot be represented as a guaranteed exact ceiling.

Reserve before mutations/allocations where possible. Map limits deterministically:
source/AST/nesting/function/parameter/local/type/call-chain and invocation limits
reject launch before statements; excess single string/list/send/dialog data is
catchable PayloadLimit before mutation; excess pattern length is PayloadLimit
and the 33rd simultaneously live pattern is PatternLimit; capture materialization
is CaptureLimit and a matching pass's work limit is MatchWorkLimit. Reclaim
unreachable interpreter values before refusing live-state admission, while still
charging in-flight work. A rejected list mutation does not change its revision.
Inability to preserve hard interpreter state terminates the affected run outside
catch, keeping a healthy terminal available. Do not put the runtime into an
allocation/retry/catch loop after exhausting its own ability to execute safely.
Fatal process OOM is not promised recoverable.

Receive-history exhaustion is the explicit exception: use the accepted bounded
tail/Continue/End recovery, with accepted Q71/Q73 rules. Do not terminate
the run automatically just because history reached its cap. New text can cause
continued eviction while the user decides; terminal/logging continue under their
own independent budgets. Do not apply loss recovery by silently increasing a cap
or disconnecting a healthy transport.

Cancelled-but-running regex/host work remains charged until it really completes.
Invalidate its results by run/operation/generation identity, without spawning an
unbounded replacement worker after repeated Stop/restart. The finite regex
timeout is approximate and cannot promise preemptive cancellation inside every
engine operation. Bound pattern creation inputs as well; no malicious-pattern
sandbox or strict regex-construction wall-clock bound is claimed. Reject/defer
new work when both workers are occupied; UI Stop admission remains independent.

Waiting for I/O, a dialog, a sleep timer or Resume retains a continuation rather
than occupying a macro CPU worker. Regex/other already-started synchronous work
is different and remains charged until it returns.

Do not impose a total execution-duration/instruction limit on ordinary loops:
long-lived monitoring and indefinite response waits are accepted. Fair scheduling,
bounded live state and visible Pause/Stop provide the control boundary.

## Matching scheduler and race mechanics

Keep source/run/session generation, wait operation identity, receive ordinal,
cursor/floor, subject revision, pause generation and deadline cutoff with work.
Never commit a stale result into a new run, wait or connection. Between pattern
searches, observe Stop and yield fairly. Avoid rescanning unchanged completed
lines for the same wait and pattern set; cursor/operation changes invalidate
only relevant cached no-match decisions. Cache keys and entries count toward
the run budget. Receive admission never waits for matching workers.

One matching pass is the scan of a fixed history cutoff/revision for one wait.
Do not reset its attempt/time counters merely because more input arrived while
it was being sliced across workers. Later passes can skip already-proven
unchanged no-match lines and evaluate new eligible text. The pass cap is not a
lifetime budget that eventually expires every indefinite wait. A failed pass
returns MatchWorkLimit; it is not a false no-match/timeout. A regex engine call
can overshoot its approximate timeout, so the aggregate active-time guard is
checked between calls and is not a strict wall-clock deadline either.

For an unfinished line, evaluate the newest available snapshot when scheduled.
Do not claim a match opportunity at every transport byte boundary: coalescing
unstarted revisions is permitted, while a committed match is immutable. Evaluate
all candidate matches needed for the selected line's earliest-position/tie rule
before committing; the first candidate searched is not automatically the winner.
Any evaluation error means the wait failed without consuming history; candidate
order cannot hide a zero-length or timeout error merely to manufacture success.

Subject/floor behavior follows accepted Q72. Preserve a committed result in a
bounded immutable object while a hold prevents delivery. Proposed Q74 invalidates
uncommitted work on a changed pause/cursor generation. At finite expiry, freeze
the last eligible receive ordinal and evaluate data admitted by that cutoff
before committing timeout; later arrivals are ineligible for that wait. Timeout
may therefore be reported after its nominal deadline while bounded matching
finishes; it is not an output-arrival grace period. Pause-before-expiry freezes
remaining local budget; pause after a committed timeout cannot resurrect it.
Use a test clock and explicit coordinator events for these rules, not timing
assumptions in later tests. This implements the accepted Q74 contract.

## Log names and file preparation

Use the captured basename, taking at most its first 64 Unicode scalars; replace
Windows-invalid filename characters and C0 controls with `_`, trim terminal
spaces/dots, and use `TabTty` if empty. Reject malformed Unicode at the source
path boundary rather than inventing partial scalar names. Preserve source
FileName/BaseName properties unmodified; sanitization affects log names only.

Compose a stem from sanitized basename, connection-start local timestamp with
UTC offset, unique session suffix and part number. Use the existing timestamp
shape `yyyyMMdd-HHmmss.fffzzz` with colon removed from its offset, a fresh UUID
suffix, and `p001` with at least three decimal digits. Resume/retry advances
part numbers without wrapping, reusing the session's naming context. Format
markers are `.log`, `.raw.tttylog` and `.term.tttylog` as in the main contract.

Open with create-new semantics; never overwrite. A name collision is a recording
preparation failure under the same explicit retry policy, not permission to
truncate or append. Retry chooses a fresh unused next part number. Close successful
handles and retain files created by a partial group open operation, even when
empty, consistent with the recording
contract. Invalid/too-long combined paths are visible preparation errors; do not
silently redirect to another directory or truncate the configured destination.
No persistent log cleanup/rotation/name override is added.

## Acceptance matrix for later authorized implementation

All rows are required planned checks, not executed results. Q70-Q77 markers
identify accepted decisions rather than conditional alternatives. Existing SSH,
vault, terminal and recording tests remain required; these cases add macro
observable behavior without replacing them.

| ID | Scenario | Required observation |
| --- | --- | --- |
| M01 | Valid UTF-8 without/with BOM; UTF-16LE/BE and UTF-32LE/BE with BOM | Same source behavior; UTF-32LE BOM not misclassified as UTF-16LE |
| M02 | Malformed encoding, unsupported/missing/duplicate version, over-limit source | Source rejection before connection, send, macro dialog/window effect |
| M03 | Invalid type/API in dead branch; unused recursive helper cycle | Whole file rejected before effects |
| M04 | Source file externally edited after validation | Running snapshot stable; next launch reads/rechecks new source |
| M05 | `var x=1` reassigned string/double; explicit long/double/list conversions | Fixed inference, no dynamic fallback or list covariance |
| M06 | Min long literal, its negation/division, signed remainder, zero double division, finite underflow | Exact documented outcomes; no wrapping or NaN/infinity propagation |
| M07 | Mixed comparison at 2^53; double-to-long at -2^63/+2^63 | Rounding and exclusive upper bound as documented |
| M08 | Well-typed `1/0` in try; same expression in an unexecuted branch | Catchable when evaluated; no optimization-dependent source failure |
| M09 | String forms/escaping/braces, adjacent hex after `\x`, surrogate pair/index, invariant culture | Exact values, UTF-16 indices, strict boundary rejection of unpaired surrogate |
| M10 | List aliases, Clone, element replacement/Add in foreach through helper | Aliases visible; clone independent; mutation detected before next iteration |
| M11 | Short-circuit with an effectful helper; for continue; helper local/top-level visibility | Correct evaluation order and scopes; source validation still covers helper |
| M12 | Relative profile path after several relative base changes/reset | Correct per-run base; no process-CWD, other-run or log-directory change |
| M13 | Macro opens an unregistered saved profile | Connection works without profile registration or file rewrite |
| M14 | Initial log creation failure after one selected file opens | No transport start; visible pause; partial empty files remain; same-snapshot retry/new parts; separate Resume |
| M15 | Early SSH prompt before Connect completes | First wait sees it; no observer-after-connect race |
| M16 | Tab moves while active/paused/dialog-waiting | Same run/session/history; window operations resolve current host; no reconnect |
| M17 | Profile saved password fails, corrected manual auth succeeds, or public-key auth succeeds | Default password-send still uses the captured configured entry; no authentication-value cache |
| M18 | In-app entry update/delete, profile edit, selected-vault switch, external stale detection | Q61 current-value and fixed-reference behavior; no hidden old-vault/name fallback |
| M19 | No profile password; wrong kind; explicit reference on Telnet/serial; cancelled shared unlock | Missing/typed errors; ordinary unlock; no guessed password/master-key/key-passphrase use |
| M20 | Pause between secret retrieval and send commitment; Stop during secret send | Discard unused plaintext/reacquire on Resume; no indefinite prepared secret; cleanup and no secret local echo |
| M21 | Remote echoes a submitted password | Output/logs preserve remote data under existing modes; no false redaction guarantee; no application-generated secret echo |
| M22 | Write/WriteLine with existing LF, CR, TAB, ESC/Ctrl+C, stateful codec and unencodable final character | Exact logical text and one appended Enter; whole preflight prevents prefix on rejection |
| M23 | Pause/Stop during a slow long send | Pause requested until current send ends; no next operation; Stop cancels unsent safely without replay |
| M24 | Partial/uncertain write vs known zero-payload rejection | Q76: forced macro end versus catchable failure; no silent duplicate command |
| M25 | Normal CRLF split across chunks, lone CR, TAB, BS, ANSI, REP, malformed-receive fragments | Exact filtered-line rules; no display reconstruction or sequence-generated text |
| M26 | Independent SSH DATA/stderr partial units, interleaved source-local CRLF and EOF | No cross-decoding; DATA `x<CR>`, stderr `y`, DATA `<LF>` leaves `y` unfinished; existing finalization order |
| M27 | Completed-line vs prompt substring, multiple candidates at different/same positions | Earliest line/position/candidate rules; no cross-line matches |
| M28 | `$`, lookahead and greedy pattern succeeds on growing prompt | Committed result never expands/retracts; scheduling dependency documented |
| M29 | Empty literal, actual zero-length regex, positive match with empty groups | Correct validation/error; no success-without-consumption loop |
| M30 | Captures under a repeated/named group; result retained after history consumption | Correct snapshot order/names/indices; bounded copies independent of future receive |
| M31 | Consumed `abc` before `def`; explicit boundary mid-line | Q72: line anchors/context retain original subject; match body starts at/after floor |
| M32 | History/line/count cap while Running, paused and dialog-waiting | One incident, bounded recent tail, continued receiver/logger, no auto disconnect/termination |
| M33 | Loss within one line, delimiter after loss, final prompt without LF | Q73: skip truncated line until delimiter; visibly explain missed prompt possibility |
| M34 | Continue followed immediately by another overflow; UserPause and recording failure also present | Q71: cutoff-specific acknowledgement, new incident, independent holds and no command replay |
| M35 | Finite timer expires around receive/pause/result admission | Q74: cutoff input and committed-result rules; no late-input revival |
| M36 | Indefinite wait and Sleep/finite wait while system clock/timezone changes | Only local remaining budgets freeze; elapsed monotonic; Instant offset snapshot stable |
| M37 | Receive EOF with retained match, final fragment and no match, including zero-timeout | Match if eligible; prompt-only final fragment; EndOfInput takes precedence over timeout; no fake newline |
| M38 | Disconnect, alternate-screen entry/exit in one batch or early SSH startup, 1048, Stop in catch loop | Uncatchable required termination even before Connect returns; 1048 alone does not stop; healthy connection preserved where applicable |
| M39 | Dialog every button set with input on/off, accepted empty text, Esc/X, Stop | Exact Button/HasText/Text/default behavior; Stop is not Cancel result |
| M40 | Dialog movement/minimized host/deferred Minimize competing with another run | Preserve request, restore prior state, reevaluate current host, no focus guarantee or other-run freeze |
| M41 | Pause during Connect/trust/unlock; Stop one of multiple unlock waiters | Q70: current attempt can finish; continuation held; other waiters/unlock state preserved |
| M42 | Close before Connect, during connected execution and while already paused | Q75: one combined confirmation, close-review hold, cancellation preserves unrelated holds; tab movement not closure |
| M43 | Manual recording Pause vs recording failure; Retry while history loss remains | Deliberate pause independent; failure holds macro; no backfill; explicit Resume cannot acknowledge loss |
| M44 | Macro input ownership with broadcast checked targets and frozen operations | Preserve ordinary checks/exclude new eligibility; no mutation of frozen recipients; restore later eligibility only |
| M45 | AI read-only sharing starts/ends on macro tab; AI attempts control while paused | Apply accepted AI-specific OFF/disabled rule; no automatic control grant on macro Stop |
| M46 | Macro ends while healthy SFTP work runs; actual disconnect/tab close | Independent transfer survives macro end; normal disconnect/close cancels it |
| M47 | Same basename concurrent runs, illegal basename chars, empty name, collision, partial group failure | Distinct grouped names; no overwrite; visible retry/new parts; no secret/argument filename content |
| M48 | EN/JA/system UI and Light/Dark/system changes during run | Stable script identifiers/semantics, localized state/errors, readable accessible controls |
| M49 | Repeated stop/restart while regex/host work remains in flight | Old work remains charged and cannot write into new run; new admission bounded |
| M50 | Telnet/serial first password send and remote echo | Q77: persistent nonblocking notice; no invented profile approval or per-send confirmation |

## Performance and responsiveness validation plan

Use the existing Windows 11 x64 reference machine/workload from
[resources and validation](../specification/resources-and-validation.md). Create
its 20 sessions with 16 launched by distinct macros and four by ordinary profile
opening; never attach a macro to an existing session. Exercise pure computation,
simple prompt matching, finite/indefinite waits and paused/dialog states. Run
with selected recordings, background/minimized tabs, tab moves, concurrent SFTP,
and adversarial-but-bounded regex patterns. Keep the original terminal/recording
integrity criteria; do not improve macro results by dropping terminal/log data.

Measure p95 local Pause/Stop command acknowledgement <=100 ms normally and
<=250 ms under the existing overload workload. Also measure time to interpreter
quiescence for pure loops, time held by regex/host work, send cleanup and remote
effects separately. A UI acknowledgement target is not a promise of wire-level
Stop completion, instantaneous regex interruption or stopping a remote command.
Report maximum/tail delays as well as p95; resolve unexplained stalls before
claiming readiness. Initial values must be tested, not silently relaxed.

For each resource bound, exercise just-below/exact/just-above cases, failure
before effects, ownership after cancellation, and unaffected sessions. Verify
actual memory/work against the accounting model, including abandoned work,
regex engine allocations and secrets across managed/native boundaries.
After implementation is explicitly requested, use deterministic transcript,
clock, send, vault and recording fault seams for these cases, plus end-to-end
validation against independent SSH/Telnet/serial peers. This document itself
does not implement those seams or run a prototype.

## Decision traceability

| Accepted decisions | Draft coverage |
| --- | --- |
| Q1-Q4, Q10-Q11, Q23-Q25, Q35-Q37, Q47-Q51, Q62-Q64 | Behavior purpose/loading; language grammar/types/functions/errors; M01-M11, M38, M49 |
| Q5-Q7, Q12, Q26, Q38, Q59-Q61 | Connection/path/credential contracts, metadata and credential API; M12-M19 |
| Q8, Q19, Q32-Q34, Q57, Q66 | Recording failure/intentional pause, bounded-history recovery, names; M14, M32-M34, M43, M47 |
| Q9, Q20-Q22, Q27-Q28, Q42-Q44, Q54-Q55, Q67 | Receive filtering/history/waits/EOF, regex and matching budgets; M15, M25-M38 |
| Q13-Q16, Q29-Q31, Q41, Q45-Q46, Q53, Q58, Q65 | Run/input/window lifecycle, safe sending, integration; M16, M20-M24, M38-M46 |
| Q17, Q39-Q40, Q48-Q49, Q52, Q56, Q68 | Date/numeric/string/argument/dialog APIs and invariant semantics; M06-M09, M36, M39-M40, M48 |
| Q18, Q51, Q69 | Error model and routine draft authority; acceptance matrix and final review gates |

Accepted Q70-Q77 refine the corresponding lifecycle, receive and secret-send
behaviors in M41-M50. Every runtime matrix row is a future verification requirement,
not evidence that a macro implementation or test suite exists.

## Final review and handoff gates

Draft checks performed on 2026-11-14: independent read-only language, receive
and lifecycle consistency reviews; corrections for cancellation examples,
factory error timing, current local echo, partial-log handle cleanup, aggregate
matching work and emergency capacity; local-link/anchor/code-fence/control-byte
checks across the eight affected documents. All 50 acceptance IDs are unique,
and Q70-Q77 have since been accepted in full. These are document checks only. No macro
implementation, runtime test, benchmark or security-validation result is claimed.

Completed on 2026-11-14: Q1-Q77 reconciliation; language/API/budget and
cross-feature checks; final user confirmation of the complete specification and
primary testing seam. The specification is ready for implementation handoff and
published as [issue #3](https://github.com/TabTty/TabTty/issues/3) with
ready-for-agent; the published body and label were verified against the local
publication document. Keep initial-release scope unchanged unless
explicitly amended, and wait for a separate implementation request.

At implementation start and before release, recheck affected primary security
and platform guidance and perform the required runtime/security validation.

## Primary references and deliberate language choices

- [C# numeric conversions](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/numeric-conversions)
  informs long/double behavior; TabTty deliberately turns non-finite double
  results into errors rather than inheriting every CLR arithmetic behavior.
- [Microsoft regex options](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options)
  and [MatchTimeout](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.matchtimeout?view=net-10.0)
  support the chosen feature set and approximate timeout distinction. Pin the
  version-1 dialect to the app's selected .NET 10 baseline.
- [Microsoft cooperative cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads)
  motivates explicit interpreter checkpoints; passing a token is not a forced
  stop guarantee for code that never observes it.
- [OWASP secrets in memory](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#25-handling-secrets-in-memory)
  and the repository's authentication/vault contracts retain short plaintext
  use lifetimes. A reusable profile reference does not imply a reusable plaintext
  authentication cache or guaranteed erasure of every process/OS copy.

These references were checked during the interview on 2026-11-14. The explicit
TabTty grammar, budgets and failure outcomes are accepted product choices,
not claims that Microsoft/OWASP mandates this macro design.
