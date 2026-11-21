# Terminal automation macros: accepted specification

Published as [GitHub issue #3](https://github.com/TabTty/TabTty/issues/3)
with ready-for-agent on 2026-11-14. The [published body](macro-github-issue.md)
contains the confirmed specification and testing decisions.

The 2026-11-19 revision specifies remembered macro trust. The 2026-11-20
revision incorporates [Q78–Q88 on shared macro constants](macro-issue.md#shared-macro-constants-follow-up)
for the macro-enabled release.

Final review confirmed on 2026-11-14. Product decisions Q1-Q77 in the
[interview](macro-issue.md) are accepted, including all Q70-Q77 recommendations.
Q69 authorizes drafting routine language, API, resource and validation details.
Those details and the primary testing seam are confirmed for implementation
handoff. This contract applies to macro-enabled builds beyond the initial release.
See the [implementation status](../specification/implementation-status.md) for verification.

Read with the [language and API](macro-language.md) and
[budgets and acceptance cases](macro-validation.md). Those documents form one
accepted contract. The interview preserves decision history; this document describes the
resulting behavior. Initial session, authentication, vault, terminal, logging,
broadcast, AI and SFTP contracts continue to apply except for the explicitly
identified future-macro adaptations below.

## Purpose and boundaries

A trusted, locally authored or reviewed `.tttym` file automates one new terminal
session using an existing saved connection-profile file. A run can assemble
strings, loop and branch, observe received text, send text/passwords, wait,
obtain time/source/argument information, ask the user for ordinary input or a
decision, and minimize/restore its current host window.

Use a TabTty-owned interpreter with whole-source checking and explicit runtime
pause/Stop checkpoints (Q62, ADR 0004). The language is C#-style, not full C#.
It does not expose arbitrary .NET members, local files, processes, network
clients, clipboard access, custom classes, source imports, user threads/tasks,
or AI/SFTP control. The application reads explicitly declared files of
non-secret, read-only shared macro constants during launch. Required profile
reads, vault retrieval and session recording are also application operations.
No untrusted-code sandbox or safety of arbitrary remote commands is promised.

Execution is attended: a visible application hosts each run and normal host-key,
vault-unlock and authentication interactions remain available. No headless mode,
secret command-line arguments or automatic host-trust acceptance is introduced.
One invocation creates a distinct run; repeated launches never attach to an
existing terminal or share script variables. A run has at most one connection
attempt. A second `Connect` is a recoverable `ConnectionAlreadyAttempted` error,
including after failure; restart the macro to start another attempt.

## Loading, launch and source identity

Support the Run Macro file picker, attended command-line launch, and an optional
Windows `.tttym` association. Windows/user choice controls that association.
Use this CLI shape, with Windows argument parsing and no second shell
interpretation:

```text
TabTty.exe --macro "C:\Ops\inspect.tttym" -- "server group A" ""
```

The source path is a single argument; arguments after `--` are ordered ordinary
strings, including empty strings. In-app launch uses one editable item per
argument, not an undocumented shell command line. File activation supplies an
empty argument list. No command-line execution-result protocol, exit-status
automation or detached/background execution is promised.

Resolve a relative launch source against the invoking process's captured working
directory before forwarding activation to an existing instance. Forward its
absolute path and parsed arguments, not a shell command. Existing activation
routing selects the most recently active surviving terminal window, creating
one if needed. Existing blocking application modals retain their bounded FIFO
activation behavior; macro dialogs do not become application-wide modals.

The receiving window applies the same macro trust decision to the file picker,
Explorer, command-line startup and forwarded requests. Forward macro requests
only between instances of the same user and integrity level. Use an envelope
version that receivers without this policy reject. The receiver captures the
source and its declared constants files and computes their identity itself.

The file picker authorizes reading the selected file. For an external request,
TabTty may read an ordinary local file to identify it. Before any operation that
could contact a remote source, ask whether to open the displayed path for this
request, with **Cancel** selected. This includes UNC paths, mapped network
drives, redirects and cloud files that need downloading. Establish local access
without following an unchecked redirect or probing a share, and keep that
guarantee through the open. If locality is uncertain, ask first. Cancelling
leaves the remote source untouched. Execution requires approval for the
captured source, declared constants and ordered invocation arguments.

Read one bounded source snapshot, detect the longest matching BOM first, and
strictly decode UTF-8 with/without BOM or BOM-marked UTF-16LE/BE/UTF-32LE/BE.
No BOM means UTF-8. Reject malformed input without repair or legacy guessing.
Reject a detected incomplete/changed read; do not claim an atomic filesystem
snapshot against arbitrary concurrent in-place writes. Compute SHA-256 over
the exact encoded bytes, including any BOM and original line endings. Decode,
validate and execute from that same snapshot. Keep it through any user review
and never reopen the path to execute it. External edits affect later launches.

### Capturing declared constants

Read the version and leading `#constants` declarations from the captured
macro. Check the declaration syntax, names and count before opening the
declared paths. Resolve relative paths against the captured macro source
directory. Accept absolute Windows paths. Apply the macro path rules for
`.`, `..`, environment variables and `~`. A later `SetBaseDirectory` call
affects runtime operations. Each declaration identifies an ordinary file.
Reject directories, devices, pipes and other special stream targets.

Apply the remote-read rule to constants files on every launch route, including
the picker. Selecting a macro permits reading that macro. Before contacting
any share, following a redirect of uncertain locality or downloading a cloud
file, obtain permission to read the displayed dependency path for this launch.
A single confirmation may list several such paths. **Cancel** is the default.
A remembered execution decision still requires read permission for any path
that may contact a remote source. If the user cancels a read, end the request
before contacting that path, creating a run or saving a trust decision.

Capture each distinct normalized absolute dependency path once per launch.
Reuse the captured bytes for identical normalized path spellings. Preserve case
distinctions in case-sensitive directories. Enforce the bounds as files are
loaded. [Admission and accounting](macro-validation.md#admission-and-accounting)
Decode each file independently using the same strict BOM rules as the macro
source. Hash the exact bytes of each file, including any BOM and original
line endings. Parse all definitions and bind the read-only values to their
declared aliases. Check every member, including members unused by this macro.
A missing or unreadable file, detected change during its read, decoding
error, invalid definition, unknown reference or type error ends the launch
before execution review or run/tab creation. Identify the file and, where
available, the error location. See the [constants file grammar](macro-language.md#constants-file-format).

Capture the files individually. The set can contain versions written at
different times. Bind the exact captured combination for validation, review
and execution. A later edit, even during review or a pause, affects a future
launch. Overlapping runs each keep their own validated values.

Validate the required version, entire grammar, types, names, permitted members,
function call graph and resource admission before running any statement. Dead
branches and unused functions are checked. Source errors cause no connection,
send, macro dialog or macro-requested window action. Source diagnostics can be
shown by the application. Runtime input/value errors are distinct from source
errors; constant folding cannot change whether an error is catchable.

Once source validation, execution approval and resource admission succeed,
allocate the new run/tab with the following values.

| Information | Meaning |
| --- | --- |
| `Macro.FullPath` | Absolute normalized path used to load the source; no claim of resolving every symlink to its physical target |
| `Macro.FileName` | Final path component including extension |
| `Macro.BaseName` | `Macro.FileName` with its final dot and following characters removed when the dot is neither the first nor the last character. Otherwise, use the whole filename. |
| `Macro.Args` | Run-local copy of invocation arguments |
| `Macro.RunId` | Non-secret unique run identity, distinct from source and session generation |

For example, `inspect.tttym` yields `inspect`, `inspect.prod.tttym` yields
`inspect.prod`, and `.macro` yields `.macro`. This rule applies to the metadata
of loaded source.

These values survive file rename, base-directory change and tab movement.
Source location diagnostics use one-based lines/UTF-16 columns, with CRLF one
line break and CR or LF one each. BOM is not a source column.

### Remembered macro trust

An allow or block applies to the exact macro source bytes, the contents of
each declared constants file in declaration order, and the ordered invocation
arguments. The source itself binds every alias to its declared path. Identical
copies loaded from another location with the same arguments match the same
decision. The resolved location is displayed at review. If a relative path
selects different bytes at the new location, look up that combination's
decision. Editing a single unused constant, whitespace, a line ending or a
BOM can change the match. Restoring all the original bytes and arguments
restores their previous decision across all launch routes.

Compare the strings after Windows argument parsing or picker entry. Preserve
argument boundaries, order, case, whitespace and Unicode spelling. Thus
`[]`, `[""]`, `["a b"]` and `["a", "b"]` are four different lists. CLI quoting
that produces the same parsed strings gives the same match.

| Decision for this content and argument list | Launch behavior |
| --- | --- |
| Allow | Run the validated snapshot at ordinary user privileges without an execution confirmation. |
| Block | End the request before creating a run or tab. The picker also honors the block. |
| No decision | Offer **Run once**, **Always allow with these arguments**, **Always block with these arguments** and **Cancel**. Cancel is the default. |

Show the full source digest, normalized absolute path and all ordered arguments
on the review screen. For each declared file, show its alias, resolved path
and full SHA-256 digest and allow inspection of the captured declarations. Show the
combined constants version digest defined below so the user can identify
this version in Settings. Render quotes, control characters, empty strings
and an empty argument list unambiguously. Let the user inspect the captured
macro source. Explain that the decision covers identical copies of these
files opened anywhere with these arguments, including later external
requests. Moving the macro can change
which profile a relative path selects. The resolved profile has its own trust
decision when the macro calls `Connect`. A macro can issue commands and submit
saved passwords. The usual SSH host-key review, vault unlock and authentication
rules apply on every connection, including SSH's continuation rules when a
saved credential is unavailable. [SSH authentication](../specification/transports-and-authentication.md#authentication-and-user-facing-messages)

Capture the final, validated argument list before looking up the decision. The
picker can combine review and argument editing. An edit invalidates the review
and requires lookup for the edited list before admission. Initialize `Macro.Args`
from the approved list. Later script edits to that run-local copy do not alter
the remembered decision.

**Run once** uses the reviewed source, constants and arguments. **Cancel** or
closing the review ends the request. Neither saves a decision. **Always allow**
saves the decision before starting this run. **Always block** saves it and ends
the request. TabTty accepts decision changes only through its local UI.

In Settings, group decisions by source digest. Within a group, display each
decision, its fingerprint and its constants version digest when applicable.
Let the user enter an argument list to find every matching decision in the
group. Recompute a candidate fingerprint with the entered arguments and the
stored digests for each record. Compare the result with that record's
fingerprint. The user can distinguish two versions with the same source and
arguments by their constants version digests and forget either decision
individually, even after deleting the source or the constants files.

Settings also offers a single reset for all remembered macro allows and blocks
for this Windows account on this PC. Forgetting one entry affects its exact
combination. Resetting all entries returns every combination to review on its
next launch. Order these operations and launch admission across windows so pending reviews respect
each individual removal or new block. A macro review already
open when the reset completes expires, including one for a previously unseen
combination. An old dialog cannot admit a run or recreate a decision. Admitted runs
and connections continue. The reset affects only macro decisions. Profile
trust and profile registrations retain their own lifetimes.

#### Fingerprints and compatible records

Use SHA-256 over the concatenated fields in the orders below. Tags are exact
ASCII bytes. Each digest occupies 32 raw bytes. Encode counts and lengths as
unsigned 32-bit big-endian integers. The argument encoding begins with the
argument count. For each argument in order, append its strict UTF-8 byte
length followed by its UTF-8 bytes, preserving the original string, without
adding a BOM.

| Source declarations | Decision fingerprint |
| --- | --- |
| Zero | Hash the tag `TabTty/MacroTrust/v2`, the source digest and the argument encoding. Consult only the v2 record at the resulting fingerprint. The existing v2 allow and block records continue to match. |
| One or more | Compute a constants version digest by hashing the tag `TabTty/MacroConstants/v1`, the declaration count and one file digest per declaration in source order. Include repeated declarations under different aliases in their respective positions. Hash the tag `TabTty/MacroTrust/v3`, the source digest, the constants version digest and the argument encoding. |

For a launch with declarations, consult only the v3 record for that exact
fingerprint. A missing v3 decision goes to review even when a source-only or
v2 decision exists. The constants and trust tags version the stored
identities independently of the `#tttym 1` language header.

Store one Windows Credential Manager generic credential per fingerprint,
scoped to the current Windows user on this PC (`CRED_TYPE_GENERIC`,
`CRED_PERSIST_LOCAL_MACHINE`). Use the fingerprint in its versioned target
name. Verify the bounded record's version, source digest, fingerprint,
decision and, for v3, constants version digest against the lookup before
using it. Store the fixed-size digests and decision metadata. Keep paths,
source text, constants file contents, argument values and passwords out of the record.
Individual removal and reset cover both formats. Trust survives sign-out.
Copying a macro, settings, profile or portable vault to another PC does not
transfer its approval. Windows protects this store at the user-account boundary.
Software running as that user
and administrators with control of the account are within that boundary.
[Windows credential types and persistence](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)

An elevated instance still honors blocks. Before executing with elevated rights,
require an explicit one-time decision in the elevated window, even for a known
allow. Explain that this run would have administrator privileges. A remembered
decision for ordinary use cannot authorize elevation.
[Windows integrity levels](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)

Look up the current record for each launch. A missing record triggers review.
An unreadable store, invalid record or failed decision write stops the launch
with a localized error. Do not fall back to cached decisions, file metadata or
ordinary settings. Rechecking or repairing the store must be possible without
executing a macro. [Windows credential lookup](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw)

## Connection and path handling

`Connect(profilePath)` explicitly requests the run's only connection. Resolve
relative paths against a run-local base initially equal to the source directory.
`SetBaseDirectory(path)` resolves a relative new base against the current base;
`ResetBaseDirectory()` restores the captured source directory. Normalize `.` and
`..`; do not expand environment variables or `~`, change process working
directory, or alter other runs. Existing Windows absolute/UNC/long-path rules
apply; invalid paths report errors without fallback.

The [file path part functions](macro-language.md#file-path-parts) extract the
directory, base name and extension from a string. Splitting uses the host OS's
separator and root rules and requires no file-system access.

Apply the [profile trust contract](../specification/profile-trust.md) when
`Connect` resolves its profile. Look up the reviewed location and file version
using the same trust records as a manual connection. When review is required,
offer **Connect once**, **Trust this version and connect**, and **Cancel**.
Connect once authorizes this attempt. Trust this version and connect also
remembers the reviewed location and version. Both choices leave an unregistered
profile unregistered. Cancel ends the attempt before recording preparation,
transport startup or credential use. Use the approved snapshot for the attempt.

Load the profile without registering it, rewriting it or changing its saved
settings. Preserve existing bounded basic load checks and runtime checks; full
editor validation is not silently added to every connection. Capture the same
immutable session settings and generation used by ordinary profile connection.
Profile-relative log destinations remain relative to the profile directory,
regardless of the macro base directory.

Attach receive/alternate-screen observation before transport startup can produce
terminal data. SSH early terminal data is eligible while connection startup is
pending. `Connect` returns only at existing transport readiness: SSH shell/PTY
startup complete, Telnet TCP ready, or serial open/configuration complete.
Telnet readiness does not prove account login. The macro cannot send terminal
input until readiness, but startup history is available to its first wait.

The optional positional arguments `logDirectory`, `logFilenameTemplate`, and
`allowLogOverwrite` follow `profilePath`. See the [recording contract](receive-logging-issue.md#log-filename-template-redesign)
for resolution and overwrite rules. All selected files open before transport
startup. A preparation error fails `Connect` immediately. A recording start
failure detected on the connected terminal disconnects the session, including
on resume or retry. An explicit retry after a later write error uses the
captured settings. A successful retry still requires a separate macro Resume.

A caught pre-connection failure does not create a usable session or authorize a
second attempt. Local dialog cancellation is reported without automatic repeat.
Actual disconnect of an established session permanently ends the macro.

## Sending and credential use

`Write(text)` sends its text without an added ending. `WriteLine(text)` appends
one captured profile Enter ending, even when text already ends in a newline.
Embedded CR/LF/TAB and explicit escaped control characters are intentional
input; do not convert embedded newlines to the Enter setting. The captured
transmit encoding and existing transport conversions still apply. This is not
a raw-packet API, bracketed-paste API or general terminal-key API.

Before any payload, validate and encode the entire logical send using a clone
of the current session encoder state, including an appended ending and required
codec closure. The captured encoding/Enter settings do not freeze codec or
Telnet negotiation state. Reject
unencodable or over-limit input without sending a prefix or changing live codec
state. Use the common ordered send owner; protocol replies remain possible and
are never mistaken for manual/macro input. No implicit line pacing is added;
authors may use Sleep between sends. Submission completion does not prove remote
execution, command completion or successful login.

Ordinary macro text obeys the session's current effective local-echo behavior,
including accepted temporary changes and Telnet negotiation; password operations
generate no local echo, including their appended ending. Stop cancels unsent
input at safe existing character/codec boundaries without synthesizing Ctrl+C,
Escape, newline, exit or disconnect solely because a healthy run ended. A script
may explicitly send a control character; that is a separate script operation.
Pause lets an already-started logical send finish (Q30), showing Pause requested
until then; Stop/Disconnect remain available. Release input ownership only after
necessary send cleanup, not before a second writer can safely operate.

### Profile password and explicit references

`WritePassword()` / `WritePasswordLine()` use the saved-password name in the
profile loaded for this connection attempt. The name is fixed for the run.
It does not depend on success/failure, the chosen authentication method, or what
password the person corrected in an authentication dialog. A password reference
can be the default even when public-key authentication established the session.
The secret is never an ordinary string property, return value or interpolation
operand. A failed connection still cannot transmit through an unavailable session.

The overloads taking a string use that explicit password name. Each send
looks up the exact name in the application-selected vault and checks the
password kind just before use. Application-committed edits, deletion and reuse
of the same name, and selection of another vault affect later lookups. An edit
to the profile file leaves the default name captured by the running macro
intact. Missing names, wrong kinds, stale stores, and unlock cancellation fail
visibly and recoverably. The selected-vault revision and role checks still
apply until each credential handoff commits.

If the selected vault file is missing or zero bytes, `WritePassword` and
`WritePasswordLine` fail before sending and raise a catchable
`CredentialUnavailable` error. Leave the configured vault path and file as
observed. For an uncaught error, show the source location and the observed vault
state in the macro execution status, with guidance to select the original vault
or restore its backup. Use the shared unlock prompt for an existing locked vault.

Telnet/serial profiles have no initial saved-password binding. Their default
password calls report `MissingProfilePassword`; explicit-reference calls can
perform terminal-level login. Imported private keys, key passphrases, master
passwords and keyboard-interactive answers are never terminal password defaults.

For a password entry, the vault-management Copy Macro Reference action copies
its name as an escaped string literal suitable for a password operation in an
external editor. Existing unlock and management rules apply.

Hold references through waits/pauses, not prepared secret plaintext. If a pause
intervenes before send commitment, dispose any prepared secret and reacquire on
resume. Once sending starts, follow the in-flight-send rule. End transient use
and clear owned buffers on completion/error/cancellation. Do not claim erasure
of every runtime/OS copy. No new retention of authentication-time passwords is
introduced. Remote echo can expose sent secrets in terminal output, recordings
and explicitly shared AI output; the feature does not redact remote data or
modify Raw logging. Before the first explicit-reference password submission over
Telnet/serial in a run, show a persistent nonblocking notice that the transport
provides no TabTty encryption and remote echo may be recorded/shared (Q77).
Keep Stop available. The profile trust decision takes place at connection time.
The password notice requires no per-send approval.

## Receive text and matching

Keep a non-consuming macro observer separate from display, scrollback, search,
highlighting and every recording mode. Only received terminal payload is input:
exclude local echo, SSH authentication/messages, SFTP data, diagnostics and UI
actions. Keep SSH DATA and stderr decoder/parser state independent and merge
completed received units in the existing session order. Do not concatenate Raw
bytes and decode them again.

Observe decoded literal text and delimiter/control classification before
screen-editing effects. Remove complete terminal sequences and other controls
except TAB and line delimiters. A sequence-generated display effect, such as
REP copying the preceding character, contributes no macro text. Likewise screen
erasure, cursor movement, wrapping and received alternate charset selection do
not reconstruct a display string for matching. Retain actual decoded text and
TAB; `abc<BS>X` becomes `abcX`.

CRLF is one delimiter; LF and lone CR are delimiters. CR may close a macro line
immediately; suppress its immediately adjacent LF from the same receive source,
including across transport chunks. An intervening received unit from that source
breaks the CRLF pair; other-source activity neither completes nor breaks it.
For example DATA `x<CR>`, stderr `y`, then DATA `<LF>` yields a completed `x`
line and an unfinished `y` line: the last LF completes the source-local CRLF
pair rather than adding a second delimiter. Delimiters are
not part of match text. Do not synthesize a delimiter at EOF, chunk end, pause,
send, resize, receive-boundary marking or decoder flush. Use the session codec's
existing malformed-receive handling; macro source decoding remains strict and
is a different concern.

The source contract preserves prompt matching even without a newline. The two
wait modes differ only in line eligibility:

| Mode | Eligible received text |
| --- | --- |
| Completed-line | A receive line with an actual received delimiter |
| Prompt | Completed lines and the current unfinished line |

Both allow substring matches within one line. No cross-line/multiline match or
full-screen recognition is promised. Normal-screen full-screen programs can
remain undetectable. Observed alternate-screen entry, including modes 47/1047/
1049, permanently stops the macro even if exit follows in the same receive batch
or the run is paused/waiting for a dialog. Cursor-save-only mode 1048 does not
trigger that stop. Keep the healthy connection, remote application and logging.
If this happens during SSH terminal startup before Connect returns, stop the
macro continuation without using user-Stop's attempt-cancellation path solely
for that screen event; let healthy session startup reach its normal result.
An independent startup/transport failure still follows ordinary session rules.

Keep history from startup and between waits. A successful wait consumes through
the match end, including earlier unmatched text; retain following text. A failed
wait/timeout does not silently consume text. `MarkReceiveBoundary()` advances
the match-start floor to the currently admitted tail before a later send. It
does not guarantee that future text was caused by that command, clear the
terminal/logs, or remove required parser state. Keep the original intact receive
line as the regex subject and restrict match start to the cursor/boundary floor
(Q72). Consuming `abc` in `abcdef` does not make `^def` match; `(?<=abc)def`
can match. Retained lookbehind context remains available, but neither a match
body before the floor nor context across a history gap is permitted.

For an alternative wait, inspect eligible unconsumed lines in receive order,
then select the earliest match start within the earliest matching line, then
candidate declaration order. Return a zero-based alternative index, match text
and captures. Evaluate against immutable text snapshots; update scheduling may
coalesce still-unstarted searches, so prompt results can depend on available
receive batches. Once committed, a match is never lengthened or withdrawn by
later input. `foo$`, a negative lookahead and a greedy repetition all describe
the current subject; they do not predict future bytes or prove a stable prompt.

Empty literal candidates are invalid. An actual regex result of overall length
zero is `ZeroLengthMatch`, not a successful wait or a match silently skipped in
favor of a later result. Empty captures inside a positive-length result remain
valid. A pattern need not be prohibited merely because some other input could
give an empty match; validate actual results. Pattern syntax and literal-candidate
value validation always occur when their factories are evaluated, including
literal arguments. InvalidPattern/InvalidArgument are catchable consistently;
macro source syntax/type checking does not evaluate those factory calls.

Use the .NET 10 left-to-right regex dialect, invariant culture, case-sensitive
by default, with optional IgnoreCase and supported inline `i/m/n/s/x` flags.
No RightToLeft, ECMAScript, runtime option for unlimited evaluation, or switch
to NonBacktracking is exposed. Lookaround/backreferences are supported; exact
budgets are in validation. Timeout is approximate per engine operation and
separate from the response-wait deadline. Regex work never runs on UI, receive
or recording owners. Captures are bounded copies, not a retained live match
object that pins unbounded old history.

### Receive EOF

At receive-direction EOF, finalize existing source decoders/parsers once in the
session's DATA-then-stderr order, then evaluate retained eligible text. Prompt
waits may use an unterminated final fragment; completed-line waits cannot.
If no eligible match can be found, return `EndOfInput` immediately even when the
wait was indefinite. EndOfInput takes precedence over WaitTimeout when EOF is
already admitted and a zero-timeout search finds no eligible match. Future waits
may still consume retained matches before
returning EndOfInput. EOF does not itself disconnect; actual close/disconnect
still permanently stops the run. An SSH exit-status message alone is neither
macro text nor proof of receive EOF.

### Bounded history and loss

History and a single receive line have independent bounds. Normal consumption
can release no-longer-needed complete lines without reporting loss. If retaining
required history or its line context would exceed a bound, request macro pause,
retain a bounded recent tail, and record explicit gap intervals. Terminal receive,
display and healthy recording continue. Report one notification per unresolved
incident rather than one per dropped unit. A stream may continue dropping older
history while that incident is unresolved; account the growing gap.

Continue acknowledges loss and rechecks retained unconsumed history for a pending
wait; End permanently stops the run. Never concatenate across a gap, treat a
truncated line as an intact line, replay a command, or clear unrelated holds.
Continue acknowledges losses through its coordinator cutoff; later new loss is
a new incident (Q71). Skip a truncated line through its next actual delimiter
in both wait modes (Q73). Its retained suffix is not a searchable new subject.
Explain that this can miss a final newline-free prompt. A huge line is a macro-history problem,
not by itself a reason to disconnect a healthy terminal or stop its logger.

## Run state, timers and user actions

A run owns one interpreter continuation, its variables, one in-progress macro
operation, a receive cursor and a set of pause holds. Waiting for data/sleep/a
dialog is not itself a user pause. Show Running, Waiting, Pause requested,
Paused, Stopping and terminal result states, with the specific pending operation
and applicable reasons. Use stable internal identifiers and EN/JA resources;
never branch script behavior on localized UI labels.

Pause preserves variables, receive history and exclusive input. Receive/display,
healthy recording, independent SFTP, wall clocks and real elapsed time continue.
Freeze remaining Sleep/finite-response-wait budgets. Remote commands, network
deadlines and OTP expiry cannot be frozen. Resume never replays a completed or
partially sent operation. An already-started Connect and its necessary
trust/authentication/unlock interactions may finish while continuation is held;
answering those dialogs may send authentication data. Stop cancels only this
run's attempt/waiter, preserving other shared-unlock users (Q70).

Keep UserPause, RecordingFailure/RecordingReady and HistoryLoss independent.
Successful recording Retry changes Failure to Ready. Explicit Resume clears
UserPause and Ready, never unresolved recording failure or unacknowledged loss.
Continue clears only its acknowledged history incident; execution proceeds only
when no hold remains (Q71).

Preserve committed matches/results/timeouts through pause, but Stop invalidates
undelivered results. Discard uncommitted worker results after pause/cursor
generation changes and re-evaluate on Resume. At expiry, evaluate a frozen
cutoff of input admitted before the deadline. Later input cannot rescue it;
pause freezes only the remaining unsettled local budget (Q74).

Ordinary errors can be caught only where documented in the language contract.
An uncaught error ends the macro with source location and a non-secret reason.
User Stop, disconnect and alternate-screen termination bypass catch. A known
zero-payload send rejection is catchable; possible partial or uncertain delivery
permanently ends the macro without automatic replay, preserving a healthy
session after required cleanup (Q76). Show the
reason and cancel pending dialogs/unsent work without returning a fabricated
normal result. No automatic retry or restart follows. A healthy connected tab
survives success, Stop and ordinary failure. A macro that finishes before making
a connection leaves its run result visible until the person closes the tab.

While a macro owns the session, block human terminal keys, text, paste, mouse
input to the remote application, and broadcast input. Browsing, selection,
copy, app controls, Pause/Stop/Disconnect and protocol-required replies remain
available. Pausing does not hand input to the person or AI. Necessary send
cleanup may delay release after Stop; show Stopping and retain Disconnect.

### Dialogs and windows

`Dialog` presents inert title/message text, a fixed supported button set and an
optional ordinary single-line input field. It returns the button and whether a
valid text value exists. Accepted empty input is distinct from no text. Cancel,
window close and Esc return Cancel with no text; Stop closes the request without
returning a normal answer. Default button is Cancel when offered, else No for
Yes/No, else OK. No password-masking/secret-input or arbitrary button API is added.

Wait only for the originating run. Identify that run and its current host; other
tabs/windows remain usable. Each run has at most one outstanding dialog. A tab
move moves/reparents its outstanding request without losing typed input. Restore
a minimized host to its prior normal/maximized state when action is needed, but
do not promise Windows foreground focus. An answer does not clear other holds.

Minimize/Restore target the window currently hosting the run's tab, including
after moves. Restore is a no-op unless minimized and preserves pre-minimize
normal/maximized state. Defer a macro Minimize while that window has any pending
user-action dialog. Reevaluate the host before a deferred action. User window
controls remain available. A pending obsolete window action must not affect a
different window or stopped run. Normal completed actions are not undone.

Include active runs in the existing combined close confirmation even before
connection. Hold new macro operations at their safe boundary during review;
started sends finish and Q70 governs started connection attempts. Cancel close
releases only the close-review hold. Confirm close stops the run and follows
ordinary connection/SFTP cleanup. Moving a tab requires no close confirmation
(Q75).

## Recording and other features

Use profile-selected recording formats. The macro default base name begins with
the captured macro basename and profile display name joined by a hyphen. Use
the shared [recording contract](receive-logging-issue.md#log-filename-template-redesign)
for destinations, template expansion, suffixes, and start failures. Templates
expand explicit naming data such as the macro basename and connection profile
name.

A user's deliberate recording Pause leaves the macro running. Resume creates
new parts without backfill. A later write failure in an active part pauses the macro;
successful explicit Retry is necessary, followed by separate explicit macro
Resume. Show the gap and failed/new-part status. Normal Text's mixed-line skip,
Raw/Terminal eligibility and incomplete I/O handling retain the recording
contract. No macro execution-trace file is introduced. Show run state, location
and bounded error details without copying arguments, sent text or captures into
diagnostics automatically.

Macro ownership preserves broadcast checks but excludes the session from newly
captured eligible recipients; other eligible selected sessions still receive.
Show unavailable counts. Never modify an already frozen recipient set. On macro
end, normal eligibility applies to later operations only.

Apply the later [AI collaboration contract](../specification/ai-collaboration.md)
independently: read-only sharing of a macro-owned session is possible, but AI
control is unavailable while executing/paused. Starting AI sharing is forbidden
for checked targets of active broadcast. Permitted AI sharing turns that tab's
check OFF/disabled and leaves it OFF after sharing ends. This AI rule supersedes
check retention only for AI-shared tabs; it is not a change to ordinary macro
eligibility. Macro Stop never grants AI control automatically.

Macro Stop/success leaves independent SFTP work on a healthy session running.
Confirmed disconnect/tab close ends it under the SFTP contract. No macro SFTP
API, AI-launch API or simulated desktop operation is introduced.

## Accepted final product decisions: Q70-Q77

The user accepted all eight recommendations on 2026-11-14. They are incorporated
above and are required by the acceptance matrix, not conditional alternatives.

| Question | Accepted rule and reason |
| --- | --- |
| Q70: Pause during connection/authentication | Let an already-started connection attempt and its necessary host-trust/auth/unlock interactions reach a result while the macro continuation is held. Accepting such a dialog may still send authentication data. Do not start the next macro operation until resumed. Stop cancels this run's attempt/waiter and never cancels another run's shared unlock need. Do not retain an entered plaintext secret merely to wait for Resume. This preserves ordinary remote deadlines and avoids restarting authentication on Resume. |
| Q71: Multiple holds and repeated overflow | Keep UserPause, RecordingFailure/RecordingReady and HistoryLoss independent. Recording Retry success changes failure to ready; explicit macro Resume clears UserPause and a ready recording hold, never an unresolved recording failure or unacknowledged gap. Continue acknowledges losses through its coordinator cutoff and clears only that history incident; a later new loss is a new incident. Continue may resume only if no other hold remains. |
| Q72: Regex context after consumption/boundary | Keep the intact original receive line as the subject and restrict match start to the unconsumed/boundary floor. After consuming `abc` in `abcdef`, `^def` does not match but `(?<=abc)def` can. MarkReceiveBoundary advances the start floor without redefining line start or forbidding retained lookbehind context. No match body may begin before the floor; no context may cross a history gap. |
| Q73: Truncated line after history loss | Skip an affected line through its next actual delimiter for both prompt and completed-line matching. Retain its recent suffix only for loss reporting/accounting; do not search it as a new intact subject. This is simple and avoids false anchors/lookbehind, but can miss a final newline-free prompt; display that limitation with Continue/End. |
| Q74: Match/deadline/pause races | Preserve an already committed match/result or timeout through pause; Stop invalidates any undelivered result. Discard uncommitted worker results when the operation's pause/cursor generation changes and re-evaluate retained input on Resume. A deadline settled before pause is not revived by later input. At a deadline, evaluate a frozen cutoff of data admitted before expiry before declaring timeout. Receive after expiry cannot rescue it; local remaining budget freezes when pause is admitted. |
| Q75: Closing a run before connection | Include an active macro in the existing combined close confirmation even before a transport exists, with one confirmation per affected close scope. While reviewing the close, hold new macro operations at the ordinary safe boundary; started sends finish, and Q70 governs a started connection. Cancel close releases only the close-review hold. Confirm close permanently stops the run and follows existing connection/SFTP cleanup. Do not prompt when merely moving a tab. |
| Q76: Partial or uncertain transmission failure | A known zero-payload rejection is catchable. Once a send may have delivered a prefix, end the macro with a clear uncertain-delivery result and do not allow catch to replay/continue automatically. Preserve a healthy session after necessary cleanup; actual transport failure still disconnects under its normal rule. |
| Q77: Unencrypted terminal password notice | Before the first explicit-reference password submission over Telnet/serial in a run, display a persistent nonblocking notice that this transport provides no TabTty encryption and remote echo may be recorded/shared. The notice adds no per-send approval. Profile trust follows the connection contract above. Keep Stop available and document the risk at the password APIs. SSH remote echo can also be recorded; SSH encryption is not a log-redaction promise. |

The final review confirmed this contract, its language/API and budgets, and the
primary application-facing run testing seam on 2026-11-14. The specification is
ready for implementation handoff and published with ready-for-agent as issue #3.
Initial-release inclusion and implementation still require explicit
authorization; neither is inferred from approving this specification.
