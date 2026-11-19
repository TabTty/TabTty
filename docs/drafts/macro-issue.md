# Specify terminal automation macros

Status: design interview opened on 2026-11-12; Q1-Q77 are accepted as amended,
including all Q60-Q77 recommendations accepted on 2026-11-14. The user explicitly
requested the [consolidated specification draft](macro-specification.md) under
Q69. Its routine language/API/budget details and primary testing seam were
confirmed in final review on 2026-11-14; Q70-Q77 are incorporated.
Q59 was accepted after its
credential-reference copy operation was explained. The user additionally asks
for easier use of the password configured in the connection profile, without
manually authoring its reference. The final clarification makes the profile's
configured saved-password reference authoritative, regardless of authentication
success, failure or the method actually used. It does not request retention of
the actual transmitted or manually corrected value. Q60-Q61 accept convenient
password-send operations and current-value retrieval using that fixed reference.
Q36 selects only long and double; Q47-Q48 now settle conversions
and arithmetic. Q39 permits author-selectable dialog text input, and the
Write/WriteLine analogy for Q45 is confirmed. The original interview is
complete. This work authorizes specification and documentation only, with no implementation or
prototype. Preserve the initial-release exclusion of macros until explicitly
amended. The [current macro language](macro-language.md#session-paths-input-waits-and-window)
uses vault entry names for password lookup. This file preserves interview history; the
[published body](macro-github-issue.md) follows the invoked to-spec template.
[GitHub issue #3](https://github.com/TabTty/TabTty/issues/3) was published with
ready-for-agent on 2026-11-14; its body and label were verified.

The [shared macro constants follow-up](#shared-macro-constants-follow-up)
records subsequent decisions about external definitions.

## Confirmed request

- Connect using a connection-profile file. Use the application's configured
  vault as the only vault selection, not as an overridable default. The Q5-Q6
  answer supersedes the original request for an optional macro-selected vault
  file; do not add a per-macro vault path or multiple-vault execution model.
- Do not automatically register a profile merely because a macro uses it.
- Add a vault-management action that copies a complete non-secret macro
  credential reference for external-editor authoring (Q59). Resolve exact
  vaultId/entryId/kind, never display names; the copy contains no password.
- Provide convenient use of the connection profile's configured saved-password
  reference, as clarified after Q59. Binding does not depend on authentication
  success or whether that password was actually used; a failed password attempt
  or success through another method does not select a different default. Manual
  corrections and keyboard-interactive responses do not replace the profile's
  reference. The request does not extend transient authentication-secret lifetime
  or expose ordinary password strings. Exact call syntax, reference capture and
  retrieval/failure behavior follow Q60-Q61.
- Provide basic control flow including `if`, `for`, `foreach`, and `while`.
  Use a defined C#-style language with explicitly supported operations and
  omitted type names/casts where inference is possible. Prefer fixing each
  variable's type from its initialization expression: `var x = 1` is an integer
  variable and `x = "done"` is an error. Dynamic reassignment is a fallback only
  if a later concrete technical assessment warrants reconsideration. Do not
  silently select it. Full C#, Roslyn scripting, unrestricted .NET access and
  JavaScript coercion are not selected. String-to-number conversion is explicit
  and conditions require Boolean values (Q24).
- The numeric types are signed 64-bit `long` and `double` only; there is no
  macro `int` type (Q36). Integer initializers therefore infer `long`. Permit
  implicit long-to-double assignment/mixed expressions, accepting possible
  rounding of large integers. Double-to-long conversion is explicit, truncates
  toward zero and rejects range overflow/NaN/infinity (Q47). Long/long division
  truncates toward zero. Integer overflow, division by zero and non-finite
  double results are errors, without wrapping or NaN/infinity propagation (Q48).
- Before side effects, check the whole loaded macro for statically knowable
  syntax/type/unsupported-operation errors (Q35). Runtime input/value errors
  still require runtime handling. Lists have a fixed element type; infer it
  when possible and require a type when an empty initializer supplies none
  (Q37). A general object/dynamic escape hatch is outside this scope.
- Support named helper functions in the same macro file with explicit parameter
  and return types and inferred locals. Defer user classes, lambdas, recursion,
  external source imports and user-created threads/tasks (Q50).
- Support try/catch for recoverable operation/value errors, including finite
  wait timeouts and input conversion failures; defer finally. User Stop,
  disconnect and alternate-screen termination cannot be caught to continue.
  Recording/history pauses retain their coordinated recovery rules; no implicit
  retry or replay is added (Q51).
- Include window minimization and restoration. Target the current window
  hosting the run's tab; restore the state before minimization and do nothing
  when it is not minimized. Focus/dialog/race details remain open.
- Provide a timed wait with its duration specified in milliseconds. Its
  validation bounds remain open; the run must remain stoppable during the wait.
- Provide local and UTC date/time, explicit formatting and elapsed time
  independent of wall-clock adjustments. Exact function/format syntax is open.
- Support external editing and in-app/file-activation launch of `.tttym` files.
  Accept UTF-8 with/without BOM and BOM-marked UTF-16LE/BE and UTF-32LE/BE.
  No-BOM source is strict UTF-8. Reject invalid sequences without repairing
  source text or guessing legacy encodings (Q25).
- Expose non-secret invocation arguments as an ordered string list, with
  explicit conversion by the macro. In-app and attended command-line launch
  support arguments; ordinary file double-click provides an empty list (Q56).
  Exact CLI/activation framing, quoting and bounds remain open.
- The macro requests its one connection explicitly. Relative profile paths
  initially use the macro's directory. A per-run base-directory setting replaces
  that base; a relative setting resolves against its current value, and reset
  restores the original macro directory. Do not change the process working
  directory, other runs or profile-relative logging paths (Q26).
- Expose the loaded macro's full path, filename including extension, basename
  without the final extension and extension including its dot. Capture these
  at run start; base-directory changes/external renames do not change them
  (Q38). Exact API names remain formal-contract work.
- Provide macro dialogs with OK, OK/Cancel, Yes/No and Yes/No/Cancel buttons;
  let the author enable or disable ordinary single-line text input independently
  of the button set (Q39 amended). Distinguish empty accepted text, cancellation
  and each button result. Closing/Esc is cancellation, never affirmation.
  Text input is not a secret-input API (Q40). Return the selected button and
  whether text is present; only input-enabled, non-cancel answers have text.
  Default to Cancel when offered, otherwise No for Yes/No, otherwise OK (Q52).
- Macro dialogs wait only for their originating run, identify it and leave
  other tabs/windows and Stop usable. Stop closes the dialog without returning
  a normal answer. Restore a minimized host when user action is needed without
  promising foreground focus. An answer does not clear other pause reasons
  (Q41). An outstanding dialog follows a moved tab without losing its input or
  decision state. Closing/stopping the run cancels it. Defer macro-requested
  minimization while the target window has a pending user-action dialog; user
  window controls and Stop remain available. Reevaluate the current host before
  executing a deferred request (Q53).
- Support variables holding strings and composition of strings from variables.
  Select ordinary escaped, verbatim, interpolated and interpolated-verbatim
  C#-style strings; defer raw triple-quoted forms and implicit numeric/string
  mixing. Exact escapes/format rules remain contract work (Q49).
- Support waiting for literal text or a regular expression and sending text.
  Match received textual responses/prompts, including prompts without a newline,
  independently of the visible screen and logs. Full-screen application state
  recognition is deferred (Q9 accepted after its earlier hold).
  Q27 selects completed-line waits and prompt waits including unfinished lines.
  Matches may cover less than a line; one line is the maximum span, not a
  whole-line-equality requirement. Q28 treats CRLF as one delimiter and LF or
  lone CR as delimiters, removes whole terminal sequences and other controls
  including backspace, and preserves TAB as the received tab character. Match
  text excludes delimiters. No edited-screen reconstruction is promised.
- Stop the macro upon observed entry into the alternate screen, including early
  connection output and paused/dialog states. Cancel unsent macro work and
  pending macro dialogs, but leave a healthy connection, display, recording and
  remote program running. Do not send Ctrl+C/quit or resume when the normal
  screen returns (Q46). This does not detect every full-screen program operating
  in the normal screen.
- Preserve receive history from connection startup and between waits, consume
  matches through their end, and retain following text. Offer an explicit
  receive boundary before a send. On history exhaustion, notify the user and
  offer Continue or End. Q42 accepts requesting macro pause, notifying once per
  unresolved incident, and retaining a bounded recent tail with explicit loss
  markers while terminal reception/recording continue. Continue accepts the gap
  and uses retained unconsumed history; a pending wait rechecks that range.
  Never match across gaps or treat a truncated line as intact. End stops the
  macro. Do not replay commands or clear other pause reasons. Exact bounds and
  gap mechanics remain to be specified.
- Default response waits to indefinite. Retain per-wait finite timeout support
  and the ability to interrupt/end a waiting run. This does not remove resource
  bounds or regex-execution limits, which remain separate decisions.
- Use .NET regex syntax, default case-sensitive matching, optional culture-
  independent case-insensitivity and numbered/named captures. Each search stays
  within one macro receive line and may match a substring; finite regex
  evaluation budgets are separate from response-wait deadlines (Q43).
- Support waiting for multiple literal/regex alternatives and return the
  selected alternative with match text/captures (Q44). At each evaluation,
  choose the earliest unconsumed receive line, then the earliest match position,
  then candidate declaration order. Commit prompt matches against currently
  available text without waiting for future longer matches or undoing committed
  matches (Q54). Reject empty literals and overall zero-length regex results;
  empty captures within positive-length matches remain valid. This also excludes
  a bare `^$` empty-line wait (Q55).
- Distinguish sending without an added ending from sending with one captured
  profile Enter ending appended, using the session's transmit encoding and
  ordering. The user's Write/WriteLine analogy matches this behavior (Q45).
  Submission completion does not prove remote command completion. Exact
  function spellings and control-character/key behavior remain open.
- Support a resumable macro pause on recording errors and by user request at
  any time. Keep receive/display/healthy recording and exclusive input ownership
  active while paused. Finish an already-started send before entering Paused,
  showing Pause requested while necessary; Stop remains available. Freeze
  remaining local sleep/finite-wait budgets, not clocks/real elapsed time or
  remote deadlines (Q29-Q31). Resume uses preserved incoming data; it does not
  replay sends.
- After recording failure, require successful explicit recording retry followed
  by a separate explicit macro Resume. Display the unfilled recording gap and
  use new log parts under the existing rules (Q32). Initial log preparation
  failures also pause, before transport starts, with the same recovery policy
  (Q34). Unrelated remaining pause reasons are not implicitly cleared.
- A user's intentional recording Pause affects recording only; the macro may
  continue. Recording Resume follows the existing new-part/no-backfill rules.
  This does not add a macro logging-control API or change the recording-failure
  pause policy (Q57).
- Preserve broadcast target checks while excluding executing, paused and
  dialog-waiting macro sessions from new operations' eligible recipients.
  Other eligible selected sessions still receive broadcast input; show the
  unavailable count. Macro completion restores eligibility for later operations
  without changing frozen recipient sets (Q58).
- On normal completion, stop or ordinary unhandled error, leave a healthy
  connection available. Ordinary errors stop the run without automatic retry;
  recording errors use the requested resumable pause instead. Disconnect still
  ends the run without automatic resumption on reconnect.
- Use the existing session recordings with formats and destination from the
  connection profile. Base names on the macro filename without its extension,
  preserving time, uniqueness, part numbering and format markers (Q8 accepted).
  Q66 excludes initial name overrides/in-session renaming; detailed sanitization
  and collision handling are in the validation draft.

The `.tttym` extension and the external-editing/in-app-and-file-launch workflow
are selected. Q62 selects a dedicated interpreter and Q63 versioned source;
the consolidated drafts specify the proposed detailed language/API contract.
The language boundary is recorded in
[ADR 0004](../adr/0004-defined-macro-language-with-csharp-style-control-flow.md).

## Round 1 decisions

Question numbering is local to this macro interview and starts at Q1,
independently of other concurrent interviews. An answer accepting a recommendation
does not accept downstream implementation choices. Q9 was initially held, then
explicitly accepted; its dependent receive-matching branches are now open.

| Question | Decision | Accepted outcome or pending status |
| --- | --- | --- |
| Q1 | Trust model | Accepted: support self-authored or reviewed, trusted macros; do not promise safe execution of unknown third-party code. |
| Q2 | Local automation scope | Accepted with additions: terminal automation, control flow, string/list/number operations, date/time and elapsed-time information, and explicit invocation arguments. Add C#-style control syntax, window minimize/restore, and a timed wait whose duration is in milliseconds. Beyond required profile/vault access and application-owned logging, defer arbitrary local file operations, process execution, and separate network access. Language/runtime and the exact window-operation contract are unresolved. |
| Q3 | Attended versus unattended execution | Accepted: visible TabTty windows and ordinary vault, host-key, and credential interactions. No silent trust approval or master passwords in source/command-line arguments. Headless unattended operation is outside this scope. |
| Q4 | Relationship between a run and terminal sessions | Accepted: one run creates and controls one new terminal session/tab; loops act within that session. Existing-session attachment and multi-session orchestration are outside this scope. |
| Q5-Q6 | Vault selection | Replaced by the user's answer: the application's configured vault is the only selection. Reject the proposed per-run vault override and simultaneous distinct vaults. Preserve one shared unlock state and existing exact vault/entry/kind reference resolution. |
| Q7 | Terminal-level secret submission | Accepted: credential references support terminal login and subsequent prompts, such as Telnet login or sudo, through a dedicated secret-send operation without ordinary password strings or generated local echo. Private-key material is not exposed to string operations. This reduces exposure but is not a sandbox or remote-echo redaction guarantee. |
| Q8 | Meaning and policy of macro-based logs | Accepted: existing session recordings use profile-selected formats/destination and the macro filename without its extension as the naming basis, retaining time, uniqueness, part numbering, and format markers. A macro execution trace is a separate requirement. |
| Q9 | Interaction model for waiting | Accepted after the earlier hold: target received textual command responses and prompts, including prompts without a newline, independently of the visible screen and logs. Defer full-screen application state recognition. Exact control processing, receive cursors, matching and consumption rules remain downstream decisions. |

## Round 2 decisions

Answers below were supplied on 2026-11-14. Accepted product behavior does not
authorize implementation. The user's Q19 and Q22 answers supersede the proposed
recording-error termination and 30,000 ms default/no-indefinite-wait rules.

| Question | Decision | Accepted outcome or pending status |
| --- | --- | --- |
| Q10 | Meaning of C#-style syntax | Accepted: a defined C#-style language and supported operation set with convenient inference/cast handling. Q23 subsequently prefers inferred fixed variable types; Q24 selects explicit conversions and Boolean conditions. Neither full JavaScript nor full C# execution is accepted. |
| Q11 | Authoring and launch workflow | Accepted: external editing, in-app Run macro file and selected Windows file association, new run/tab per launch, no initial integrated editor/debugger. Q25 completes the BOM-aware Unicode matrix and strict no-BOM UTF-8 policy; UTF-8-only support is superseded. |
| Q12 | Profile selection and relative paths | Accepted: one explicit connection, macro-directory-relative profile paths, existing profile-relative logging paths, no profile registration or second connection. Accepted Q26 adds a per-run base-directory override/reset. |
| Q13 | Window minimize/restore target | Accepted: target the window currently hosting the run's tab, following transfers. Minimize the entire window and restore its previous normal/maximized state; no-op when not minimized. Focus/activation details remain open. |
| Q14 | User input during execution | Accepted: block manual keys/text/paste and incoming broadcast input to the executing macro's session, while allowing browsing/copying, application controls and protocol-required replies. Preserve target selections. Q29 retains this ownership while paused. |
| Q15 | Session after macro termination | Accepted: on success, user stop or ordinary macro error, leave a healthy connection/tab available without automatic exit/Ctrl+C/disconnect. Explicit disconnect or actual connection loss terminates the run. Recording errors now pause under amended Q19. |
| Q16 | Stop guarantee | Accepted: visible stop works during timed/response waits and computation-only infinite loops without freezing the app. Cancel pending work and unsent macro input. Already transmitted bytes/remote operations cannot be undone. Q62 later selects the interpreter; draft validation defines response targets. Pause is distinct. |
| Q17 | Date/time information | Accepted: local and UTC wall-clock values, explicit formatting, and elapsed time independent of wall-clock changes. Exact syntax and timezone/offset details remain open. |
| Q18 | Ordinary macro failures | Accepted: unhandled errors stop with source location and a non-secret reason, without automatic sends/replay/retry. A failed send may have transmitted a prefix. Recording errors are the explicit pause exception under Q19. |
| Q19 | Recording failure and user pause | Amended: show recording errors and pause resumably; user-requested pause is also available. Q29-Q32 now select pause scope, send completion boundary, local timer freezing and explicit recovery; Q34 includes initial log preparation failure. Numeric bounds and races remain open. |
| Q20 | Received-text representation | Resolved through Q27-Q28: filtered individual receive lines, completed-line and unfinished-prompt waits, substring matches allowed, TAB retained, other controls/terminal sequences removed and CR/LF delimiters excluded. Do not reconstruct edited screen contents. The previous CR/LF/backspace-preserving proposal is superseded. |
| Q21 | Receive history and match consumption | Accepted: bounded history from connection startup and between waits; consume through match end, preserve following text, and allow an explicit pre-send receive boundary. Exact representation/API/overflow behavior remain open. |
| Q22 | Response-wait deadlines | Amended: indefinite by default, with per-wait finite timeout overrides retained. Waiting must remain interruptible/stoppable. A timeout error does not establish remote failure/stoppage and must not cause automatic command replay. Infinite response waits do not imply infinite regex evaluation or unlimited history. |

## Round 3 decisions

Answers were supplied on 2026-11-14. Q23 replaces the proposed dynamic variable
model with a preference for inferred fixed types, Q28 retains TAB instead of
replacing it, and Q33 rejects automatic termination in favor of a user decision.

| Question | Decision | Accepted outcome or pending status |
| --- | --- | --- |
| Q23 | Variable typing | Prefer inference from initialization expressions with a fixed variable type: infer integer for `var x = 1`, reject `x = "done"`. Q36 subsequently restricts numeric types to long/double, so this integer is long. Dynamic reassignment remains an unselected fallback only for demonstrated difficulties. Q35 selects pre-execution detection of statically knowable errors. |
| Q24 | Implicit conversion and conditions | Accepted: explicit string-to-number conversion and Boolean conditions, without JavaScript truthiness/loose equality. Use explicit conversion or string composition/interpolation. Numeric-representation details remain open. |
| Q25 | Source encoding matrix | Accepted: UTF-8 with/without BOM, BOM-marked UTF-16LE/BE and UTF-32LE/BE, no-BOM strict UTF-8, invalid-sequence errors rather than repair/legacy guessing. No promise to detect all unintended but valid byte sequences. |
| Q26 | Base-directory override/reset | Accepted: per-run base initially the macro directory; relative overrides use the current base; reset returns to the original macro directory. Other runs, process working directory and profile-relative logs are unchanged. Q38 selects four separately exposed source file identity values. |
| Q27 | Completed-line versus prompt waits | Accepted with clarification: a match spans at most one filtered receive line but may match only part of it. Separate completed-line and unfinished-prompt waits; no cross-line matching or implicit whole-line equality. |
| Q28 | Plain-line normalization | Accepted with TAB amendment: CRLF is one delimiter; LF/lone CR are delimiters; preserve TAB as the actual tab character; remove whole terminal control sequences and other controls, including backspace. Delimiters are not in match text. `abc<BS>X` becomes `abcX`, not screen-edited `abX`. |
| Q29 | Scope of macro pause | Accepted: keep connection, receiving, display, healthy recording and input exclusivity active; Stop before manual input. Macro pause does not pause recording or the remote program. Q39-Q41 now select ordinary text/decision dialogs and their run-local waiting scope. |
| Q30 | Pause requested during a send | Accepted: finish the already-started single send before Paused, with Pause requested status and Stop available. Do not start the next macro operation or replay sends. Send errors follow Q18; prepared secret plaintext must not inherit an indefinite pause lifetime. Operation-size/latency bounds remain open. |
| Q31 | Time while paused | Accepted: freeze local sleep/finite response-wait remaining time; clocks, real elapsed time, remote deadlines and liveness continue. Use preserved receive data on resume. |
| Q32 | Resume after recording failure | Accepted: explicit successful recording retry, then separate explicit macro Resume. New parts follow existing rules, missing recording is shown and not backfilled, and commands are not replayed. |
| Q33 | Receive-history exhaustion | Amended: notify the user and let them choose Continue or End. Automatic run termination is rejected. Q42 now accepts bounded recent-tail retention with explicit gaps, continuation through retained unconsumed history, and no replay. A healthy terminal connection must not be confused with macro-history loss. |
| Q34 | Initial recording preparation failure | Accepted: pause before transport on initial log creation failure, preserving saved-profile/pre-transport recording requirements. Recover under Q32 before connection. File cleanup/profile reload details remain open. |

## Round 4 decisions

The user's final complete answer on 2026-11-14 accepts Q35-Q46, amending numeric
types to long/double only and making text-input presence selectable in Q39.
Do not infer a numeric-conversion policy from an earlier interrupted reply.

| Question | Decision | Accepted outcome or pending details |
| --- | --- | --- |
| Q35 | Pre-execution checking | Accepted: reject statically knowable whole-source syntax/type/unsupported-operation errors before connection, sends, user dialogs or macro window operations. Dynamic value failures remain runtime errors. Compiler/runtime remains unselected. |
| Q36 | Numeric types | Amended: only signed 64-bit long and double, with no int type. Integer literals infer long. Numeric conversions, mixed arithmetic, division and non-finite handling are not selected by this type-set answer; see Q47-Q48. |
| Q37 | Lists | Accepted: one fixed element type, inferred where possible, with explicit element type for otherwise untyped empty initialization. Heterogeneous lists and a general object/dynamic escape hatch are outside scope. Syntax/common numeric type rules remain open. |
| Q38 | Macro file information | Accepted: full path, filename including extension, basename without final extension, and dot-prefixed extension, captured from loaded source at run start. Base-directory changes or external renames do not change these values. |
| Q39 | Dialog operations and results | Accepted with amendment: author-selectable single-line text-input presence with OK, OK/Cancel, Yes/No or Yes/No/Cancel. Distinguish accepted empty input, cancellation and each result. Closing/Esc is cancellation, never affirmation. Default buttons/result details follow Q52. |
| Q40 | Input secrecy scope | Accepted: ordinary string input only; managed secrets stay behind credential-reference operations. No implicit password handling/masking/protection guarantee for arbitrary prompt strings. A secret-input feature would require a separate contract. |
| Q41 | Dialog ownership and waiting | Accepted: identify run/current host, wait only for that macro, keep other tabs/windows and Stop available, and close a stopped run's request without returning an ordinary answer. Restore minimized hosts without guaranteeing foreground focus. Other pause reasons remain. Movement/concurrent window requests follow Q53. |
| Q42 | Overflow choice and continuation | Accepted: pause request and one notification per unresolved incident; receive/recording continue; retain a bounded recent tail with explicit loss. Continue accepts the gap and rechecks retained unconsumed history for a pending wait, without joining gaps/treating truncated lines as intact. No send replay or implicit release of other pauses. End stops. Exact bounds/gap mechanics remain open. |
| Q43 | Regex behavior | Accepted: .NET syntax, case-sensitive default, optional culture-independent case-insensitivity, numbered/named captures, substring matches within one receive line, and finite evaluation budgets separate from indefinite response waits. Exact features/flags and budget values remain open. |
| Q44 | Waiting for alternatives | Accepted: wait for multiple literal/regex alternatives and return selected alternative plus match text/captures under the common history/pause/timeout rules. Priority and zero-length handling follow Q54-Q55. |
| Q45 | Sending text and a line | Accepted: no appended ending versus one profile-configured Enter ending, using captured session encoding/ordering. The user's Write/WriteLine analogy is correct; exact API names remain contract work. Submission is not proof of remote command completion. |
| Q46 | Alternate-screen boundary | Accepted: alternate-screen entry stops the macro, including early connection output and paused/dialog states. Cancel unsent macro input and pending dialogs; keep healthy connection/display/recording and remote program running; no automatic quit/Ctrl+C or resume when normal screen returns. This is a protocol boundary, not universal full-screen-app detection. |

## Round 5 decisions

On 2026-11-14, the user accepted Q47-Q58, then accepted Q59 after clarification.
They additionally requested easier password use, then clarified that the source
is the connection profile's configured password regardless of success/failure,
not the actual credential sent or a corrected manual input. These
decisions do not authorize implementation; Q62 subsequently selects the runtime.
The separate AI
interview has since accepted input-owner exclusion, including paused macros;
the cross-feature constraints below reflect that later evidence.

| Question | Decision | Accepted outcome or pending recommendation |
| --- | --- | --- |
| Q47 | Numeric conversions | Permit implicit long-to-double conversion for assignment/mixed numeric expressions, explicitly accepting possible rounding of large integers. Require explicit double-to-long conversion, truncating toward zero and rejecting range overflow/NaN/infinity. Fixed long variables cannot directly receive double values. |
| Q48 | Arithmetic error behavior | Make long/long division return long with truncation toward zero. Report integer overflow, division by zero and non-finite double results as errors. Do not wrap integers or silently propagate NaN/infinity. Mixed-type promotion follows Q47. |
| Q49 | String literal forms | Support ordinary escaped, verbatim, interpolated and interpolated-verbatim C#-style strings. Use verbatim strings for Windows paths and interpolation for variable composition. Defer raw triple-quoted strings and implicit numeric/string mixing. Detailed escapes/format rules remain contract work. |
| Q50 | Named helpers | Support named helper functions within the same macro file, with explicit parameter/return types and ordinary local var inference. Initially defer user classes, lambdas, recursion, external source imports and user-created threads/tasks. Functions sequence through the same run/session. |
| Q51 | Recoverable errors | Add try/catch for recoverable operation/value errors such as finite wait timeout or input conversion failure. Initially defer finally. User Stop, disconnect and alternate-screen termination cannot be caught to continue the run; recording/history pauses remain coordinator-managed recovery. No implicit retry/replay is added. |
| Q52 | Dialog defaults and result shape | Return the selected button and whether a valid text value exists. Only enabled-input, non-cancel answers have text; accepted empty text remains distinct from no value. Prefer Cancel as default where offered, otherwise No for Yes/No, otherwise OK. The author chooses input presence independently of button set. |
| Q53 | Dialog movement and competing window requests | Follow a moved tab with its outstanding macro dialog, preserving text/decision state. Closing/stopping the run cancels it. Defer macro-requested minimization while that window has a pending user-action dialog; keep user-initiated window actions and Stop available. Reevaluate the current host before performing a deferred request. |
| Q54 | Alternative match order | At each evaluation, choose the earliest unconsumed receive line, then earliest match position in that line, then candidate declaration order. An unfinished prompt match commits against currently available text; do not wait for a potentially longer future match or undo a committed match when text grows. Exact incremental evaluation/resource mechanics remain open. |
| Q55 | Empty/zero-length waits | Reject an empty literal wait and a regex result whose overall match length is zero, preventing waits that succeed without consuming text. Empty capture groups inside a positive-length match remain valid. This excludes a bare `^$` empty-line wait unless an explicit later alternative is selected. |
| Q56 | Invocation arguments | Expose only non-secret invocation arguments as an ordered string list; conversion to other types is explicit in the macro. Allow arguments through in-app launch and an attended command-line launch; ordinary file double-click supplies an empty list. Exact CLI/activation framing, quoting and bounds remain to be designed. |
| Q57 | Intentional recording pause | A user's explicit logging Pause pauses only recording; the macro may continue and recording Resume uses existing no-backfill parts. Distinguish this deliberate choice from unexpected recording failure, which still pauses the macro under Q19/Q32. Do not add a script logging-control API through this decision. |
| Q58 | Broadcast eligibility during macro ownership | Preserve broadcast checks but mark executing/paused/dialog-waiting macro sessions unavailable for new broadcast operations. Exclude them from the captured recipients while allowing other eligible selected sessions; display the unavailable count. Ending the macro restores eligibility for later operations, never changes an already frozen recipient set. |
| Q59 | Authoring credential references | Accepted after clarification: add a vault-management action to copy a complete non-secret macro credential reference for pasting into an external editor. Keep existing exact vaultId/entryId/kind resolution, never lookup by display name or copy secret values. Follow-up clarification requests convenient use of the profile's configured saved-password reference regardless of actual authentication use or success; it does not request retention of a transmitted/manual password. |

Q59 clarification (subsequently accepted): the UI action helps an author select an existing
vault entry for a terminal-level secret-send operation, for example a sudo
password prompt after connection. The author would select the saved entry in
vault management, copy its macro reference, and paste that reference into the
macro. The copied content identifies the vault, entry and credential kind; it
does not contain the password or master password. TabTty resolves that reference
against the application's selected vault at use time, under the existing unlock
and identity rules. Ordinary SSH connection authentication already uses the
profile's credential reference and does not require a duplicate macro reference.
This proposal is an authoring convenience, not a change to vault selection or a
new secret-returning API. Exact pasted syntax remains open. The follow-up
profile-password request is a separate convenience feature, not a rejection
of reference-based selection for other credentials. The user's final wording
explicitly includes the configured item even when authentication fails. This
does not request retention of the actually accepted password; existing
authentication-only secret lifetimes remain binding.

## Round 6 decisions

The user accepted every Q60-Q69 recommendation on 2026-11-14 and requested the
specification draft. The default password source is the saved-password reference
configured in the connection profile, regardless of authentication outcome.
No actual authentication-password retention is selected. The language/API,
resource and acceptance details are drafted under Q69 in macro-specification.md,
macro-language.md and macro-validation.md; Q70-Q77 were subsequently accepted.

| Question | Decision | Accepted recommendation |
| --- | --- | --- |
| Q60 | Convenient profile-password submission | Let password-send operations omit the credential argument to use the configured profile password, analogous to `WritePassword()` and `WritePasswordLine()`. The line variant appends the captured profile Enter ending. Names are illustrative pending the API contract. Explicit-reference arguments select other credentials. No ordinary password string is returned; generated local echo and diagnostics exclude it. A missing profile password reference is a recoverable error, never an empty send or a guess. The default reference remains meaningful regardless of authentication success; actual terminal sending still requires a connected usable session. |
| Q61 | Reference capture, updates and retrieval failures | Capture the configured reference from the profile loaded for the connection attempt; later profile-file edits do not retarget that run. On each password-send operation, retrieve the then-current value of that entry from the sole app-selected vault and release transient plaintext after use. Thus an entry update affects later sends, while deletion/wrong kind/wrong vault/stale state fails visibly and recoverably without name fallback, hidden old-vault use or implicit selection changes. Normal shared unlock applies, and unlock cancellation fails that operation without claiming success. Preserve existing lifetimes; no copy of the actual authentication password is retained. |
| Q62 | Execution engine | Select a TabTty-owned interpreter for the defined language, with whole-source syntax/type/call-graph checks before effects and explicit pause/Stop checks during pure computation as well as waits. Run evaluation without blocking the UI and bound host operations separately. Do not execute unrestricted C# or select Roslyn scripting. This is a design choice for trusted macros, not a sandbox or measured performance claim; scheduling and resource limits follow separately. |
| Q63 | Source file shape and versioning | Keep `.tttym` as readable source with a required language-version declaration, without a JSON wrapper. Validate the version and whole loaded source before effects; reject missing/unsupported versions rather than guessing. Execute a single loaded snapshot so external edits affect only later runs. Preserve the accepted BOM-aware decoding. Exact header spelling and diagnostic coordinates belong to the formal language contract. |
| Q64 | Regex feature boundary | Support ordinary left-to-right .NET backtracking matching, including lookaround and backreferences, with invariant-culture behavior and finite per-evaluation budgets. Do not silently substitute NonBacktracking or add RightToLeft/ECMAScript modes. Evaluation timeout is a recoverable error, distinct from response-wait timeout; do not retry it automatically without limit. Exact options/capture shape and budgets remain contract work. |
| Q65 | Embedded newlines and control characters in sends | Let explicit string contents include CR, LF, TAB and control characters such as ESC/Ctrl+C through supported escapes, subject to the session's transmit codec and protocol handling. Do not rewrite embedded newlines to the Enter setting. Only the line-send variant appends one configured Enter ending, even if the supplied text already ends in a newline. Strictly preflight the whole operation before any payload; unsupported text is an error rather than replacement. Defer a separate general terminal-key API initially. Explicit scripted Ctrl+C is distinct from Stop, which never synthesizes it. |
| Q66 | Initial log-name override scope | Use the captured macro basename as the naming basis for the first macro version, preserving the existing sanitization, timestamp/offset, unique suffix, part and format structure and profile-relative destination. Do not initially add script/argument overrides or in-session renaming. Exact sanitization/bounds/collision handling will be stated in the detailed contract. |
| Q67 | Receive EOF without disconnect | At a receive-direction EOF, process final decoded text and search retained unconsumed history. Prompt waits may match the final unterminated fragment; completed-line waits still require an actual line delimiter. If no eligible match exists and no more receive data can arrive, return a recoverable end-of-input error immediately, even for an indefinite wait. Do not manufacture a newline or equate half-close with disconnect. Actual disconnect still permanently ends the macro. |
| Q68 | Locale-independent data operations | Make numeric parsing/formatting and default interpolation independent of the Windows/UI locale, with `.` as the decimal separator and no implicit locale-specific thousands parsing. Default date/time text to an unambiguous ISO 8601 form with local offset or UTC indication; allow explicit formats. UI language does not change macro semantics. Exact supported formats and data APIs follow in the contract. |
| Q69 | Completing routine contract details | After these product choices, draft the remaining grammar/API spellings, scopes, operation/result types, conservative resource budgets and acceptance scenarios within accepted behavior. Continue asking about unresolved user-visible tradeoffs, including pause/overflow races, rather than treating them as implementation details. Present the consolidated contract for final shared-understanding review; this delegation would authorize documentation only, not code/prototypes. |

## Round 7 acceptance and final review

The user accepted every Q70-Q77 recommendation on 2026-11-14 and invoked to-spec.
The explicit choices in the
[consolidated draft](macro-specification.md#accepted-final-product-decisions-q70-q77) cover
pause during connection, independent holds/repeated overflow, regex context,
truncated lines after loss, match/deadline/pause races, closing active runs,
uncertain send failure and unencrypted-password notices. They are accepted
requirements, reflected in the behavior and validation contracts. Routine grammar, API spelling, source
versioning, scalar/list operations, formatting and conservative budgets are
now concrete draft sections, rather than questions to reopen individually.

The user confirmed the consolidated contract and primary application-facing
macro-run testing seam on 2026-11-14. The to-spec publication uses ready-for-agent,
without changing initial-release scope or authorizing code. Specification
readiness is established; implementation and runtime validation remain unperformed.

## Shared macro constants follow-up

The user confirmed Q78–Q88 on 2026-11-20, with Q81 amended to use the macro
source encoding rules. The version 1 specification is ready for implementation
of shared macro constants in a macro-enabled release. The
[language contract](macro-language.md#shared-macro-constants),
[launch and trust contract](macro-specification.md#capturing-declared-constants)
and [acceptance cases](macro-validation.md#acceptance-matrix-for-later-authorized-implementation)
incorporate these decisions. The [trust decision record](../adr/0009-bind-shared-macro-constants-to-launch-approval.md)
explains the effect of editing a shared file.

| Decision | Accepted behavior |
| --- | --- |
| Q78 — Shared data | An external file defines named, read-only, non-secret values that macro statements and helpers can use. The file supplies data only. |
| Q79 — Explicit selection | Each consuming macro declares the file it uses. Several macros can declare the same file. |
| Q80 — Lifetime and approval | Read and hold the constants for one launch. Edits affect subsequent launches. Remembered execution decisions cover the macro source, ordered invocation arguments, and the captured constants. A change to the constants selects the decision for the resulting combination. |
| Q81 — File format and encoding | Each constants file contains one JSON object with unique member names. Decode each file independently using the macro source rules. Without a BOM, read strict UTF-8. With a BOM, accept UTF-8, UTF-16LE/BE or UTF-32LE/BE, matching the longest BOM first. Reject malformed input. |
| Q82 — Value types | Accept JSON strings, booleans and numbers. An integer spelling denotes a signed 64-bit `long`. A fraction or exponent denotes a finite `double`. Arrays, objects as values and `null` are invalid. |
| Q83 — Declarations and names | At the top of the macro, declare each file with `#constants Alias "path"`. Use `Alias.Name` in statements and helpers. Different aliases can expose the same member name. |
| Q84 — File location | Accept absolute paths and resolve relative paths against the directory of the macro source. Apply the [remote read confirmation rules](macro-specification.md#loading-launch-and-source-identity) to each dependency before any potential remote access. |
| Q85 — Content identity | Include every declared file's exact encoded bytes in the execution decision. A change to a BOM, encoding, whitespace or line ending selects the decision for the resulting combination, even when all parsed values are equal. |
| Q86 — Language and compatibility | Extend `#tttym 1` with the optional directive. Earlier readers reject it before execution. A macro with zero declarations continues to use its existing allow or block for its source and arguments. |
| Q87 — Validation scope | Validate every entry in every declared file before execution, including entries the macro never references. Valid unused entries are welcome. A duplicate or invalid definition stops the launch and identifies the affected file and position. |
| Q88 — Reviewing and forgetting decisions | At launch, show each declared file's alias and resolved path and let the user inspect its captured contents. In Settings, distinguish earlier and later constants versions for the same macro and arguments. The user can forget one decision even after deleting the original files. |

For example, both a startup macro and a diagnostic macro can declare
`#constants Common "common.json"`. Given the file below, both can call
`WaitPrompt(Literal(Common.Prompt), Common.TimeoutMs)` after connecting.

```json
{
  "Prompt": "Ready>",
  "TimeoutMs": 5000
}
```

The macro and its constants file may use different encodings. A UTF-8 macro
can read a UTF-16LE JSON file with a BOM. Saving that file as UTF-8 keeps
`Common.TimeoutMs` equal to 5000 and changes the content covered by approval.

## Historical branch inventory

The inventory below records earlier planning, not current blockers or unanswered
questions. The consolidated behavior/language/validation drafts supersede it.

- Trust and local capabilities (Q1-Q2)
  - Language/runtime comparison under the Microsoft/OpenSSL dependency policy;
    exact syntax, types, numeric limits, lists, scopes and function/error APIs.
    Q47-Q51 settle the broad numeric, string, helper and recoverable-error rules.
  - Versioned `.tttym` contents, source encoding, external editing versus an
    in-app editor, and syntax diagnostics. The external-editing workflow is
    accepted, and Q25 selects the BOM-aware Unicode matrix.
  - Execution isolation, CPU/memory limits, regex bounds, and stopping a loop
    that never reaches a terminal operation.
  - Exact date/time, timezone, elapsed-time, formatting, argument and information
    APIs; locale-independent execution and localized UI errors.
  - Loaded macro file identity and metadata, fixed inferred types, numeric/list
    rules, pre-execution checks and the scope of dynamic input validation.
- Invocation and session ownership (Q3-Q4 and the added window operations)
  - Launch entry points, arguments, relative-path bases, file activation, UI
    routing, concurrent-run limits and identifying a run separately from its
    source file.
  - Ordinary user input, paste, broadcast input and tab movement during a run.
  - The host window affected by minimize/restore, previous maximized state,
    focus/activation, authentication dialogs, other tabs, and competing window
    operations from multiple runs.
  - Send completion versus remote command completion, Enter/control characters,
    encoding, pacing, partial writes and cancellation.
  - Success, error, user stop, disconnect and application exit; treatment of the
    session and unsent input, with no implicit replay on reconnect.
  - Resumable pause versus irreversible stop; pause requests during send,
    connect/authentication, wait, computation and window operations; multiple
    simultaneous pause reasons; manual-input ownership and disconnect while paused.
  - Macro text/decision dialogs: results/cancellation versus Stop, current host,
    restoration, tab movement/closing, simultaneous requests and ordinary versus
    secret input. Do not inherit application-wide modal blocking automatically.
- Single selected vault and secret use (Q5-Q7)
  - Convenient submission from the profile's configured saved-password reference
    (source settled by the user's final clarification), reference capture and
    current-value retrieval, update/deletion/failure behavior. Do not retain
    actual authentication passwords or use corrected input as the default.
  - Exact credential-reference expression/resolution, unlock cancellation and
    shared concurrent first-use prompts under the existing single-vault model.
  - Changes to the application-selected vault during a run; preserve identity
    checks without inventing an independent per-macro vault selection.
  - The same vault/unlock state serves ordinary sessions and macros. The macro
    feature does not introduce vault-management/write operations.
  - Credential selection for terminal-level login, local-echo suppression,
    secret lifetime, plaintext transports and remote echo in recordings.
- Logging (Q8)
  - Naming sanitization, collisions, source edits/renames, optional overrides,
    naming precedence, paths and part lifecycle.
  - Effect of recording failures on macro execution and any separate diagnostic
    trace, with explicit treatment of secrets and command arguments.
  - Detailed recording-error pause/recovery and initial log preparation failure
    versus failure after connection. Q57 settles intentional recording Pause as
    independent of macro execution.
- Waiting and command flow (Q9/Q21/Q27 accepted, Q28 retains TAB;
  Q22 defaults to indefinite, Q33 requires a Continue/End choice)
  - Literal/regex options and captures, matching multiple alternatives, receive
    encoding, SSH output streams, controls, CR/backspace and multiline matches.
  - Subscription before initial output, bounded history, matching cursors,
    consumption of matches, and prompt/response races around sending.
  - Wait timeouts versus regex evaluation timeouts, cancellation, buffer
    overflow, disconnect, and explicit recovery without automatic command replay.
  - Local timer freezing, incoming data during pause, EOF without a match and
    required-history exhaustion during an indefinite wait or pause.
  - Gap-aware bounded recovery after the user's choice, truncated lines,
    pending match results, incremental evaluation under accepted Q54 ordering,
    positive-length enforcement under Q55 and capture ownership.
  - Accepted alternate-screen entry termination (Q46), including short-lived
    entry/exit in one receive batch, early output and paused/dialog states.
- Cross-feature integration
  - Broadcast checks remain separate from eligibility; Q58 accepts behavior
    for macro-owned targets without changing frozen recipient sets.
  - The completed AI contract excludes control during executing/paused macros
    and permits read-only sharing subject to independent broadcast exclusions.
    Active broadcast checked targets cannot start sharing, even if macro-owned.
    Permitted sharing turns its check OFF/disabled, and ending sharing leaves
    it OFF. This accepted AI-specific rule is distinct from macro Q58 retaining
    checks for sessions without AI sharing.
  - A healthy connection survives Macro Stop, so its existing independent SFTP
    work is not stopped merely because the macro ends. A policy change would
    need a separate decision; no macro SFTP API is selected here.
- Final contract
  - Acceptance scenarios, amendments to baseline contracts, glossary terms,
    ADRs for substantial trade-offs, and implementation-readiness review.

## Existing constraints and observed implementation

Read-only repository inspection on 2026-11-12 found initial-release code and a
2026-11-11 implementation report. Older draft statements that implementation
has not begun are historical, not a reason to assume the code is absent.
No macro runtime or matching API was found. No code was changed or tested for
this interview.

- A saved profile and a profile registration are different concepts. Core
  connection reads a file independently of registration, while the current
  ordinary UI opening path registers it. The requested macro path must preserve
  that distinction.
- Logging starts according to the saved profile and prepares its files before
  transport startup. Macro-based names are anticipated by the future-macro
  constraints. A recording-disabled session cannot enable recording midway.
- Current vault selection is application-wide. Credential references require
  exact `vaultId`, `entryId`, and `kind`; choosing another file does not authorize
  matching an unrelated credential by name.
- Receive observation must not consume data away from the terminal/logger or
  depend on screen scraping. Output may arrive before connection completion;
  a simple subscription after connecting can miss a prompt.
- Text recording finalizes edited normal-screen lines and is not a source for
  immediate prompt matching. Macro match consumption refers to its own search
  position, never removal from the terminal/logger. A boundary captured before
  sending limits the search range but cannot prove that later output is a
  response to that particular command.
- SSH DATA and separately sent stderr retain their independent decoder/parser
  state. The existing completed-operation composition must not be replaced by
  re-decoding their concatenated Raw bytes for macro text. Macro history limits
  and EOF-without-match outcomes still require their own contract.
- Existing future-macro constraints terminate a run on disconnect and prohibit
  automatic resumption or replay after reconnect. Any multi-connection proposal
  must explicitly revisit the meaning of an intentional disconnect.
- Secret values must stay out of diagnostics, generated macro source, command
  lines and application-generated local echo. Remote echo can still expose a
  secret in session recordings; Telnet does not encrypt transmission.

Round 2 window/input inspection also found the following constraints:

- [The accepted split-layout ADR](../adr/0002-two-level-splits-with-one-session-per-tab.md)
  keeps one session per terminal tab and moves that tab between groups/windows
  without changing its session. The later Q13 answer selects that tab's current
  host window as the macro operation target.
- [Existing activation behavior Q115](window-lifecycle-proposals.md) restores
  the pre-minimization normal/maximized state. The current file-activation code
  instead sets a minimized window to Normal; that implementation difference
  is not a macro requirement or authorization to change code in this interview.
- Broadcast target selection is retained under the concurrent
  [broadcast/split interview](broadcast-and-split-issue.md). Do not silently
  clear those selections to implement a proposed macro-input exclusion.
- Windows foreground activation is separately constrained by the OS; successful
  restoration must not be assumed to guarantee foreground focus. See
  [Microsoft SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).

Related documents: [future macro constraints](future-macro-design.md),
[profiles](connection-profiles-issue.md), [authentication](authentication-issue.md),
[vault](credential-vault-issue.md),
[session and recording](../specification/session-and-recording.md),
[persistence and windows](../specification/persistence-and-windows.md), and
[implementation status](../specification/implementation-status.md).

## Primary guidance checked on 2026-11-12

- [Microsoft AssemblyLoadContext](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext?view=net-10.0)
  and [secure coding guidance](https://learn.microsoft.com/en-us/dotnet/standard/security/secure-coding-guidelines):
  assembly loading separation is not a security boundary for arbitrary code.
- [Microsoft regex guidance](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-regex):
  trusted patterns, execution timeouts and bounded inputs are distinct concerns;
  a timeout or NonBacktracking is not a boundary for malicious patterns.
- [OWASP secrets in memory](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#25-handling-secrets-in-memory)
  and [logging exclusions](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html#data-to-exclude):
  minimize secret lifetime/copies and exclude passwords and keys from ordinary
  diagnostic logs. This does not imply that arbitrary terminal output can be
  reliably redacted.

## Additional language and cancellation evidence for round 2

- [Microsoft Roslyn Syntax API](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-analysis)
  describes syntax-tree analysis; [Roslyn scripting examples](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Scripting-API-Samples.md)
  describe hosting a C# execution engine. Selecting C#-style syntax does not
  itself select full C# execution. A supported subset and its behavior would
  need an explicit TabTty language specification.
- [Microsoft managed cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads)
  is cooperative. A cancellation token alone does not stop a loop that never
  observes it. [Microsoft thread destruction guidance](https://learn.microsoft.com/en-us/dotnet/standard/threading/destroying-threads)
  states that `Thread.Abort` is unsupported on current .NET and discusses
  separate processes for third-party code needing forced termination. This
  evidence motivates a product stop requirement without selecting a runtime.

## Round 3 evidence and constraints, checked 2026-11-14

- [Microsoft implicit local typing](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/implicitly-typed-local-variables)
  distinguishes C# `var` inference from dynamic variables.
  [Microsoft dynamic](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/interop/using-type-dynamic)
  and [boxing/unboxing](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/types/boxing-and-unboxing)
  describe separate runtime-binding and representation concepts. Record the
  desired macro experience (omitted type names/casts, possible reassignment) as
  product semantics without selecting .NET `dynamic` or a storage representation.
- JavaScript [Boolean conversion](https://tc39.es/ecma262/multipage/abstract-operations.html#sec-toboolean)
  and [loose equality](https://tc39.es/ecma262/multipage/abstract-operations.html#sec-islooselyequal)
  are independent rules. A JavaScript-like typing preference is not acceptance
  of these conversions.
- [Unicode BOM guidance](https://www.unicode.org/faq/utf_bom.html) and
  [Microsoft StreamReader](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamreader.-ctor?view=net-10.0)
  describe UTF-8/16LE/16BE/32LE/32BE BOMs. Recognizing those marks does not decide
  the product's accepted encoding matrix. No-BOM data has no universally
  reliable encoding identification; the Q25 default is an explicit format rule.
- Strict decoding is a separate choice from BOM detection. Microsoft documents
  invalid-sequence error handling for
  [UTF-8](https://learn.microsoft.com/en-us/dotnet/api/system.text.utf8encoding.-ctor?view=net-10.0),
  [UTF-16](https://learn.microsoft.com/en-us/dotnet/api/system.text.unicodeencoding.-ctor?view=net-10.0),
  and [UTF-32](https://learn.microsoft.com/en-us/dotnet/api/system.text.utf32encoding.-ctor?view=net-10.0).
  Do not claim that strict decoding detects every file written in an unintended
  encoding when its bytes are also valid in the selected one.
- Existing recording failure/retry keeps the connection/receiver active,
  creates new parts on explicit retry, and never backfills the missing interval.
  Normal-screen Text additionally skips a mixed line already in progress at
  retry. Macro pause cannot stop remote execution or promise complete recording.
  See [session and recording](../specification/session-and-recording.md).
- Pause requests and send completion need a separate contract from Stop.
  Cancelling a prefix and then replaying a whole send on Resume would violate
  the accepted no-replay rule. Q30 now accepts finishing the current send before
  Paused. This is a product contract, not an implementation or a promise of
  instantaneous pause.
- The macro-history budget is separate from transport queues. Waiting forever
  or pausing does not authorize unbounded history. The user replaced automatic
  Q33 run failure with a Continue/End decision. Actual retention and continuation
  policy are now selected in Q42 and must not be misreported as a transport failure
  or disconnect a healthy session. Exact limits remain to be specified.
- Credential references may survive a pause, but prepared plaintext secret
  buffers must not inherit an indefinite pause lifetime. Preserve the existing
  [authentication policy](authentication-issue.md); final pause/send mechanics
  must specify credential retrieval and cleanup at their actual use boundaries.

## Round 4 evidence and constraints, checked 2026-11-14

- Read-only inspection found no macro parser/compiler/typechecker/runtime or
  adopted Roslyn dependency. Inferring fixed types from initialization
  expressions can be specified through a finite set of expression/API typing
  rules without adopting all C# generic/overload/lambda inference. This is a
  design inference, not a measured implementation-cost claim; no demonstrated
  difficulty currently justifies automatically choosing the Q23 dynamic fallback.
- Existing application dialogs show current-host resolution and cancellation
  examples, but their application-wide modal/file-activation behavior is not
  automatically suitable for an indefinite macro prompt. Q41 deliberately asks
  about run-local waiting; keep pending Stop/cancellation separate from an
  ordinary dialog result. See [window persistence](../specification/persistence-and-windows.md).
- Finite macro-history capacity, continued terminal reception and an indefinite
  user decision cannot preserve every incoming character simultaneously. The
  bounded recent-tail/drop strategy in Q42 is now accepted, with explicit
  loss indication and a Continue/End choice; it has not been implemented. Preserve
  existing recording and protocol queue rules independently of this choice.
- [Microsoft regex options](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options#compare-using-the-invariant-culture)
  documents default case-sensitive behavior and culture-dependent IgnoreCase.
  Q43 selects CultureInvariant behavior even when case-insensitivity is requested
  inline. [Grouping constructs](https://learn.microsoft.com/en-us/dotnet/standard/base-types/grouping-constructs-in-regular-expressions)
  documents numbered/named captures.
  [MatchTimeout](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.matchtimeout?view=net-10.0)
  is a separate, approximate per-match evaluation limit; it is not a strict
  instantaneous cutoff or the response-wait deadline. NonBacktracking excludes
  some constructs, so a dialect choice does not settle every allowed feature.
- The existing [terminal contract](../specification/terminal-and-encodings.md)
  supports alternate-screen entry through DEC private modes 47, 1047 and 1049;
  mode 1048 only saves/restores the cursor. An ordered entry event can be
  detected even if entry/exit occur within one receive batch; a final UI snapshot
  alone can miss that event. Early connection output and paused runs also need
  observation under accepted Q46. No macro observer has been implemented.
- [Vim's official tips](https://raw.githubusercontent.com/vim/vim/master/runtime/doc/tips.txt)
  documents disabling the screen switch with `:set t_ti= t_te=`.
  [xterm control sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
  defines the alternate-screen controls. Therefore Q46's observable protocol
  boundary cannot be described as detecting every full-screen application.
  It is not process-name/title detection and does not automatically quit Vim.

## Round 5 evidence and constraints, checked 2026-11-14

- The final complete Q36 answer selects only long and double. It did not itself
  approve a conversion policy from a superseded partial reply; the later
  Q47-Q48 acceptance now establishes conversion/arithmetic behavior.
  [Microsoft numeric conversions](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/numeric-conversions)
  documents implicit long-to-double conversion in C# and possible precision
  loss. From the documented binary64 representation, all integers between
  -2^53 and +2^53 inclusive are exactly representable; some larger integers are
  also exact, but not every long is. This is a mathematical consequence, not
  a tested TabTty implementation claim.
- [Microsoft explicit numeric conversions](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/conversions#1032-explicit-numeric-conversions)
  describes double-to-long truncation toward zero and checked overflow handling.
  [Arithmetic operators](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/arithmetic-operators)
  distinguishes integral division/overflow from double NaN/infinity behavior.
  Accepted Q47-Q48 are explicit macro-language choices, not automatic inheritance
  of C#. In particular, non-finite double results are errors in this language.
- [Microsoft strings](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/strings/)
  documents ordinary, verbatim and interpolated forms. Q49 selects a supported
  subset; it does not adopt every current C# string feature.
- The accepted [broadcast contract](../specification/broadcast-and-split-views.md)
  separates remembered checks from eligible recipients and freezes active
  operations. Q58 extends eligibility for macro ownership; it must not erase
  checks, queue input for later readiness or mutate frozen recipients.
- At the round 5 inspection, the [AI collaboration interview](ai-terminal-collaboration-issue.md)
  had accepted Q1-Q8 and Q16 was pending. The round 6 check below supersedes
  that snapshot. Do not broaden AI APIs to launch macros through this interview.
- Credential references already require exact vaultId/entryId/kind; names are
  duplicate-capable labels and cannot serve as lookup identity. The current
  vault management UI shows entryId but has no dedicated complete-reference
  copy operation. Accepted Q59 adds that authoring operation without exposing
  secrets. See [profile management](profile-management-proposals.md),
  [vault format](../specification/vault-format.md) and
  [persistence](../specification/persistence-and-windows.md).
- Existing application vault-path changes discard the old unlock state, and
  wrong/missing/type-mismatched references fail without fallback. Macro behavior
  during an application vault-path change remains a separate unresolved branch.
  The proposal exposes the saved password through the password accessor. The
  account required by the [vault format](../specification/vault-format.md#encrypted-payload-schema-and-limits)
  stays outside that accessor's return value.

## Round 6 evidence and constraints, checked 2026-11-14

- Password retrieval in `TerminalTab` first uses the saved
  reference, then requests fresh input after a failed attempt without silently
  updating the vault. In `SshSessionWire.AuthenticateAsync`, the Password send
  scope disposes its secret and writer before reading the next success/failure
  response. Successful authentication returns without an exported credential
  result. Thus rereading the configured entry cannot prove reuse of the actual
  accepted password. The [authentication contract](authentication-issue.md) and
  [vault format](../specification/vault-format.md) also end ordinary temporary
  credential ownership at authentication completion. The user's final
  clarification removes the need for that lifetime amendment: the desired
  source is the profile's configured saved-password reference even after a
  failed attempt, not the actually accepted value. No retention extension is
  selected or proposed in the current frontier.
- Password may be an accepted partial-success factor before another method
  completes authentication. Keyboard-interactive uses distinct, potentially
  multiple prompts and rounds; its responses are not classified as reusable
  passwords by prompt text or echo flags. See the accepted
  [SSH authentication choices](ssh-authentication-proposals.md) and
  [RFC 4256](https://www.rfc-editor.org/rfc/rfc4256.html). Key passphrases and
  vault master passwords are different secrets, never substitute profile
  passwords. Telnet/serial connection readiness has no built-in account login
  or initial profile-password binding; an explicit reference remains available
  for terminal-level login without inventing a default credential.
- [OWASP secrets-in-memory guidance](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#25-handling-secrets-in-memory)
  calls for minimizing plaintext lifetime/copies and clearing owned buffers.
  [Microsoft SecureString guidance](https://learn.microsoft.com/en-us/dotnet/api/system.security.securestring?view=net-10.0)
  does not recommend SecureString for new .NET development;
  [ZeroMemory](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0)
  clears the supplied buffer, not all runtime/OS copies. Preserve the accepted
  use-boundary cleanup; the clarified convenience request requires only a
  reference, not retention of an authentication-secret value through pauses.
- Saved entries can be updated or deleted, the app-selected vault path can
  change, and external file changes can mark a vault stale. Existing exact
  reference resolution fails rather than selecting a same-named substitute.
  Q61 proposes capturing reference identity while retrieving current values,
  preserving update/deletion effects on subsequent use. Authentication success
  is not the criterion for selecting that default reference.
- The app targets .NET 10/WPF; no Roslyn scripting or macro execution engine is
  adopted. [Microsoft cooperative cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads)
  requires execution to observe cancellation; simply passing a token cannot stop
  an uncooperative loop. [Thread destruction guidance](https://learn.microsoft.com/en-us/dotnet/standard/threading/destroying-threads)
  rules out Thread.Abort on modern .NET. These facts motivate Q62, without proving
  any proposed interpreter's performance or selecting numeric resource limits.
- [Microsoft regex options](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options#nonbacktracking-mode)
  confirms that NonBacktracking excludes lookaround/backreferences. Existing
  highlight limits (including 50 ms evaluation and 65,536 UTF-16-unit lines)
  did not automatically establish macro limits. The resource draft now proposes
  those values explicitly along with aggregate matching limits. .NET MatchTimeout is approximate and not a
  guaranteed Stop-latency bound. Q64 must precede the detailed resource contract.
- The round 6 inspection saw AI Q1-Q27 accepted. The later consolidated
  [AI contract](../specification/ai-collaboration.md) supersedes that snapshot:
  sharing includes accepted OFF/disabled check behavior and does not restore
  the old check on end. Macro Q58 retains checks only where that independent
  AI-specific rule does not apply. AI cannot take input from a paused macro.
- [SSH EOF/close rules](../specification/transports-and-authentication.md)
  distinguish receive-direction EOF from connection closure. Final decoder/parser
  processing and log-tail flushing do not themselves establish a macro line
  delimiter. Q67 proposes the macro-specific no-more-input outcome while
  preserving disconnect termination and the prompt/completed-line distinction.
