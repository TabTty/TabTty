# Native terminal codec implementation consultation

Status: identified custom-processing approaches approved through 2026-11-17; no current codec-design consultation remains open. ISO-8859-10/11/14/16 table reuse was approved on 2026-11-16, and Japanese proposal A plus shared recovery/exact-send proposal B on 2026-11-17. The three architecture designs, all 48 identities, and avoidance of a managed terminal fallback remain accepted. Newly discovered custom mappings or compatibility corrections still require consultation. These design decisions do not authorize implementation or establish runtime compatibility.

## Consultation requirement

Before selecting or implementing custom codec processing, present the affected identities and behavior, why an existing permitted library/API is insufficient, viable alternatives, the proposed custom scope, maintenance/compatibility implications, and observable acceptance cases. Obtain the user's decision first. Include reuse of current custom mappings and state machines, not only newly invented algorithms. Do not reduce encoding coverage, substitute a near-equivalent encoding, adopt another dependency, or restore C# fallback without a separate explicit decision.

Apply this requirement to new or changed custom scope, not to reapproving the specific decisions recorded below.

Assess permitted Microsoft native APIs or data against the exact contract. A matching code-page name is not evidence of matching tables, strict reverse mapping, incremental boundaries, malformed-unit recovery, or terminal-specific designation rules. Direct native Windows calls from Rust are the planned base for Microsoft code pages and are not a managed C# terminal fallback. Exact-behavior and supported-platform checks remain implementation acceptance work; no native API has been runtime-validated by this note.

## Evidence and initial consultation candidates

| Area | Current evidence | Decision still needed |
| --- | --- | --- |
| UTF-8 and common receive framing | Rust strings use UTF-8; the current Rust terminal already contains incremental parsing/decoding. Standard conversion helpers alone do not specify the terminal's source/interval and malformed-input policy. | Proposal B approves the specified shared recovery and exact-send policy. Consult about additional custom behavior, not routine integration of that policy. |
| Remaining Windows/OEM/ISO/KOI8-R and Chinese/Korean code pages | All 37 remaining single-byte identities and cp936/cp949/cp950 use the generic managed Microsoft-code-page path today; Microsoft lists native identifiers for them. No additional identity-specific table/patch was identified in the local review. | Plan native Microsoft conversion with approved shared policy B. Exact mapping and platform availability remain acceptance checks; newly needed corrections or replacement data require consultation. |
| ISO-8859-10, -11, -14 and -16 | Current C# uses explicit Unicode mapping tables for these four identities, separate from its .NET code-page provider. | Approved on 2026-11-16: reuse those current Unicode mapping tables in Rust with bounded lookup/reverse lookup. This does not approve additional compatibility corrections or the methods for other identities. |
| Japanese CP932/EUC-JP/JIS compatibility | Current C# has CP932 extension handling, a selected 94-by-94 JIS/EUC mapping, kana/designation state, exact Roman behavior and malformed-unit recovery. The accepted JIS contract explicitly differs from a generic mail-oriented ISO-2022-JP conversion. | Proposals A and B approved on 2026-11-17. Speculative porting of managed CP932 overrides remains excluded; consult if a concrete native mismatch needs correction. |

The current local evidence is [TerminalEncoding](../../src/TabTty.Core/TerminalEncoding.cs), [LegacyReceiver](../../src/TabTty.Core/LegacyReceiver.cs), [LegacyIsoTables](../../src/TabTty.Core/LegacyIsoTables.cs), and the [accepted terminal/encoding contract](../specification/terminal-and-encodings.md). These observations establish existing custom work, not that every such override must be copied unchanged to a native API implementation.

## Approved ISO table reuse

The user approved reusing the current Unicode conversion tables for ISO-8859-10/11/14/16 on the Rust side. Retain the accepted byte mappings, undefined-position behavior, exact reverse conversion, and data provenance. This is a bounded data/lookup port, not permission to invent new mappings. T07 records this approval alongside the remaining single-byte assessment below.

## Japanese assessment

Windows lists CP932, EUC-JP (51932), and JIS variants (50220/50221/50222). Their existence is not in doubt. The question is whether their complete behavior matches TabTty's selected terminal contract. The native conversion functions expose code page, flags, input, and output buffers, but no TabTty IN/OUT preferences or reusable terminal charset state. Both functions require zero flags for the three JIS variants; their ordinary strict/error flags cannot simply be applied to those variants. This is a documented API restriction, not proof that every mapping is incompatible. See the [identifier list](https://learn.microsoft.com/en-us/windows/win32/intl/code-page-identifiers), [receive API](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-multibytetowidechar), and [send API](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-widechartomultibyte).

Microsoft's managed [EUC-JP source](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/EUCJPEncoding.cs.html) itself derives mappings from CP932, remaps extension positions, and excludes JIS X 0212. This supports considering Microsoft mappings as the base. It does not establish that the native 51932 API has identical mapping, reverse-choice, malformed-unit, or chunk behavior. Neither native 51932 equivalence nor native CP932 incompatibility has been demonstrated in this review.

The accepted JIS behavior deliberately includes terminal features beyond mail-oriented ISO-2022-JP: configurable Kanji IN/OUT sequences, independent kana choice, selected Microsoft extensions, and exact terminal-control recovery. [RFC 1468](https://www.rfc-editor.org/rfc/rfc1468.html) specifies ASCII/Roman distinctions and mail-specific restrictions, including an ASCII ending. The existing product contract is the authority for TabTty's extensions; changing to generic mail conversion would be a product behavior change.

### Approved bounded approach (2026-11-17)

Use native Microsoft CP932 conversion as the planned base character mapper, called directly from Rust, subject to acceptance verification. The user approved keeping the following explicitly owned policies in Rust. These are predominantly existing C# responsibilities, not features made necessary only by choosing Rust.

**Proposal A — Japanese compatibility policy.**

- Use one shared JIS/EUC 94-by-94 repertoire and exact reverse-selection policy derived from the adopted CP932 positions. Retain extensions such as circled numbers and the specified duplicate-character choices. Select the smallest adopted row/cell after single-byte/kana choices. Own this selected mapping and reverse policy, rather than delegate the whole JIS/EUC conversion to native 5022x/51932. Storage and initialization details are not settled here; no new mapping data is generated in this review.
- Preserve CP932/JIS optional SO/SI **receive** handling. Preserve EUC SS2 kana and consume a well-shaped unsupported SS3 sequence as one unsupported unit. This does not add a CP932 SO/SI transmit mode.
- Preserve JIS ASCII/Roman distinctions, configured IN/OUT byte order, independent transmit eight-bit/SO/SI kana, and unsupported two-byte repertoire states until redesignation. Maintain the existing codec-closure and paste ordering rules.
- Keep the JIS/EUC grid exclusions separate from ordinary CP932 support. In particular, the grid's exclusion of positions outside its adopted range must not remove ordinary CP932 private-use coverage.

**Proposal B — Shared receive recovery and exact-send policy.**

- Preserve independent source state across receive chunks. A chunk boundary is not EOF. A malformed continuation replaces the pending prefix once and reprocesses the offending byte, so a following newline or escape control is still executed exactly once. Discard an unfinished designation at EOF, while replacing an unfinished printable character once.
- Preflight the entire outgoing text in isolated state. Reject unmappable text, malformed Unicode, or an exceeded payload limit without sending a prefix or changing live encoder/local-echo state. Do not substitute a similar character, normalize text, or change character width to make conversion succeed.
- Treat source/recording provenance, shared Estimate/Execute interpretation, and bounded state as existing terminal integration requirements. Approval covers these specified codec policies, not arbitrary new repairs for other codecs.

FFI, UTF-16 buffer conversion, sizing, and error checks are ordinary adapters. The consultation concerns the selected mappings, state transitions, replacement grouping, and strictness guarantees. Exact Unicode preservation does not mean original-byte round-trip identity: accepted JIS/EUC duplicate positions intentionally canonicalize on transmit.

### Alternatives and maintenance implications

1. **Selected: Microsoft CP932 base plus proposals A and B.** Keep one JIS/EUC repertoire and explicit terminal state. This preserves accepted behavior and avoids maintaining a second full Japanese-to-Unicode mapping source. TabTty remains responsible for the bounded selection/state/recovery layer and its independent acceptance vectors.
2. **Native EUC/JIS conversion plus verified adapters.** Assess native 51932/5022x for all adopted mappings, reverse choices, and terminal cases. This might reduce mapping code if equivalence is established, but the terminal state/recovery and exact-send policies still need an owner. Exact equivalence and the size of any required corrections are presently unknown; do not silently choose this route or introduce corrections.
3. **Adopt generic converter semantics.** This may simplify the custom layer but can change the accepted extensions, IN/OUT/kana behavior, and malformed-input handling. It requires a separate specification decision and is not recommended for the agreed compatibility objective.

No new third-party crate or managed terminal callback is proposed. If a specific native CP932 mapping fails the contract, present the exact input/output discrepancy and a bounded correction or data alternative to the user before adopting it. The current C# [duplicate-position table](../../src/TabTty.Core/Cp932Duplicates.cs) is not evidence that Win32 needs the same patches, and porting it is **not** included in proposals A or B.

### Planned acceptance evidence

These are future implementation checks, not results of this static review.

| Area | Observable acceptance cases |
| --- | --- |
| CP932 base | 8160 -> U+FF5E; 5C -> U+005C; 7E -> U+007E; 9B5B -> U+5ABC. Both ED40/FA5C decode to U+7E8A and EEEF/FA40 to U+2170. Reject inexact Unicode substitutions. |
| Selected JIS/EUC mapping | U+FF5E -> 2141/A1C1; U+2460 -> 2D21/ADA1; U+7E8A -> 7921/F9A1; U+2170 -> 7C71/FCF1. U+301C must not be substituted with U+FF5E. Check adopted cells, exclusions and duplicate reverse choices against independent expectations. |
| JIS exact state | Backslash, tilde, yen and overline retain their distinct Unicode values. Kanji to backslash with OUT J emits ESC ( J then ESC ( B; Kanji to yen with OUT B emits ESC ( B then ESC ( J. IN @ and B use the same accepted repertoire. |
| Kana and unsupported repertoires | Preserve eight-bit/selected receive SO/SI behavior, EUC SS2, and the independent JIS transmit selection. JIS ESC $ ( D/O/P/Q enters an unsupported two-byte repertoire until redesignation; a shaped unit produces one replacement. |
| Recovery and boundaries | EUC 8F A1 A1 -> one replacement; 8F A1 0A -> replacement then LF. CP932 lead followed by LF preserves LF. Every chunk split gives the same result; pending character EOF gives one replacement; unfinished designation EOF gives none. DATA/stderr and recording provenance remain independent. |
| Send atomicity | Unmappable input, malformed Unicode or payload rejection yields no sent prefix and no live encoder/local-echo state change. Verify actual session input/paste/recording and shared strict filename consumers where applicable. |

## Remaining code pages: static assessment completed 2026-11-17

The remaining 37 single-byte identities are ISO-8859-1 through -9, -13 and -15 (11); cp437/737/775/850/852/855/857/860/861/862/863/864/865/866/869 (15); cp874 and cp1250 through cp1258 (10); and koi8-r (1). Together with cp936, cp949 and cp950, these 40 identities currently use the generic Microsoft-code-page path in the local source. Unlike the four approved ISO tables and the Japanese mapping/state layer, the reviewed code contains no additional identity-specific mapping table or patch for this group.

Microsoft's [identifier list](https://learn.microsoft.com/en-us/windows/win32/intl/code-page-identifiers) includes their corresponding code pages: ISO 28591-28599/28603/28605, the named cp numbers, and KOI8-R 20866. Use explicit identifiers, not system-default ACP/OEMCP. [IsValidCodePage](https://learn.microsoft.com/en-us/windows/win32/api/winnls/nf-winnls-isvalidcodepage) checks installed availability; its documentation says installable pages are loaded by default from Windows Vista onward. This supports native Windows conversion as the planned implementation route, while supported-target availability remains an acceptance check.

Preserve the approved shared framing/recovery and exact-send policy around native conversion. In particular, the API's default replacement must not determine malformed-unit grouping, and send substitution detection must cause preflight rejection. The documented [receive flags](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-multibytetowidechar) and [send flags/default-character reporting](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-widechartomultibyte) provide candidate mechanisms, not evidence that every native mapping matches the accepted tables. Do not enable normalization, glyph-control substitution, or composition to make an otherwise inexact conversion succeed.

No additional custom mapping need has been established by this static assessment. It is sufficient to finalize the design/ticket allocation, not to claim verified native compatibility. During authorized implementation, compare every single-byte position and the multibyte mapping/framing cases against independent expected data, including undefined positions, duplicate reverse mappings, best-fit candidates, cp1258 combining marks, and C0/C1 behavior. Preserve the approved payload/source/EOF rules. If these checks expose a need for new correction logic or data, pause adoption of that correction and consult with the exact discrepancy. Existing approved A/B work can proceed independently where unaffected.

## Decision status

- ISO-8859-10/11/14/16 existing-table reuse: **approved** on 2026-11-16.
- Proposal A, Japanese compatibility policy in Rust: **approved** on 2026-11-17.
- Proposal B, shared recovery and exact-send policy in Rust: **approved** on 2026-11-17.
- Remaining-code-page static assessment: **complete; no additional custom mapping need demonstrated**. Runtime equivalence remains implementation acceptance work.
- Additional CP932 overrides or new mapping corrections: **not approved; consult only if a concrete mismatch is found**.
- Implementation/prototyping/runtime probes: **not authorized by these design consultations**.

## Primary references checked

- [Rust String documentation](https://doc.rust-lang.org/std/string/struct.String.html): UTF-8 string representation and Unicode conversion helpers, not a complete 48-codec conversion offering.
- [encoding_rs project documentation](https://github.com/hsivonen/encoding_rs): Web Encoding Standard scope; non-Web DOS encodings require a different offering. This illustrates why a general Rust crate is not proof of full coverage; it is not adopted by this project and the current dependency policy remains unchanged.
- [Microsoft code-page identifiers](https://learn.microsoft.com/en-us/windows/win32/intl/code-page-identifiers): available named code-page identifiers, not a guarantee of TabTty-specific compatibility.
- [Microsoft MultiByteToWideChar](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-multibytetowidechar) and [WideCharToMultiByte](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-widechartomultibyte): explicit code pages, conversion/error flags, restrictions, and best-fit/default-character considerations. Do not assume one flag combination works for every codec.

No new conversion code, prototype, mapping generator, or runtime probe was run. The user approved the ticket breakdown and requested publication on 2026-11-17. The [coordination map](https://github.com/TabTty/TabTty/issues/16) and its 23 implementation tickets record these decisions. Additional discovered custom work still requires consultation, and implementation requires a separate explicit request.
