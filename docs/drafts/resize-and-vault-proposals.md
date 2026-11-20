# Confirmed resize, logging, and portable vault decisions

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q106-Q108 accepted on 2026-11-08, with the user's vault extension
`.tttykey` replacing the proposed extension. Implementation remains excluded.
The decisions below are confirmed; explicitly identified detailed-format and
evaluation work remains open. No code,
prototype, vault, key material, cryptographic tests, or performance measurements
have been produced.

## Q106: Reflow normal output while preserving logging provenance

Reflow the normal screen and its retained scrollback when the
terminal width changes. Join only continuations that originated from soft wrap;
preserve real received LF/CRLF boundaries, text order, attributes, meaningful
spaces, and received-TAB provenance. Do not split wide characters or combining
sequences. Padding introduced solely because a wide character cannot fit at the
right margin is layout, not received text to copy, search, or log.

Preserve cursor/content anchors through reflow, including a pending delayed wrap.
Do not treat new physical row numbers as new output identities. Preserve
recorded content ranges, content revisions, and interval eligibility separately
from layout changes. Changing width, colors, or overlays alone does not create
a new Text snapshot revision. Preserve a selection/search target when its
content survives; apply existing clearing rules when the selected content or
current result is changed or evicted. Browsing anchors follow surviving content
without jumping to the bottom merely because the window is resized.

Resize is an ordered local terminal event, not receive data. It must not flush
an incomplete character/control sequence or separate received CR from a
subsequent LF. Apply geometry changes to the receive-only Text state as well
as the combined display, without importing local-echo content or cursor effects.
Use the Text state's own content retirement to determine Text eligibility.

### Preserve the 5,000-physical-row meaning

The configured limit continues to mean normal-screen active rows plus retained
scrollback, counted at the current terminal width. Do not silently change it to
5,000 logical lines or an unbounded retained character count.

Narrowing can increase the physical row count without any new reception.
If the new layout exceeds the limit, retire the oldest portions. Immediately
before retirement, submit eligible, changed/unrecorded portions to the Text
writer using the existing eviction rule. Add no newline for the local wrap or
resize. Do not record an already-recorded range again, or make paused/excluded
content eligible. Expanding later cannot recover already retired contents.

This is the precise exception to "resize does not affect logs": reflow itself
creates no record, but actual quota eviction can cause an earlier Text flush.
Moving an active row into retained scrollback without eviction still creates
no record. UI scrolling does not change either behavior.

Transfer the eligible snapshot and its provenance to the bounded recording
writer before discarding its backing content. This does not require waiting for
each disk flush on the UI thread. A writer that cannot accept the data follows
the agreed recording-error policy; never silently discard it as a successful log.
Queue limits and detailed failure handling remain a separate bounded-I/O review.

For geometry validation, keep a minimum of two terminal columns so a double-width
character can be represented. Do not let the effective active-screen height
exceed the configured total retained-row limit: if the application window could
display more rows, cap the terminal grid at that limit and leave remaining
client space outside the grid. Never silently raise the configured retention
limit. Broader numeric limits remain part of capacity/validation design.

### Alternate screens and the saved normal screen

Use traditional cell-grid resizing without reflow for the alternate screen.
Preserve in-bounds cells, discard out-of-bounds cells as needed, and allow the
peer's actual redraw to update the display. Notify size changes through the
transport's supported mechanism. Do not inject a full-screen snapshot into logs.
Alternate-screen Text already records sanitized receive-order output, so clipping
display cells does not require a last-moment screen dump.

While the alternate screen is active, retain the saved normal screen at its
existing geometry. Reflow it once to the final geometry when returning, applying
the same quota/eligibility rules then. Temporary narrow widths while it was
hidden do not independently delete its retained history. Previously flushed
normal contents are not recorded again just because they are restored.
While parked, its retained rows are counted at that saved layout width; the new
width is applied when reflow actually occurs.

Raw records no local resize events. Terminal mode adds no synthetic resize
control, redraw copy, padding, or line ending. Actual peer output caused by the
size notification remains ordinary received output and may appear in logs.
Terminal mode still does not promise identical standalone screen replay without
the original size history.

This decision resolves local resize behavior; general nonlinear cursor edits,
interleaved partial-line finalization, and detailed writer limits remain
separate open logging work.

### Acceptance examples for later implementation

| Scenario | Required outcome |
| --- | --- |
| Receive `ABCDEFGHIJKL\r\n` at width 6, then resize to width 3 and back | Text contains `ABCDEFGHIJKL\r\n` once; display wrapping changes only. |
| For illustration, a 3-row limit contains `ABCDEFGHIJKLMNOPQR` without a newline at width 6; resize to width 3 | Six rows would be needed. Before the oldest three retire, write eligible `ABCDEFGHI` without a newline. |
| Receive LF after the preceding retirement | Write the eligible remaining `JKLMNOPQR` and LF; do not repeat the prefix. |
| The retired prefix was already recorded or excluded by pause/resume | Do not append it during resize; preserve the same recording provenance. |
| Receive `abc\r`, resize, then receive LF | Preserve the single received CRLF boundary. |
| Resize during a multi-byte character or VT sequence | Keep parser/decoder state; do not invent replacements or complete the unit early. |
| A wide character moves to the next row | Keep the character whole; do not log or copy layout padding as a space. |
| Resize an alternate screen | No screen dump; record only actual eligible receive/local-echo operations according to each mode. |

### Reference and deliberate adaptation

Tera Term 5.7.1 copies the smaller old/new width during resize and can discard
right-hand cells. The accepted design deliberately avoids copying that behavior
for normal output. Microsoft Terminal uses normal-screen reflow and traditional
alternate-screen resizing, including deferred normal-screen reflow while the
alternate screen is active. These are implementation references, not evidence
that either product implements TabTty's logging provenance rules.

## Q107: A small versioned TabTty vault container

Use a TabTty-specific, documented encrypted container with the `.tttykey`
extension. Use existing OpenSSL cryptographic primitives and a
fixed reviewed algorithm profile; do not invent a cipher or expose arbitrary
cryptographic combinations as UI settings. KeePass interchange is not an initial
requirement. This choice concerns scope and lifecycle fit, not a claim
that a new file format is inherently safer than an existing one.

### Comparison with KDBX 4.1

KDBX offers an existing published format and potential KeePass interoperability.
Under the current dependency policy, adopting it would still require a TabTty
implementation with its own parser/security review. Its XML, attachments,
history, and extensions need explicit handling; unsupported data must not be
silently discarded or executed. Its derived-key and save-time salt model is
different from the wrapped random-data-key design below. It can meet portability
and unlock-lifetime requirements with different retained key material; it is not
impossible or insecure, but adds requirements without a current interchange need.

### Confirmed protection and data model

1. Derive a 256-bit key-encryption key (KEK) from the master password with
   Argon2id and a random salt.
2. Generate a random 256-bit data-encryption key (DEK), protected by the KEK
   using authenticated encryption.
3. Retain that DEK as a root and derive a fresh payload key for each encryption
   attempt using HKDF-SHA-256, as fixed in `../specification/vault-format.md`.
   Encrypt/authenticate the complete payload with AES-256-GCM under that key.
   Keep entry names, passwords, private-key data,
   and the entry inventory inside that encrypted payload.
4. Store only format/algorithm/KDF information, required identifiers, salt,
   nonces, the encrypted DEK, ciphertext, and authentication tags outside it.
   Public metadata and ciphertext size are not secret. Bind required metadata
   cryptographically so it cannot be changed without detection after unlock.

Use a bounded UTF-8 JSON payload inside the encrypted binary envelope, with
stable vault/entry identifiers and a versioned schema. JSON is never written
unencrypted to disk. Reject duplicate fields/identifiers, unsupported required
versions, and excessive sizes/counts/depth before use. The exact binary framing,
field types, secret-byte representation, and numeric bounds remain part of the
remaining detailed format specification. Avoid compression and
entry-level encryption in the initial format to keep the authenticated data
model small.

Authenticate the complete decrypted payload before parsing or using any of it.
Keep encrypted payload data rather than a cache of all plaintext entries.
When an entry is needed, temporarily decrypt the bounded payload, extract only
the required authentication material, and clear owned temporary buffers. Other
entries may exist transiently in that buffer; do not claim they were never
decrypted. Do not materialize a long-lived object graph containing all secrets.
Selected credential plaintext still ends at the accepted authentication boundary.

After unlock, retain the necessary DEK until application exit and discard the
master password and KEK. Ordinary saves use that DEK and its existing encrypted
wrapper without another password prompt. The wrapper's authenticated metadata
must therefore contain only fields unchanged by ordinary saves. Authenticate
mutable payload metadata separately under the data-key protection. A changed
password creates a new salt, KEK, and DEK and re-encrypts the full current payload;
do not merely rewrap a data key recoverable from an older compromised copy.

### Cryptographic review profile

The selected v1 profile is Argon2id v1.3 with a 16-byte random salt, 64 MiB
memory, three passes, four lanes, and 32-byte output. This is RFC 9106's second,
lower-memory recommendation, not measured TabTty performance. Lanes are part
of the stored derivation profile; execution thread count is a separate concern.
Accept only explicitly supported KDF profiles before expensive work; never
silently lower parameters or substitute a weaker KDF on failure.

AES-256-GCM uses a 12-byte nonce and 16-byte tag. Generate salt,
nonces, and keys with the cryptographic random generator and stop on generation
failure. The completed `../specification/vault-format.md` defines key-use rules,
copy/restore/divergent-vault behavior, separate key purposes, and exact AAD bytes.
Random nonces alone are not a proof of non-repetition, and a file-local counter
alone does not solve reuse after cloning/restoring a vault. Keep these as
explicit format-review requirements rather than asserting runtime validation.

Document the full format and review its composition, parser, secret ownership,
and update/recovery transitions before implementation readiness. After separate
implementation authorization, verify standard vectors, tampering/truncation,
malicious lengths/KDF requests, copy/restore behavior, and fault paths; complete
an independent security review before release. No such tests or review have been
performed in this specification round.

### Confirmed password input policy

For a newly created or changed master password, allow 14 through 1024 Unicode
scalar values after NFC normalization, including spaces and Japanese. Encode
the normalized result with strict UTF-8. Do not trim, case-fold, apply NFKC,
silently truncate, or replace invalid Unicode. Reject line breaks and control
characters with an explicit validation message. Permit paste and an explicit
show/hide control; confirm the new password twice. Recommend a long random
passphrase without mandatory character-class combinations or periodic changes.

The limits are a TabTty product choice, not a claim of NIST compliance or
adequate offline guessing resistance based on length alone. The exact versioned
normalization/encoding rule belongs to the format. Unlock must apply the file
version's rule and must not invalidate an existing file merely because a later
release changes new-password recommendations.

## Q108: Vault operation, password changes, and recovery

Provide creation, first-use unlock, entry management, master-password change,
manual encrypted backup, and explicit restore. Preserve the accepted one-vault,
all-windows, unlock-until-full-app-exit lifecycle. Keep cloud synchronization,
automatic merge, multiple open vaults, OS auto-unlock, recovery keys, and
plaintext export outside initial scope.

Use a default local vault location under `%LOCALAPPDATA%\TabTty`, with an
explicit alternate file selection and the selected path in local app settings.
Profiles refer to stable vault/entry identifiers, not secret values or absolute
private-key source paths. A copied vault can be selected on another PC and
unlocked with its master password; original-machine DPAPI material is not a
decryption requirement. Q111-Q113 subsequently establish exact credential
references and trusted profile activation after basic loading checks, without
a separate profile approval gate. Ordinary host-key and vault checks still apply.

An explicit password-change action asks for the new password, not another
routine unlock during the same application run. Switch to the new keys only
after saving and verifying the replacement. Previously copied files and old
encrypted backups remain usable with their old password; changing the current
file cannot revoke them.

### Saving, backup, and conflicts

- Write an encrypted temporary file in the same directory/volume, flush it,
  then perform a supported replacement with backup. Keep plaintext out of
  temporary files. Apply suitable current-user file permissions to vault,
  temporary files, and backups without making those ACLs part of portable
  decryption. A local filesystem is the initial supported writable location;
  network-share/cloud-sync concurrent editing is not promised.
- Retain one previous successfully authenticated and parsed encrypted generation
  as an automatic backup; also allow explicit encrypted backup to a user-chosen
  location. A backup may retain deleted entries and an earlier master password.
- Use a single writer shared by the application's windows. Prevent cooperating
  second writers with locking independent of the replaced file, and compare the
  expected file state before saving. On detected external change, stop writes
  and new credential retrieval from that stale vault snapshot; preserve active
  connections, report the conflict, and permit an encrypted rescue copy where
  applicable. Resolve/reload the vault on the next application run instead of
  adding repeated unlock prompts. Digest checks are conflict detection, not a
  cross-process atomic compare-and-swap or rollback-proof guarantee.
- Do not promise that a replacement call leaves the original path unchanged
  on every failure or guarantees power-loss durability. Windows replacement
  failures can leave different combinations of original, backup, and temporary
  files. Preserve recoverable candidates and report failure accurately; never
  replace an unreadable vault with a new empty one.
- After an interrupted save, restore only a candidate that authenticates and
  passes schema validation, using an explicit recovery choice. Do not trust
  a file merely because its timestamp is newest. Preserve the originals until
  recovery succeeds.
- An authentication failure means a wrong password or damaged/altered file;
  do not claim to distinguish these reliably. A valid older file can still
  authenticate: a portable file alone does not provide rollback prevention.
- A forgotten master password cannot be reset to recover existing contents.
  Encrypted backups address file loss/corruption, not a forgotten password.

## Primary references reviewed on 2026-11-08

- [Tera Term 5.7.1 buffer resize](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L492)
- [Microsoft Terminal resize policies](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalCore/Terminal.cpp)
- [Microsoft Terminal reflow](https://github.com/microsoft/terminal/blob/main/src/buffer/out/textBuffer.cpp)
- [KeePass KDBX 4.1 format](https://keepass.info/help/kb/kdbx.html)
- [KeePass XML schema](https://keepass.info/help/download/KDBX_XML.xsd)
- [OWASP cryptographic storage and KEK/DEK](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
- [RFC 9106 Argon2 parameters](https://www.rfc-editor.org/rfc/rfc9106.html#section-4)
- [OpenSSL 3.5 Argon2](https://docs.openssl.org/3.5/man7/EVP_KDF-ARGON2/)
- [OpenSSL random generation](https://docs.openssl.org/3.5/man3/RAND_bytes/)
- [NIST GCM specification](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)
- [NIST password guidance and scope](https://pages.nist.gov/800-63-4/sp800-63b.html#passwordver)
- [Microsoft ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
- [Microsoft flushing](https://learn.microsoft.com/en-us/windows/win32/fileio/flushing-system-buffered-i-o-data-to-disk)
- [Microsoft file security](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights)
- [Microsoft file locking](https://learn.microsoft.com/en-us/windows/win32/fileio/locking-and-unlocking-byte-ranges-in-files)
