# Specify TabTty's SSH client using OpenSSL

> Baseline decisions were consolidated on 2026-11-10 in the [transport and authentication contract](../specification/transports-and-authentication.md). See [implementation status](../specification/implementation-status.md) for build and validation evidence.

The 2026-11-10 findings in `technical-contract-review.md` constrain the remaining
packet, key-transition, extension, authentication, and channel contracts without
changing the accepted algorithm policy. They are source review, not conformance
or interoperability test results.

## Accepted architecture

Implement a TabTty-owned SSH client protocol and session layer in C#, analogous
in responsibility to RLogin's Cssh. Use official OpenSSL for cryptographic
operations. The decision and its maintenance implications are recorded in
`../adr/0001-own-ssh-client-with-openssl.md`.

SSH.NET and a modified ssh.exe are outside this direction. RLogin is a reference
for the architectural separation, not a selected dependency or an instruction
to copy its implementation.

Preserve terminal-channel bytes for raw receive logging and per-direction text
conversion. UTF-8 remains the default; the Tera Term encoding inventory also
applies to SSH. A local ConPTY is not part of this SSH data path.

## Confirmed initial SSH scope

- Support SSH 2 only. SSH 1 and legacy algorithms such as DSA and SHA-1
  signatures are outside the initial scope. RSA keys remain supported with
  SHA-2 signatures; RSA key type and RSA/SHA-1 signatures are distinct.
- Support password, public-key, and keyboard-interactive authentication.
- Use Q137's fixed reviewed algorithm policy in `ssh-transport-policy-proposals.md`:
  prefer ML-KEM/X25519 hybrid KEX with listed traditional alternatives, AES-GCM
  then AES-CTR with SHA-2 EtM, and no initial user algorithm editor or weak-mode
  fallback. Strict KEX is enforced when negotiated; non-supporting servers can
  connect using the accepted AES-only policy. Show negotiated choices in
  connection details rather than injecting them into terminal output/logs.
- Disable SSH compression in both directions (`none`), without a setting (Q138).
- Automatically rekey at 1 GiB in either direction or one hour, whichever comes
  first, respecting tighter per-key limits; support peer-initiated rekey (Q139).
  Preserve the session/log parts without user reauthentication. Failures/timeouts
  disconnect without automatic reconnect; changed host keys still need approval.
- Confirm first-seen host keys. For a changed saved key, ask whether to replace
  it and connect or cancel, before user authentication proceeds.
- Host-key interaction, explicit credential saving, and PEM/PKCS#8 and OpenSSH
  private-key support are specified in `authentication-issue.md`, together with
  their remaining compatibility and lifecycle decisions.
- Connect directly to the destination and provide an interactive terminal shell.
  Create each session from a saved connection profile. Each tab owns an
  independent SSH connection; sharing between terminal tabs is deferred.
- Include [SFTP v3 file transfer](../specification/file-transfer.md) from a connected SSH
  terminal tab, on a separate subsystem channel within the tab's existing SSH
  connection. See [ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md).
- Defer legacy SCP, jump hosts, port forwarding, X11 forwarding, external
  authentication agents, and agent forwarding.
- On disconnect retain the tab and displayed output. Reconnection is manual,
  without automatic input/command replay, as specified in
  `connection-profiles-issue.md`.
- Q140 requires successful user authentication, session channel, PTY, and shell
  requests before Connected/terminal input. Do not wait for a guessed prompt or
  queue pre-ready user input. Process legitimate early channel output normally.
  Use the actual dimensions and accepted terminal type/VERASE, request IUTF8
  from the transmit codec, and leave unspecified modes to server defaults.
  Do not add automatic environment/startup commands or no-PTY fallback.
- Q142 shows authentication banners and relevant protocol messages as inert
  text in per-tab connection information, outside terminal output/session logs.
  Actual terminal-channel welcome/error output stays terminal data.
- Q143 distinguishes exit notification, directional EOF, channel CLOSE, and
  transport shutdown. Process accepted output/log work, flush eligible Text
  once on receive completion, retain the tab, and never infer exit code zero.
  Explicit local disconnect uses at most two seconds of peer-close grace,
  cancels unsent input, and sends no terminal exit command. Disk draining has
  its separate contract; the grace is not a total durable-save guarantee.

## Responsibility boundary

| Area | Owner |
| --- | --- |
| Cryptographic randomness, primitive key operations, signatures, ciphers, hashes, MACs, and supported KDF operations | OpenSSL libcrypto |
| bcrypt_pbkdf for encrypted OpenSSH private-key files | Approved OpenSSH/OpenBSD source and required Blowfish/helper support |
| SSH negotiation, protocol-specific crypto composition and inputs, packet protection/state, key exchange, rekeying, and disconnect handling | TabTty SSH transport |
| Authentication exchanges, host-key verification policy, credential interaction, cancellation, and partial-success handling | TabTty authentication/session layer |
| Channel lifecycle, remote PTY, shell request, terminal bytes, flow control, resize, EOF, and close | TabTty SSH connection/session layer |
| Character conversion, terminal emulation, rendering, highlighting, logs, profiles, and future macro integration | TabTty application layers |

OpenSSL's libssl implements TLS-family protocols and is not required for this
SSH design. Using libcrypto does not provide SSH wire formats or state machines.
For example, generic ChaCha20-Poly1305 AEAD is not interchangeable with the
OpenSSH SSH packet-protection construction.

## Recommended native integration, not yet a pinned build

Use the supported OpenSSL 3.5 LTS line, fixing an exact validated patch version
for each build. The research baseline on 2026-11-07 is 3.5.9; the line's published
support end is 2030-04-08. A future implementation must recheck current patches.

Recommend a small TabTty-owned C ABI adapter around libcrypto. Keep SSH protocol
and session logic in C#. The adapter centralizes native ownership, buffer lengths,
error retrieval, provider selection, and cleanup; it is not another SSH engine.
Direct P/Invoke is an alternative if the final API boundary remains tractable.

Bundle the needed native libraries and CRT dependencies with the self-contained
application. Do not require the user to install OpenSSL or select a PATH copy.
The exact OpenSSL build, supported CPU architectures, adapter packaging, and
licensing notices remain distribution decisions.

## Protocol contracts to complete before implementation

- Separate identification, negotiation, key exchange and host-key checks,
  protected packets, authentication, and connection/channel state machines.
- Treat rekeying as transport behavior required after connection establishment,
  including server-initiated rekeying and direction-specific state changes.
- Apply Q137-Q139's accepted algorithm/compression/rekey policy while specifying
  exact algorithm negotiation, extension negotiation, strict key exchange,
  sequence handling, malformed-message handling, timeouts, and resource bounds.
- Keep protocol fields under their SSH-defined byte/string encodings. Terminal
  receive/transmit codec settings affect terminal payloads, not usernames,
  authentication prompts, passwords, algorithm identifiers, or other SSH fields.
- Distinguish terminal row/column resize from SSH channel-window byte credits.
- Define initial PTY dimensions and modes before the shell request, subsequent
  resize, flow control, remote EOF/CLOSE, and local cancellation.

## Session logging boundary

The received Raw payload boundary is terminal-channel payload after SSH
decryption, verification, and channel extraction, before terminal decoding.
Remote echo is included. There is no separate transmit log.
Raw must preserve those received bytes exactly and exclude local echo. Text
also remains receive-only (Q90 restored). Q94 accepts Terminal mode
of ordered UTF-8 text and supported VT controls in `receive-logging-issue.md`,
with Q92's local Enter CRLF and Q93's no-heuristic-deduplication rule. Q91's structured
Raw container is superseded. SSH local echo remains off in the initial release,
so that mode has no local-echo source for SSH. None of these choices
adds authentication/transport packets to the terminal-payload boundary.

Protocol packets, authentication exchanges, and local diagnostics are not
terminal payloads. Their optional presentation or diagnostics retention must be
specified separately; do not merge them into the receive stream as a side effect.
Text logs remain UTF-8 without a BOM. On the normal screen they derive content
from terminal display state after editing; a lone CR is a cursor operation,
not a text-log flush boundary. Alternate-screen Text also records received text,
with terminal controls removed or replaced. See `receive-logging-issue.md` for
finalized-line semantics and the alternate-screen fallback.

## Product decisions still to resolve

Q131-Q132 in `connection-attempt-proposals.md` confirm explicit connection/
protocol deadlines and optional response-requesting SSH liveness checks.
They are accepted and preserve the protocol ownership boundary. Q133-Q136 in
`ssh-authentication-proposals.md` accept authentication method/retry policy,
key types and import envelopes, canonical vaulted keys, and local host-key
management.

Q137-Q139 in `ssh-transport-policy-proposals.md` accept a fixed reviewed
algorithm/strict-KEX compatibility policy, no compression, and automatic rekey.
These are confirmed requirements, not implementation authority.

Q140-Q143 in `ssh-session-proposals.md` accept PTY/shell readiness, normal and
extended stderr handling, protocol messages outside terminal output/logs, and
remote/local closure. Raw preserves both supported payload sources in message
order without source markers. Display/Text/Terminal isolate each source's codec
and incomplete-control parsing, then apply completed operations to shared
terminal state. Do not infer source identities from text or promise screen
reconstruction from the headerless Raw concatenation.

- Exact algorithm/extension negotiation and native validation contracts under
  Q137, with packet-count/nonce/key-use limits and malformed-message handling.
- Detailed key/import validation limits, authentication state/round/probe bounds,
  and secret/reference schema under the accepted Q133-Q135 scope.
- Exact host-identity normalization and local record schema. Multiple approved
  key formats are supported; known_hosts import/export is excluded initially.
- Resource limits and capacity/performance targets; detailed timer/rekey/cancellation
  contracts under Q139 and Q131-Q132's accepted waits and optional
  authenticated liveness policy. Reconnection remains explicit without replay.

Private-key file support and the accepted narrow source-dependency exception
for encrypted OpenSSH keys are described in `authentication-issue.md` and the
SSH ADR. The general protocol/session boundary remains unchanged.

## Implementation acceptance work to plan

Once implementation is authorized, validate transport and authentication
interoperability before full terminal integration. Required acceptance cases
include host-key mismatch, cancellation, partial authentication success, rekey,
fragmented reads, malformed packets, sustained output, paste backpressure,
initial PTY size, repeated resize in less/top/vim, EOF/CLOSE, and cleanup.
Cryptographic and protocol behavior requires reference-vector and interoperability
checks; successfully displaying one shell is not sufficient acceptance evidence.
No such tests have been run during specification work.

## Primary references

- [OpenSSL library responsibilities](https://docs.openssl.org/3.5/man7/ossl-guide-libraries-introduction/)
- [OpenSSL releases](https://www.openssl-library.org/source/)
- [OpenSSL release strategy](https://www.openssl-library.org/policies/releasestrat/)
- [OpenSSL EVP integration](https://docs.openssl.org/3.5/man7/ossl-guide-libcrypto-introduction/)
- [RFC 4251: SSH architecture and data types](https://www.rfc-editor.org/rfc/rfc4251.html)
- [RFC 4253: SSH transport](https://www.rfc-editor.org/rfc/rfc4253.html)
- [RFC 4252: SSH authentication](https://www.rfc-editor.org/rfc/rfc4252.html)
- [RFC 4254: SSH connections, PTY, channels, and resizing](https://www.rfc-editor.org/rfc/rfc4254.html)
- [RFC 4256: keyboard-interactive authentication](https://www.rfc-editor.org/rfc/rfc4256.html)
- [RFC 8308: extension negotiation](https://www.rfc-editor.org/rfc/rfc8308.html)
- [Strict key exchange draft, version 02](https://datatracker.ietf.org/doc/html/draft-ietf-sshm-strict-kex-02)
- [OpenSSH protocol extensions](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL)
- [OpenSSH private-key format](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL.key)
