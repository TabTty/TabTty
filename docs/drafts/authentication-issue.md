# Specify authentication, host-key trust, and credential handling

> The [profile trust specification](../specification/profile-trust.md) supersedes the historical rules below for admitting a profile before its credentials can be used.

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Specification draft only. The confirmed sections record agreed product
decisions; remaining decisions are listed separately. No code, prototype,
or key material has been created or imported.

## Confirmed profile trust and credential references (Q111-Q113)

Use exact vault/entry references and permit deliberate sharing, with the
update/delete/migration rules in `profile-management-proposals.md`. The user
rejected the proposed per-profile action-approval mechanism on 2026-11-08.
Treat explicitly opened profiles, including external activation and edits, as
trusted configuration after basic bounded loading checks. Do not require
destination/credential/log-path review or local approval records at connection.
Validate detailed settings and consistency whenever the editor opens a file,
during changes, and on save. SSH host-key checks and vault unlock still apply.
The later Telnet scope change removes initial saved-password/automatic-login
settings and their editor warning; no connection-time approval is added.

## Confirmed authentication methods

Q131-Q132 in `connection-attempt-proposals.md` confirm client connection waits
distinct from user/authentication waits and optional SSH liveness checks.
Q129-Q130's Telnet automatic-login helper was superseded on 2026-11-08: Telnet
is ready at transport connection, with manual login initially and future macros
responsible for any automated remote login.

Q133-Q136 in `ssh-authentication-proposals.md` are accepted: profile method
allowlist/order, Password-only new-profile default, one selected private key,
separate Keyboard-interactive responses, and bounded retries without automatic
saved-secret updates. Partial success continues the server's required factors;
it is not complete login. Password submissions and interactive conversation
starts are limited to three each per authentication stage; individual prompts
are not failed retries. The draft defines key types/import envelopes, canonical
vaulted keys, and local host-key management as confirmed requirements.

Q161 in `key-import-passphrase-proposals.md` is accepted with source-key
passphrases restricted to strict UTF-8 initially. Do not offer an encoding
selector, normalize the input, or retry with another encoding. Keep these
import-only bytes independent of terminal encoding and vault-master-password
rules; discard the source passphrase when the import operation ends.

Support password, public-key, and keyboard-interactive authentication in the
initial release. Keyboard-interactive must handle multiple prompts and multiple
rounds rather than assume every prompt requests a reusable password.

Private-key file formats are specified below. Support Ed25519, ECDSA NIST
P-256/P-384/P-521, and RSA 2048-8192 bits with SHA-2 signatures only.
The `ssh-rsa` RSA key blob is distinct from the excluded SHA-1 signature.
SSH certificates/CA trust, FIDO keys, and external authentication agents and
agent forwarding are deferred. Saved private keys are imported into the selected
credential vault; loading them does not depend on an external key-file path.

## Confirmed host-key interaction

- On a first-seen host key, display its fingerprint and request approval to save
  and use it before continuing authentication.
- If a presented key differs from the saved key, display the connection
  destination and old/new fingerprints and ask whether to replace it.
- Provide explicit "Replace and connect" and "Cancel" actions. Default to
  cancellation, not automatic replacement.
- Withhold user authentication credentials while the trust decision is pending.
- Approval applies to the key shown in that confirmation. Replace the saved
  record with the approved key and continue; do not approve a different key
  obtained on a later connection without the required trust decision.
- Cancellation ends the connection attempt and preserves the saved key.
- A user trust decision does not bypass verification of the server's SSH
  exchange signature or other protocol-integrity checks.

## Confirmed host-key persistence identity

Share host-key records within the current Windows user, keyed by the destination
hostname or IP address and port number. Connection-profile names and SSH login
usernames do not create separate trust records for the same destination.
Different host aliases and a hostname versus its resolved IP address remain
separate identities; do not merge them automatically through DNS resolution.

Use a TabTty-specific local host-key store independent of the vault. Keep one
approved key per public-key format per destination/port; ECDSA curves are distinct
formats. Adding another type requires approval; replacing one preserves other
approved types. RSA SHA-2 signature variants for the same key do not create
different identities. Provide destination/port/type/fingerprint display and
explicit deletion without disconnecting existing sessions. Exclude known_hosts
import/export, file discovery, and automatic server-announced key learning initially.
Copying a profile/vault does not copy host trust. DNS ASCII case is insensitive;
preserve trailing-dot distinctions, IPv6 scope, and separate IPv4/mapped-IPv6
identities. Complete exact normalization/schema rules without alias inheritance.

## Confirmed credential storage direction and lifetime

- Do not persist passwords by default. Never retain source-key passphrases after
  their import operation.
- Use a portable, master-password-protected credential vault for saved
  authentication material, as specified in `credential-vault-issue.md`. This
  supersedes the earlier Credential Manager-only persistence direction. Keep
  references in connection profiles; future macros must use the same principle.
- Do not automatically persist arbitrary keyboard-interactive responses, such
  as one-time codes.
- Store passwords and imported canonical PKCS#8 private-key material in one
  selected vault shared by all profiles and windows. Importing a key leaves
  the source file intact; later authentication uses the imported material and
  does not require the source file or its passphrase. The original encryption
  wrapper is not retained; the imported key is protected by the vault envelope.
- Profile exports contain only non-secret configuration and references. Moving
  the separately encrypted vault is explicitly supported; plaintext credential
  export has not been selected.
- Retain unsaved passwords and interactive responses only while authentication
  needs them; source-key passphrases last only for import. End each retention on
  its operation's completion, failure, or cancellation;
  do not create a cache spanning connections or lasting until application exit.
- Request unsaved credentials again on reconnection. Explicitly saved credentials
  may be retrieved again from the unlocked vault.
- Ask for the vault master password on first use in an application run. Keep the
  vault unlocked until application exit and do not repeat that prompt for later
  connections. Discard the master password after deriving the necessary key;
  retain required vault decryption key material until exit. This is distinct from
  retaining plaintext connection credentials or an unsaved-credential cache.
- Additional windows and tab detach/merge operations share that unlock state;
  only the end of the application run ends its agreed lifetime.

The role of any Windows credential storage in this new design is not selected.
It must not make the vault dependent on the original computer or silently bypass
the first-use master-password requirement on a new application run.

## Security engineering policy

Treat current security best practices and applicable industry standards as an
ongoing design and maintenance requirement. Consult current Microsoft platform,
OpenSSH/OpenSSL, and relevant IETF/OWASP primary guidance when authentication or
secret-handling behavior changes, when relevant dependencies change, at
implementation start, and before release. Record material deviations and their
rationale. This policy does not itself claim compliance with a formal security
certification. The baseline primary-guidance review was performed on 2026-11-07;
file-input validation and trust guidance was rechecked on 2026-11-08 for Q112-Q113.
Authentication methods, key-file compatibility, and host-record guidance were
also rechecked on 2026-11-08 for the accepted Q133-Q136 decisions; see their draft
for primary sources and explicit product-scope choices.

The Q137-Q139 transport policy is also accepted, with its primary-source review
on 2026-11-08 recorded in `ssh-transport-policy-proposals.md`. The scoped AES-only
non-strict compatibility choice, hybrid preference with traditional alternatives,
and excluded algorithms are explicit product decisions. This does not change
the accepted user/host key types, trust prompts, vault unlock, or secret lifetimes.

Q140, Q142, and Q143 in `ssh-session-proposals.md` accept session readiness,
protocol-banner presentation, and closure, with RFC 4252/4253/4254/8160 and
reference-source review on 2026-11-08. Q141's normal/extended output composition
is subsequently accepted; both are remote terminal payload, distinct from
authentication exchanges and local connection errors. Display authentication banners as inert
server text in per-tab connection information, without consent UI or session-log
injection. Do not store authentication responses in that message area or introduce
profile approval. Ordinary terminal-channel welcome/error output stays terminal data.

Microsoft recommends validation of file-activation input and OWASP recommends
bounded parsing plus syntactic and semantic input validation. Neither mandates
a per-profile consent UI. TabTty deliberately treats opened `.ttty` files as
trusted operational configuration and confines the full semantic/consistency
validator to the editor. This is a product-specific departure from applying
that full validator to every external input before use, not a claim of full
OWASP conformance. Basic loading bounds and protocol/credential checks remain;
the trust model does not protect against a malicious profile selecting a
different destination, saved credential, or log path. The user's rationale is
direct file-based connection with checks at configuration time rather than
additional activation-time review. See `profile-management-proposals.md`.

Apply these requirements when implementation is authorized:

- Keep persisted secrets in the encrypted portable vault. Retrieve the selected
  credential when needed. Report storage/read/deletion failures accurately; never silently
  substitute plaintext files, improvised cryptographic algorithms, or retention
  beyond the agreed lifetimes.
  An explicit operation may continue without saving when appropriate, but the
  UI must not report a failed save as successful.
- Minimize plaintext lifetime and copies through input controls, encoding,
  native interop, and SSH message construction. Define ownership and cleanup of
  secret buffers, clearing buffers under application control before releasing
  them. Localize unavoidable immutable-string use; neither SecureString nor
  garbage collection is evidence of complete erasure or dump resistance.
- End use of source-key passphrases and import buffers when import finishes,
  fails, or is cancelled. End use of temporary passwords, decrypted authentication keys,
  and their working buffers when authentication succeeds, fails, is cancelled,
  or disconnects. Apply this to temporary copies of persisted credentials too.
  SSH traffic keys have a different required lifetime during an active session
  or rekey; clear obsolete traffic keys at the end of that lifetime. Vault unlock
  keys follow the separately agreed application-run lifetime.
- Exclude application-managed secret values from diagnostic logs, exception
  details, telemetry, command-line arguments, environment variables, settings,
  generated macro source, and exports. Authentication-event diagnostics may
  describe results without storing passwords, private-key material, or arbitrary
  challenge responses.
- Complete SSH host-key trust and cryptographic verification before sending
  user-authentication requests or proofs. Delay secret retrieval or key decryption
  until needed after that decision where possible. Require SSH encryption and
  integrity protection before password authentication.
- Treat vault encryption as protection of stored data, not isolation of an
  unlocked process from other code with sufficient access to that process. A
  credential-reference API alone cannot sandbox arbitrary same-user macro code.
  Resolve macro execution and trust when that later feature is specified;
  macros are excluded from the initial release.

Microsoft advises against SecureString for new .NET development. Prefer explicit
ownership and minimized exposure rather than making that class an acceptance
requirement. Review actual copies and cleanup paths; do not promise that all
runtime, OS, or diagnostic-dump copies can be erased.

### Session logs and unencrypted transports

The diagnostic-log exclusion above does not redefine session-log contents.
Raw and Text include terminal data supplied by the peer, including remote echo,
and exclude locally generated echo (Q90 restored). Terminal mode includes enabled
local echo as ordered UTF-8 text and supported VT controls, without original-byte
fidelity (Q94). Its scope and lifecycle are confirmed in Q95-Q98 of
`receive-logging-issue.md`, with the revised mode name and provisional shared
extension. These decisions do not
change the authentication/secret exclusions below.
A manually typed secret may be recorded by that mode when locally displayed,
and secret text sent back by the peer can appear in any session log.
Session logs cannot promise complete secret redaction or alter exact Raw bytes
to provide it.
Application-managed passwords, keys, passphrases, and authentication responses
must not be injected into local echo or its records. SSH authentication exchanges
remain outside session logs. Do not extend credential retention to scan output
for known passwords or claim to identify arbitrary manual secret input.
The connection profile determines whether to record terminal output.
An enabled recording can be paused and resumed during the session; a session
started with recording disabled cannot enable it midway. See
`connection-profiles-issue.md` for the agreed workflow and remaining details.

The local-echo logging boundary was reviewed against OWASP's logging exclusion
guidance on 2026-11-08, including the subsequent separation of recording modes.
Keep known application-managed secrets out of both
diagnostics and generated echo. Explain at the local-echo setting that manually
entered visible secrets can be recorded by the new combined mode. The requested
terminal transcript is distinct from application security-event diagnostics; it preserves the agreed
terminal content instead of promising general-purpose password detection.

Ordinary Telnet provides no transport encryption. Vault encryption protects
stored data, not bytes subsequently sent over Telnet. Keep the SSH authentication
requirements distinct from terminal login prompts on Telnet or serial sessions.

## Confirmed Telnet scope change (supersedes Q129-Q130)

On 2026-11-08 the user removed Telnet from built-in automatic login. A successful
TCP connection with the terminal transport available completes Telnet connection
setup. Expose Connected/ready-for-input without waiting for textual prompts,
credential sending, or remote account authentication; do not label it verified
remote login. Telnet option negotiation can continue while connected and must
not become an all-options-complete barrier. Normal disconnect/error handling
still applies. SSH retains its distinct protocol-authentication requirements.

The initial release has no Telnet username/password authentication fields,
saved-credential binding, prompt matcher, login-stage timer, or automatic secret
retrieval/submission. Connecting Telnet alone never needs vault unlock. Treat
login prompts, rejection messages, and remote account logout as ordinary received
terminal data unless the transport actually closes. Manual terminal input and
the agreed local-echo/logging rules remain unchanged; do not guess secret input.

The earlier Telnet/saved-password editor acknowledgement is no longer an initial
requirement because that setting is removed. Do not replace it with a connection
warning or approval gate. The accepted trusted-profile/basic-checks and full
editor-validation timing remain. SSH host-key, vault, and credential checks are
unchanged for SSH connections that need them.

Initial Telnet and serial login is manual. Future macros may implement prompt
matching, sending, timeouts, and application-level login detection. Revisit
their credential-use and plaintext-transmission configuration when macros are
specified; do not copy the discarded helper rules into an initial hidden engine.
RFC 854 has no standard textual login success; RFC 2941's optional Telnet AUTH
negotiation is separate and is not added to the initial scope.

## Confirmed private-key format scope

Support the exact Q135 matrix in `ssh-authentication-proposals.md`: single-key
OpenSSH none/none or bcrypt/aes256-ctr; PKCS#8 PEM/DER unencrypted or PBES2 +
PBKDF2 + AES-CBC with listed SHA-1/SHA-2 PRFs; traditional RSA/EC PEM unencrypted
or listed AES-CBC variants. Legacy PEM derivation is import compatibility only.
Exclude PPK, PFX/PKCS#12, PBES1, scrypt PKCS#8, DES/3DES PEM, unlisted OpenSSH
ciphers, and multi-key bundles. Validate before committing canonical PKCS#8 DER
inside the encrypted vault. No plaintext key file or saved source passphrase is
created; a later connection uses vault unlock without the source passphrase.

OpenSSL 3.5 reads PKCS#8 and traditional supported PEM key formats. OpenSSH's
openssh-key-v1 format requires separate parsing, and its encrypted form requires
bcrypt_pbkdf, which is not supplied by OpenSSL 3.5's standard KDF provider.
Neither OpenSSL's SSHKDF nor Windows BCryptDeriveKeyPBKDF2 substitutes for it.

Reuse the OpenSSH/OpenBSD bcrypt_pbkdf implementation and its required
Blowfish/helper source within this key-file scope. This is an explicitly agreed
source-dependency exception, reflected in AGENTS.md and the accepted SSH ADR,
rather than an OpenSSL transitive dependency. It does not entail modifying or
bundling ssh.exe. Pin and review the adopted source and retain required notices
when implementation is authorized; no source has been adopted during this
specification phase.

## Remaining decisions

- Exact host-identity normalization and host-record schema/resource bounds.
  Multiple key types are accepted; known_hosts import/export is excluded initially.
- Exact credential-reference schema and key-import resolution details; naming,
  shared-entry lifecycle, deletion, and profile/vault migration are accepted
  in Q111-Q113.
- Key import validation/resource bounds, exact
  entry schema, encryption/container design,
  key handling, backup/recovery, and password-change behavior in
  `credential-vault-issue.md`.
- Detailed repeated-method/probe/interactive-round state and resource bounds under
  accepted Q133. Initial Telnet and serial automatic login is excluded;
  Q131 connection/user-wait separation remains settled.
- Future macro access to secrets and interactive trust/authentication decisions
  are deferred as described in `future-macro-design.md`.

## Acceptance cases to define for later implementation

- First-seen key: approve/save/connect or cancel without saving or authenticating.
- Changed key: show both fingerprints; replace only on explicit approval;
  cancellation leaves the old record intact and sends no user credentials.
- Invalid exchange signature: fail even if the displayed key was previously
  trusted or the user would otherwise approve a replacement.
- Authentication cancellation, incorrect passphrases, multiple keyboard prompts,
  partial authentication success, and key-file parsing failures.
- Stored-secret reference resolution, explicit deletion, missing vault entries,
  and invalid/missing key-import source files, without writing secret values into
  logs or profile exports. An already imported key works without its source file.
- The same hostname/IP and port reuse a host-key record across profiles and SSH
  usernames; aliases, hostname versus IP, and different ports do not inherit it.
- Success, failure, and cancellation end temporary credential retention;
  reconnect prompts again unless an explicitly saved credential is selected.
- First required vault use prompts once for its master password; later connections
  in the same app run reuse the unlock state. Application restart requires a new
  unlock. Connection-secret cleanup does not discard the retained vault key.
- Credential-store failures remain visible and never create a plaintext fallback
  or falsely successful save/deletion result.
- Application diagnostics and exports exclude secret values across success and
  error paths; receive-log cases separately cover secret-bearing remote echo
  without claiming automatic redaction.
- Review temporary secret ownership and cleanup across managed/native boundaries,
  including failed key parsing and cancelled authentication.
- Telnet connects and becomes usable without vault access, credential submission,
  prompt recognition, or login confirmation. Textual login rejection leaves the
  connection usable; actual transport closure disconnects it. No initial Telnet
  saved-password UI or related warning acknowledgement remains.
- An externally edited destination/credential reference is used after basic
  loading checks and normal protocol/credential checks, without comparing it
  with local approval history. Opening it in the editor runs full validation.

## Primary references

- [Microsoft file activation validation](https://learn.microsoft.com/en-us/windows/apps/develop/launch/handle-file-activation#remarks)
- [OWASP safe parsing and input validation](https://cheatsheetseries.owasp.org/cheatsheets/Input_Validation_Cheat_Sheet.html)
- [OWASP authorization principles](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html)

- [OpenSSL PEM and private-key reading](https://docs.openssl.org/3.5/man3/PEM_read_bio_PrivateKey/)
- [OpenSSH private-key format](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL.key)
- [OpenSSH bcrypt_pbkdf implementation](https://github.com/openssh/openssh-portable/blob/master/openbsd-compat/bcrypt_pbkdf.c)
- [OpenBSD bcrypt_pbkdf manual](https://man.openbsd.org/bcrypt_pbkdf.3)
- [OpenSSL SSHKDF](https://docs.openssl.org/3.5/man7/EVP_KDF-SSHKDF/)
- [OpenSSL 3.5 default-provider algorithms](https://github.com/openssl/openssl/blob/openssl-3.5/providers/defltprov.c)
- [Windows generic credentials and persistence](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)
- [Windows CredWrite](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)
- [Windows CredRead](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw)
- [Microsoft SecureString guidance](https://learn.microsoft.com/en-us/dotnet/api/system.security.securestring?view=net-10.0)
- [Microsoft ZeroMemory](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory)
- [OpenSSL memory allocation and clearing](https://docs.openssl.org/3.5/man3/OPENSSL_malloc/)
- [OWASP secrets in memory](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#25-handling-secrets-in-memory)
- [OWASP data to exclude from logs](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html#data-to-exclude)
- [OWASP fail-safe principle](https://devguide.owasp.org/en/02-foundations/03-security-principles/#fail-safe)
- [Microsoft command-line audit logging](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-auditsettings#includecmdline)
- [Credential Guard scope and limitations](https://learn.microsoft.com/en-us/windows/security/identity-protection/credential-guard/considerations-known-issues)
- [RFC 4252 authentication security](https://datatracker.ietf.org/doc/html/rfc4252#section-11)
- [RFC 2946 Telnet security considerations](https://www.rfc-editor.org/rfc/rfc2946.html#section-4)
- [RFC 854 Telnet protocol](https://www.rfc-editor.org/rfc/rfc854.html)
- [RFC 2941 optional Telnet authentication](https://www.rfc-editor.org/rfc/rfc2941.html)
