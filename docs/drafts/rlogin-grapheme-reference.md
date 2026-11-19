# RLogin grapheme and long-cluster reference

Research date: 2026-11-18. Scope: the Windows terminal **RLogin by
kmiya-culti**, not the Unix `rlogin` command. This is a static primary-source
review for TabTty's specification discussion. No RLogin executable, prototype,
or runtime test was run, and no RLogin source is adopted as a dependency.

## Verified reference

The official site identifies RLogin **2.31.9**, dated **2026-09-25**. The
GitHub release and tag identify commit
`66506599f3baa9f1e916b1268a8a659738dbb30a`; GitHub's release API reports
publication at `2026-09-24T23:00:57Z` (2026-09-25 in Japan). All source links
below use that immutable commit. This date is the release date, distinct from
the research date. [Official site](https://kmiya-culti.github.io/RLogin/),
[release](https://github.com/kmiya-culti/RLogin/releases/tag/2.31.9),
[tag API](https://api.github.com/repos/kmiya-culti/RLogin/git/ref/tags/2.31.9),
[release API](https://api.github.com/repos/kmiya-culti/RLogin/releases/tags/2.31.9).

## Answer to the overflow concern

**Observed in source:** RLogin bounds the text attached to a terminal cell much
more tightly than 256 Unicode scalars. `MAXCHARSIZE` is 16, and the append guard
includes the terminating null, leaving **15 UTF-16 code units total**, including
the base character. Supplementary characters consume two units. This is neither
15 additional combining marks nor 15 Unicode scalars. The cell layout uses an
inline character area and an adjacent two-unit union; the limit follows the
explicit constant and guard, not just the declared array length.
[Capacity and layout](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.h#L567-L612),
[append guard](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L159-L185).

**Observed in source:** When an append would exceed that bound,
`CCharCell::operator +=` emits a `TRACE` and returns without appending. Its
return type is `void`. `PUTADD` cannot observe an overflow result: it continues
updating the cell timestamp, last string, paired wide cell, and invalidation,
then returns success. These branches contain no user-facing notification,
replacement marker, or connection-close request. A two-unit addition is rejected
as a whole by the append guard. Separately, assignment from a string copies at
most 15 units; that is truncation by code unit rather than an explicit grapheme
boundary. [Cell append and assignment](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L159-L199),
[caller](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L10857-L10891).

**Inference from those paths:** Oversized additions are silently omitted from
the retained display text while input processing continues; this is not a
cluster-length-triggered disconnect policy. It also is not lossless support
for arbitrarily long clusters. This conclusion is limited to the inspected
cell-overflow path, not a claim that every allocation failure, script action,
or transport failure can never disconnect RLogin. The receive callback consumes
the processed byte count and does not handle cell overflow as a connection
error. [Receive callback](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/RLoginDoc.cpp#L2178-L2230).

## Character grouping, width, and streaming

The following are source observations, not claims of complete Unicode grapheme
conformance or runtime visual fidelity.

| Topic | Verified behavior and evidence |
| --- | --- |
| Combining marks and variation selectors | The UTF-8 completion path tests `UNI_IVS` or `UNI_NSM` and, if a previous base exists, calls `PUTADD` at its saved position. It then exits that character's processing. Without a previous base this branch does not create a dotted-circle or independent cell. [NSM/VS handling](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2707-L2716). |
| Normalization and Hangul | A conditional path attempts pairwise `UnicodeNomal` composition, repositions to the preceding character, and includes separate Hangul component append cases. Therefore stored display text cannot be assumed to preserve all received code-point sequences unchanged even before overflow. [Normalization and Hangul](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2648-L2705). |
| Supplementary characters | The decoder turns the completed scalar into packed UTF-16 using `UCS4toUCS2`; the cell assignment and append operations store the high and low halves separately. There is also special handling for surrogate-valued UTF-8 inputs. This establishes representation, not strict UTF-8 validation conformance. [Decoder](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2611-L2646), [storage](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L145-L182). |
| Emoji, ZWJ, and ZWNJ | Emoji modifiers append only when the saved base has the Emoji flag. Both U+200C and U+200D set joining state; the joiner and subsequent character can be appended at the saved base position. Emoji presentation attributes are conditional on the DirectWrite build path. These are explicit rules rather than evidence of general UAX #29 segmentation. [Emoji and join state](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2767-L2849). |
| Unicode tables | The pinned `UniCharTab.h` identifies Unicode 17.0.0 and names EastAsianWidth, UnicodeData, NormalizationTest, and emoji-data as its inputs. This header does not establish passing GraphemeBreakTest or a particular grapheme algorithm. [Table provenance](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/UniCharTab.h#L1-L15). |
| Columns | Width is chosen as one or two cells using wide/ambiguous flags; ambiguous width depends on an option. `PUTADD` changes the attached string without advancing the cursor or recomputing the base cell's width. Thus later VS/ZWJ additions cannot be assumed to recalculate the final sequence's column width. [Width selection](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2757-L2765), [one/two-cell dispatch](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2826-L2845), [append](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L10857-L10891). |

**Streaming inference:** Receive-buffer boundaries alone do not finalize this
grouping state. The previous base/position, joining flag, partial decoded value,
and surrogate state belong to `CTextRam`; `Write` dispatches bytes, and its
`FLUSH` updates display/trace state without resetting those fields. UTF-8 lead
bytes set persistent decoder state for later continuation bytes. Splitting a
base and its following mark, or the bytes of one scalar, across ordinary calls
therefore has a source-supported continuation path. Control sequences, resets,
line-edit modes, and every chunking permutation were not runtime-verified.
[State fields](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.h#L1178-L1194),
[byte loop](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L4586-L4626),
[flush](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L9547-L9591),
[decoder continuation setup](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2537-L2559).

## Consequences for editing, copying, and recording

- **Cursor and erase are cell operations.** Backspace decrements the column
  with wrap/margin handling; it does not remove the last combining mark.
  ECH sends a cell rectangle to `ERABOX`, whose ordinary erase replaces cells.
  This supports treating attached text as cell contents, but does not prove
  every wide-cell partial-erase edge case is correct.
  [Backspace](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L2897-L2918),
  [ECH](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRamProc.cpp#L5972-L5976),
  [ordinary erase](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L10371-L10382).
- **Copy and line recording use retained cell text.** `EditCopy` exports each
  cell's stored string and steps over recognized wide-cell pairs. `GetLine`
  likewise reads cell strings, and `CallReceiveLine` writes that result. The
  consequence of the earlier overflow is that discarded additions cannot
  reappear in these exports.
  [Copy](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L5344-L5375),
  [line extraction](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L5660-L5709),
  [line recording](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L8497-L8514).
- **Other recording modes are distinct.** `PUTADD` calls `CallReceiveChar`
  before its bounded append. The character/control log branch writes that
  incoming character, while the raw log branch writes consumed input bytes in
  the receive callback. Source inference: a display append being dropped need
  not drop the same addition from those modes. This is not a guarantee about
  logging conversion, I/O failures, or every character ignored earlier in parsing.
  [Callback before append](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L10857-L10878),
  [character/control recording](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/TextRam.cpp#L8565-L8606),
  [raw recording](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/RLoginDoc.cpp#L2213-L2220).

## Limited paste observation and TabTty implications

The inspected `SendPasteText` and `SendBracketedPaste` routines do not contain
a 1 MiB rejection. They build the text/buffer and can open a confirmation dialog
when the counted ordinary characters exceed 500 or controls are present,
depending on options. **500 is a confirmation threshold, not a maximum.**
Clipboard acquisition, edit-control capacity, allocation limits, and transport
queues were not audited end to end, so this does not establish an unlimited
paste capability. [Inspected paste routines](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/RLoginView.cpp#L3533-L3597).

For TabTty, this evidence supports discussing **connection continuity separately
from cell-storage/rendering capacity**. It does not justify adopting RLogin's
silent loss, its exact 15-unit bound, its grouping rules, or its dependency set.
Preserving full logical text while bounding rendering work would be a separate
TabTty design, not verified RLogin behavior. Any proposed replacement for
TabTty's 256-scalar receive limit or 1 MiB paste limit still needs an explicit
product decision covering retained text, copy, recording, memory, and progress.
This note changes no accepted contract and authorizes no implementation.
