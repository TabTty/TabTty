# Confirmed connection attempts and manual Telnet login

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q131-Q132 accepted as recommended on 2026-11-08. Q129-Q130 were
accepted and then superseded by the explicit removal of Telnet automatic login
in the user's Q133-Q136 answer. Specification only; no implementation, prototype,
credential generation, or runtime validation is authorized.

Read with `authentication-issue.md`, `credential-vault-issue.md`,
`connection-profiles-issue.md`, and the accepted own-SSH/OpenSSL ADR.
The trusted-profile activation policy remains: no new profile approval gate.

## Q129-Q130: Superseded by manual Telnet login

The user superseded the accepted helper on 2026-11-08, in the answer accepting
Q133-Q136. Telnet is outside built-in automatic login. Once TCP is connected
and terminal transport is available, the session is Connected and ready for
input. Do not wait for prompts, send credentials, or detect shell/login success.
Option negotiation may continue and is not an all-options-complete readiness
barrier. This state does not prove remote account authentication.

Remove the former username/password fields and vault binding, prompt strings,
case options, prompt matching/provenance machinery, stage waits, automatic-send
completion, Stop Automatic Login, and manual-takeover rules from initial scope.
Connecting Telnet must not unlock the vault or retrieve any saved secret. There
is no generic send-saved-secret command in the initial release. Remove the related
Telnet/saved-password editor warning; do not introduce a replacement connection
gate. Existing connection deadlines, terminal options, logging, and manual input
remain unchanged. Login prompts and rejection messages are ordinary received
output. Remote account logout does not disconnect TabTty unless transport ends.

Initial Telnet and serial login is manual. Future macros may implement login
sequences and their own completion/timeout rules using the shared session and
receive seams in `future-macro-design.md`; there is no initial macro runtime or
hidden helper. Do not preserve Q129-Q130's old matching defaults as future macro
requirements. SSH's protocol authentication remains separate.

## Q131: Connection deadlines and cancellation

Before transport startup, accepted Q158 in `profile-file-preparation-proposals.md`
uses a fixed 30-second profile-load budget and a separate fixed 30-second budget
for initial log-directory/all-file preparation. Keep these distinct from the
profile-configured transport budget below. Cancellation/expiry cannot be undone
by a late file result; bound unfinished-operation ownership until real completion.

Use a profile connection-wait setting of 30 seconds, configurable from
1 to 300 seconds. Apply one shared budget to DNS/address connection attempts and,
for SSH, the initial identification/handshake/key exchange. Apply a fresh budget
to later non-authentication protocol waits such as channel/PTY/shell setup.
Unrelated bytes or packets must not indefinitely reset an awaited-step deadline.
Serial open failures are reported normally; a blocking driver must not freeze
the UI, and a late open after cancellation must be cleaned up rather than used.
Log-open failures retain their existing before-transport behavior.

Do not count local host-key, vault, password/passphrase, or keyboard-interactive
dialogs against that connection budget. Do not impose a universal 30-second
client deadline on SSH user-authentication response waits either: a server may
perform delayed authentication or out-of-band approval. Keep those waits visible
and cancellable instead. Server-controlled authentication/OTP deadlines still
apply; the client cannot pause them while a local dialog is open. Handle remote
closure immediately and do not accept a late dialog result into a dead attempt.

Provide Cancel Connection for an in-progress attempt. It ends that attempt,
invalidates pending callbacks/prompts/submissions, and releases its owned
resources without another disconnect confirmation. Closing a connecting tab
still follows the separately agreed tab-close confirmation. Cancelling one
attempt does not relock the shared already-unlocked vault or stop other sessions;
the accepted Q115 rule for cancelling one shared pending vault-unlock dialog
still applies to its waiting attempts.

On failure/cancellation retain the tab and available output, with a distinct
status. Do not automatically reconnect or replay buffered input/paste. A manual
reconnect rereads the saved profile and obtains unsaved credentials again.
SSH partial authentication success is not connection success; final success
and terminal channel/shell establishment remain distinct protocol states.
Method ordering/retry limits are accepted in Q133 of `ssh-authentication-proposals.md`.

Q139 in `ssh-transport-policy-proposals.md` applies the configured protocol wait
to rekey, excluding required local trust interaction. Ordinary rekey preserves
the session and log parts without authentication or vault re-unlock. Its failure
uses disconnect handling without automatic reconnect or replay.

Q140 in `ssh-session-proposals.md` confirms SSH readiness only after successful
authentication/channel/PTY/shell setup, without guessing a prompt. Do not queue
terminal input before readiness; process legitimate early channel output.
Q143 confirms a separate fixed two-second peer-close grace for explicit local
disconnect. It is not the setup/rekey deadline or a total file-drain bound.
Cancel Connection for an attempt retains the rules above; live tab/window close
retains its existing confirmation without a second disconnect confirmation.

## Q132: Optional SSH liveness checking

Provide response-requesting SSH liveness checks after authentication and
interactive session establishment, disabled by default. A profile can enable
them with a 30-second default interval, editable from 1 to 3,600 seconds. Use
three consecutive unanswered probe intervals as the fixed initial failure
threshold, without another retry-count UI setting.

Use authenticated encrypted SSH protocol traffic, not terminal characters,
newline/NUL injection, or an assumed reply to SSH_MSG_IGNORE. Valid authenticated
traffic proves SSH-transport activity and resets the idle/failure tracking;
unverified TCP bytes do not. A valid rejection of an unsupported probe request
can establish liveness too. This checks SSH transport responsiveness, not that
the remote shell/application is making progress. Probe messages are excluded
from all terminal logs and never appear as terminal input/output.

After an interval without valid inbound traffic, send the first probe. Each
subsequent full interval without valid traffic completes one unanswered probe
window: send the next probe after the first and second, and disconnect after
the third. Do not count fictitious probes during scheduler suspension. This is
not a promise of an exact 90-second wall-clock cutoff from the last received
packet; the initial idle interval precedes the three response windows. Protocol
ordering during rekey and precise timer/queue contracts remain technical work.

On liveness failure use ordinary disconnect handling: keep the tab/output,
finish eligible logging, and require explicit reconnect. Do not infer an
application idle timeout or implement automatic reconnect. Telnet/serial do
not receive automatic keepalive characters or AYT/BREAK commands; those explicit
commands keep their existing role.

## Primary evidence and rationale reviewed on 2026-11-08

Tera Term wait/waitregex are macro commands, not evidence that TabTty needs a
built-in login helper. Its documentation warns that old receive-buffer contents
can satisfy later waits; future macro receive semantics need their own design.
Telnet RFCs define data/negotiation and echo, not standard login/password strings
or a successful textual login indication. The user's later scope change removes
the helper; no Oniguruma or macro engine is adopted.

Tera Term 5.7.1 defaults ConnectingTimeout to 0 (Windows TCP/IP behavior) and
HeartBeat to 300 seconds; its SSH2 heartbeat sends SSH_MSG_IGNORE without a
reply requirement. The accepted finite connection timeout and response-requesting
liveness option therefore deliberately differ. OpenSSH documents ConnectTimeout
for initial connection/handshake, ServerAliveInterval default 0, and CountMax
default 3; those are design references, not adoption of ssh.exe or exact timer
equivalence. RFC 4252/4256 require multi-step authentication handling and do not
mandate a 30-second client user-input deadline.

- [Tera Term 5.7.1 wait implementation](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/ttpmacro/ttmdde.c#L453)
- [Tera Term wait](https://teratermproject.github.io/manual/5/en/macro/command/wait.html)
- [Tera Term old receive-buffer behavior](https://teratermproject.github.io/manual/5/en/macro/command/flushrecv.html)
- [RFC 854 Telnet](https://www.rfc-editor.org/rfc/rfc854.html)
- [RFC 857 ECHO](https://www.rfc-editor.org/rfc/rfc857.html)
- [RFC 2941 Telnet authentication](https://www.rfc-editor.org/rfc/rfc2941.html)
- [Microsoft recv boundaries](https://learn.microsoft.com/en-us/windows/win32/api/winsock/nf-winsock-recv)
- [Microsoft ordinal comparisons](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-strings)
- [Tera Term 5.7.1 timeout/heartbeat defaults](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/release/TERATERM.INI)
- [Tera Term 5.7.1 SSH heartbeat](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/ttssh2/ttxssh/ssh.c#L7410)
- [OpenSSH client timeout/liveness settings](https://man.openbsd.org/ssh_config)
- [RFC 4252 authentication states and timeouts](https://www.rfc-editor.org/rfc/rfc4252.html)
- [RFC 4256 keyboard-interactive exchanges](https://www.rfc-editor.org/rfc/rfc4256.html)
- [OpenSSH server LoginGraceTime](https://man.openbsd.org/sshd_config#LoginGraceTime)
