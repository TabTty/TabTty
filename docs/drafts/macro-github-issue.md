## Problem Statement

TabTty users need to repeat terminal workflows reliably: open a saved connection profile, recognize a prompt or line of received text, assemble and submit commands, reuse a configured password, wait, and ask the person for input or a decision. Manual repetition is slow and error-prone. Screen scraping and unrestricted C# execution would make receive semantics, cancellation, credential lifetime, and recovery difficult to reason about.

The workflow must remain controllable while a remote command is running, receiving output, or waiting indefinitely. A pause must preserve progress without replaying a command, and a recording failure or lost receive history must be visible before automation continues.

## Solution

The [shared macro constants follow-up](macro-issue.md#shared-macro-constants-follow-up)
extends version 1 after the original publication. Q78–Q88 specify external
read-only definitions and include their captured file contents in macro trust.
The [language](macro-language.md), [launch](macro-specification.md) and
[validation](macro-validation.md) contracts describe the extension.

The [current macro language](macro-language.md#session-paths-input-waits-and-window)
specifies name-based password submission. The original proposal below records
the former UUID-based interface.

Add attended automation using trusted, externally authored `.tttym` source files and a defined, versioned C#-style language. Each invocation creates one new macro run and terminal tab, can explicitly attempt one saved-profile connection, and shares TabTty's existing session, recording, authentication, and vault behavior.

Provide typed variables and lists, control flow, same-file helpers, string and time operations, line/prompt waits, text and reference-based password submission, optional-input dialogs, and host-window minimize/restore. Check the entire source before effects. Expose Pause, Resume, and Stop throughout execution, with bounded interpreter and matching work. Keep a healthy terminal session usable after the macro ends.

Q1-Q77 are accepted as amended, including all Q70-Q77 recommendations on 2026-11-14. The user confirmed the complete specification and primary testing seam on 2026-11-14 and authorized publication with ready-for-agent. The specification is ready to guide implementation when separately requested. This does not authorize implementation or add macros to the initial release; those repository constraints remain in force.

## User Stories

1. As an operator, I want to run a trusted macro file, so that I can repeat terminal tasks without manually entering each step.
2. As an operator, I want each invocation to create a distinct run and tab, so that concurrent executions do not share variables or take over an existing terminal.
3. As a macro author, I want to connect through a saved profile file without registering it, so that automation does not alter my registered-profile list.
4. As an operator, I want ordinary host-trust and authentication interactions to remain available, so that automation respects my connection decisions.
5. As a macro author, I want one explicit connection attempt per run, so that reconnection cannot silently replay earlier steps.
6. As a macro author, I want relative paths to begin at the macro directory, so that I can move an automation folder without rewriting every profile path.
7. As a macro author, I want to change and reset the run-local base directory, so that path resolution is explicit and independent of other runs.
8. As an operator, I want the application-selected vault to be the only vault selection, so that macros use the same credential context as the rest of TabTty.
9. As a macro author, I want a convenient operation for the profile's configured saved password, so that I do not need to copy its reference into every script.
10. As a macro author, I want that password identity to remain independent of authentication success or method, so that a failed or key-authenticated connection does not change its meaning.
11. As an operator, I want later password sends to retrieve the current saved entry, so that an intentional credential update takes effect without caching an old authentication value.
12. As a macro author, I want explicit credential references for other saved passwords, so that terminal-level login can select the intended entry precisely.
13. As an operator, I want to copy a complete non-secret macro reference from vault management, so that I can use saved credentials without exposing plaintext in the editor.
14. As an operator, I want missing, deleted, stale, or wrong-vault references to fail visibly, so that automation never guesses another credential.
15. As an operator, I want password submission to avoid local echo and automatic diagnostic copies, so that TabTty does not create unnecessary plaintext exposure.
16. As an operator, I want an unobtrusive notice for Telnet/serial password submission, so that I understand transport and remote-echo exposure without repeated approvals.
17. As a macro author, I want familiar C#-style conditionals and loops, so that I can express repeatable workflows clearly.
18. As a macro author, I want initialization-based fixed type inference, so that accidental reassignment to an unrelated type is caught before execution.
19. As a macro author, I want only long and double numeric types with defined conversions, so that arithmetic is simple and predictable.
20. As a macro author, I want strings, interpolation, and homogeneous lists, so that I can build commands and iterate over ordinary data.
21. As a macro author, I want same-file typed helper functions, so that repeated logic can be named and reused without imports or unrestricted runtime access.
22. As a macro author, I want documented recoverable errors and try/catch, so that expected failures can be handled without suppressing Stop or unsafe transmission outcomes.
23. As an operator, I want full-source checks before effects, so that an invalid unused function or branch cannot be discovered only after commands have run.
24. As a macro author, I want a required language version, so that future changes cannot silently reinterpret old source.
25. As an operator, I want UTF-8 and BOM-marked UTF-16/UTF-32 source support, so that common editor encodings work without ambiguous legacy guessing.
26. As an operator, I want one validated source snapshot per run, so that external edits affect later invocations rather than changing active execution.
27. As a macro author, I want source path, filename, basename, and extension metadata, so that a macro can identify itself without accessing arbitrary files.
28. As a macro author, I want ordered ordinary string arguments, so that one source file can handle different non-secret inputs.
29. As a macro author, I want locale-independent number and time operations, so that the same macro behaves consistently with English and Japanese UI settings.
30. As a macro author, I want local and UTC time plus monotonic elapsed time, so that automation can create timestamps and measure progress.
31. As a macro author, I want millisecond sleep, so that I can deliberately pace commands when needed.
32. As a macro author, I want Write and WriteLine behavior, so that I can choose whether to append the profile's Enter ending.
33. As a macro author, I want explicit embedded newlines and control characters to retain their meaning, so that intentional terminal input is not silently rewritten.
34. As an operator, I want the entire send checked for encoding and size before payload submission, so that a locally invalid command is not partly sent.
35. As an operator, I want partial or uncertain delivery to end the macro, so that automatic recovery cannot accidentally duplicate a command.
36. As a macro author, I want fixed-string and regular-expression waits, so that I can recognize expected terminal responses.
37. As a macro author, I want completed-line waits and unfinished-prompt waits, so that newline-free prompts remain usable without treating every fragment as a complete line.
38. As a macro author, I want substring matching within one receive line, so that a response need not occupy the entire line.
39. As a macro author, I want received text independent of display edits and logging, so that matching does not depend on screen layout, local echo, or recording mode.
40. As a macro author, I want TAB preserved and CR/LF handling defined across chunks, so that text matching is predictable for streamed output.
41. As a macro author, I want early connection output and between-wait history retained, so that an immediately arriving prompt is not missed.
42. As a macro author, I want successful waits to consume through the match and preserve following text, so that subsequent steps can process remaining responses.
43. As a macro author, I want an explicit receive boundary before sending, so that I can exclude already-admitted text from new match starts.
44. As a macro author, I want anchors and lookbehind to retain the original intact line context, so that consumption does not invent a new line start.
45. As a macro author, I want alternative waits with deterministic selection and captures, so that one operation can distinguish success, failure, and other responses.
46. As a macro author, I want default response waits to be indefinite but cancellable, so that long-running operations need no arbitrary timeout.
47. As a macro author, I want optional finite response deadlines distinct from regex evaluation limits, so that slow remote work and excessive matching work have different outcomes.
48. As a macro author, I want committed matches to remain stable as more text arrives, so that a returned prompt result cannot later change.
49. As a macro author, I want receive EOF to search remaining eligible text and then report end-of-input, so that an indefinite wait cannot remain stuck after input ends.
50. As an operator, I want history exhaustion to pause automation with Continue or End, so that I decide whether missing output makes further execution inappropriate.
51. As an operator, I want receive and healthy logging to continue during history-loss recovery, so that the macro does not disrupt the terminal session.
52. As an operator, I want truncated lines excluded until a real delimiter, so that retained suffixes cannot falsely look like intact prompts.
53. As an operator, I want Pause at any time, including computation-only loops, so that I retain control without losing variables or history.
54. As an operator, I want an already-started send to finish before Paused, so that pause does not cut a logical command at an arbitrary point.
55. As an operator, I want an already-started connection and required authentication interactions to reach a result during pause, so that resuming does not restart authentication or retain plaintext unnecessarily.
56. As an operator, I want independent user, recording, and history-loss holds, so that resolving one reason cannot silently bypass another.
57. As an operator, I want local sleep and response budgets to freeze during pause, so that manual review does not consume their remaining time.
58. As an operator, I want Stop to end the run without sending an invented exit or control sequence, so that I can inspect the healthy remote session afterward.
59. As an operator, I want alternate-screen entry to stop the macro, so that unsupported full-screen interaction does not continue blindly.
60. As an operator, I want macro input ownership to persist while paused, so that manual, broadcast, or AI input cannot unexpectedly alter the remote workflow.
61. As an operator, I want browsing, copying, and application controls to remain usable, so that I can inspect output and stop or disconnect the run.
62. As a macro author, I want a run-local message/decision dialog with optional ordinary text input, so that the user can participate in the workflow.
63. As a macro author, I want cancellation distinguished from accepted empty text, so that a dismissed prompt cannot be mistaken for a valid answer.
64. As an operator, I want dialogs to follow moved tabs without losing input, so that organizing windows does not lose progress.
65. As a macro author, I want minimize and restore to target the current host window, so that window operations follow the run after a tab move.
66. As an operator, I want needed dialogs to restore a minimized host and defer conflicting macro minimization, so that the macro cannot hide a pending decision.
67. As an operator, I want close confirmation to include an active macro even before connection, so that closing a tab cannot silently discard an active workflow.
68. As an operator, I want macro-based recording names with unique session and part identifiers, so that concurrent runs remain distinguishable without overwriting files.
69. As an operator, I want initial recording failure to hold transport startup, so that a run does not begin unrecorded when its selected recording could not open.
70. As an operator, I want recording Retry followed by separate macro Resume, so that I can review the recording gap before automation continues.
71. As an operator, I want deliberate recording Pause independent of macro Pause, so that I can control those activities separately.
72. As an operator, I want healthy SFTP work to survive macro completion or Stop, so that ending automation does not cancel an independent transfer.
73. As an operator, I want broadcast and AI collaboration to respect macro ownership, so that combined features do not compete for terminal input.
74. As an operator, I want bounded memory, matching work, and concurrent runs, so that one macro cannot indefinitely block the UI or consume unlimited history.
75. As an operator, I want localized status and error UI with stable script identifiers, so that language and theme preferences do not change program semantics.
76. As a maintainer, I want feature tests driven through the macro-run entry point and existing session boundaries, so that tests protect observable behavior while allowing internal implementation changes.

## Implementation Decisions

**Scope and module boundaries**

- Use a TabTty-owned interpreter, as selected by ADR 0004, with a source loader/checker, run coordinator/evaluator, bounded receive matcher, and application host integration. These are responsibility boundaries, not a requirement to expose a public API for each internal component.
- Reuse the saved-profile loader, ordered terminal input owner, receive decoder/parser, recording owner, exact-reference credential resolver/shared unlock, and window/tab lifecycle. Preserve the accepted owned SSH protocol/session layer and OpenSSL cryptographic boundary. Follow the Microsoft/OpenSSL dependency policy; no scripting framework or third-party runtime is selected.
- Macros are trusted local source, not sandboxed untrusted code. Execution is attended in the visible application. No automatic host-trust acceptance or changed credential lifetime is introduced.
- One invocation owns one new tab/run, at most one connection attempt, one interpreter continuation, one in-progress macro operation, one receive cursor, and a set of independent holds. Second Connect is recoverable ConnectionAlreadyAttempted even after failure. Never attach to an existing session or automatically reconnect/restart.
- Provide Run Macro file selection, attended command-line activation, and an optional Windows-controlled `.tttym` association. CLI uses a macro option followed by one source-path argument; a double-hyphen delimiter introduces ordered ordinary string arguments. Preserve empty arguments, apply Windows argument parsing once, and never reinterpret them as a shell command. In-app arguments are separately editable items; file activation supplies none.
- Capture the invoking process directory to resolve the source path before forwarding activation. Forward an absolute source path and parsed arguments. Use existing most-recent-surviving-window routing and bounded application-modal activation queue. Macro dialogs are run-local rather than application-modal. No CLI exit-result protocol is promised.

**Source, language, and value semantics**

- The first non-comment/non-whitespace item is the version directive `#tttym 1`, followed by a line ending or EOF. Exactly one directive is required. Support line comments and non-nested block comments. Strictly decode UTF-8 with/without BOM and BOM-marked UTF-16LE/BE or UTF-32LE/BE; longest BOM first, no BOM means UTF-8. Reject malformed/unsupported source without replacement or guessing.
- Read and validate one bounded source snapshot. Detect incomplete/changed reads where possible, without promising an atomic snapshot against arbitrary concurrent in-place writes. Later edits affect later runs. Validate syntax, all branches/functions, types, names, allowed members, call graph, and admission before connection, send, macro-dialog, or macro-window effects. Diagnostics use one-based lines/UTF-16 columns with CRLF counted once and BOM excluded.
- Use case-sensitive ASCII identifiers and Unicode comments/strings. Admit lexical blocks, initialized declarations, assignments, calls, if/else, for, foreach, while, break, continue, return, and try/catch. Require braces on control-flow bodies and semicolons on simple statements. Top-level return without a value completes the run. For-continue executes the iteration clause; break/continue cannot cross a function boundary.
- Types are bool, signed 64-bit long, finite binary64 double, string, homogeneous List of a supported value type, and the documented builtin value/enumeration types. Void is return-only. Var infers once from its initializer; later assignment, arguments, returns, and list writes must be assignable. No object, dynamic, null, nullable, list covariance, or automatic string-to-number conversion.
- A mixed numeric list literal promotes to double; an existing list of long never converts implicitly to a list of double. An empty list literal requires declaration/parameter/return context. Parameters pass values, with list values sharing the list identity. Clone makes a shallow independent list. Foreach accepts lists, has a read-only iterator, and detects revision changes before the next step, including aliased element replacement. Changing a nested list does not mutate its outer list revision.
- Named typed helpers are file-scoped, may be called before declaration, and cannot capture top-level locals implicitly. Require all non-void paths to return an assignable value; do not assume loops prove return. No recursion, user overloads, optional/named parameters, ref/out, first-class functions, imports, classes, lambdas, tasks, or threads. Reject even unused call cycles/depth excess. Duplicate or shadowed visible local/parameter names are invalid.
- Evaluate operands and call arguments left-to-right; Boolean AND/OR short-circuit and require bool. Use ordinary arithmetic, comparisons, equality, unary sign/not, and explicit numeric casts. Assignment/compound assignment/postfix increment or decrement are statements or for clauses, not value expressions. Index lvalues are direct local/parameter list indexes and are evaluated once. Compound assignments do not implicitly narrow. No loose equality/truthiness, ternary, bitwise/shift, prefix increment, assignment chains, switch, do/while, goto, or finally.
- Arithmetic/comparison promote mixed long/double operands to double, accepting precision loss. Long-only comparison is exact; integer division truncates toward zero and remainder follows the dividend sign. Arithmetic overflow, division/remainder by either numeric zero, and non-finite double results are errors. Finite underflow may become zero. Explicit double-to-long conversion requires a finite value in the interval from minus 2^63 inclusive to plus 2^63 exclusive, then truncates toward zero. Long minimum divided by minus one overflows; its remainder by minus one is zero.
- Decimal integer literals allow optional L suffix and separators between digits. Fraction/exponent or D-suffix literals select double; decimal points require digits on both sides. No hex/binary numeric literals. Direct unary minus can express long minimum; an otherwise out-of-range positive literal is invalid. Source-invalid literals fail preflight, while well-typed arithmetic/cast/parsing value failures occur when evaluated even for constants.
- Support ordinary, verbatim, interpolated, and interpolated-verbatim strings with C#-style documented control/hex/Unicode escapes and doubled braces. No char or raw triple-quoted literals. Ordinary strings reject physical newlines; verbatim strings preserve them. Interpolation accepts string, bool, numbers, Instant, and documented enums using invariant text, not arbitrary objects/lists/credentials. Explicit formatting functions replace alignment/format interpolation syntax.
- Strings are immutable UTF-16 sequences; length, indexes, substrings, and match coordinates use code units. String index returns a one-unit string. Reject unpaired surrogates at send/path/UI boundaries. Equality and default searches are ordinal; optional string ignore-case is ordinal ignore-case. Do not normalize or implicitly trim. String addition requires two strings; numeric/string mixing uses explicit formatting or interpolation.
- Expose bounded string operations for length, substring, contains/prefix/suffix/index search, literal replacement/split, join, ASCII-space/TAB/CR/LF trim, and invariant case mapping. Replace and Split require a nonempty literal; Split preserves empty items. Lists support count, add, insert, remove, clear, shallow clone, and indexing, with normal range/type checks.
- ParseLong accepts an optional sign and decimal digits. ParseDouble additionally permits fractional and signed exponent parts with required digits. No implicit whitespace, thousands separators, literal suffixes/separators, NaN, or infinity. FormatNumber supports long G, D with precision 1-19, X with precision 1-16; double G/R, F/E with precision 0-15. Formats are case-sensitive and use invariant .NET 10 semantics. Default interpolation uses decimal long and round-trip double.
- Expose Now/UtcNow, FormatTime, UnixMilliseconds, and ElapsedMilliseconds. Instant captures its offset; default text is ISO date/time with seven fractional digits and explicit offset, including zero offset for UTC. Explicit formats support year/month/day/hour/minute/second, one to seven fractional digits, offset, quoted literals, and ASCII punctuation/space. No date parsing/calendar arithmetic/timezone database. Unix milliseconds floor toward negative infinity; elapsed uses monotonic time from admission including pauses. Clock formatting does not promise hardware precision.
- Capture immutable Macro metadata for absolute normalized source path, filename including extension, basename with final extension removed, final extension including dot, and non-secret unique RunId. Do not promise physical symlink resolution. Args is a run-local mutable list copied from invocation. File rename, tab movement, and base changes do not rewrite captured metadata.

**Connection, paths, sending, and credentials**

- Connect resolves its profile path against a run-local base initially equal to the source directory. SetBaseDirectory resolves a relative replacement against the current base; ResetBaseDirectory restores the captured source directory; BaseDirectory returns the current absolute base. Normalize relative segments without environment-variable/tilde expansion or process-directory mutation. Existing Windows absolute/UNC/long-path behavior applies.
- Load the saved profile without registration, rewriting, or silently adding editor-level validation to ordinary connection. Capture immutable session settings/reference identity; profile-relative recording destinations remain relative to the profile directory. Startup retries for recording use this snapshot, not an edited profile.
- Attach macro receive and alternate-screen observation before transport startup can deliver data. SSH readiness means shell/PTY startup completion; Telnet means TCP ready, not logged in; serial means opened/configured. Only ready sessions can receive macro sends. Startup text is retained for later waits. Session-dependent calls, including MarkReceiveBoundary, report NotConnected before readiness.
- Prepare all selected recording files before transport starts. Failure enters the recording hold; close handles for a partially opened group and retain any created empty files. Retry uses new parts and the same snapshot; successful Retry still needs explicit macro Resume. Changing profile settings requires a new run.
- Write submits exact text; WriteLine appends one captured profile Enter ending even if text already ends in a newline. Preserve embedded CR/LF/TAB/escaped controls. Use captured transmit encoding and existing live stateful codec/Telnet conversions; do not introduce raw-packet, key-simulation, or bracketed-paste semantics. No implicit send pacing; Sleep is explicit.
- Preflight the entire logical send with a clone of the current encoder state, including the appended ending and required closure, before payload commitment. Encoding/size rejection sends no prefix and does not alter live codec state. Submit through the existing ordered input owner. Successful submission is not proof of remote execution/login. Ordinary text uses current effective local echo; password operations, including appended ending, never generate local echo.
- Pause permits an already-started logical send to finish and displays Pause requested meanwhile. Stop cancels unsent input at existing safe character/codec boundaries; release input ownership only after required cleanup. Never synthesize Ctrl+C, Escape, newline, exit, or disconnect merely because a healthy macro ends.
- A send failure proven to have submitted zero payload is catchable, using a specific error or OperationUnavailable. Possible prefix/uncertain delivery is uncatchable macro termination, without automatic retry. Preserve a healthy session after cleanup; transport failure still follows normal disconnect rules.
- The sole application-selected vault supplies all credentials. No macro vault path/override, simultaneous hidden vault, fallback by name, or macro vault-management API exists. CredentialRef contains exact vault UUID, entry UUID, and kind. Credential creates a validated canonical identity without unlocking/retrieving secrets. Copy Macro Reference emits a complete non-secret reference expression from management UI.
- WritePassword and WritePasswordLine accept either no credential argument or an explicit CredentialRef. Without an argument they use the saved-password reference captured from the profile loaded for the connection attempt, regardless of failed authentication, selected authentication method, or manual correction. Public-key authentication can succeed while this reference remains the default. Connection failure does not create a usable send path.
- Resolve the current entry value immediately before each password use. In-app updates affect later sends; profile-file edits do not retarget the run. Missing/deleted/wrong-kind/wrong-vault/stale entries and unlock cancellation fail visibly and recoverably. External stale-store handling follows the vault contract and requires application restart before further use; do not auto-reload or retain an old vault to satisfy a reference.
- Telnet/serial have no profile password binding, so omitted-reference calls report MissingProfilePassword; explicit password references can perform terminal login. Private keys, source-key passphrases, master passwords, keyboard-interactive answers, and corrected transient authentication input are never defaults or ordinary script strings.
- Hold references through waits/pauses, not prepared plaintext. If paused before commitment, release prepared secret buffers and retrieve again on Resume. Clear owned transient buffers on completion/error/cancellation without claiming erasure of every runtime/OS copy. Normal shared unlock remains application-wide until exit.
- Remote echo remains received data and may enter display, recordings, or explicitly shared AI output. Do not redact Raw recording or promise SSH encryption redacts output. Before the first explicit password send over Telnet/serial per run, display a persistent nonblocking notice that TabTty provides no transport encryption and remote echo may be recorded/shared. Execution continues, Stop remains available, and no per-send/profile approval store is added.

**Received text, waits, and bounded history**

- Observe decoded terminal payload without consuming it from display/recording. Exclude local echo, authentication/protocol diagnostics, SFTP, and UI data. Keep SSH DATA/stderr decoder/parser states independent and merge received units in existing session order; never concatenate Raw bytes and decode them again.
- Observe actual decoded text before screen edits. Remove terminal sequences and non-TAB controls other than delimiters. Sequence-generated display text, including REP, contributes nothing; cursor movement, erasure, backspace, wrapping, and charset effects do not reconstruct screen text. Preserve actual decoded TAB and literal characters.
- CRLF is one delimiter; LF and lone CR delimit lines. CR may complete a line immediately. Suppress its adjacent same-source LF across chunks; an intervening same-source unit breaks adjacency, whereas other-source activity does not. Delimiters are excluded from match text. Never invent a delimiter at EOF, chunk end, pause, send, resize, boundary marking, or decoder flush. Existing malformed-receive handling applies independently of strict source-file decoding.
- Literal/Regex factories return validated WaitPattern values when evaluated, for constant and dynamic arguments alike. Empty literal is invalid. Use invariant .NET 10 left-to-right regex with lookaround/backreferences, optional ignore-case, and supported inline i/m/n/s/x flags. Exclude RightToLeft, ECMAScript, unlimited evaluation, and a NonBacktracking switch. Factory syntax/value failures remain catchable; preflight must not evaluate factories and change catchability.
- WaitLine searches only lines with actual delimiters; WaitPrompt also searches the current unfinished line. Both allow one-line substrings. WaitAnyLine/WaitAnyPrompt snapshot an ordered list of candidates. Select earliest eligible matching line, earliest start position, then candidate order. Return zero-based alternative index, text, original-line index/length, and bounded group/capture snapshots. Single-candidate alternative index is zero.
- Groups include group zero and numeric order with .NET names; unmatched groups have no captures, empty value, index minus one, length zero, and Success false. A successful empty group differs from an unmatched group. Group value/index/length describe the last capture; Captures preserves engine order. Literal results contain only group zero. Returned lists are independent bounded copies.
- An actual overall zero-length regex match reports ZeroLengthMatch without consuming or silently skipping to another candidate. Positive matches may contain empty groups. Do not reject a pattern only because it could match empty text on some other input.
- Retain startup/between-wait history. Success consumes through match end including earlier unmatched text; failure/timeout does not consume. MarkReceiveBoundary advances the start floor to the admitted tail, without claiming causal linkage to a following command. Keep the original intact line as regex subject and allow retained prefix lookbehind; anchors retain their original meaning. Match bodies cannot start before the floor, and context never crosses gaps.
- Match immutable snapshots. Unstarted searches may be coalesced, so unfinished-prompt outcomes depend on available receive batches. Once committed, a result is never lengthened/retracted by future text. End anchors, lookahead, and greedy patterns describe the current subject and do not establish future stability.
- No-timeout wait overloads are indefinite. Explicit finite milliseconds are nonnegative; zero searches only already-admitted history without waiting for future input. Regex work remains bounded and cancellable even for zero timeout. Response deadlines do not replace profile/network/authentication deadlines. Sleep takes nonnegative milliseconds; zero yields and checks cancellation.
- At receive EOF, finalize existing decoders/parsers once in DATA-then-stderr order and search retained eligible text. The final fragment is eligible only for prompt waits. If no eligible match remains, return EndOfInput even for indefinite waits; admitted EOF wins over zero-timeout WaitTimeout. Later waits may consume retained matches first. EOF/exit status are not actual disconnect; actual disconnect permanently stops the run.
- If required history, line size/count, or context exceeds bounds, add HistoryLoss and request pause. Retain bounded recent tail with explicit gaps; receive/display/healthy recording continue. One unresolved incident accumulates subsequent loss rather than issuing a dialog per dropped unit. Continue acknowledges losses through the coordinator cutoff and clears only that incident; later new loss creates a new incident. End permanently stops the run.
- Never join text across a gap, replay a command, or treat a truncated suffix as an intact line. Skip the affected line through its next real delimiter in both wait modes. Explain to the user that this can miss a final newline-free prompt. A huge line alone is not a terminal/logger failure.

**Run coordination, dialogs, and other features**

- Display Running, Waiting, Pause requested, Paused, Stopping, and terminal outcomes with current operation/reasons. Waiting for data/sleep/dialog is not itself UserPause. Keep stable internal identifiers and English/Japanese resources. Honor application language/theme settings; script behavior never depends on localized labels.
- Pause preserves variables, receive history, and exclusive input; receive/display, logging, independent SFTP, wall clock, and monotonic elapsed continue. Freeze remaining local sleep/finite-response budgets only. Remote activity, connection deadlines, and OTP expiry are not frozen.
- An already-started Connect and necessary trust/authentication/unlock interaction can finish while continuation is held; accepting a dialog can still send authentication data. Do not begin the next macro operation until all holds clear. User Stop cancels only this run's connection/waiter, preserving other shared-unlock users. Do not retain entered plaintext solely to await Resume.
- Keep UserPause, RecordingFailure/RecordingReady, HistoryLoss, and CloseReview independent. Successful Retry changes RecordingFailure to RecordingReady. Explicit Resume clears UserPause and RecordingReady, never an unresolved failure/gap. History Continue only clears its acknowledged incident. Execution proceeds only when all holds clear; dialog answers do not clear unrelated holds.
- Committed matches/results/timeouts survive pause; Stop invalidates undelivered results. Reject stale uncommitted worker output after pause/cursor generation changes and reevaluate retained input on Resume. At a deadline, settle using a frozen cutoff of data admitted before expiry; later input cannot rescue an expired wait. A settled timeout is not revived by pause; freeze only unsettled remaining local budget.
- User Stop, actual disconnect, alternate-screen entry, uncertain send delivery, and hard interpreter-state exhaustion bypass catch. Other uncaught errors terminate with bounded non-secret source diagnostics. No automatic restart/replay. Healthy session/display/recording remain after success, Stop, or ordinary failure. A run that finishes before connecting leaves a visible result tab until closed.
- While owned, block human remote-input keys/text/paste/mouse, broadcast input, and AI control even during pause. Keep browse/select/copy/application controls/Pause/Stop/Disconnect and necessary protocol replies available. Preserve ownership through send cleanup; show Stopping and retain Disconnect.
- Alternate-screen entry for modes 47/1047/1049 permanently stops the macro even during pause/dialogs, early startup, or same-batch entry/exit. Mode 1048 alone does not. Preserve the healthy session and remote application without sending quit/control input. During early SSH startup this event stops continuation without invoking user-Stop's attempt-cancel path solely for the screen event; healthy startup may complete normally. Normal-screen full-screen applications are outside scope and not universally detectable.
- Dialog accepts inert title/message, OK/OKCancel/YesNo/YesNoCancel buttons, independently optional ordinary single-line text, and initial text that must be empty when input is disabled. Return Button/HasText/Text; accepted empty text has HasText true, absent/cancelled text does not. Accessing Text without a value is NoTextValue. Cancel, window close, and Escape return Cancel without text; Stop returns no normal answer. Default Cancel when offered, otherwise No for YesNo, otherwise OK.
- Title/input reject CR/LF/NUL and other C0 controls; message can contain line breaks. Tab navigates rather than inserting text. Invalid pasted input preserves the current field and shows localized validation, without silent rewriting. No markup execution, arbitrary button labels, secret/masked input, or secret return type.
- At most one dialog per run; other tabs/windows remain usable. Identify run and current host. Moving the tab moves/reparents the request preserving input. Restore a minimized host to its previous normal/maximized state when action is needed, without promising foreground focus. MinimizeWindow/RestoreWindow resolve the current host; Restore is a no-op unless minimized. Defer macro Minimize while that host has any pending user-action dialog, rechecking host/run before acting. User window controls remain available.
- Include active runs in the existing combined close confirmation even before transport creation, one confirmation per affected close scope. CloseReview holds new operations at a safe boundary; started sends finish and started Connect follows its pause rule. Cancel close clears only CloseReview. Confirm stops the run and applies ordinary connection/SFTP cleanup. Moving a tab is not closure and adds no confirmation.
- Recording prefixes names with the captured macro basename, limited to 64 Unicode scalars. Apply the same application log-name choices and Text/Raw/Terminal format markers as for ordinary connections. Retry opens a new part under the settings captured at connection start. Follow the validation draft for sanitization and collisions. Never derive names from arbitrary arguments/secrets or add overrides/in-session rename/trace logs.
- Deliberate recording Pause is independent of macro pause. Recording failure holds the macro; explicit Retry creates new parts without backfill, then separate macro Resume is required. Preserve Text mixed-line skip, Raw/Terminal eligibility, partial-group cleanup, and incomplete-I/O rules. Bounded diagnostics do not automatically dump variables, arguments, sends, captures, or secrets.
- Preserve broadcast target checks but exclude macro-owned sessions from newly captured recipients, show unavailable counts, and never mutate frozen recipient sets. Eligibility returns only for later operations after macro end. Apply the accepted AI-specific rule independently: sharing is forbidden on checked active-broadcast targets; permitted sharing turns that tab's check OFF/disabled and leaves it OFF afterward. Read-only AI sharing is possible; control is unavailable while the macro executes/pauses, and Stop does not automatically grant it.
- Independent SFTP continues on a healthy session after macro success/Stop; actual disconnect/confirmed close follows normal transfer cancellation. No macro SFTP or AI-control API is added.

**Errors, resource bounds, and scheduling**

- A single MacroError catch per try can inspect stable Code and bounded Message/Line/Column; nest try blocks if needed. No throw/rethrow/filter/finally or CLR catch-all. Catch does not roll back effects. Localized Message excludes secrets, sent text, arguments, and captures; scripts branch on Code.
- Value errors include InvalidArgument, NumericOverflow, DivideByZero, NumberFormat, Format, IndexOutOfRange, CollectionModified, and NoTextValue. Connection/path errors include ProfileLoad, ConnectionAlreadyAttempted, ConnectionFailed, UserCancelled, NotConnected, and Path. Credential errors include MissingProfilePassword, CredentialUnavailable, CredentialKindMismatch, VaultMismatch, VaultStale, and VaultUnlockCancelled. Wait errors include WaitTimeout, EndOfInput, InvalidPattern, RegexTimeout, ZeroLengthMatch, CaptureLimit, and MatchWorkLimit. Admission errors include Encoding, PayloadLimit, PatternLimit, and OperationUnavailable. Source errors and coordinated holds are not MacroError exceptions.
- Initial engineering limits are conservative contract guards, not measured capacity or a hard process-working-set guarantee. Apply existing session/log/SFTP/AI limits independently; macro admission cannot silently borrow reserved capacity. Keep stale work charged until it actually finishes, even after Stop, and prevent it from affecting a new run.

| Resource | Initial contract bound |
| --- | --- |
| Source | 1 MiB encoded bytes and 1,048,576 decoded UTF-16 units |
| Syntax/checker | 100,000 nodes; nesting 128; 256 helpers; 32 parameters/helper; 1,024 locals/frame |
| Types/calls | List nesting 4; acyclic longest helper chain 64, checked even when unused |
| Active runs | 16 application-wide, including paused/waiting runs; existing activation queue 64 |
| Interpreter accounting | 32 MiB/run, 256 MiB/application |
| Admission reserve | 8 MiB/run within totals: 7 MiB history plus 1 MiB exclusively for coordinator/error/Stop progress |
| String/list | 1,048,576 UTF-16 units/string; 65,536 elements/list |
| Arguments | 64 items; 4,096 units/item; 16,384 units total; reject NUL/invalid Unicode |
| Receive history | 1,048,576 retained units; 65,536 line records; 7 MiB actual owned history including metadata/context/gaps |
| Receive line | 65,536 units including required consumed prefix context |
| Gap metadata | One unresolved incident; coalesced/bounded intervals with obsolete metadata discarded |
| Pattern | 4,096 units; 32 simultaneously live patterns/run; no unbounded global cache |
| Alternative wait | 1-16 candidates, copied at start |
| Regex search | Approximate 50 ms timeout per engine search, never unlimited |
| Matching pass | 65,536 candidate-line searches and 1,000 ms accumulated active work per initial/revision/deadline-cutoff pass; queue/pause excluded |
| Match result | 128 groups including zero; 4,096 captures; 262,144 units materialized result text |
| Search snapshots | One in-progress and one replaceable pending revision/run; bounded line copies |
| Logical send | 1 MiB encoded including Enter, within existing 2 MiB session input accounting; codec closure reserved separately |
| Dialog | One/run; title 256 units; message 16,384; input/initial text 4,096 |
| Finite sleep/wait | Milliseconds from zero through long maximum; overflow-safe remaining-budget arithmetic |
| Workers | At most two macro evaluation/search workers application-wide |
| Evaluation slice | Yield/check pause/Stop around 5 ms or 1,024 instructions, whichever first |

- Charge owned capacities before allocation/mutation where possible: at least 64 bytes/syntax node, 32/value/list/string/pattern wrapper, 16/list or frame slot, plus UTF-16 storage and owned buffers/metadata. Count shared immutable storage once and clones separately. CLR/regex/WPF/native/OS overhead requires measurement and is not a promised exact ceiling.
- Source/type/call/invocation bound failures reject launch. Single value/send/dialog excess is PayloadLimit before mutation; pattern count is PatternLimit. Result excess is CaptureLimit without consumption; aggregate matching excess is MatchWorkLimit without consumption, distinct from total response-wait lifetime. History exhaustion uses user recovery; exhausted hard interpreter accounting terminates the run. Do not block UI/receive/recording owners with regex or evaluation, and do not occupy CPU workers while awaiting I/O/user actions/pause.

## Testing Decisions

- Confirmed primary seam: the application-facing macro-run entry point used by Run Macro/file activation. Supply source/profile files and ordinary arguments, drive received payload and user Pause/Resume/Stop/dialog responses, and observe outgoing terminal bytes, run state/results, window/dialog requests, profile-registration effects, and recording files. Assert behavior and event ordering, not AST shapes, evaluator instructions, private fields, or mock-call counts.
- Exercise the real loader/checker/evaluator/matcher/coordinator through scripts at that seam. Reuse the existing terminal-session transport seam, real decoder/parser/recording behavior, and credential/shared-unlock services. Provide controllable time, transport outcomes, and user-interaction completion at the macro-host boundary for deterministic races, rather than adding test-only interfaces to every language component or a parallel fake terminal/vault implementation.
- Existing prior art includes saved-profile loopback connection/data exchange, exact received-payload Raw recording, recording Resume without backfill, paused-send queue tests, shared-unlock waiters, and WPF window/input tests. Add a small number of independent-peer and WPF integration checks for protocol/OS behavior that the controlled host cannot establish. Preserve the existing terminal-output test seam and all baseline transport/vault/recording tests.
- Required acceptance coverage is listed in the 50-case matrix below. All rows, including Q70-Q77 cases, are requirements rather than executed results.
- Run deterministic event-order permutations for committed versus uncommitted results, deadline cutoff versus pause/input, Continue versus renewed overflow, Retry versus other holds, send versus Stop, and shared unlock versus one cancelled run. Check that no extra command, secret echo, fabricated dialog answer, or profile registration occurs.
- Measure on the existing Windows 11 x64 reference workload with 20 sessions, 16 macro-created and four ordinary; include computation loops, regex-heavy streams, history pressure, dialogs, paused runs, active recording, minimized/moved tabs, and SFTP. Target p95 local Pause/Stop acknowledgement within 100 ms normally and 250 ms under the existing overload workload. Separately measure interpreter quiescence, regex/host work, send cleanup, and remote effects, reporting maximum/tail delay too. Acknowledgement is not wire completion or remote cancellation. Per-engine regex timeout is approximate. Preserve terminal/recording integrity and test each bound just below, at, and above its limit, including ownership after cancellation and unaffected sessions. Validate initial limits rather than silently relaxing them.
- No macro code, executable fixtures, runtime tests, performance measurements, or security-validation results are part of this documentation task. Recheck affected platform/security guidance at implementation start and before release, and document material deviations.

**Required acceptance matrix**

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

## Out of Scope

- Implementation/prototyping without a separate explicit request, and automatic inclusion in the initial release.
- Full C#/.NET or JavaScript compatibility, Roslyn scripting, arbitrary CLR/reflection, filesystem/process/network/clipboard access, external imports, custom classes, recursion, user threads/tasks, and untrusted-code sandboxing.
- Headless/unattended operation, secret CLI arguments, process exit-result automation, attaching to existing sessions, multiple connections per run, automatic reconnection/retry/replay, or built-in Telnet login configuration.
- Full-screen automation, cross-line regex matching, screen reconstruction, and guaranteed detection of full-screen programs that never enter alternate screen.
- Secret string access, password-input dialogs, credential export/management from macros, multiple vault selection, profile approval storage, automatic host-trust acceptance, or log redaction of remote output.
- Macro source editor/debugger/recorder, execution trace files, general terminal-key APIs, SFTP/AI APIs, desktop automation, log-name overrides, and in-session recording-name changes.

## Further Notes

- The interview uses its own Q1-Q77 numbering. The latest amendments govern: the app vault is the sole selection; profile password means the configured saved reference even after failed authentication; long/double types are fixed after inference; waits default to indefinite; partial-line prompts remain supported; TAB is preserved; alternate-screen entry ends the macro; and all final Q70-Q77 recommendations are accepted.
- The final review confirmed the consolidated behavior, language/API, resource/acceptance contract, and primary testing seam. Publish with the canonical ready-for-agent label. This label means the specification can guide later authorized work; it does not override the repository's separate implementation and release-scope gates.
- Resource numbers are engineering guards awaiting measurement. Trusted scripts can still submit destructive remote commands; terminal submission cannot prove remote completion. Pause cannot stop remote time or undo bytes already sent. Continued execution after history loss can miss a newline-free final prompt, and remote echo can expose sent credentials. These are explicit product limitations, not unfinished Q70-Q77 decisions.
- Preserve the accepted file-backed profiles, shared portable vault/unlock, terminal input/logging, SSH/OpenSSL, broadcast/split-view, AI collaboration, and SFTP contracts. Subsequent changes must reconcile these interfaces rather than silently changing another feature's accepted behavior.
