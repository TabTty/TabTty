# Technical contract review after Q159-Q160

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Reviewed 2026-11-10 through documentation and source inspection. No TabTty
implementation, prototype, key processing, terminal experiment, or benchmark
has run. This historical review records facts that constrained the specification,
technical resolutions derived from accepted behavior, and candidates still
requiring review at that stage. The resulting completed contracts and readiness
status are now in `../specification/implementation-readiness.md`; this source
review itself is not runtime validation.

The user accepted Q159-Q160's capacity targets and overload policy. Detailed
resource accounting must follow those choices rather than use silent output
loss or early history eviction to meet a target. `specification-frontier.md`
remains the full remaining-work index; this review covers selected terminal,
SSH, and vault seams, not every open branch.

## Terminal receive classification and logging

Keep incremental decoder/parser state per received source, separate from local
echo. A local display operation cannot finish a received character, escape
sequence, or charset designation. Preserve received unit order and recording
eligibility through chunk boundaries; a pending sequence is not permission to
delay or replay a C0 operation that has already executed.

Never classify raw bytes `80`-`9F` globally as C1 controls. For example, CP932
`9B 5B` maps to U+5ABC, so interpreting its first byte as CSI destroys valid
text. Encoding-owned EUC SS2/SS3 are also distinct from VT control spelling.
Use seven-bit ESC spellings for the initial VT control/reply baseline; do not
enable global eight-bit C1 interpretation implicitly. This is a technical
compatibility resolution, not automatic encoding detection.
[Microsoft CP932 mapping](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP932.TXT),
[Microsoft terminal parser, pinned review revision](https://github.com/microsoft/terminal/blob/5f7dc283013152f89bfb76950515c33f45157ad7/src/terminal/parser/stateMachine.cpp)

The selected codec owns multibyte continuation and JIS designation rules. An
unrelated C0/ESC must not be swallowed as a fake continuation: apply the agreed
replacement/resynchronization rule, then process the control. Conversely, a
valid multibyte continuation that resembles a printable VT delimiter remains
part of the character. Exact malformed-unit consumption vectors are still
required for each codec family.

The following Text behavior follows Q149-Q151, rather than copying a reference
application's logger:

| Received operation | Display effect | Normal-screen Text consequence |
| --- | --- | --- |
| CR | Move to the left edge without erasing | Do not finalize |
| LF | Move down, scrolling at the bottom margin; receive LNM may also return the column | Finalize eligible text with LF, or CRLF for an actual received CR/LF pair |
| VT or FF | LF-like display movement, including receive LNM | Do not synthesize a logged LF or newline finalization |
| IND, `ESC D` | Move down in the same column; scroll at the margin | No synthetic newline |
| NEL, `ESC E` | Move to the first column of the next row; scroll if needed | No synthetic newline |
| RI, `ESC M` | Move up; reverse-scroll at the top margin | No synthetic newline |
| Cursor positioning | Move within the applicable bounds | No finalization merely because the cursor moved |
| Actual scroll displacement beyond all retained output | Retire the displaced content | Q151 flushes eligible unrecorded content without adding LF |

Alternate-screen Text likewise must not invent LF for VT/FF/IND/NEL; retain its
already accepted sanitized-stream policy, including standalone CR handling.
Mouse scrolling and local wrapping are still unrelated to received newlines.
Receive LNM must not rewrite the saved Enter mapping or alter its connection
snapshot. Reference display movement is documented separately from these
TabTty logging and input-policy inferences.
[Tera Term 5.7.1 controls](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/about/ctrlseq.html),
[Microsoft output dispatch](https://github.com/microsoft/terminal/blob/5f7dc283013152f89bfb76950515c33f45157ad7/src/terminal/parser/OutputStateMachineEngine.cpp)

### Explicit reference differences

These resolutions preserve earlier accepted requirements where reference
behavior differs. Include them in the eventual exhaustive control table:

- ED2 clears active-screen content without moving it into scrollback first.
  Tera Term and Microsoft implementations can scroll the old page into history;
  adopting that side effect would undermine Q149's explicit erase treatment.
- ED3 clears scrollback while preserving the active page and its cursor/margins.
  Apply Q149 deletion semantics without a pre-erase Text snapshot. This follows
  Microsoft's scope; Tera Term's broader ClearBuffer reset is not selected.
- SU/SD operate on the scrolling region independently of the cursor row, matching
  the reviewed source behavior. Microsoft's prose description is not sufficient
  to determine that boundary.
- IND keeps its column even when LNM is enabled. This follows the Tera Term
  manual and Microsoft dispatch, rather than the Tera Term source's additional
  carriage-return side effect in that mode.

These are static source comparisons, not observed runtime equivalence.
[Tera Term buffer handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c),
[Tera Term control handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c),
[Microsoft control dispatch](https://github.com/microsoft/terminal/blob/5f7dc283013152f89bfb76950515c33f45157ad7/src/terminal/adapter/adaptDispatch.cpp)

### Input framing and reference key map

Tera Term's installer selects IBMKEYB.CNF as the default keyboard configuration.
Its Home/End sequences are `CSI 1~` / `CSI 4~`, independently of application
cursor mode. Do not label Microsoft's different Home/End table as the Tera Term
default. Normal arrows use `CSI A/B/C/D`; application arrows use `SS3 A/B/C/D`.
The complete modified-key and keypad matrix remains to consolidate while
preserving accepted Enter, Backspace, IME, and local-shortcut rules.
[Tera Term default key map](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/release/IBMKEYB.CNF),
[Tera Term keyboard handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/keyboard.c)

For a paced paste, finish whole-input validation and strict encoding preflight
before sending. Capture peer bracketed-paste mode immediately before the first
send and keep that framing decision for the operation. Order any necessary
codec boundary, start marker, payload, codec closure, and end marker together.
A mode change affects the next paste. Healthy-connection Stop completes a
started wrapper exactly once without sending an extra newline; disconnect must
not replay a pending closure on another connection.

Automatic terminal replies must wait until that paste's closure instead of
being inserted as paste contents. Keep this queue bounded under Q160; SSH/
Telnet transport negotiation remains separate from terminal payload. This is a
TabTty ordering resolution derived from accepted paste semantics, not a claim
that the reference specifies asynchronous Stop races. Codec-generated shifts
are not user control characters and do not become separate pacing characters.
[Tera Term clipboard framing](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/clipboar.c)

### Japanese mapping findings that still require exact tables

Two real differences prevent treating a generic .NET JIS decoder as the whole
accepted contract:

- RFC 1468's JIS Roman designation `ESC ( J` maps `5C` to U+00A5 and `7E` to
  U+203E. Tera Term and Microsoft's historical implementation treat those
  positions as ASCII. Choose and document the exact receive and strict-transmit
  rule, including its interaction with the accepted OUT J preference; merely
  saying "standard/Windows-compatible" leaves this unresolved.
- Microsoft's reviewed ISO-2022 decoder classifies `ESC $ ( D` as its JIS0208
  mode. Delegating the whole stream to it can reinterpret JIS X 0212 data as
  another repertoire, contrary to Q145's explicit replacement requirement.
  TabTty's designation/repertoire layer must retain that distinction.

Also complete the CP932 extension-to-JIS/EUC matrix and malformed byte-consumption
vectors. Do not resolve any difference with best-fit output or a following
control consumed as character data.
[RFC 1468](https://www.rfc-editor.org/rfc/rfc1468.html),
[.NET 10 ISO-2022 source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Encoding.CodePages/src/System/Text/ISO2022Encoding.cs),
[Tera Term charset source](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/charset.cpp)

## SSH protocol constraints

The accepted algorithm list and OpenSSL boundary stand. The remaining contract
must make these constraints explicit:

| Area | Source-backed constraint |
| --- | --- |
| Packet framing | RFC 4253 requires support for payloads through 32,768 bytes and total packets through 35,000 bytes; those are required receive capabilities, not a universal maximum. Bound lengths and arithmetic before allocation/decryption. |
| Directional keys | NEWKEYS itself uses the old keys; switch the sending direction after sending it and the receiving direction after receiving it. Keep the initial session identifier through rekeys. Account for correctly ordered in-flight messages. |
| Strict KEX | Negotiate the matching standard or OpenSSH markers during initial KEX; verify the first received packet and legal KEX ordering. Apply directional sequence resets only when strict KEX was negotiated. |
| Extension parsing | Accept well-formed EXT_INFO up to the transport's supported packet limit; derive count limits from remaining packet bytes, not an arbitrary smaller extension cap. Unknown values are binary strings, including NUL. Receipt is optional. |
| GCM negotiation | For a direction selecting OpenSSH AES-GCM, lack of a common MAC-list entry is not by itself a negotiation failure. Do not apply the original RFC 5647 negotiation wording indiscriminately to deployed OpenSSH names. |
| Packet protection | GCM authenticates plaintext packet length as AAD, uses a 12-byte IV and 16-byte tag, and exposes no provisional plaintext before authentication. CTR plus EtM has distinct sequence/length/ciphertext framing. |

Sources for packet/key transitions, strict KEX, extension handling, and packet
protection respectively:
[RFC 4253](https://www.rfc-editor.org/rfc/rfc4253.html),
[strict-kex-02 Internet-Draft](https://datatracker.ietf.org/doc/html/draft-ietf-sshm-strict-kex-02),
[RFC 8308](https://www.rfc-editor.org/rfc/rfc8308.html),
[OpenSSH PROTOCOL](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL),
[RFC 5647](https://www.rfc-editor.org/rfc/rfc5647.html).
OpenSSH points to the [AES-GCM individual draft](https://datatracker.ietf.org/doc/html/draft-miller-sshm-aes-gcm-01)
for its negotiation difference. That draft is expired, not an adopted IETF
standard. Recheck moving source/draft references when freezing the contract.

The agreed 1 GiB/one-hour rekey triggers still need exact directional byte
accounting, reservation before tighter limits, and simultaneous/peer-initiated
transitions. RFC 4344's packet/block recommendations must not be mislabeled as
universal MUST requirements. Hybrid KEX must follow the SSH-specific shared-
secret composition rather than reuse a TLS hybrid encoding.
[RFC 4344](https://www.rfc-editor.org/rfc/rfc4344.html),
[RFC 10042](https://www.rfc-editor.org/rfc/rfc10042.html)

Authentication message 60 is method-dependent; dispatch it against the current
outstanding request. PK_OK and partial success are not final authentication.
A password-change request needs an explicit unsupported-flow result or another
permitted method; it does not require adding a password-change UI. For keyboard-
interactive, answer exactly the requested prompt count/order, including zero
prompts and empty responses, without overlapping unanswered requests. A normal
multi-round exchange is not automatically a failed password attempt. Complete
numeric round, stage, body, and work bounds as TabTty limits.
[RFC 4252](https://www.rfc-editor.org/rfc/rfc4252.html),
[RFC 4256](https://www.rfc-editor.org/rfc/rfc4256.html)

DATA and stderr share one channel byte window. Credit arithmetic stays within
uint32, and outgoing data obeys both peer window and maximum packet size. EOF
ends the entire channel receive direction, not just stderr; finalize both source
decoders/parsers in a defined order. CLOSE may arrive without EOF and receives
one response if not already sent. Complete initial window/credit thresholds,
unknown extended-data accounting, request-reply ordering, and buffer ownership;
preserve the existing distinction between peer-close grace and disk drain.
[RFC 4254](https://www.rfc-editor.org/rfc/rfc4254.html)

Key parse success alone is insufficient: define type-specific public/private/
pairwise checks, resource guards before expensive validation, and handling of
unsupported checks. Host identity still needs an exact IDNA/IP mapping shared
by resolution and trust lookup, alongside accepted port/alias distinctions.
Q161 now fixes imported-key passphrases to strict UTF-8 initially, without a
selector or alternate-encoding retries; do not apply the vault's NFC policy.
[OpenSSL key validation](https://docs.openssl.org/3.5/man3/EVP_PKEY_check/),
[RFC 5891](https://www.rfc-editor.org/rfc/rfc5891.html),
[Microsoft IdnMapping](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.idnmapping?view=net-10.0)

## Vault key-use and file-format review

Ordinary saves must remain possible with the unlocked DEK and its existing
wrapped representation, without retaining the master password or KEK. Explicit
master-password change still creates a new salt, KEK, and DEK and re-encrypts
all current contents. Neither operation revokes copied old vaults.

RFC 9106's lower-memory recommended Argon2id profile is version 1.3, 65,536 KiB,
three iterations, four lanes, a 16-byte salt, and a 32-byte output. It remains a
candidate here, not a benchmarked default. A narrow v1 allowlist can bound
untrusted KDF parameters; do not accept arbitrary RFC maxima or silently weaken
the profile on a slower PC. Lanes affect the KDF result; execution threads are
a different parameter.
[RFC 9106](https://www.rfc-editor.org/rfc/rfc9106.html#section-4),
[OpenSSL Argon2](https://docs.openssl.org/3.5/man7/EVP_KDF-ARGON2/)

For random GCM IVs, NIST's invocation limit concerns all instances using the
same key. A counter in a portable, copied, or restored file cannot prove that
global limit or nonce uniqueness. Copying existing ciphertext is not a new
encryption; encrypting a new candidate, including a retry, is. Do not present
one local revision counter as solving this branch/copy problem.
[NIST SP 800-38D, section 8.3](https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication800-38d.pdf)

The refinement now selected in `../specification/vault-format.md` retains the
wrapped random 256-bit DEK as a root
derivation key, then derive a fresh 256-bit payload key for each encryption
attempt using HKDF-SHA-256, a new random 32-byte salt, and unambiguously framed
purpose/version/vault context. Use that derived key for one AES-256-GCM payload
encryption with a fresh 96-bit nonce and 128-bit tag, then clear it. The root
is not used directly as a payload GCM key. This is a TabTty
composition using standard primitives, not an RFC-approved TabTty format,
collision-free guarantee, or forward-secrecy claim. Exact framing/key use is
specified in the completed contract; independent security review remains a
release gate.
[RFC 5869](https://www.rfc-editor.org/rfc/rfc5869.html),
[OpenSSL HKDF](https://docs.openssl.org/3.5/man7/EVP_KDF-HKDF/)

The exact format must distinguish immutable wrapping AAD from revision-varying
payload AAD, otherwise ordinary saves may require a discarded KEK. Authenticate
the version, algorithms, KDF/input rules, vault identity, wrapped-key context,
and payload framing with exact bytes; do not rely on an implicit JSON
reserialization. Perform bounded structural/allowlist checks before the KDF,
but do not treat unauthenticated header values as trusted metadata.
[RFC 5116](https://www.rfc-editor.org/rfc/rfc5116.html#section-3.3)

Check random-generation return values and authentication completion before
using any decrypted key or payload. Specify separate owners and cleanup for
master input, normalized bytes, KDF context, KEK, retained DEK, per-save keys,
whole decrypted payload, selected credentials, native keys, and encrypted
candidate files. Zeroing owned buffers does not prove erasure of every managed,
native, OS, or dump copy.
[OpenSSL random generation](https://docs.openssl.org/3.5/man3/RAND_bytes/),
[OpenSSL AEAD interface](https://docs.openssl.org/3.5/man3/EVP_EncryptInit/#aead-interface),
[Microsoft ZeroMemory](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0)

ReplaceFile's write-through flag is unsupported and its failure cases can leave
different original/replacement/backup name states. Model those outcomes rather
than assume every failed save leaves every path unchanged. Explicit flushes
and locks help define ownership but do not imply universal power-loss durability,
atomic compare-and-swap against noncooperating writers, or reliable cloud merge.
[Microsoft ReplaceFile](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew),
[Microsoft file flushing](https://learn.microsoft.com/en-us/windows/win32/fileio/flushing-system-buffered-i-o-data-to-disk),
[Microsoft byte-range locking](https://learn.microsoft.com/en-us/windows/win32/fileio/locking-and-unlocking-byte-ranges-in-files)

## Consolidation still required

Turn the findings into complete, versioned contracts: terminal control/key/
mouse and codec vectors; recording/event ownership; SSH framing/state/resource
tables; vault framing/schema/key-use/file-failure rules; and the aggregate
resource/acceptance matrix. Include the other profile/settings/window branches
listed in `specification-frontier.md`. Do not confuse a source comparison or
a list of intended tests with executed verification. Report readiness only
after these specification gaps are resolved, and still await an explicit
implementation request.
