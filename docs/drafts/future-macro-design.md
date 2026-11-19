# Preserve a path for future macros

The [macro feature interview](macro-issue.md) opened on 2026-11-12. Q1–Q77 and
the [shared macro constants follow-up](macro-issue.md#shared-macro-constants-follow-up)
are accepted and preserve the constraints here. The interview covers
specification only; it does not authorize a macro runtime or change the
initial-release scope.

That interview accepts trusted macros, attended execution, one new session per
run, reference-based secret submission and macro-based session-log names. It
selects a defined C#-style language boundary (ADR 0004), with inferred fixed
types, only long/double numeric types, and a TabTty-owned interpreter (Q62);
BOM-aware UTF-8/16/32 source
decoding; millisecond waits; and minimize/restore of the current host window.
The application-selected vault remains the only vault. Received-line/prompt
matching permits substrings within one line, preserving TAB while removing
other controls. Pause scope/send boundaries/local timer freezing and explicit
recording recovery are accepted; response waits default to indefinite.
History exhaustion uses a bounded recent tail, explicit gaps and a user
Continue/End decision. Source filename information, ordinary run-local dialogs
with optional text input, .NET regex/alternative waits and Write/WriteLine-style
sending are accepted. Alternate-screen entry stops the macro while preserving
a healthy terminal session and its recording. Exact syntax/bounds are now
specified and accepted in consolidated final review on 2026-11-14.
Q47-Q58 also accept numeric conversion/arithmetic rules, common C#-style string
forms, same-file typed helper functions, recoverable try/catch without finally,
dialog defaults/movement, deterministic alternative ordering and positive-length
matches, non-secret string invocation arguments, and independent intentional
recording pause. Macro ownership excludes a session from new broadcast recipient
sets while preserving its target check. Q59's non-secret credential-reference
copy action is accepted after clarification. The user also requests convenient
use of the profile's configured saved password, explicitly regardless of actual
authentication use or success/failure. This is a default credential reference,
not retention of a transmitted or manually corrected password. Accepted Q60-Q61
provide default/explicit password submission, captured reference identity and
current-value retrieval with visible errors;
ordinary string access to secrets and longer authentication lifetimes are not
authorized.
Q60-Q77 are all accepted. The user requested the consolidated
[behavior draft](macro-specification.md), [language/API draft](macro-language.md)
and [resource/validation contract](macro-validation.md). They include accepted
routine details under Q69 and accepted Q70-Q77 behavior choices. See the
interview for accepted decisions and the drafts for current operational details.

Final specification and testing-seam confirmation was completed on 2026-11-14;
[GitHub issue #3](https://github.com/TabTty/TabTty/issues/3) is published with
ready-for-agent. This establishes specification readiness for later authorized
implementation, without changing the initial-release exclusion below.

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Macros are deferred beyond the initial release. This is a design constraint for
the initial modules, not an instruction to implement a script runtime, macro UI,
plugin framework, or speculative execution adapters.

The selected future macro-file extension is `.tttym`. Q63 additionally selects
readable source with a required language-version declaration and execution of
one validated snapshot. These future-feature decisions do not add macro
execution, an editor or macro-file activation to the initial release.

Macros may declare external JSON files of named, read-only scalar values.
Each launch checks the complete set of definitions and uses the captured
values through the end of that run. The declaration and access syntax are in
the [language contract](macro-language.md#shared-macro-constants).

The macro-enabled design lets the user remember an allow or block for an exact
combination of source bytes, declared constants file bytes and ordered
arguments on this PC. Identical copies opened elsewhere with the same
arguments use the same decision. The user can also run the macro once.
In Settings they can forget an individual decision for a constants version
or reset all remembered macro decisions. A running
macro's `Connect` checks the resolved profile's location and version under
the shared [profile trust contract](../specification/profile-trust.md).
Trusting that version from a macro leaves an unregistered profile unregistered.
When an external request could contact a remote source, TabTty asks for
permission to read it before identifying its contents. See the [launch contract](macro-specification.md#loading-launch-and-source-identity)
and [remembered macro trust](macro-specification.md#remembered-macro-trust).

## Initial design constraints

- Keep connection lifecycle, terminal input, receive processing, and credential
  handling in modules whose interfaces can be used without simulating UI clicks.
  The initial UI uses those interfaces; a future macro caller can use the same
  session behavior and error handling.
- Keep a small session interface around profile-based connection, disconnection,
  sending input, and observing connection/receive state. Hide SSH/Telnet/serial
  details behind the transport seam already required by the three protocols.
  Final type signatures remain an implementation-design task.
- Separate ordered received data and text from display styling. A future receive
  observer must not depend on screen scraping, highlight completion, active-tab
  visibility, or reading a log file. It must not consume data away from the
  terminal or logger.
- Keep credential values behind the credential-handling module and use references
  in profiles. Future macro access must follow the security policy in
  `authentication-issue.md`; this preparation is not a promise to sandbox arbitrary
  code running as the Windows user.
- Define cancellation and connection-completion outcomes in the initial session
  design so later waiting/sending operations can stop predictably. Disconnect
  terminates a future macro run; reconnect does not automatically resume or replay
  it. UI and future macro sends must share the session's input ordering.
- Avoid requiring a macro runtime for ordinary profile connection or logging.
  Telnet connection completion means the terminal transport is connected and
  usable; it does not wait for remote textual login or prove remote account
  authentication. The user superseded the built-in Telnet login helper on
  2026-11-08. Initial Telnet and serial login is manual. Future macros can own
  prompt matching, credential submission, application-level login completion,
  and related timeouts; do not implement helper logic or restore Q129-Q130's
  discarded prompt settings in preparation for that future feature.
  A future macro can start observing the connected Telnet stream without waiting
  for a built-in login-completed event. Receive observation must account for
  output arriving with connection establishment; exact subscription/replay rules
  are future macro API decisions, not an initial hidden auto-login engine.
- Keep log-name composition separate from received-content processing and the
  file-write/pause/retry lifecycle within the logging module. The initial
  profile/time/session/part naming rule is a default policy, not a permanent
  requirement that every recording name originate from a profile name.
- Preserve a path for later user-specified naming, macro-name-based naming, or
  an explicitly supplied startup/invocation naming argument. A future macro
  caller supplies naming data to the logging module rather than creating log
  files itself. Keep destination resolution, filename validation, no-overwrite
  creation, collisions, grouping of selected Raw/Text/Terminal files, and part
  management in that module. Shared extensions do not identify a recording mode;
  naming uses the selected format and its current filename-marker policy.
- Limit naming context to explicit non-secret naming data; do not use the entire
  macro/process argument collection or credentials as a filename. Naming does
  not bypass saved-profile connection or recording-eligibility requirements.
  The initial design needs localized composition, not speculative adapters,
  a plugin framework, a template engine, or an implemented macro runtime.

## Deferred decisions

The future feature now selects a dedicated interpreter, fixed typing, a
versioned source format and reference-based secret submission. Detailed API,
receive, resource and pause/stop rules are in the consolidated draft, with
Q70-Q77 incorporated and final contract/testing-seam review confirmed on 2026-11-14.
This does not claim a security sandbox. Q77 selects a persistent nonblocking
notice before the first explicit password send over Telnet/serial in each run;
no initial Telnet saved-password setting or warning acknowledgement is added.
In particular, Roslyn/C#
scripting remains an earlier candidate, not an accepted dependency or initial
release requirement.

Invocation syntax and ordinary string arguments are specified for macro version 1.
User-configurable naming templates, name overrides/precedence and in-session
renaming are deferred beyond that version. The current filename is chosen when
its file is created; no in-session renaming is added to the initial release.

Do not let these deferred choices block initial-release specification readiness.
Readiness still requires completing the initial session/receive/credential design
and the other open initial-release branches. Implementation remains subject to a
separate explicit user request.
