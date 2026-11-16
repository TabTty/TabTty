# Confirmed legacy encoding coverage and Japanese compatibility

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q144-Q145 accepted as recommended on 2026-11-08 after Q141 acceptance.
Specification only; no codec implementation, conversion table import,
runtime experiments, tests, installation, or publication is authorized.

Read with `teraterm-encoding-baseline.md`, `search-and-encoding-proposals.md`,
and the accepted input/logging specifications. The 48-choice inventory, UTF-8
default, no automatic detection/UTF-16, independent optional transmit encoding,
next-connection-only changes, source isolation, and logging modes remain fixed.

The follow-up source review in `technical-contract-review.md` identifies the
CP932/C1 boundary and JIS Roman/X 0212 differences that the exact mapping and
parser tables must resolve. Do not assume the Microsoft JIS decoder alone
implements the accepted repertoire and replacement policy.

## Q144: Complete the inventory without adopting another codec library

Preserve all 48 accepted encoding identities. Use Microsoft's
base/CodePages encodings where their actual identity and behavior match the
selected communication format. Supplement missing single-byte encodings with
small TabTty-owned mappings based on the Unicode Consortium's published
mapping data. Do not reduce the list to the encodings returned by a particular
.NET installation, silently substitute a similar name, or download codecs at run time.

The reviewed Microsoft table lacks ISO-8859-10, -14, and -16; its name
`iso-8859-11` maps to CP874. TabTty offers ISO-8859-11 and CP874 as separate
identities, so use an independent ISO-8859-11 mapping as well. Keep code-page
IDs explicit for names such as Shift_JIS/CP932, GB2312/CP936, and KS5601/CP949.
Implementation must verify the exact selected .NET servicing version rather
than infer repertoire from an alias. Missing backend support is a development/
packaging defect to resolve before release, not a user installation prerequisite.

This is data-driven TabTty code with Microsoft codecs, not adoption of another
third-party codec library or Tera Term implementation. Pin the data provenance,
version/digest, and applicable notices when implementation is authorized; retain
the required license notices with distribution. The dependency policy governs
adopted libraries, not a prohibition on consulting standards/mapping documents.
No source/table has been imported into application code in this phase.

Match Tera Term's inventory and selected configuration conventions while using
explicitly documented standard/Windows-compatible mappings. Do not promise
bug-for-bug equivalence for malformed data, silent omission, or approximate
transmit conversions. Preserve the accepted receive U+FFFD replacement and
whole-unit transmit rejection when text cannot be encoded. Disable best-fit
fallback and automatic full-width/half-width/normalization substitutions; report
the offending input rather than sending a visually similar character.

For CP932, the reviewed Microsoft mapping gives `81 60` -> U+FF5E,
`5C` -> U+005C, and `7E` -> U+007E. Keep those Unicode values in text/copy/log
processing independently of the selected glyph appearance. Do not automatically
equate U+301C with U+FF5E or U+00A5 with U+005C based on visual similarity.
Other JIS/EUC states have their own mapping rules; do not blindly apply an
ASCII or CP932 byte interpretation while a different state is designated.

Where legacy maps have duplicate representations or extensions, publish a
specific mapping/representability decision and acceptance vectors rather than
claim universal Unicode round-trip identity. Detailed CP932 extension mapping
into JIS/EUC remains technical comparison work. It cannot silently turn into
best-fit output or byte sequences outside the chosen repertoire.

## Q145: Japanese compatibility settings and supported repertoire

Provide a Japanese-encoding details area for the relevant profile settings,
keeping one JIS encoding choice rather than exposing .NET implementation code
pages 50220/50221/50222 as separate user choices.

| Setting | Initial choices | New-profile default |
| --- | --- | --- |
| JIS transmit Kanji-IN | `ESC $ @`, `ESC $ B` | `ESC $ B` |
| JIS transmit Kanji-OUT | `ESC ( B`, `ESC ( J` | `ESC ( B` |
| Receive half-width kana compatibility | 8-bit, 7-bit SO/SI for relevant Japanese receive modes | 8-bit |
| Transmit half-width kana compatibility | 8-bit, 7-bit SO/SI for JIS transmission | 8-bit |

Keep receive/transmit kana choices distinct. Apply settings only to relevant
Japanese modes, not Unicode, other language encodings, or every C0 control in
all codecs. Save the choices in the profile and capture them at connection start
under Q125. Detailed visibility/enabled-state rules and receiver designation
variants belong in the codec/terminal matrix. Exclude the nonstandard outgoing
`ESC ( H` option initially. Do not synthesize a hidden legacy mode on file load.

Use SO (`0E`) to enter 7-bit kana and SI (`0F`) to leave it when that mode is
selected; preserve half-width kana as half-width Unicode rather than converting
them to full-width text. Use ordinary EUC-JP SS2 (`8E A1` through `8E DF`) for
half-width kana. Standard EUC kana does not require a separate nonstandard
transmit toggle. Keep encoding-generated shift/control bytes distinct from user
control characters in the paste filter and terminal VT input parser.

Initially cover the ordinary JIS X 0208-based Japanese repertoire and half-width
kana, with the Windows-compatible mappings explicitly qualified under Q144.
Do not add JIS X 0212 or JIS X 0213 support in this round. For a complete valid
EUC SS3/JIS X 0212 unit outside the supported repertoire, render U+FFFD rather
than silently dropping its text; Raw still preserves its received bytes.
Reject unencodable outgoing input before transmitting any of that logical input
unit. Invalid/truncated sequences follow the accepted replacement/finalization
policy, with exact consumption/resynchronization cases specified before
implementation; never consume a following unrelated control as a fake continuation.

Preserve incremental state across chunks and safe send units. UTF-8 BOM handling
and the per-source parser isolation accepted in Q141 remain separate rules.
An input unit rejected during strict preflight must not mutate live encoder state.
The selected setting does not depend on the UI language or automatically change
when the peer prints a particular byte sequence.

## Reference findings and deliberate differences

Static review of Tera Term v5.7.1 finds 52 active KanjiList entries with aliases/
duplicates reducing to 48 identities. Its shipped TERATERM.INI uses Kanji-IN B,
Kanji-OUT B, and receive/transmit kana 8-bit. Its missing-setting helper fallback
uses OUT J instead. The accepted new-profile defaults follow the shipped
configuration, not that fallback.

The reviewed transmit source contains an EUC half-width-kana TODO with direct
single-byte output, and JIS 7-bit paths whose SI/SO ordering differs from the
documented convention. These are static source observations, not a claim based
on executing Tera Term. Q145 chooses standard SS2 and SO/SI forms and records
the difference instead of reproducing that source behavior.

Tera Term's reviewed JIS/EUC conversion routes through CP932. Its receive SS3
handling consumes supplementary bytes without a JIS X 0212 Unicode mapping;
its transmit conversion/fallbacks also allow some non-round-tripping aliases.
TabTty's explicit replacement/rejection policy therefore intentionally differs.
Microsoft CP51932 also lacks JIS X 0212, but its EUC kana uses SS2. Microsoft
50220 can convert half-width kana to full-width, while 50221/50222 select other
shift forms; none is a direct substitute for all of the settings above.

The inventory has already been accepted. Backend coverage and exact mappings
are facts to research, not questions asking the user which code-page aliases
exist. Q144-Q145 confirm completing coverage with data mappings and the
chosen compatibility/repertoire policy.

## Evidence reviewed on 2026-11-08

- [Tera Term 5.7.1 encoding list](https://raw.githubusercontent.com/TeraTermProject/teraterm/v5.7.1/teraterm/common/ttlib_charset.cpp)
- [Tera Term 5.7.1 shipped settings](https://raw.githubusercontent.com/TeraTermProject/teraterm/v5.7.1/installer/release/TERATERM.INI)
- [Tera Term encoding settings manual](https://teratermproject.github.io/manual/5/en/menu/setup-additional-coding.html)
- [Tera Term 5.7.1 transmit conversion](https://raw.githubusercontent.com/TeraTermProject/teraterm/v5.7.1/teraterm/common/makeoutputstring.cpp)
- [Tera Term 5.7.1 control constants](https://raw.githubusercontent.com/TeraTermProject/teraterm/v5.7.1/teraterm/common/tttypes.h)
- [Tera Term 5.7.1 receive conversion](https://raw.githubusercontent.com/TeraTermProject/teraterm/v5.7.1/teraterm/teraterm/charset.cpp)
- [Tera Term 5.7.1 JIS/SJIS conversion](https://raw.githubusercontent.com/TeraTermProject/teraterm/v5.7.1/teraterm/common/codeconv_mb.cpp)
- [Microsoft code-page identities](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/EncodingTable.Data.cs.html)
- [Microsoft EUC-JP implementation](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/EUCJPEncoding.cs.html)
- [Microsoft ISO-2022 implementation](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/ISO2022Encoding.cs.html)
- [Microsoft CP932 mapping published by Unicode](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP932.TXT)
- [Unicode ISO-8859-10 mapping](https://www.unicode.org/Public/MAPPINGS/ISO8859/8859-10.TXT)
- [Unicode ISO-8859-11 mapping](https://www.unicode.org/Public/MAPPINGS/ISO8859/8859-11.TXT)
- [Unicode ISO-8859-14 mapping](https://www.unicode.org/Public/MAPPINGS/ISO8859/8859-14.TXT)
- [Unicode ISO-8859-16 mapping](https://www.unicode.org/Public/MAPPINGS/ISO8859/8859-16.TXT)
- [Unicode data license](https://www.unicode.org/license.txt)

Remaining technical work includes a complete mapping/extension matrix,
JIS designation/reset/error-consumption and codec/VT-boundary tables, stable
profile IDs and setting applicability, and data provenance/notices. Authorized
implementation must later validate mapping vectors, every chunk split, strict
send preflight/state isolation, and reference interoperability. No such runtime
verification has been performed; these decisions are not readiness or implementation
authorization.
