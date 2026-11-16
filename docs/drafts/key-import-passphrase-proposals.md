# Confirmed imported private-key passphrase encoding

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q161 accepted with the user's UTF-8-only amendment on 2026-11-10.
Specification only. No key file, password, vault, implementation,
conversion, or cryptographic test has been created or processed.

Read with `authentication-issue.md`, `credential-vault-issue.md`, and
`ssh-authentication-proposals.md`. Q135 already accepts importing a validated
private key into the encrypted vault and discarding its source passphrase.
This decision concerns the bytes supplied while opening the original key file,
not SSH terminal communication or the vault's master password.

## Q161: UTF-8 only for an existing key's passphrase

Use strict UTF-8 for source-key passphrases in the initial release. Do not offer
an encoding selector or an alternate encoding setting. The user selected this
scope instead of the proposed 47-encoding manual selector; that proposal is
superseded. Arbitrary binary passphrase input is also outside the initial UI.

This is a deliberate import compatibility limit: otherwise supported encrypted
keys that require different passphrase bytes cannot be imported through this
UI. The envelope does not generally identify the original character encoding,
so do not claim to detect and specifically reject a source encoding. ASCII
passphrases whose bytes are identical in UTF-8 remain compatible regardless of
the source tool's encoding label. Terminal communication retains its separately
accepted 48-encoding inventory.

Apply the following confirmed behavior:

- Always use UTF-8, including manual retries. Do not derive passphrase encoding
  from the profile, terminal, UI language, or current Windows ANSI code page.
- Encode exactly the entered Unicode text, without NFC/NFKC, trimming, case
  folding, best-fit substitution, or automatic retry with other encodings.
  Add no BOM or terminating NUL to the password bytes supplied to the KDF.
- Reject invalid Unicode and embedded NUL before decryption. Do not impose the
  vault's creation-time
  minimum length on an existing source passphrase. Preserve spaces and an empty
  input where the supported file format permits it. Other input/resource limits
  remain part of the bounded key-import contract.
- Report inability to open the encrypted key without claiming to distinguish a
  wrong passphrase, incompatible source encoding, or damaged input with certainty.
  State the UTF-8-only import scope in the import help. Users can correct their
  input and retry, but TabTty does not try another encoding or rewrite the
  source file to convert its passphrase. Format/algorithm exclusions and bounds
  established independently should remain specific errors.
- After successful import, store the validated canonical private key in the
  vault. Do not store the source passphrase or require its source encoding for
  later authentication. Clear owned import input and derived working material
  on success, cancellation, or failure according to the existing secret policy.

The `.tttykey` master password retains its accepted NFC then strict UTF-8 rules.
SSH protocol password/keyboard-interactive encodings also retain their separate
protocol contracts. The source-key import decision does not change either behavior.

## Evidence and later acceptance checks

OpenSSL documents that an existing passphrase-protected object needs the same
passphrase bytes/encoding used when it was created. Its general OSSL_STORE
interface specifies UTF-8. TabTty's accepted initial scope supplies strict
UTF-8 bytes without legacy fallback; verify each selected import API's length
and byte-preservation contract rather than assume all APIs behave identically.
[OpenSSL passphrase encoding](https://docs.openssl.org/3.5/man7/passphrase-encoding/)

PBKDF2 takes a password octet string; a displayed Unicode string alone does not
specify those bytes. The supported OpenSSH key path similarly feeds passphrase
bytes to bcrypt_pbkdf. Do not borrow an ordinary bcrypt password-length rule for
this different KDF. [RFC 8018](https://www.rfc-editor.org/rfc/rfc8018.html),
[OpenSSH key-file handling](https://github.com/openssh/openssh-portable/blob/master/sshkey.c)

After implementation is explicitly authorized, include independently generated
fixtures using ASCII and non-ASCII UTF-8 passphrases. Verify combining-character
differences without normalization, leading/trailing spaces, invalid-Unicode/NUL
rejection, retry with corrected input, and source-file immutability. Include
CP932/EUC-JP/Windows-1252 fixtures whose passphrase bytes differ from UTF-8 and
verify failure without alternate-encoding attempts or vault mutation. Verify
that the UI offers no encoding selector and that changing terminal encoding does
not change import behavior. Successful import must subsequently work using only
the copied vault and its master password. No such tests have run.
