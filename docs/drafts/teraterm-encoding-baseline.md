# Tera Term communication encoding baseline

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Reference: Tera Term 5.7.1, released on 2026-10-09 and checked on 2026-11-07.
This inventory concerns terminal communication encodings, not UI language,
font coverage, macro source files, or log-file encodings. It is a specification
reference, not evidence of implemented or tested TabTty support.

Q125-Q126 in `search-and-encoding-proposals.md` confirm next-connection-only
encoding edits and explicit UTF-8 initial-signature handling without detection.
These are accepted TabTty scope/behavior choices, not verified claims
of Tera Term 5.7.1 runtime equivalence. The inventory below is already accepted.

Q141 is also accepted: normal SSH channel data and extended stderr use independent
incremental receive-source state and initial UTF-8-signature recognition, while
Raw preserves their original payload bytes in receive-message order.

The 2026-11-08 static review in `legacy-encoding-proposals.md` supplies backend
coverage and Japanese-default findings. Q144-Q145's completion/mapping policy
and compatibility settings are accepted as recommended.

## Reference inventory

The stable version's `KanjiList` and official encoding documentation describe
48 distinct encoding choices after duplicate entries and aliases are removed.

| Group | Encoding identities | Count |
| --- | --- | ---: |
| Unicode | UTF-8 | 1 |
| ISO-8859 | ISO-8859-1, -2, -3, -4, -5, -6, -7, -8, -9, -10, -11, -13, -14, -15, -16 | 15 |
| Japanese | Shift_JIS (CP932), EUC-JP, JIS | 3 |
| Korean and Chinese | KS5601 (CP949), GB2312 (CP936), Big5 (CP950) | 3 |
| OEM and Thai code pages | CP437, CP737, CP775, CP850, CP852, CP855, CP857, CP860, CP861, CP862, CP863, CP864, CP865, CP866, CP869, CP874 | 16 |
| Windows code pages | CP1250, CP1251, CP1252, CP1253, CP1254, CP1255, CP1256, CP1257, CP1258 | 9 |
| Other Cyrillic | KOI8-R | 1 |

Use code-page identity as well as the familiar label when establishing behavior.
For example, the reference's GB2312 selection uses CP936 and its KS5601 selection
uses CP949. Do not substitute other Chinese or Korean encodings based only on
similar display names.

`EUC` and `EUC-JP` name the same choice; `Windows-1251` and `CP1251` also identify
the same code page. Duplicate reference UI entries are not separate encodings.
The reference uses the same inventory for independently selectable receive and
transmit encodings. TabTty follows this model: use the same encoding in both
directions by default, with an option to select different values.

## Reference behavior requiring explicit treatment

- UTF-8m was merged into UTF-8 in Tera Term 5 and is not a distinct choice in
  this inventory. This does not establish a requirement to normalize all text
  to NFC or NFD.
- JIS is one encoding choice with associated settings: transmit Kanji-IN uses
  `ESC $ @` or `ESC $ B`, and Kanji-OUT uses `ESC ( B` or `ESC ( J`.
  Receive and transmit also have separate 7-bit SO/SI half-width kana settings.
  Nonstandard `ESC ( H` is normally hidden in the reference UI. Determine
  TabTty's corresponding options rather than substituting a list of .NET
  ISO-2022-JP code-page numbers for the reference behavior.
- Tera Term's Japanese EUC conversion uses its own CP932/JIS/SJIS conversion
  paths. Matching the encoding choice does not establish identical handling
  of every extension, malformed sequence, or replacement character.
- Neither UTF-16 LE nor UTF-16 BE is a terminal communication choice in the
  reference inventory.

The fixed version's active KanjiList has 52 entries; EUC alias and repeated
ISO-8859-5, CP866, and CP1251 entries reduce this to the accepted 48 identities.
Shipped TERATERM.INI defaults are Kanji-IN B, Kanji-OUT B, and receive/transmit
kana 8-bit. The helper's missing-value fallback uses OUT J; do not conflate it
with shipped defaults. These are source findings, not runtime validation.

Microsoft's reviewed CodePages table does not supply ISO-8859-10/-14/-16 and
maps the name ISO-8859-11 to CP874. Therefore that provider alone is not a
complete implementation of the inventory. Q144 accepts independent mapping
data for those four ISO identities. The reference's JIS/EUC conversion and
Microsoft's Japanese providers also differ on kana/extension handling; see the
confirmed details and remaining comparison matrix in `legacy-encoding-proposals.md`.

## TabTty requirements and unresolved details

- UTF-8 remains the default, and encoding selection is manual.
- Automatic encoding detection and UTF-16 LE are outside the initial scope.
- Text-log files remain UTF-8 without a BOM, independently of the selected
  communication encoding.
- Invalid receive sequences use U+FFFD; unencodable outgoing input is rejected
  with a notification. These are agreed TabTty behaviors.
- Design SSH for the same encoding inventory as Telnet and serial from the
  start. TabTty's own SSH protocol/session layer uses OpenSSL cryptography and
  preserves terminal-channel bytes for the selected codecs. See
  `../adr/0001-own-ssh-client-with-openssl.md` and `ssh-transport-issue.md`.
- Q144-Q145 accept a Japanese details area with JIS IN @/B and OUT B/J,
  shipped B/B defaults, separate receive/transmit kana 8-bit defaults and
  optional 7-bit SO/SI, and no outgoing ESC ( H option. Use standard EUC SS2
  kana and SO/SI ordering without automatic full-width conversion. Initially
  exclude JIS X 0212/X 0213; unsupported received text uses the agreed replacement
  and unencodable transmit units fail before sending. Detailed extension/mapping,
  designation, and error-consumption tables remain technical work.
- Check mapping coverage against the reference. Tera Term has its own Japanese
  conversion paths; the availability of a similarly named .NET encoding does
  not prove equivalence. The dependency policy in `../../AGENTS.md` still applies.

## Primary references

- [Tera Term 5.7.1 release](https://github.com/TeraTermProject/teraterm/releases/tag/v5.7.1)
- [Encoding inventory in the 5.7.1 documentation](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/usage/character_encoding.html)
- [KanjiList in the 5.7.1 source](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/common/ttlib_charset.cpp)
- [Receive/transmit settings in the 5.7.1 source](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/coding_pp.cpp)
- [JIS settings in the 5.7.1 manual](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/menu/setup-additional-coding.html)
- [UTF-8m explanation in the 5.7.1 glossary](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/about/glossary.html)
- [Encoding output in the 5.7.1 source](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/common/makeoutputstring.cpp)
- [Current official encoding guide](https://teratermproject.github.io/manual/5/en/usage/character_encoding.html)
