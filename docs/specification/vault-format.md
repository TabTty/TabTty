# Credential vault version 1 contract

Specification baseline, 2026-11-10, completing Q107-Q108/Q135/Q161 within the
accepted OpenSSL boundary. No cryptographic implementation or validation has
run. Independent security review remains a release gate. This is a TabTty
format, not a standardized or certified vault format.

## Key hierarchy and master input

Use Argon2id v1.3, memory 65,536 KiB, three passes, four lanes, salt 16 bytes,
and output 32 bytes as the only v1 KDF profile. Lanes are serialized; execution
thread count is not. No machine-dependent weakening or parameter guessing.
Master input follows the accepted NFC then strict UTF-8 rule, 14-1024 Unicode
scalars for create/change, no trim/case fold/NFKC, invalid Unicode/control/line
break rejection, confirmation twice. Unlock applies the same versioned rule.

On 2026-11-12, TabTty lowered the minimum from 15 to 14 at the user's request.
This is a product limit for deriving the encryption key of a portable file.
[NIST SP 800-63B-4's 15-character rule](https://pages.nist.gov/800-63-4/sp800-63b/authenticators/#password-verifiers)
applies to centrally verified single-factor authentication. Earlier TabTty
builds reject a 14-character master password. Existing vaults remain readable
with their original passwords.

The KEK wraps a randomly generated 32-byte root DEK using AES-256-GCM. Retain
that root only while the application run remains unlocked; discard master/KEK
working buffers. Each payload encryption derives its own 32-byte key using
HKDF-SHA-256 with the root as IKM, a fresh 32-byte random salt, and the info
below. Encrypt the entire JSON payload once under that derived key using
AES-256-GCM, then clear the derived key. This refines the earlier direct-DEK
payload wording while retaining its whole-payload protection and unlock lifetime.

All GCM nonces are 12 bytes and tags 16 bytes. Generate independent keys, salts,
and nonces using checked OpenSSL random-generation calls. Fail without saving
on RNG, KDF, encryption, or authentication failure. A fresh salt/nonce is required
for every new encryption attempt, including after cancellation or failed saves;
copying already completed ciphertext bytes is not another encryption.

This probabilistic separation does not prove collision freedom across portable
copies. Never reuse a derived payload key for another encryption, use a local
counter as a global nonce proof, or claim rollback prevention/forward secrecy.
The root is for HKDF, not also direct payload GCM. Normal saves reuse the existing
root wrapper. Explicit master-password change creates a fresh Argon salt, KEK,
root, wrapper, payload salt, nonce, and whole ciphertext. Old copies remain
decryptable with their old password; changing a password does not revoke them.

## Exact framing

The file consists of a fixed 184-byte header, N ciphertext bytes, then a 16-byte
payload tag. All integers are unsigned big-endian. UUID bytes use RFC network
order, not the mixed-endian layout of a default .NET Guid byte array. No padding,
optional header fields, trailing bytes, or compression are allowed in v1.

| Offset | Bytes | Field and v1 value |
| ---: | ---: | --- |
| 0 | 8 | ASCII `TTTYKEY` followed by one zero byte |
| 8 | 2 | Container version, 1 |
| 10 | 2 | Flags, 0 |
| 12 | 4 | Header length, 184 |
| 16 | 16 | Nonzero random vault UUID, stable across ordinary saves/password changes |
| 32 | 2 | KDF profile, 1 = the Argon2id profile above |
| 34 | 2 | Wrapping suite, 1 = AES-256-GCM |
| 36 | 2 | Payload suite, 1 = HKDF-SHA-256 then AES-256-GCM |
| 38 | 2 | Master-input rule, 1 = accepted NFC/UTF-8 rule |
| 40 | 4 | Argon memory, 65536 |
| 44 | 4 | Argon passes, 3 |
| 48 | 4 | Argon lanes, 4 |
| 52 | 16 | Argon salt |
| 68 | 12 | Root-wrapping nonce |
| 80 | 32 | Encrypted root DEK |
| 112 | 16 | Root-wrapping tag |
| 128 | 32 | Fresh payload HKDF salt |
| 160 | 12 | Payload nonce |
| 172 | 8 | N, payload ciphertext length |
| 180 | 4 | Reserved, 0 |

Let `H[a:b]` mean the exact file header bytes from a inclusive to b exclusive.
The following labels are ASCII without an implicit NUL; the explicitly named
zero byte is part of the format:

- Wrapping AAD: ASCII `TabTty/vault/v1/wrap`, one zero byte, H[0:80].
- Payload AAD: ASCII `TabTty/vault/v1/payload`, one zero byte, H[0:184].
- HKDF info: ASCII `TabTty/vault/v1/payload-key`, one zero byte,
  H[8:10], H[34:38], H[16:32], in that order.

Thus wrapping authenticates only metadata unchanged by ordinary saves;
payload authentication binds the complete wrapper and revision framing.
HKDF info contains no root-dependent wrapper hash/ciphertext. Neither AAD nor
info is reconstructed by serializing JSON. Validate fixed values, exact file
length, nonzero ID, and bounded N before allocation/KDF. Do not trust any
unauthenticated metadata as evidence that decryption succeeded.

Unwrap and verify the root tag before using the root. Derive and decrypt the
payload into owned provisional memory, verify its tag before parsing, and
verify the inner vault identity against the header. A successful wrapper tag
alone does not make a damaged payload usable. Authentication failure does not
reliably distinguish a wrong password from altered/corrupt input.

## Encrypted payload schema and limits

UTF-8 JSON without a BOM, one object, no duplicate properties/IDs or comments:
`schemaVersion: 1`, `vaultId` (canonical UUID string), and `entries` (array).
No unknown properties in this private v1 schema; future fields require a new
supported schema/container rule. Array order is the user's entry order.

Each entry has `entryId`, `kind` (`password` or `privateKey`), `name`, `username`
(string or null), and exactly its applicable secret field: `passwordUtf8`
(canonical padded Base64 of unnormalized strict UTF-8), or `privateKeyPkcs8`
(canonical padded Base64 of validated unencrypted PKCS#8 DER). Key entries also
store the public type and SHA-256 fingerprint, recomputed/checked when loaded.
No source key path, source passphrase, remembered encoding, or opaque external
file dependency is stored. UUIDs never derive from entry names or secrets.

Initial defensive limits, not measured capacity claims: N <= 16 MiB, whole file
<= 16 MiB + 200 bytes, at most 1,024 entries, JSON depth <= 8, names <= 256
Unicode scalars, usernames <= 1,024 UTF-8 bytes, password material <= 16 KiB,
and canonical private-key DER <= 64 KiB. Enforce encoded and decoded lengths
before Base64 allocation, then validate content. Entry IDs are unique within
the vault. References require exact vault ID, entry ID, and expected kind.

## Imported keys

Keep Q134-Q135's exact key/envelope allowlist; no new format fallback. Source
passphrases use strict UTF-8 only (Q161), with no normalization, added BOM/NUL,
encoding selector, or alternate-encoding retry. Reject invalid Unicode/NUL;
do not apply the master-password minimum to existing key input. Source files
remain untouched. A successful import stores only canonical validated material.

Before native parsing, limit source size to 1 MiB, decoded object size to
256 KiB, nesting to 16, PBKDF2 iterations to 1-10,000,000, bcrypt_pbkdf rounds
to 1-1,024, and KDF salt to 1-64 bytes. Reject unknown cipher/KDF options and
trailing extra key objects; permit only framing whitespace outside the one
supported object. These are initial work limits, not standards maxima or exact
execution-time guarantees. One expensive import/KDF operation runs at a time
outside UI/session workers; cancellation invalidates its result and preserves
ownership until native completion. Do not force-free live native buffers.

Check key type and size before expensive validation: Ed25519, exact supported
named EC curves, or RSA 2048-8192 bits. Reject extra primes for the initial RSA
import path. Perform supported public/private/pairwise validity checks and
derive/compare public material. A check returning unsupported is not success:
use the documented type-specific equivalent or reject the import. Invalid
private/public consistency never becomes a vault entry. Do not log parser input,
passphrases, decrypted ASN.1, or OpenSSL error data containing secrets.

## Secret owners and cleanup

| Owner | Material | End of ownership |
| --- | --- | --- |
| Master interaction | Input, normalized UTF-8, confirmation copies | Derivation completed, cancelled, or failed |
| KDF/wrapping operation | Argon memory, KEK, provisional root | Completion; only authenticated root can transfer to vault owner |
| Selected vault owner | Root and authenticated encrypted snapshot shared by connections, management tabs, and the file's editor | Full app exit or end of selection without an open editor |
| Editor for another vault | Root and authenticated encrypted snapshot of the opened file | Editor closure, after in-flight operations settle, unless that file has become the selected vault |
| Payload operation | Derived key and full provisional plaintext | Tag/schema failure or selected extraction/save completion |
| Authentication owner | Selected credential and native private key | Authentication success/failure/cancel/disconnect |
| Import owner | Source passphrase, decrypted key, native temporary objects | Import completion/cancel/failure, after in-flight native calls end |
| File operation | Encrypted candidate and handles | Verified commit or bounded recovery/cleanup |

Use owned clearable buffers where available and minimize immutable secret
strings. Do not cache all decrypted entries until exit. Clearing buffers is not
a promise to erase every runtime/OS/dump copy. An external vault change blocks
new retrieval and writes from the stale snapshot, while existing connections
continue. A selected vault detected as stale remains blocked at that location
until the next application run under Q108. An editor-only vault detected as
stale can be freshly unlocked after its owner is released and the file reopened.
Reconciliation below concerns the application's own in-progress save.

A selection change transfers roles between owners after affected operations
settle. Promote an already unlocked, usable editor owner when its file becomes
the selected vault. Retain the previous owner only while its standalone editor
remains open. Release its root once there is no owner that needs it. The
connection credential resolver uses only the selected vault. See
[File Explorer and editors](persistence-and-windows.md#file-explorer-and-editors)
for window behavior and [OWASP guidance on handling secrets in memory](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet#25-handling-secrets-in-memory)
for the basis of the editor-only lifetime.

## Save, backup, restore, and failures

Use one owner and one writer at a time per vault file. Serialize its operations
across management views, and publish verified commits to all consumers of that
file. Prevent cooperating second writers using a lock independent of the file
being replaced, as required by Q108. Capture target identity/digest at load,
recheck before commit, and refuse detected external changes. Write only
encrypted same-directory candidates with restrictive user access, flush them,
then replace target while retaining one previous valid encrypted backup. A new
target uses non-overwriting create. Use `.bak` for the previous generation and
unique operation IDs for candidates/recovery names. Never use plaintext temp
files or weaken storage to recover from a write failure.

| Outcome | Required state |
| --- | --- |
| Failure before replacement | Keep prior effective snapshot/root; retain draft; safely close/delete only owned candidate if possible |
| Confirmed successful replacement and reread verification | Publish new encrypted snapshot; for password change, transfer new root and clear old root/KEK |
| Replacement API error or uncertain name state | Inspect target/candidate/backup; identify by expected digest, length, and authentication; freeze further writes/retrieval until resolved |
| Original remains intact | Keep old effective generation; report failed save, never Saved |
| Candidate became target despite reported failure | Verify before treating it as committed; do not perform a second blind replacement |
| Target missing/damaged but valid backup remains | Offer explicit recovery; do not silently replace it with an empty vault |
| Conflict with another writer | Preserve external file and owned recovery candidate; no automatic merge/overwrite |

During password change retain both old and candidate roots only until commit
outcome is resolved, so an ambiguous replacement cannot discard the only usable
key. A cancelled operation before commit leaves the old vault; cancellation
after commit does not pretend to roll it back. Manual backup copies completed
authenticated encrypted bytes. Explicit restore verifies the selected backup,
preserves the current file under a noncolliding recovery name, and uses the
same guarded commit path. Backup passwords remain those of their generation.
Local file locks/digests do not provide transactional guarantees against every
noncooperating or cloud writer, or universal power-loss durability.

## Verification gate

After implementation authorization, verify Argon2/HKDF/GCM known-answer vectors,
cross-PC unlock, every header field's AAD coverage, wrong password, truncation,
duplicate/oversized JSON, all supported key envelopes, copy/restore/divergent
saves, failed RNG, partial writes/replacement outcomes, cancellation, and memory
ownership. Benchmark the fixed Argon profile on supported reference hardware.
Independent review of the custom format, native adapter, and SSH integration is
required before release; any discovered design issue returns to this contract.

Basis: [RFC 9106](https://www.rfc-editor.org/rfc/rfc9106.html),
[RFC 5869](https://www.rfc-editor.org/rfc/rfc5869.html),
[RFC 5116](https://www.rfc-editor.org/rfc/rfc5116.html),
[NIST GCM guidance](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf),
[OpenSSL Argon2](https://docs.openssl.org/3.5/man7/EVP_KDF-ARGON2/),
[key validation](https://docs.openssl.org/3.5/man3/EVP_PKEY_check/),
[Windows replacement outcomes](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
Numeric application limits and this composition are TabTty choices, not values
mandated or a format endorsed by these sources.
