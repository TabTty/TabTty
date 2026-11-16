# Specify a portable credential vault

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Specification draft only. Portability, content scope, one shared vault, unlock
lifetime, container direction, and operation policy are confirmed. The detailed
format is completed in `../specification/vault-format.md`; implementation and
validation remain unperformed. No vault,
key material, code, prototype, or cryptographic test has been created or run.

## Confirmed format and operation policy (Q107-Q108 accepted)

`resize-and-vault-proposals.md` records the 2026-11-08 comparison of a narrow
TabTty container with KDBX 4.1. The user accepted the whole-payload AES-256-GCM
design with Argon2id-protected key wrapping, amending the extension to `.tttykey`.
Password handling, replacement/backup, concurrency, and recovery policies in
that document are also accepted. This confirms the design direction, not
completed implementation or cryptographic validation.
Exact byte framing, nonce/key-use rules including cloned/restored vaults,
numeric bounds, and the independent-review plan are now specified in that
contract. Executed verification and independent security review remain release gates.

See `technical-contract-review.md` for the 2026-11-10 nonce/key-use and file-
replacement findings. Its per-save HKDF composition is now selected in the
detailed contract without changing unlock lifetime. Q161 in
`key-import-passphrase-proposals.md` confirms UTF-8-only source-key passphrases
initially, without a selector or alternate-encoding retries. It does not change
the vault master-password policy.

## Confirmed product requirements

- Save authentication material in an encrypted vault file that can be moved or
  backed up and opened on another PC using a master password. The original
  Windows account/computer must not be required for decryption.
- Use `.tttykey` for the credential vault, distinct from imported SSH key files.
- Ask for the master password when the application first needs the locked vault.
  After successful unlock, do not ask again during ordinary use until TabTty
  exits. A later application run needs a new unlock.
- Keep the vault unlocked across connection changes, disconnect/reconnect, idle
  periods, and Windows screen locking. Do not add automatic relocking that
  contradicts the chosen application-run lifetime.
- Preserve explicit opt-in saving. Unsaved connection credentials still have the
  existing authentication-only lifetime and require input again on reconnection.
- Keep secret values out of profile files, diagnostics, and plaintext temporary
  files. Profiles use credential references; the encrypted vault is a separate
  file required when moving saved credentials to another computer.
- Use one selected vault shared by all connection profiles and all TabTty
  windows in the application run. Multiple simultaneously open vaults are
  outside the initial scope.
- Support passwords and explicit import of complete private-key material.
  Q135 stores canonical PKCS#8 DER within the encrypted vault. A source-key
  passphrase is transient import input, not a separately saved entry or a
  per-connection dependency. Authentication using an imported key must continue to
  work if its source file is later moved or removed. Import never deletes the
  source key file as a side effect.
- Share one unlock state across windows. New windows, tab transfers, and file
  activation do not prompt again after successful unlock. Closing one window
  while the application continues running does not discard the vault key.
- Closing the final application window ends the run and discards the unlock
  state; no background/tray lifetime extends it. A tab being transferred into
  another/new window does not constitute application exit.

The earlier Windows Credential Manager-only persistence direction is superseded.
No Windows auto-unlock option has been selected; such an option must not silently
replace the confirmed first-use master-password prompt.

## Key and plaintext lifetime

Keep only necessary vault decryption key material for the application run. Clear
the master password after derivation and transient wrapping keys after unlocking
the data key. Do not retain all decrypted credential objects until exit merely
because the vault remains unlocked. Retrieve the selected entry when required,
then end use of its plaintext after authentication success, failure, cancellation,
or disconnect. The accepted whole-payload design temporarily decrypts other
entries too; do not claim per-entry decryption or retain all plaintext objects.

Clear owned key/buffer material when its lifetime ends, including the retained
vault key on orderly application exit. Do not claim guaranteed erasure of every
runtime/OS copy or dump. SSH traffic keys have their own connection/rekey lifetime.

## Confirmed user-facing operation policy

- Store the selected vault's location in local app settings; use stable vault/
  entry references in profiles so moving the files does not require embedding
  secrets there. Imported keys use canonical PKCS#8 DER inside the encrypted payload;
  an additional external-key-file authentication mode has not been selected.
- Default the vault location under `%LOCALAPPDATA%\TabTty`, allowing explicit
  selection of another local file. Retain one previous valid encrypted generation
  automatically and support manual encrypted backup. External changes stop
  writes and new credential retrieval from the stale snapshot without closing
  existing terminal connections. See the accepted Q108 recovery/conflict rules.
- Provide creation, unlock, entry management, encrypted backup/copy, and master
  password change. Keep cloud synchronization/merge, sharing, multiple open
  vaults, OS automatic unlock, and recovery keys outside the initial scope.
- Without a separate recovery mechanism, a forgotten master password cannot be
  reset to decrypt existing contents. Encrypted backups protect against lost or
  damaged files; they do not recover a forgotten password by themselves.

## Confirmed cryptographic direction, requiring detailed format review

Use existing OpenSSL cryptographic primitives; do not invent cryptographic
algorithms. Use Argon2id with a random salt to derive a key-encryption
key (KEK), protecting a random data-encryption key (DEK), with AES-256-GCM for
authenticated protection. Use the retained DEK as the root of per-encryption
HKDF-SHA-256 payload keys under the completed format. Retain the root for the app run; ordinary saves can
reuse its existing wrapped representation without retaining the master password
or KEK.

OpenSSL 3.5 supplies Argon2id without another Argon2 implementation dependency.
RFC 9106's 64 MiB, three-iteration, four-lane profile is the fixed v1 baseline,
not a measured performance claim. Verify the selected profile through later
authorized performance evaluation and security review; never silently
truncate passwords or fall back to a weaker KDF. Password handling is accepted:
NFC then strict UTF-8, 14-1024 Unicode scalar values for creation/change, no
trimming/case folding, and no character-class requirement. Reject invalid
Unicode, line breaks, and control characters explicitly; permit paste and
show/hide. Preserve the file version's rules when unlocking older files.

The design must specify random generation and its failure handling, salt/nonce
sizes, GCM tag size, per-key nonce uniqueness/usage limits, separate key purposes,
and authenticated version/algorithm/KDF/vault metadata. Reject authentication
failure before using decrypted data. Do not overwrite the original on an unlock
failure or claim to reliably distinguish a wrong password from tampering/corruption.

For an explicit master-password change, use new salt, new KEK, and a new
DEK with full re-encryption of the current contents. Rewrapping the same DEK alone
would let someone who extracted that DEK from an old copy decrypt later contents
using it. Changing keys does not revoke already copied old vaults/backups.

A narrow versioned TabTty container is selected after comparison with KDBX 4.1.
Use a bounded UTF-8 JSON payload inside the encrypted envelope, with no initial
compression or entry-level encryption. Exact framing, authenticated metadata,
nonce/key-use limits, and numeric bounds remain open. The selected composition,
parser, update procedure, and secret lifetime need independent review before
release; selecting algorithms alone is not sufficient validation.

## File robustness requirements

- Treat vault input as untrusted. Bound file size, KDF memory/work parameters,
  salt/nonce/tag lengths, entry counts, payload lengths, and parsing depth before
  expensive work or allocation. Reject unsupported required versions/algorithms.
- Write only encrypted temporary files, then use an appropriate replace/flush
  procedure that preserves the prior usable file on recoverable failure. Define
  interrupted-save recovery, backups, and concurrent-write conflict handling.
  Do not silently overwrite edits or claim power-loss/cloud-sync guarantees.
- Authenticated encryption detects altered data but does not by itself detect
  replacement with a valid older copy. Do not promise rollback prevention from
  a portable file alone.
- Keep errors visible and avoid plaintext storage fallbacks. A cancelled or failed
  unlock prevents use of saved credentials; never turn it into an empty vault or
  overwrite it automatically.

## Remaining decisions

Q135 in `ssh-authentication-proposals.md` accepts validated canonical PKCS#8
private-key data inside the encrypted vault, with source-key passphrases used
only during import and not retained. This supersedes the earlier possibility
of separately saved source-key passphrases. The accepted import envelope matrix
is defined there; no key import or conversion has been performed in this phase.

Q111-Q113 in `profile-management-proposals.md` are accepted with amendments:
profiles may share exact vault/entry/kind references; shared updates affect
future authentication; deletion leaves unresolved references; migration uses
separate `.ttty` and `.tttykey` files. External profile activation is trusted
after basic loading checks, with no per-profile approval gate before access to
the selected unlocked vault entry. Required host-key checks and exact reference
resolution remain. Detailed validation occurs every time the editor opens a
profile, during changes, and on save. The container and unlock lifetime remain
as already accepted.

- Exact key validation/resource bounds and
  imported-key/credential-reference schema. Profile moves are new local
  registrations without changing vault/entry references.
- Exact container framing/schema, cryptographic parameter profile, nonce/key-use
  limits, resource bounds, and independent-review/verification plan. Container
  direction, extension, and password input rules are accepted in Q107.
- Detailed file-permission and failure-state specification for the accepted
  creation/password-change, backup/recovery, storage, and conflict policies.

## Acceptance cases for later implementation

- Importing a private key using its needed source passphrase leaves the source file
  unchanged. Copying the profile and vault to another PC permits use of that
  entry after unlock without requiring the original private-key path or passphrase.
  No plaintext key temporary file or saved source-passphrase entry is created.
- Multiple windows and profiles share one selected vault and one unlock state.
  Tab detach/merge and opening another profile do not request its master password
  again during the application run.
- Shared-entry updates affect every referring profile's future authentication;
  deleting a profile never deletes its vault entries. Deleted entries fail
  resolution rather than being replaced by same-named entries.
- Opening a copied profile on another PC requires the referenced vault and
  ordinary first-use unlock, but no additional local profile approval.
- Ending use of an authentication credential does not discard the app-lifetime
  vault key; final application exit clears owned vault key material.

## Primary references, checked 2026-11-07

- [OpenSSL Argon2](https://docs.openssl.org/3.5/man7/EVP_KDF-ARGON2/)
- [RFC 9106 Argon2 parameters](https://www.rfc-editor.org/rfc/rfc9106.html#section-4)
- [OWASP password derivation guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [OWASP cryptographic storage](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
- [OWASP key management](https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html)
- [NIST SP 800-38D GCM](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)
- [OpenSSL random generation](https://docs.openssl.org/3.5/man3/RAND_bytes/)
- [Microsoft SecureString guidance](https://learn.microsoft.com/en-us/dotnet/api/system.security.securestring?view=net-10.0)
- [Microsoft document update alternatives](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf)
- [Microsoft ReplaceFile](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
- [Microsoft buffered file writes](https://learn.microsoft.com/en-us/windows/win32/fileio/flushing-system-buffered-i-o-data-to-disk)
- [KeePass published KDBX format](https://keepass.info/help/kb/kdbx.html)
