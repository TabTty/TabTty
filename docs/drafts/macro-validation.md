# Macro budgets and acceptance cases: accepted contract

Drafted under Q69 and confirmed in final review on 2026-11-14. Read the
[behavior](macro-specification.md) and [language/API](macro-language.md) contracts.
Values below are accepted initial engineering
guards, not measured capacity, completed tests or implementation authorization.
Q70-Q77 were accepted in full on 2026-11-14; their cases are now required.
The 2026-11-20 shared macro constants decisions Q78–Q88 add launch budgets
and acceptance cases M62–M73.
The existing terminal, input, log, SFTP and AI budgets remain separate
and binding; macro admission cannot borrow their reserved capacity silently.

## Confirmed primary testing seam

Use one primary feature seam: starting and controlling a macro run through the
same application-facing run entry point used by Run Macro/file activation.
Supply source, constants and profile files and ordinary arguments, drive received payload and
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
satisfying the invoked to-spec skill. It does not authorize implementation or testing code. The
matrix below describes the observable contract to verify once implementation is
explicitly requested.

## Admission and accounting

| Resource | Accepted initial bound |
| --- | --- |
| Source file | 1 MiB encoded bytes; 1,048,576 decoded UTF-16 units; reject oversized source before unbounded allocation |
| Constants declarations | Up to 16 per macro. Each alias counts, including a second alias for the same file. |
| Constants files | Up to 1 MiB of encoded bytes per file and 1 MiB total across distinct normalized dependency paths per launch, in addition to the macro source allowance. Count BOMs and all whitespace. |
| Shared macro constants | Up to 4,096 definitions total across those distinct paths, including unused definitions. |
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
Charge the source and constants from the start of each load. Apply the 32 MiB
per-launch limit and 256 MiB application total during decoding, validation
and pending review as well as execution. Include captured bytes, decoded text,
keys and typed values, parser buffers and review copies. Reuse one captured
snapshot per normalized dependency path within a launch. Two aliases for that
path share its byte and member allowances. Reserve capacity before reading or
materializing data and check variable-length input as it arrives. A failed
read, rejected declaration, cancelled review or completed run releases its
storage when the last owner finishes.
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

Apply the shared [recording contract](receive-logging-issue.md#log-filename-template-redesign)
to macro `Connect`. With a fixed name, verify that `error` preserves existing
files and fails the connection before transport, `overwrite` replaces the
contents and `append` preserves the original bytes and adds the new recording
at the end. Repeat with one selected file missing and confirm it is created
when preparation succeeds. Exercise an unavailable path and a file held by
another writer. Verify that the profile selects the formats and that the
macro's relative directory uses its captured working directory. On resume
and retry, use the policy captured at connection start and verify the shared
recording start failure rule. Preserve the source `FileName` and `BaseName`
values when sanitizing substitutions in the filename template.

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
| M04a | Compute source metadata for `inspect.tttym`, `inspect.prod.tttym`, `.macro`, and `.macro.tttym` at the loaded-source boundary. Validate source referencing `Macro.Extension`. | Basenames are `inspect`, `inspect.prod`, `.macro`, and `.macro`. The removed member is rejected during source validation. |
| M04b | On Windows, call the three file path part functions with the language contract examples, empty input, root-only paths, repeated trailing separators, and a relative path containing `..` before and after a run-base change. For any future host that recognizes only `/`, also exercise `dir/` and `dir\`. | The three results match the [file path parts contract](macro-language.md#file-path-parts). Windows recognizes both separators. On a `/`-only host, `dir/` has an empty base name and extension, and `dir\` keeps the backslash in its base name. Roots are preserved as directory portions where applicable. `..` stays in the supplied spelling and results are independent of the run base. A nonexistent or remote path can be split without file access or a prompt. |
| M05 | `var x=1` reassigned string/double; explicit long/double/list conversions | Fixed inference, no dynamic fallback or list covariance |
| M06 | Min long literal, its negation/division, signed remainder, zero double division, finite underflow | Exact documented outcomes; no wrapping or NaN/infinity propagation |
| M07 | Mixed comparison at 2^53; double-to-long at -2^63/+2^63 | Rounding and exclusive upper bound as documented |
| M08 | Well-typed `1/0` in try; same expression in an unexecuted branch | Catchable when evaluated; no optimization-dependent source failure |
| M09 | In ordinary and interpolated ordinary text and shared constants, read `\x1BD`, `\x01BD`, `\x1bD`, and `\u01BD`. Try `\x`, `\x1`, `\xG1`, and `\x1G`. In an ordinary directive path use `"shared\x2Etttyc"` to select an existing `shared.tttyc`. Compare verbatim text and paths. | The first three strings decode to ESC + D, U+0001 + BD, and ESC + D. The Unicode escape gives U+01BD. TabTty and VS Code locate each invalid escape at its backslash, including in an ordinary directive before file access. Verbatim text preserves the backslash. Submitting ESC + D on a UTF-8 session yields bytes 1B 44; other encodings use the session encoder. Verify UTF-16 indexing and strict boundary rejection of unpaired surrogates. |
| M10 | List aliases, Clone, element replacement/Add in foreach through helper | Aliases visible; clone independent; mutation detected before next iteration |
| M11 | Short-circuit with an effectful helper; for continue; helper local/top-level visibility | Correct evaluation order and scopes; source validation still covers helper |
| M12 | Relative profile path after several relative base changes/reset | Correct per-run base; no process-CWD, other-run or log-directory change |
| M13 | Macro resolves an unregistered profile that needs review. Exercise Connect once, Trust this version and connect, and Cancel. | Both connect choices use the reviewed snapshot and leave the profile unregistered. Only Trust this version and connect remembers the location and version for later connections. Cancel starts neither recording nor transport and uses no credential. |
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
| M47 | Concurrent runs using a serial template, illegal basename characters, empty name, fixed-name collision, partial group failure | Serial allocation yields distinct grouped names. Fixed names follow the effective collision policy. Group failures and retry outcomes are visible. Names omit secrets and invocation arguments. |
| M48 | EN/JA/system UI and Light/Dark/system changes during run | Stable script identifiers/semantics, localized state/errors, readable accessible controls |
| M49 | Repeated stop/restart while regex/host work remains in flight | Old work remains charged and cannot write into new run; new admission bounded |
| M50 | Telnet/serial first password send and remote echo | Q77: the persistent nonblocking notice requires no per-send confirmation. Profile trust was checked at connection time. |
| M51 | An unseen source and argument pair is opened through the picker, Explorer, CLI or a forwarded request | Review shows the full source digest, inspectable snapshot, path and unambiguous arguments. Cancel is the default. Closing or cancelling creates no run, tab or decision. Run once starts one run and the next launch asks again. |
| M52 | Always allow, sign out and back in, then open an identical copy from another directory through another entry point | The same ordered arguments reuse the macro allow. New arguments require review. The run sees its actual location and arguments. A different profile selected by a relative path is checked against its own location and version, with the ordinary host-key and vault checks. |
| M53 | Keep the arguments fixed and replace a file at the same path, change just its BOM or line endings, or replace it while a review is open | Each unseen pair requires review. An approved run executes the bytes displayed for that review. Restoring the original bytes and arguments reuses their decision. |
| M54 | Allow one argument list and block another for the same source. Open a renamed copy through the picker, Explorer, CLI and forwarding with the applicable list. Forget one decision in another window. | Each launch honors its own pair. A block ends the request without a run, tab or override prompt. Forgetting either entry restores review for that pair alone. A stale dialog cannot undo the change. |
| M55 | Read a decision after sign-in, under another account, and on a second PC with copied settings, macro and portable vault | Only the original account on the original PC reuses the decision for the pair. Neither credential target names nor records contain raw argument values or passwords. |
| M56 | Look up a pair with only a content-only record present, or with a missing, malformed, incompatible or unreadable pair record. Fail a decision write. | A missing pair record requires review even if a content-only decision exists. An invalid pair record or a read or write error stops launch with a visible error. A cached allow or sidecar file cannot supply the decision. |
| M57 | An external launch refers to a UNC share, a mapped drive, or a local-looking path redirected to a remote file during opening | No remote contact before explicit read permission, including during classification. Cancel leaves the source untouched. After read consent, the source and arguments select the usual allow, block or review outcome. |
| M58 | A remembered allow or block is opened in an elevated instance, a request crosses integrity levels, or forwarding reaches an older receiver | Elevated execution needs an explicit local decision for that run. A block still prevents it. Forwarding across integrity levels or to a receiver without this policy starts no run. |
| M59 | Compare `[]`, `[""]`, `["a b"]`, `["a","b"]` and `["b","a"]`. Vary case, whitespace and Unicode normalization. Try different CLI quoting that parses to the same strings. | Each distinct ordered list has its own decision. Identical parsed lists match across routes, including Explorer and CLI with zero arguments. |
| M60 | Edit the picker's arguments after a lookup, then execute. Have the running macro modify its own `Macro.Args`. | Admission uses the decision for the final edited list. A blocked pair cannot run and an unknown pair requires fresh review. Later mutation of the script's copy leaves the stored decision tied to the launch arguments. |
| M61 | Reset all macro decisions with both allowed and blocked pairs, a launch review open in another window, an active connection, and trusted and registered profiles. | The next launch of each pair asks again. The old review cannot launch a run or restore a decision. The active connection continues. Profile trust and registrations are preserved. |
| M62 | Open a version-1 macro with no declarations using existing allow and block records. Add a declaration and open the new source in updated and older builds. | The original macro honors both existing decisions for their respective arguments. The updated build accepts `#constants` in version 1 and checks the combined inputs. The older version-1 grammar rejects the declaration before execution. |
| M63 | Use one UTF-8 macro with constants encoded as UTF-8 with and without BOM or BOM-marked UTF-16LE/BE and UTF-32LE/BE. Include Japanese text. Try malformed bytes and BOM-less UTF-16 declarations. | Each file uses its own encoding detection. All six valid files yield the same text. An invalid file prevents launch and the diagnostic identifies the offending dependency. |
| M64 | Declare string, Boolean, signed 64-bit integer endpoints, and floating-point literals using `var` and explicit scalar types. Try widening, mismatched types, overflow, missing initializers, interpolation, arithmetic, names, function calls, and a JSON object. | Declarations follow macro literal and assignment rules. The integer endpoints bind exactly as long. An exponent gives a double, including `1e0`. Each invalid file prevents launch. |
| M65 | Two macros declare the same file. A helper in one macro reads a member. Two different aliases have a member of the same name. Try assigning to a member, declaring a duplicate alias or referring to a missing or mistyped member in an unused helper. | Every macro and helper sees the typed value from the named alias. Each invalid source fails static checking before a run starts. |
| M66 | A macro reads only `Common.Prompt`. Add an unused valid declaration, then try a duplicate name, a malformed trailing declaration and a missing second dependency. | All valid definitions are accepted. Each failure prevents creation of a run or tab and all macro effects. Diagnostics identify the file and, for syntax or definition errors, the location. |
| M67 | Launch from another working directory, move a macro and its relative dependency together, and declare a separate absolute path. Use two aliases for the same normalized path. Change the run's base directory in its first statement. | Relative declarations resolve at load time from the macro source directory. Each alias of one normalized path exposes the same captured values. Absolute declarations continue to select their files. |
| M68 | Launch a local source declaring a UNC, mapped-drive or redirected dependency through the picker, CLI, Explorer or forwarding. Try cancelling read permission and consenting with a stored execution decision. | Before any possible remote access the user can inspect the affected path and cancel. Cancellation leaves the remote file untouched and ends the launch. After consent, the captured source, dependencies and arguments select the execution decision. |
| M69 | Exercise exact and just-over declaration, distinct-byte and definition limits, with repeated paths, two individually valid files exceeding the aggregate, and several reviews held open. | A valid set at each limit can launch when memory is available. Excess inputs are rejected before effects. Repeated aliases count as declarations and share one file snapshot. Pending loads and reviews respect per-launch and application memory limits. Cancelling makes their released capacity available to later launches. |
| M70 | Edit or replace a constants file during execution review and again during an admitted run. Delete the file before accepting a pending review. | Review and execution use the same captured data, including after deletion. The next launch reads the then-current files and a missing file prevents that launch. |
| M71 | Hold source and arguments fixed. Edit an unused constant, whitespace, a BOM, or line endings. Swap different contents between two declared files, then restore the original bytes. Also open a byte-identical copy of the whole set in another folder. | Each previously unseen combination goes to review. Swapping the two contents changes the combination. Restoring the inputs or opening the identical copy reuses the decision for those bytes and arguments. |
| M72 | Allow one constants version and block another for the same macro and arguments. Inspect the review, delete the files, and look up those arguments in Settings. | Review shows each alias, resolved path and captured contents. Settings distinguishes the two versions and can forget either decision by itself. Recreating the corresponding files gives review for the forgotten version and honors the decision for the other. |
| M73 | Launch with a constants declaration when only a legacy source-and-arguments decision exists. Exercise a failed trust write and a reset across both decision formats while a dependency review is open. | Approval requires the exact dependency combination. A failed write stops launch. Reset clears both formats and invalidates the pending review across windows. Subsequent launches ask again. Decision records omit source text, paths, argument values and constants values. |

## Macro syntax acceptance

The following additional cases cover the [macro syntax specification](https://github.com/TabTty/TabTty/issues/103).
They define acceptance in the macro-enabled build.

| Issue | Author action and observation |
| --- | --- |
| [#104](https://github.com/TabTty/TabTty/issues/104) | Admit an old completed `OK`. Start `WriteLine("status", markReceiveBoundary: true)`, admit a fresh identical line while submission is in progress, then start `WaitLine(Literal("OK"), 0)`. Only the fresh line satisfies it. A proven zero-byte rejection leaves the old line eligible in a subsequent catch; an uncertain partial send ends the run with no retry. Exercise a prompt and an ordinary startup-history wait. Both checkers accept the Boolean option and flag a string value at that argument. Editor completion offers the option. |
| [#105](https://github.com/TabTty/TabTty/issues/105) | Run the IsReady helper in the [rethrow example](macro-language.md#rethrowing-a-caught-error) with a timeout and with EndOfInput. Verify the outer catch and the uncaught display use the original wait location. Inside nested catches verify the inner error, then the outer one after the inner catch completes. Both checkers accept a returning/rethrowing helper and reject a bare throw in an unused helper outside its own catch. |
| [#106](https://github.com/TabTty/TabTty/issues/106) | Exercise the [recording option table](macro-language.md#connection-recording-options) with a fixed filename. Cover all three explicit policies, a policy-only call, blank naming fields, each nonblank override on its own, and an override equal to the profile value. Confirm the inherited or explicit policy through the files and the connection outcome. Both the launch checker and VS Code flag `allowLogOverwrite` or a misspelling as unknown, a duplicate at its second name and a Boolean `logCollision` at its value. Completion offers each unused option with its string type. At execution, pass `"append"` as a literal and through a variable. Pass an unsupported string, a blank, `"Append"` and `"append "` as literals and computed values. Each invalid value raises catchable `InvalidArgument` before any recording file opens or transport starts. |
| [#107](https://github.com/TabTty/TabTty/issues/107) | Run the [named waits](macro-language.md#named-wait-alternatives) with the same two distinct prompts before and after prepending a third candidate. Both original branches retain their names. Use two matches at the same start to select the first label, then swap them and check the new winner. Check unfinished versus completed lines. At launch and in VS Code underline the second name in `[ok: Literal("a"), ok: Literal("b")]` as DuplicateAlternative. |
| [#108](https://github.com/TabTty/TabTty/issues/108) | With two index helpers that each record one call, run `rows[Outer()][Inner()] += 3;` and observe the changed element through its original alias. Check outer then inner evaluation, invalid outer with no inner/RHS call, and invalid inner with no write. Each bounds error identifies its own bracket. Both checkers reject string writes, fresh result receivers and incompatible RHS types, including in unused functions. Compare writes to Macro.Args, a saved copy of capture text and the original match. |
| [#109](https://github.com/TabTty/TabTty/issues/109) | Exercise M09 through launch validation, VS Code diagnostics and session submission, including an ordinary directive path and its verbatim counterpart. |

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
| Q78–Q88 | Shared macro constants in version 1, full-file validation, per-launch snapshots, execution review and individual decisions for each version in Settings. M62–M73. |

Accepted Q70-Q77 refine the corresponding lifecycle, receive and secret-send
behaviors in M41-M50. Every runtime matrix row is a future verification requirement,
not evidence that a macro implementation or test suite exists.

## Final review and handoff gates

Draft checks performed on 2026-11-14: independent read-only language, receive
and lifecycle consistency reviews; corrections for cancellation examples,
factory error timing, current local echo, partial-log handle cleanup, aggregate
matching work and emergency capacity; local-link/anchor/code-fence/control-byte
checks across the eight affected documents. The original 50 acceptance IDs are unique,
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
