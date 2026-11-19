# Confirmed SSH authentication, keys, and host records

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q133-Q136 accepted as recommended on 2026-11-08. In the same answer,
the user superseded Q129-Q130 by excluding built-in Telnet automatic login;
see `connection-attempt-proposals.md`. Specification only; no implementation, key generation,
key import, cryptographic execution, or publication is authorized.

These proposals preserve the own-SSH/OpenSSL boundary, profile-file trust,
host-key verification before authentication, and one unlocked vault per app run.
Transport KEX/cipher/MAC/compression choices are accepted in
`ssh-transport-policy-proposals.md`; detailed protocol contracts remain.

## Q133: Configured authentication methods and retries

The current [SSH authentication contract](../specification/transports-and-authentication.md#authentication-and-user-facing-messages)
sets keyboard-interactive eligibility separately from the profile's ordered
password and public-key methods and defines the continuation rules for missing
vaults and keys. The original decision is recorded below.

Use a profile allowlist of Password, Public key, and Keyboard-interactive,
with an explicit priority order. Enable Password only in a new profile; the
user can enable additional methods when needed. The initial suggested priority
is Public key, Password, then Keyboard-interactive, restricted to the selected
methods and the server's currently offered methods. Permit reordering in the
profile editor. Do not discover other keys/entries or enable another method
merely because the server asks for it.

A missing/wrong-kind configured credential or unsupported selected key is an
actionable configuration error, not a reason to substitute another entry or
silently proceed to another method. Server rejection of a valid selected key
can advance to another explicitly allowed method as described below.

A profile selects one vault private-key entry for Public key and a saved password
reference or explicit input for Password. The initial release does not offer a
list of keys to try or multi-key authentication requiring two different keys.
Present the methods as allowed alternatives/continuations, not as a guarantee
that every checked factor will be required by the server. If the server accepts
one allowed method completely, authentication is complete. After partial success,
continue with an allowed remaining method rather than treating it as either full
success or a failed password. Do not automatically replay an already accepted
static credential to satisfy a repeated method that needs another identity.

Password and Keyboard-interactive remain distinct. Never infer from prompt text,
an echo flag, or a question count that an interactive response is the profile's
saved password. Display each server-supplied prompt with its echo/masking flag,
preserve order, support multiple rounds and zero-prompt exchanges, and permit
empty answers. User responses remain transient and are not automatically saved
or reused. A subsequent interactive conversation can collect fresh responses;
individual questions and normally progressing rounds are not failed retries.

Try configured alternatives in priority order when a method is unavailable or
a key is rejected. For a rejected password or failed interactive conversation,
offer fresh entry/restart, advancing to another allowed method if available,
or cancellation. Cap Password submissions and Keyboard-interactive conversation
starts at three each per authentication stage, including the first attempt;
count failed restarts, not successful partial authentication or each prompt.
Do not automatically resend a rejected saved value or overwrite its vault entry.
Corrections used for the current attempt are transient; updating stored secrets
is an explicit vault operation. The server may impose a tighter or different
limit, which the client cannot accurately infer from a generic failure message.

Public-key probes/PK_OK are not authentication success. Use only the selected
key, avoid repeated probing of unchanged combinations, and keep probe/resource
limits distinct from user entry counts. A local vault/import passphrase error
is not a server authentication failure. Cancellation ends the attempt under
Q131; do not retain unsaved responses for reconnect. Final USERAUTH_SUCCESS and
terminal channel establishment remain distinct states. Detailed repeated-method,
prompt/round bounds, and algorithm-fallback state tables remain technical work.

## Q134: Initial key types

Support ordinary file-backed user keys and raw host public keys as follows:

| Key type | Initial scope |
| --- | --- |
| Ed25519 | `ssh-ed25519` |
| ECDSA | NIST P-256, P-384, P-521 with their standard SSH SHA-2 signatures |
| RSA | 2,048-8,192 bits; `rsa-sha2-256` and `rsa-sha2-512` signatures only |

The RSA upper bound is a TabTty resource/compatibility choice, not an IETF
maximum. Do not reject RSA public-key blobs merely because their format name is
`ssh-rsa`: SHA-2 signatures use the same key representation and fingerprint.
The excluded `ssh-rsa` signature algorithm is the SHA-1 choice, not every RSA key.

Keep DSA and SHA-1 SSH signatures excluded. Initially exclude Ed448, other EC
curves, RSA-PSS-only keys, SSH certificates/CA trust, security-key/FIDO `sk-*`
keys, external tokens, and authentication agents. These require additional
protocol/device/trust behavior beyond ordinary private-key import. Do not add
legacy-signature fallback or silently enable an unsupported provider. No key
generation feature is introduced by selecting these import/authentication types.

## Q135: Key import, supported envelopes, and vault representation

Import one existing private key at a time, validate it, and
store its canonical PKCS#8 DER private-key representation inside the encrypted
`.tttykey` payload. That inner representation is private plaintext only while
owned transiently in memory; it is never written to an unencrypted temporary
file. The enclosing accepted Argon2id/AES-GCM vault protects persisted material.
This settles the key representation without changing the accepted vault crypto.

Ask for a source key's passphrase only when needed during import. After successful
import, discard that passphrase and the source encryption wrapper; later use
requires the selected vault entry and its existing app-run unlock state, not
another source-key passphrase prompt. Preserve the original source file unchanged.
Its path/passphrase is no longer a dependency, and changing the original file's
passphrase does not change the imported copy. Show the key type and fingerprint
in the import result so the user can identify the item.

This supersedes the earlier possible storage of source-key passphrases:
the initial imported-key path does not need a separate saved key-passphrase entry
or per-connection source-key decryption. Unsaved SSH passwords and keyboard-interactive responses
keep their established lifetimes; this proposal does not turn them into app-long
plaintext caches. During authentication, retrieve only the selected key and clear
owned private-key working material when authentication ends.

Use the following explicit import compatibility matrix:

| Input | Permitted initial forms |
| --- | --- |
| OpenSSH `openssh-key-v1` | One key; unencrypted `none`/`none`, or `bcrypt` with `aes256-ctr` |
| PKCS#8, PEM or DER | Unencrypted PrivateKeyInfo or the encrypted form below, for the selected key types |
| Encrypted PKCS#8 | PBES2 + PBKDF2 + AES-128/192/256-CBC; PRF HMAC-SHA-1/256/384/512 |
| Traditional RSA/EC PEM | Unencrypted PKCS#1 RSA or SEC1 EC, or AES-128/192/256-CBC legacy PEM encryption |

Legacy encrypted PEM uses its specified MD5/EVP_BytesToKey derivation for import
compatibility only. Do not preserve that protection as the vault's storage scheme
or create new files using it. Likewise, HMAC-SHA-1 as a legacy file KDF component
does not enable SHA-1 SSH signatures. The source passphrase is consumed exactly
as required by the input format; do not apply the vault master-password NFC rule,
trim, truncate, or otherwise alter it.

Q161 in `key-import-passphrase-proposals.md` is accepted with the user's
UTF-8-only amendment: encode the input as strict UTF-8 without a BOM, encoding
selector, or alternate-encoding retry. Reject invalid Unicode and embedded NUL;
do not apply terminal encoding or vault-master-password normalization. An
otherwise supported encrypted key requiring different passphrase bytes is
outside initial compatibility. The earlier manual legacy selector is superseded.

Initially exclude PPK, PKCS#12/PFX, PBES1, scrypt-based PKCS#8, DES/3DES PEM,
OpenSSH ciphers other than the listed one, and multiple-key bundles. Report the
unsupported envelope/algorithm accurately rather than trying weaker fallbacks.
These are product compatibility limits, not claims that OpenSSL cannot read
other formats. Keep adoption of bcrypt_pbkdf within the existing scoped exception.

Check container/length/KDF bounds before expensive work, then algorithm/key-size,
private/public consistency, and key validity before committing an entry. Use
OpenSSL primitives and the accepted adapter boundary; parsing alone is not key
validation. Failure leaves the vault/source unchanged and does not expose key
contents in diagnostics. Exact size/KDF-work caps, native validation contracts,
entry schema, and failure cleanup
cases remain technical acceptance work. No conversion or validation has been
executed in this documentation phase.

## Q136: Local host-key management

Use a TabTty-specific store under the current Windows user's local app
data, independent of the encrypted vault so host identity can be checked before
credential retrieval. Provide a management view with destination/port, public-key
type, SHA-256 fingerprint, and an explicit deletion operation. Do not expose raw
private keys here. Keep OpenSSH known_hosts import/export, automatic file discovery,
and automatic server-announced host-key learning outside the initial release.

Allow one approved public key per public-key format for each destination/port;
ECDSA curves are distinct formats. Preserve already approved keys of other types
when adding/replacing one. Prefer a mutually supported already known type where
the protocol's permitted algorithm order allows it; no algorithm downgrade to
an excluded signature is permitted. A presented unapproved additional type still
requires confirmation and is not trusted because another type was approved.
Replacing a changed same-type key uses the already accepted Replace and connect /
Cancel dialog and updates only that record. All acceptance still requires a
valid SSH exchange signature before user authentication.

RSA SHA-256 versus SHA-512 signatures for the same `ssh-rsa` public-key blob do
not create different stored identities or a key-change warning. Deleting a host
record does not disconnect existing sessions; the next use requires the normal
trust decision. Copying profiles/vaults to a new PC does not silently transfer
host trust. This is host identity verification, not the rejected profile-action
approval scheme.

Preserve separate identities for different aliases and a hostname versus its
resolved address. DNS ASCII case is insensitive; do not automatically merge
`host` with `host.` or IPv4 with IPv4-mapped IPv6. Preserve relevant IPv6 scope
information. Exact IDNA/normalized-address rules must use the same effective
destination for resolution and trust lookup; no visual-similarity or DNS-result
based trust inheritance. Complete the normalization/schema matrix before
implementation rather than claiming arbitrary OpenSSH-file compatibility.

## Primary evidence reviewed on 2026-11-08

RFC 4252 distinguishes method offers, partial success, public-key probes, and
final authentication success. RFC 4256 defines interactive prompt rounds, echo
flags, empty answers, and zero-prompt exchanges. OpenSSH's default method order
and retry knobs are reference facts, not requirements for the accepted TabTty
allowlist/default. Client attempts cannot assume a fixed server MaxAuthTries
budget; modern OpenSSH separately bounds public-key probes.

RFC 8332 preserves the RSA key blob across SHA-2 signature choices and recommends
avoiding RSA shorter than 2,048 bits. OpenSSL/OpenSSH document the selected key
formats and envelope defaults. Legacy PEM derivation is retained only for input
compatibility and is weaker than the selected vault protection. The finite
matrix and canonical imported representation are TabTty product choices.

OpenSSH known_hosts can encode patterns/negation, hashed names, certificate
authorities, and revoked keys. A future importer cannot simply ignore unsupported
markers and treat the remaining material as ordinary trusted pins. Deferring
that feature avoids an incomplete claim of interoperability in the initial release.

- [RFC 4252 authentication](https://www.rfc-editor.org/rfc/rfc4252.html)
- [RFC 4256 keyboard-interactive](https://www.rfc-editor.org/rfc/rfc4256.html)
- [OpenSSH client authentication preferences](https://man.openbsd.org/ssh_config#PreferredAuthentications)
- [OpenSSH server AuthenticationMethods](https://man.openbsd.org/sshd_config#AuthenticationMethods)
- [OpenSSH release notes](https://www.openssh.org/releasenotes.html)
- [Microsoft OpenSSH key management](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement)
- [RFC 5656 ECDSA](https://www.rfc-editor.org/rfc/rfc5656.html)
- [RFC 8332 RSA SHA-2 signatures](https://www.rfc-editor.org/rfc/rfc8332.html)
- [OpenSSH key formats/defaults](https://man.openbsd.org/ssh-keygen)
- [OpenSSH private-key container](https://raw.githubusercontent.com/openssh/openssh-portable/master/PROTOCOL.key)
- [OpenSSL 3.5 PKCS#8](https://docs.openssl.org/3.5/man1/openssl-pkcs8/)
- [OpenSSL 3.5 format conventions](https://docs.openssl.org/3.5/man1/openssl-format-options/)
- [OpenSSL legacy PEM derivation](https://docs.openssl.org/3.5/man3/PEM_read_bio_PrivateKey/)
- [RFC 8018 password-based encryption](https://www.rfc-editor.org/rfc/rfc8018.html)
- [OpenSSH known_hosts format](https://man.openbsd.org/sshd.8#SSH_KNOWN_HOSTS_FILE_FORMAT)
- [OpenSSH host-key learning](https://man.openbsd.org/ssh_config#UpdateHostKeys)
- [RFC 4343 DNS case](https://www.rfc-editor.org/rfc/rfc4343.html)
- [RFC 1034 absolute/relative DNS names](https://www.rfc-editor.org/rfc/rfc1034.html#section-3.1)
- [RFC 5952 IPv6 representation](https://www.rfc-editor.org/rfc/rfc5952.html)
