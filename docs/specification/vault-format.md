# Credential vault version 1 contract

Specification baseline, 2026-11-10, completing Q107-Q108/Q135/Q161 within the
accepted OpenSSL boundary. No cryptographic implementation or validation has
run. Independent security review remains a release gate. This is a TabTty
format, not a standardized or certified vault format.

The complete management-operation contract incorporates architecture Q12-Q16,
confirmed on 2026-11-18. Its design is ready for separately authorized
implementation; the future verification specified below has not been executed
as part of this design review.

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

UTF-8 JSON without a BOM, one object, no duplicate properties or comments:
`schemaVersion: 1`, `vaultId` (canonical UUID string), and `entries` (array).
No unknown properties in this private v1 schema; future fields require a new
supported schema/container rule. Array order is the user's entry order.

Each entry has `kind` (`password` or `privateKey`), `name`, and exactly
its applicable secret field: `passwordUtf8`
(canonical padded Base64 of unnormalized strict UTF-8), or `privateKeyPkcs8`
(canonical padded Base64 of validated unencrypted PKCS#8 DER). Key entries also
store the public type and SHA-256 fingerprint, recomputed/checked when loaded.
No source key path, source passphrase, remembered encoding, or opaque external
file dependency is stored. The vault ID is generated independently of its
entries and remains part of the container's authentication and recovery rules.

Initial defensive limits, not measured capacity claims: N <= 16 MiB, whole file
<= 16 MiB + 200 bytes, at most 1,024 entries, JSON depth <= 8, names <= 256
Unicode scalars, password material <= 16 KiB, and canonical private-key DER
<= 64 KiB. Enforce encoded and decoded lengths before Base64 allocation, then
validate content. Names are unique across all
entries in a vault. Compare names ordinally and case-sensitively with no trimming
or Unicode normalization. Reject duplicate names on load, add, import, and
rename. A reference resolves the exact name in the selected vault and checks
the expected kind. Later operations resolve the current contents under that
name, including after deletion and reuse or after changing the selected vault.

The SSH username belongs to the connection profile. Vault entry creation,
editing, and key import have no username field. A legacy entry with a
`username` property fails payload validation. The pre-release format has no
migration path for these files.
[SSH authentication](transports-and-authentication.md#authentication-and-user-facing-messages)

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
| Master interaction | Input, normalized UTF-8, confirmation copies | Derivation completed, cancelled, or failed, after actual native use ends |
| KDF/wrapping operation | Argon memory, KEK, provisional root | Actual completion; only an authenticated root can transfer to a surviving authorized vault owner |
| Selected vault owner | Root and authenticated encrypted snapshot shared by connections, management tabs, and the file's editor | Manual lock, full app exit, end of selection without an open editor, or completed archive, after dependent work settles |
| Editor for another vault | Root and authenticated encrypted snapshot of the opened file | Editor closure unless that file has become selected, or completed archive, after dependent work settles |
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
This does not clear an independent partial/unresolved publication restriction
from the application's own save. Reconciliation below concerns that save and
does not clear independent external-stale restrictions.

Manual lock clears the selected vault's owned root through the same disposal
path used on exit. External-stale restrictions survive disposal. An unresolved
publication that still needs the unlocked owner must be reconciled before
manual lock. The 2026-11-26 guidance check confirmed the existing owned-buffer
cleanup approach. [Microsoft ZeroMemory](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0),
[OWASP handling secrets in memory](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html#25-handling-secrets-in-memory)

A selection change transfers roles between owners after affected operations
settle. Promote an already unlocked, usable editor owner when its file becomes
the selected vault. Retain the previous owner only while its standalone editor
remains open. Release its root once there is no owner that needs it. The
connection credential resolver uses only the selected vault. See
[vault selection and operation hosting](persistence-and-windows.md#vault-selection-and-operation-hosting)
for settings, role-transfer and window coordination, and
[OWASP guidance on handling secrets in memory](https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet#25-handling-secrets-in-memory)
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
| Candidate at target and all required preservation verified | Publish new encrypted snapshot; for password change, transfer new root and clear old root/KEK |
| Candidate at target but required preservation missing, different or unverified | Keep prior effective snapshot/root and draft; block ordinary writes and new retrieval. Report the changed target and failed/unresolved preservation separately; no ordinary success or false unchanged result. |
| Replacement API error or uncertain name state | Inspect target/candidate/backup; identify by expected digest, length, and authentication; freeze further writes/retrieval until resolved |
| Original remains intact | Keep old effective generation; report failed save, never Saved |
| Candidate became target despite reported failure | Verify before treating it as committed; do not perform a second blind replacement |
| Target missing/damaged but valid backup remains | Offer explicit recovery; do not silently replace it with an empty vault |
| Conflict with another writer | Preserve external file and owned recovery candidate; no automatic merge/overwrite |

During password change retain both old and candidate roots while required by
unsettled work or outcome reconciliation within the responsible owner's existing
lifetime. Do not discard a needed root merely because publication is ambiguous,
or retain roots beyond that lifetime solely for file-operation metadata. A
cancellation result alone establishes no publication only if issue was prevented;
after issue, settle actual work and inspect effects rather than infer rollback.
Manual backup copies completed
authenticated encrypted bytes. Explicit restore verifies the selected backup,
preserves the current file under a noncolliding recovery name, and uses the
same guarded commit path. Backup passwords remain those of their generation.
Local file locks/digests do not provide transactional guarantees against every
noncooperating or cloud writer, or universal power-loss durability.

### File-operation integration and unresolved ownership

Use the [shared verified-file operation contract](persistence-and-windows.md#verified-file-operations)
for complete physical publication, inspection and cleanup. Supply encrypted
immutable candidate bytes, original-basis evidence and the vault's established
access/writer/preservation requirements. Keep cryptography, schema validation,
root adoption and stale-state decisions in the vault owner. Equality to an
authenticated encrypted image proves that generation; matching a vault ID alone
does not authenticate unknown bytes. A separately selected restore requires
independent authentication, and its recovery copy preserves the exact original
bytes even when damaged. The common module never owns plaintext or root keys.

Adopt a candidate snapshot/root only after both target publication and required
backup/recovery preservation are verified. For partial publication, keep the
prior effective in-memory state and stop ordinary writes and new retrieval;
existing terminal connections continue. For a password change, accurately name
the observed on-disk candidate generation rather than claiming the old password
still applies to that file. Unavailable evidence may be reinspected, but a known
missing artifact is not repaired by inspection. Ordinary reopening cannot clear
this restriction. Use explicit authorized recovery with a quiescent reservation
handover, or ordinary validation in a later application run. Neither path may
retroactively label the earlier partial save successful or clear an independent
external conflict contrary to its stricter lifetime rule.

Pending I/O, native work and owner-dependent verification keep their owners
alive. After actual work settles, visible closure/exit may leave preserved
encrypted artifacts and unresolved non-secret evidence under the shared contract.
Serialize owner release against new owner-dependent inspection. If the application
continues, the file-operation record and target restriction survive view closure.
The selected owner retains its established application-run role; an editor-only
owner can release secrets after closure and in-flight settlement under ADR 0008.
Do not retain keys just to inspect encrypted artifacts, start a late callback
against a released owner, or introduce a hidden application lifetime.

## Complete vault management operations

One deep vault-management module completes create/unlock, entry management,
private-key import, master-password change, encrypted backup, restore and archive
using the existing shared file owner. Its interface provides named complete
operations on a non-secret file-bound context, such as `Unlock`, `Initialize`,
`AddPassword`, `EditEntry`, `DeleteEntry`, `ImportPrivateKey`,
`ChangeMasterPassword`, `Backup`, `Restore` and `Archive`. Names are illustrative;
callers do not obtain an unlocked owner, choose locked/unlocked restore paths,
assemble serialization and mutation, adopt roots, or request a separate refresh.
The module owns those steps and committed metadata/revision publication.

UI owners retain localized forms, controls, file pickers, confirmation/impact
display, hosting and closure. Existing profile owners find known registered
references and retain profile persistence; settings owners retain settings
persistence. The public interface adds no generic command dispatcher,
prepared-operation lifecycle or second secret owner. Plaintext export and an
initial macro runtime remain excluded.
Selection/settings and window ordering follow
[vault selection and operation hosting](persistence-and-windows.md#vault-selection-and-operation-hosting),
including Busy rejection before settings save and coordination of every effective
selected-path adoption. A vault operation cannot silently retarget an editor or
make settings, profile and vault files one transaction.

### Context, admission and observation

A context carries only non-secret opened-location, binding and role information.
It owns no root, plaintext, independent encrypted snapshot or effective revision.
Holding a context or metadata subscription does not prolong unlock lifetime.
Bindings distinguish role epochs and opened locations even for copied vaults
with the same ID; existing directory/alias coordination still applies. An
A-to-B-to-A selection sequence does not revive the first selected A context.
An editor binding ends with its editor role even if another role keeps the shared
owner alive. Selected-only contexts become invalid for new requests on selection
change; admitted operations and surviving editors keep their fixed targets.

Each named operation reserves bounded capacity synchronously and returns an
accepted operation ID/view or typed rejection without waiting for filesystem or
native work. Rejection changes no files and transfers no input ownership.
Acceptance transfers only explicitly accepted inputs, not authority to bypass
later validation. Deferred execution retains the target, operation kind and
entry anchor: binding, original entry name, expected kind, and observed revision.
The per-file owner orders conflicting work. Reject obsolete anchors/contexts
even if an entry with that name exists in a later revision. Queued
inputs, unlock participation and acquired images remain owned and charged.

A repeated click or keypress on a busy management form starts only the
original operation and its confirmation. If another view requests a
conflicting management operation on the same vault, report Busy immediately
in that view. Starting it after the first operation ends requires a new user
request.

Entry menu commands, double-click editing and the Delete key use the same
selection value. Capture it when the menu opens, or when the double-click or
keypress occurs. It contains the view's vault binding and role epoch, the
revision displayed at that time, and the original name and kind of each
targeted entry. The per-file owner validates the selection as a whole. A
write also checks the captured revision at the point of mutation so that a
change during confirmation cannot redirect it.

If the binding has expired, the revision has changed, or any targeted entry
fails to match, leave all entries untouched. Tell the user the selection
changed and show the current list with its search and sort preserved so the
user can select again. This also
applies when an entry is replaced under the same name or a master password
change advances the revision while leaving every displayed row intact.
Restoring a visible selection by name during refresh does not renew a
previously captured command.

An ordinary caller awaits one initial observation; it need not run a monitoring
loop. The operation view also exposes current state, explicit work cancellation,
reconciliation of the same operation and actual-resource release. Cancelling an
observation wait or dropping a view ends only observation. Observations carry
an operation ID and increasing observation revision; metadata carries binding/
owner epoch and effective vault revision. Old observations cannot reverse newer
state or revive an archived/released binding. Delivery coalesces current state,
and a late subscriber can read it without replaying unbounded history.

Precompute the non-secret metadata projection from the validated candidate and
publish it with the adopted encrypted snapshot, effective revision and any new
root in the shared owner only after complete target/preservation proof. Notify
consumers afterward. UI delivery failure cannot reverse a commit, make it an
unsuccessful save, or authorize mutation replay. A current-state read catches up
without re-execution. Partial publication retains the adoption/retrieval freeze
and truthful on-disk generation facts defined above.

### Required operation results

| Operation | Required completion and adoption |
| --- | --- |
| Unlock existing file | Authenticate and validate one bounded acquired image, then publish the shared unlocked state and metadata for that attempt. Do not publish a file, initialize implicitly or cache all plaintext entries. |
| Create at a missing path | Publish a new validated encrypted vault with a new identity/root without overwriting, then adopt only after target and applicable preservation proof. A newly appearing target is a collision. |
| Initialize an empty file | Revalidate the confirmed same regular file, preserve its exact empty original through guarded initialization and adopt only after target and required preservation proof. Parking the original is a physical effect even if installation fails. |
| Add/edit/delete entry | Enforce the exact name, expected kind, and revision. Apply known-profile impact confirmation to secret changes and destructive operations. A kind change creates a new entry. Publish the complete result before adopting its revision and metadata. An existing reference resolves while its exact name is present. Renaming requires the confirmation described below. Established sessions retain their authenticated connection. |
| Import private key | Use one bounded acquired source image and its matching passphrase interaction; enforce the accepted envelope allowlist and actual key validation. Publish only canonical validated PKCS#8, keep native buffers through actual completion, and leave the source intact without retaining its path/passphrase as an authentication dependency. |
| Change master password | Apply the existing input rules and fresh root/wrapper/payload encryption. Complete proof precedes root/snapshot/metadata adoption. Retain needed old/candidate roots only within their established lifetime and report an observed on-disk candidate truthfully; old copies retain their old password. |
| Encrypted backup | Copy the exact authenticated encrypted generation to a separately selected non-overwriting destination and verify it. Do not change the source revision or re-encrypt to an implied new password. |
| Explicit restore | Independently authenticate/validate the separate backup, enforce the identity conditions below, preserve exact original target bytes where present and verify target plus required recovery copy before snapshot/root adoption. Keep the editor's target path. |
| Archive | Move only the confirmed source to a unique sibling without overwriting and verify actual source/destination effects. Completion ends the old usable owner state without creating a vault or changing selected path, profiles or `.bak`. Show the archive path and close a standalone editor after acknowledgment. |

Before renaming an entry, show its old and new names and explain that existing
profile and macro references will stop finding the entry. For the selected vault,
list the readable registered profiles that refer to the exact old name in the
matching credential field, including references in disabled methods. Show the
number of registered profiles the scan could not check. Offer **Rename and update
registered profiles**, **Rename only**, and **Cancel**. A standalone editor for a
vault outside the current selection offers rename only or cancel.

Update profiles only after the vault rename has been verified. Each reviewed
registration must still exist, its file bytes must still match the reviewed
revision, and its editor must have no unsaved changes or pending save. Change only
the matching credential field through the normal verified profile save. Report
the count updated and identify profiles that could not be saved. Each save has
its own outcome. The vault rename and verified profile writes remain in effect
if another write fails. Macros and unregistered files are updated manually.

The profile catalog processes one file at a time. Editor saves and rechecks
can run between files in a rename. Immediately before each update, recheck the
registration, reviewed file revision, unsaved edits and any pending save for
that file, including a save retained after its editor closed. If any check
fails, report the path and reason, skip that file and continue with the next.

If the write outcome is uncertain and can be rechecked, report its path as
unconfirmed and retain the original operation ID in the catalog for this
application run. The profile editor's **Check save result** action checks
that write. Opening the editor alone leaves the check pending. Rechecking
applies only to that file and grants no profile trust.

When the write is verified, an open editor with no intervening edits displays
the new credential reference and uses the verified revision for its next edit.
If the user edited the form after the write began, keep that draft and its
original base revision. Tell the user the file changed and require review of
the saved revision before a later save can overwrite it.

The **Manage vault** command offers master password setup when the selected
vault path is missing or zero bytes. Use the shared initialization prompt,
which identifies the configured path and the observed file state. Saving
confirms creation or guarded initialization, and cancelling leaves the target
as observed. Open the management tab only after that initialization succeeds.
The management tab has no separate creation command. Manual unlock and
credential selection in the profile editor also use the shared prompt for
missing and zero-byte targets. The [vault window rules](persistence-and-windows.md#file-explorer-and-editors)
define the destination after setup and how the profile picker opens management.
SSH authentication reports either state and can continue using only an
eligible method independent of the unavailable vault.
[SSH authentication](transports-and-authentication.md#authentication-and-user-facing-messages)
Macro password sends report either state through the
[macro credential error contract](../drafts/macro-specification.md#profile-password-and-explicit-references).
Explorer editors require an existing, nonempty vault.
Bind confirmation to the opened location and observed missing/empty state. For
an empty target, establish the identity and zero length of the same regular file
before changing it. A replacement, nonempty file or unavailable prerequisite
causes conflict/unavailability, never a broader overwrite. Failed unlock of a
nonempty file, unsupported format, external-stale state and partial/unresolved
publication do not authorize initialization. New identity/root creation does
not repair old references or change the configured path.

Restore retains these exact identity branches. An unlocked owner requires the
independently authenticated backup's vault ID to match its own. Locked recovery
obtains an expected ID only when the bounded original passes v1 framing/header/
length validation; that ID constrains restore without authenticating the damaged
target. Missing, zero-byte or structurally invalid originals have no established
ID, so the authenticated backup supplies it. Do not infer identity from profile
references or names. Refuse a recognizable newer container version or v1 nonzero
flags instead of treating it as corrupt empty input. A missing-target restore
is explicit recovery, not the implicit empty initialization forbidden above.

After external-stale detection, permit Q108's encrypted rescue copy when an
authenticated encrypted snapshot remains available. Copy only that retained
generation to a separately selected non-overwriting destination, identifying its
generation and stale status. Do not trust unknown current bytes, retrieve
credentials, write the blocked source or clear restrictions. If the authenticated
image is unavailable, report that instead of reconstructing it from partial
evidence. This is encrypted preservation, not secret export.

### Guarded initialization, archive and physical outcomes

Use the shared verified-file module for ordinary publication, preservation and
reconciliation. Narrow initialization/archive mechanics use the same internal
target/namespace admission authority for every affected source, destination,
candidate and parked/recovery name, reserved in a consistent order. A separate
move registry cannot admit overlapping mutations. Do not expose reserve/write/
commit/release phases to callers or add a public generic move operation.

For empty initialization, report the parked original, absent target, installed
candidate and recovery material independently. In the original execution only,
narrow compensation may return the exact owned empty original to a confirmed-
absent source using a non-overwriting move, after candidate installation was
prevented or definitively settled without installation. Verify compensation and
report failure. Never overwrite a competing file, race issued work or perform
this move during reinspection. Uncertain publication retains evidence and
restrictions for explicit recovery. Once preservation is proven, failure to
delete an owned parked empty file is cleanup failure, not failed publication.

Archive does not require successful unlock or content authentication: it can
preserve locked, readable damaged or unsupported bytes within existing limits.
It neither validates those bytes nor recovers a password or declares the file
usable. Independent external-stale and partial/unresolved restrictions still
apply. Before confirmation, acquire bounded evidence of the regular file's
opened location, physical identity, length and contents. Reject directories,
reparse targets and unavailable prerequisites. If an unlocked owner exists,
compare its editing basis and take the existing stale path on external change.
Show the source path and effect, bind the reply to that evidence and operation,
then recheck before the non-overwriting move. A changed/replaced source invalidates
approval; never move new bytes on the old answer. Internal IDs/digests need not
be shown to the user, and identity evidence is not content authentication.

Maintain coordination and ownership through actual completion and inspection.
Bytes verified at the archive destination alone do not prove the original name
is absent. If a different file appears at the source or it cannot be inspected,
retain the known archive fact and report conflict/unresolved source state. Do
not touch that new file or use credentials from the old owner. An unchanged
source and absent destination establish no move only after issued work settles.
An error or cancelled wait does not prove no move. Never blindly repeat it,
delete unidentified files or claim immunity to every noncooperating writer.
Known missing artifacts require recovery, not an infinite inspection loop.
Completed archive releases the old root after dependent work settles; later
creation is a separate explicit operation with a new identity.

Keep domain outcome, observed file facts, effective adoption, restrictions,
artifacts and pending work separate. Distinguish completion, user decline,
stopped-before-issue, host loss, invalid/unsupported input, authentication failure,
conflict, known partial publication and unavailable evidence. Adapter failure
before mutation is interaction failure, not authentication failure or consent.
An error after publication can coexist with verified commitment. Later external
change does not retroactively change a verified commit into failure. Source-key
passphrase requests are not failed vault unlocks. Use non-secret error codes;
do not retain raw exceptions, parser/OpenSSL error data or secret inputs in
history/diagnostics. Cancellation alone establishes no publication only if
mutation issue was prevented. After issue, no-effect conclusions require settled
actual work and verified physical outcomes; cancellation does not establish rollback.

Reconciliation joins the current inspection pass and never re-encrypts,
republishes, rebases, restores or repeats archive. Later inspection may acquire
new resources: release of one pass is not final operation retirement or proof
of current quiescence. Coordinate new owner-dependent inspection with owner
termination. After actual work settles, the existing visible unresolved-close
policy applies; non-secret evidence/restrictions can survive view closure without
roots retained solely for them. Only final operation retirement ends inspection
admission. No hidden process lifetime or recording five-/ten-second timeout
extends or replaces these rules.

### Interaction, temporary secrets and shared unlock

The interaction coordinator routes typed requests to a live UI host and binds
replies to operation/request IDs, target, relevant revision and inspected source
facts. Changed prerequisites yield stale-review/conflict, not automatic rebase
or confirmation loops. Present only the existing required confirmations. Profile
owners supply exact known registered-reference impact and unavailable/scope
information. Unknown files are not a frozen global inventory. A known change
invalidating displayed impact requires fresh review. Only the explicit rename
choice authorizes edits to the reviewed registered profiles.

The adapter owns controls and unaccepted replies; the operation owns accepted
inputs. Define one acceptance/cleanup handoff so late/duplicate replies are
cleared by their current owner and never reused. Do not acquire secret input
before bounded execution can own it. Clear master/KEK, import passphrase, payload
plaintext and authentication working copies at their established end of use,
after actual native use ends. Retaining a failed draft preserves non-secret edits
and outcome information, not passwords/passphrases or all decrypted entries.
Retrying can require fresh input. Necessary candidate roots and encrypted recovery
images follow the separate uncertainty/lifetime rules above.

Track actual unlock consumers separately from observers. Cancelling observation
or dropping a view does not remove an admitted operation's unlock interest.
Explicit stop or coordinated host/role termination may remove that consumer;
others continue the shared per-file attempt. User cancellation/failure of the
shared prompt ends its participants without automatic prompt loops. Hosting may
transfer to a surviving window or the coordinator may resolve the interaction.

When the last consumer leaves, stop admission to that attempt and request
cancellation. Keep its native work, input and worker charge through actual
completion. New consumers cannot join the cancelled attempt or adopt late success
under another epoch. A new attempt requires fresh admission after the old work
is safe to retire; cancellation cannot create a second expensive worker.
Serialize root adoption against cancellation and owner termination. Clear a root
never adopted by a surviving authorized owner after actual use instead of
publishing it from a late callback.

Shared unlock completion must not depend on releasing a mutation admission
waiting for that unlock. Do not occupy the global expensive-work slot while
waiting for human input, file pickers or impact confirmation. Release physical
mutexes before reentrant UI/profile/settings callbacks; logical admission remains
owned without a mutex held across asynchronous waits.

The selected-only authentication resolver hands off only the requested exact
credential. Recheck selection epoch, revision eligibility and restrictions at
handoff; clear a superseded extraction instead of delivering it or retrying
against another vault. A valid handoff follows the authentication owner's existing
lifetime. Management updates neither disconnect established sessions nor cache
their plaintext credentials in the vault module.

### Capacity and retirement

Use finite versioned internal limits for owner-role/binding records, admitted/
queued operations, unlock consumers, observers/subscriptions, interactions and
replies, queued secret inputs, native/KDF work, immutable encrypted images,
metadata delivery and artifacts. Include provisional plaintext/native buffers
in byte accounting. Reserve before acceptance/allocation; reject/defer new work
without effects when full. Reserve capacity for outcome evidence, restrictions,
root transfer and committed metadata before issuing publication. A known disk
change must not depend on an unreserved result-record allocation afterward.

The implementation reserves up to 64 vault publication result slots and
64 management delivery slots. The shared budget for non-secret metadata
projections is 64 MiB. An issued unresolved change with an unlocked owner
keeps its result slot through adoption or owner disposal. Views share the
committed projection, and its byte charge follows that projection through
subsequent revisions and view release.

Actual work remains charged after cancellation; non-secret unresolved records
remain charged after roots are released. Use compact evidence when sufficient.
Charge an image once only when ownership is actually shared, and never create
implicit per-observer copies. Unsubscription bounds delivery without cancelling
work or erasing results. Coordinate artifact accounting with the shared file
module so ownership is neither omitted nor counted as independent duplicate
copies. Unresolved artifact accounting ends only after verified deletion or an
explicit charged handoff. Verified manual backups/archive destinations may
become ordinary long-lived user files after explicit ownership/accounting
handoff; uncertain destinations and parked/candidate files remain charged.

Retire only resolved records with no live resources. Unknown/retired IDs report
that fact and cannot replay mutations. Never discard unresolved restrictions by
TTL, memory pressure, context recreation or selection changes. Final user files
do not justify discarding unresolved evidence. Fix concrete type names and new
finite limits at implementation start and test the thresholds; existing file/KDF/
import limits remain binding. No measured performance or finite completion
deadline for native/OS I/O is promised.

## Verification gate

The [accepted 23-case management-operation verification plan](../drafts/whole-codebase-architecture-deepening.md#candidate-4-verification-plan)
is required future verification, not executed evidence. Exercise the ordinary
named operations, observations, shared metadata and selected credential resolver
with real vault/OpenSSL behavior and independent known-answer/independently
produced fixtures. A writer/reader round trip or mock vault alone is insufficient.
Controlled internal filesystem, completion/time, interaction and profile/settings
adapters expose faults and ordering; retain Windows/native integration for actual
handles, access rules, aliases and completion. Ownership instrumentation may
observe release without recording secrets or replacing cryptography. Verify
shared unlock/hosting, stale replies, role and selection races, native cleanup,
initialization/archive/restore guards, partial publication, notification failure,
reconciliation/reopen restrictions and all capacity thresholds. Q12-Q16 establish
design readiness, not authorization to implement or a claim that these tests ran.

Verify that password and private-key entries can be created, edited, and reopened
without a username. Verify that an authenticated payload with the legacy
`username` property is rejected.
Verify duplicate-name rejection across kinds when adding, importing, renaming,
and opening an authenticated vault. Verify that renaming leaves the old name
unresolved, that a later same-name replacement resolves by name, and that an
editing operation captured before the replacement still fails its revision check.

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
