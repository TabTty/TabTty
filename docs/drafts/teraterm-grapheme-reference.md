# Tera Term grapheme and long-input reference

Reviewed 2026-11-18 for TabTty's specification discussion. This is a
source-reading report, not an implementation decision or runtime validation.
No Tera Term code, dependency, or runtime is adopted.

## Reference and evidence boundary

The reference remains **Tera Term 5.7.1**, tag `v5.7.1`, commit
`ae000ef3ab4eeda0d078a430465fbf4347c15e45`, released 2026-10-09. The official
release page and release API confirm the version/date; the Git tree API
resolves the selected tag to that commit. This report does not substitute the
rolling development branch. Sources: [official release](https://github.com/TeraTermProject/teraterm/releases/tag/v5.7.1),
[release metadata](https://api.github.com/repos/TeraTermProject/teraterm/releases/tags/v5.7.1),
[tag resolution](https://api.github.com/repos/TeraTermProject/teraterm/git/trees/v5.7.1).

Relevant repository context: [existing reference baseline](teraterm-behavior-baseline.md),
[glossary](../../GLOSSARY.md), and [ADR 0007](../adr/0007-complete-terminal-processing-in-rust.md).
Only official source and documentation were inspected. Selected source files
were downloaded to the OS temporary directory for read-only searches. No
program, prototype, benchmark, or runtime test was executed.

## Stored text and overflow

The following findings are direct source observations unless marked inference:

- A cell holds a base scalar, last scalar, column count, UTF-16 base, and separate
  combination buffers. Their capacities stop at **100 UTF-32 elements** and
  **100 UTF-16 code units**, excluding the base. Full buffers silently skip
  additions; the UTF-32 last-scalar field also stops updating. No disconnect
  occurs in this path. [Structure/limit](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L55-L88),
  [append implementation](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L292-L341).
- Joining uses combining-character classification, ZWJ, and same-block virama
  handling. An unattached mark receives an NBSP base. Nonspacing additions retain
  width; spacing additions can increase it. [Joining](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L2967-L3032),
  [insertion](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L3124-L3182).
- Rendering and clipboard extraction use the UTF-16 combination buffer. Erasing
  a base cell frees its combination storage. [Drawing](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L3556-L3581),
  [copy](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L1873-L1900),
  [reset](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L164-L179),
  [erase](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L1098-L1113).
- **Static inference:** `a` plus 256 U+0301 marks retains only the base plus 100
  marks in these buffers. Supplementary additions exhaust UTF-16 sooner.
  If the last stored scalar is ZWJ, subsequent ordinary characters can keep
  joining and being dropped: for example, `a` + 99 U+0301 + ZWJ, then ordinary
  text without intervening cursor controls. This consequence of stale
  last-scalar state is **not runtime verified**. [Capacity checks](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L320-L340),
  [ZWJ predicate](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L3013-L3016).

## Scalars, width, emoji, and cursor behavior

The decoder emits a Unicode scalar through `PutU32`. Its UTF-8 handling rejects
surrogate encodings and out-of-range four-byte sequences through its replacement
path. Supplementary scalars are encoded as a UTF-16 surrogate pair for Windows
consumers. Thus scalar count, UTF-16 length, and terminal columns are distinct
quantities in this reference. [UTF-8 decoder](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/charset.cpp#L684-L722),
[UTF-16 conversion](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/common/codeconv.cpp#L496-L528).

`UnicodeIsCombiningCharacter` assigns nonspacing treatment to `Mn`, `Me`, and
selected `Sk` entries, and spacing treatment to `Mc`. Its generated table includes
VS1–VS16 and emoji skin-tone modifiers. These are explicit table/predicate
mechanisms; they do not establish complete Unicode extended-grapheme-cluster
conformance, regional-indicator pairing, or correct rendering for every emoji
sequence. [Classification](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/unicode.cpp#L219-L245),
[variation selectors](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/unicode_combine.tbl#L322),
[skin tones](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/unicode_combine.tbl#L535),
[table generation rules](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/unicode/get_combine_table.pl#L25-L50).

The pinned manual distinguishes host-expected width, Tera Term's cell width, and
font-designed width. It documents East Asian Width, configurable ambiguous width,
emoji width overrides based on Extended_Pictographic, and per-character overrides;
it warns that mismatched widths can misalign output. This is not a universal
"one grapheme equals two cells" policy. Font/OS shaping and fallback remain
separate from storage and column calculation. [Pinned Unicode manual](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/usage/unicode.html#L16-L64),
[width and emoji discussion](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/usage/unicode.html#L129-L238).

The terminal caller interprets insertion results as zero, one, or two columns;
zero advances neither cursor nor wrap state. Pending autowrap is resolved only
when the incoming scalar does not combine with the previous cell. These are
terminal-column operations, not UTF-16-code-unit cursor steps.
[Cursor/wrap handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c#L853-L935).

## Receive boundaries and logs

UTF-8 partial bytes and count live in the persistent `CharSetData` object.
`VTParse` passes received bytes to that object and does not reset it merely
because the input buffer is exhausted. **Static inference:** a receive chunk
boundary alone is neither a scalar boundary nor a new combining sequence; the
existing cell and decoder state remain available to later input. This is not a
claim of runtime coverage for all split points or intervening terminal controls.
[Decoder state](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/charset.cpp#L54-L85),
[partial-byte handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/charset.cpp#L616-L681),
[receive loop](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c#L5382-L5478).

The display-storage limit is not applied to the scalar supplied to the text-log
path: `PutU32` calls display insertion, then passes the original scalar to
`OutputLogUTF32`. With a text log open, that function sends eligible characters
to `FLogPutUTF32`, with its separate newline handling. Binary logging receives
terminal bytes in `CommRead1Byte`, after Telnet processing and before character
decoding. **Inference:** combination-buffer saturation alone does not remove
those original characters/bytes from these logging inputs. This does not prove
successful disk writes, lossless text-log encoding, or equivalence to TabTty's
three session-log contracts. [Text-log input](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c#L947-L953),
[text-log forwarding](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c#L448-L469),
[binary-log boundary](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/ttpcmn/ttcmn_buff.c#L84-L134).

## Scoped paste observation

No **1 MiB admission guard** was found in the inspected normal clipboard path.
It obtains text, optionally confirms/edits it, normalizes line endings, allocates
bracketed-paste framing when applicable, and hands the result to `SendMemTextW`.
That routine uses `size_t` and `wcslen`; sending is bounded by currently available
communication-buffer space. This is evidence for the inspected code path only,
not a promise of unlimited paste or a review of every clipboard/dialog/OS
constraint. [Clipboard path](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/clipboar.c#L198-L321),
[send storage](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/sendmem.cpp#L568-L591),
[send chunk selection](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/sendmem.cpp#L387-L457).

## Implication for the pending TabTty discussion

This reference does not justify adopting a 256-scalar disconnect policy or a
1 MiB paste rejection threshold. Its retained-text loss should also not be
silently copied. The specification discussion should distinguish original
received content, retained content, rendering limits, send admission, and
actual resource failure. Whether TabTty changes any existing contract remains
a separate decision; this report authorizes no implementation and establishes
no new implementation readiness claim.
