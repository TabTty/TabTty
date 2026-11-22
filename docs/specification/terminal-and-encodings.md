# Initial terminal and encoding contract

Implementation allocation update, 2026-11-16: the
[complete Rust terminal design](native-terminal-core.md) implements the
behavior below without managed fallback, including all supported encodings
and input modes. See the [implementation status](implementation-status.md)
for current validation.

Specification baseline, 2026-11-10. Completes the reference-based choices under
Q103/Q117-Q128/Q144-Q145/Q149-Q154. Tera Term 5.7.1 is the behavior reference;
explicit TabTty adaptations below and prior user decisions take precedence.
This is neither implemented compatibility nor a claim of every xterm feature.

The shared-grapheme decisions accepted on 2026-11-18 in Q20-Q22 and Q24-Q27 of
the [whole-codebase interview](../drafts/whole-codebase-architecture-deepening.md)
amend the Unicode and input-capacity contract below. The restored original Q26
shared-budget policy supersedes the later independent Q28-Q29 accounting branch;
the configurable 512 MiB default source cap and confirmation for CR/LF or
source size above 1 KiB remain.
Candidate 6's final Q23 review was accepted on 2026-11-18. These amendments record
design decisions, not new implementation or runtime validation.

## Terminal operations

Accept seven-bit ESC control spellings. High bytes belong to the selected
codec, not global raw C1 recognition. Decoded C1 control scalars are unsupported
nonprinting controls, not an alternate way to execute VT commands; Raw still
preserves the received bytes. Default origin mode off, autowrap on,
insert/LNM off, application cursor/keypad off, normal screen, mouse off, bracketed
paste off, fixed white foreground/black background, and visible blinking block
cursor. Default tab stops are columns 9, 17, 25, and so on (one-based).

| Family | Initial supported operations |
| --- | --- |
| C0 | NUL ignored; BEL visual app policy; BS moves left without erase; HT moves to next stop; LF/VT/FF move down (LNM adds return); CR returns; SO/SI invoke the applicable charset/kana state; CAN/SUB cancel an unfinished escape |
| ESC | `7/8` save/restore cursor, rendition, origin/wrap and charset state; `D/E/M` IND/NEL/RI; `H` set tab; `=/ >` application/numeric keypad; `c` RIS |
| CSI cursor | `A/B/C/D` relative, `E/F` next/previous line column 1, `G` and backtick HPA, `d` VPA, `H/f` CUP/HVP; default count 1, zero count means 1; clip to applicable bounds |
| CSI editing | `@` ICH, `P` DCH, `X` ECH, `L/M` IL/DL, `J/K` ED/EL 0/1/2, `3J` scrollback erase; blanks use current erase rendition |
| Scrolling | `S/T` SU/SD across the scrolling region, independent of cursor row; `r` DECSTBM with valid top < bottom; invalid margins leave prior state |
| Tabs/save | `I/Z` CHT/CBT, `g` TBC 0/current or 3/all; CSI `s/u` cursor-position save/restore, distinct from full ESC 7/8 state |
| Modes | ANSI 4 insert and 20 receive-side LNM; DEC ?1 application cursor, ?6 origin, ?7 autowrap, ?25 cursor visibility; unsupported modes ignored as whole mode items |
| Screens | ?47 switch without clear/save; ?1047 enter alternate and clear alternate on return; ?1048 full cursor save/restore; ?1049 save normal cursor, enter cleared alternate, then restore on return; repeated set/reset is idempotent |
| Replies | Primary DA `CSI ?1;2c`; DSR 5 -> `CSI 0n`; CPR 6 -> `CSI row;column R`; private CPR ?6 -> private reply; no arbitrary OSC/device query answers |
| Cursor style | DECSCUSR `CSI Ps SP q`: 0/1 blinking block, 2 steady block, 3/4 underline, 5/6 bar; Windows-disabled blink remains disabled |
| Input modes | Mouse ?9/1000/1002/1003, coordinate format ?1006, bracketed paste ?2004 |
| Reset | CSI `!p` DECSTR resets rendition, insert/origin/LNM, margins, cursor/application keypad modes and saved cursor defaults, without clearing text/history or switching screen; RIS additionally clears both screens/history, resets tabs/charset/input modes and returns normal |

Apply private/parameter/intermediate/final spelling as a unit; an unknown command
does not execute a known-looking suffix. Ignore unsupported well-framed controls
within parser limits. OSC terminates with BEL or ST; DCS/SOS/PM/APC terminate with
ST, with CAN/SUB cancellation. Embedded eligible C0 controls execute once; an
ESC beginning a new sequence cancels the incomplete preceding sequence unless
it is the string terminator. Truncated controls at EOF are discarded, not
printed as text. Over-limit parser work follows explicit resource failure.

ED2 erases the active page without first scrolling it into history. ED3 erases
history only, preserving active page/cursor/margins. IND keeps its column even
under LNM. Neither LNM nor RIS rewrites profile Enter/encoding settings,
disconnects transport, restarts logs, or changes the app theme. Text erasure
follows Q149; movement/reset does not manufacture logged LF. DECCOLM, outer-window
commands, remote title/icon/clipboard changes, images, and hyperlink activation
are excluded. Consume OSC 0/1/2/8/52 without those external effects; OSC 8 labels
remain ordinary subsequent text. OSC's terminating BEL does not ring the bell.

ASCII and DEC special graphics can be designated into G0/G1 with ESC ( / ESC )
and invoked by SI/SO outside Japanese codec-owned states. Map DEC line graphics
to their Unicode drawing characters. A charset designation never silently
changes the profile's selected communication encoding.

## Rendition, geometry, and Unicode

SGR supports reset 0; bold 1, faint 2, italic 3, single underline 4, inverse 7,
conceal 8, strike 9; resets 22/23/24/27/28/29; 30-37/40-47 and 90-97/100-107;
39/49 restore fixed defaults; indexed and RGB 38/48 in semicolon syntax and
colon forms with omitted/zero color-space slot. Unsupported blink/double-
underline/color-space variants are ignored, without changing stored text.
Conceal affects rendering, not secret redaction. Highlight styling is visual
only and follows selection > search > first matching rule > terminal rendition.

Use the xterm-style 16-entry palette in order:
`000000 CD0000 00CD00 CDCD00 0000EE CD00CD 00CDCD E5E5E5`
`7F7F7F FF0000 00FF00 FFFF00 5C5CFF FF00FF 00FFFF FFFFFF`.
Indices 16-231 use the six-level RGB cube 0/95/135/175/215/255, and 232-255
use gray 8+10n. Default white is FFFFFF, independent of index 7. Retain true RGB.
Selection is white on 0050A0 and search is black on FFD54F, with a white outline
for the current match. For each visible cursor shape (block, bar, underline or
unfocused outline), choose whichever of black and white has the higher contrast
ratio against the cell's displayed background after terminal rendition,
highlighting, search and selection. Use the sRGB relative luminance and
[contrast ratio defined by W3C](https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio).
Focus loss gives an outline. A cursor hidden by the peer stays hidden.

A filled block redraws a visible glyph in the color opposite its fill using
the cell's effective bold and underline, including attributes added or cleared
by the winning highlight rule. A concealed glyph stays concealed under the
cursor. Verify rendered colors as a cell changes from a white highlight to
search yellow (black cursor in both cases) and then selection blue (white
cursor). On an FF0000 background the cursor is black. Verify the outline after
focus loss and the effective glyph decorations and concealment under a block.

Pin Unicode 17.0.0, UAX29 revision 47, UAX11 revision 44, Emoji 17/UTS51 revision
29 for segmentation/width data. Do not derive cell width from fonts or whichever
Unicode data happens to ship in the OS. Initially render through a TabTty-owned
WPF control with TextFormatter/DrawingContext and installed font fallback;
renderer changes cannot alter this cell contract. No web renderer or third-party
terminal control is introduced.

| Case | Cell/content rule |
| --- | --- |
| Ordinary Unicode cluster | Maximum width of its non-mark base scalars: Wide/Fullwidth 2, Ambiguous profile 1/2, other 1 |
| Recognized emoji sequence | Two cells for the pinned repertoire defined below; VS15 in the retained spelling selects the ordinary text-width rule |
| Combining/variation/joiner continuation | Extend the preceding eligible cluster without normalizing text; chunk boundaries do not end clusters |
| Isolated mark | One cell with a dotted-circle rendering aid; stored/copied/logged content excludes the synthetic dotted circle |
| Interior/repeated U+FEFF | Preserve in content/copy/logs as a zero-width format; only Q126's first complete UTF-8 signature per source is consumed |
| Unrecognized grapheme/ZWJ sequence | Retain scalars subject to the stored-prefix guard below and use the ordinary maximum-base rule; no promise of one combined glyph |
| Legacy decoded text | Same Unicode width policy after exact mapping; no extra hidden locale/OS byte-width override |
| Wide cluster at last column | With wrap, move the complete cluster to the next row; without wrap, reserve the last two cells by moving its start left one column; never store half a cluster |
| Edit/erase touches part of a cluster | Affect the entire cluster and leave cleared cells; no orphan continuation cell or split Unicode content |

Text-changing operations advance revisions; pure attribute/layout changes do
not. An incrementally extended cluster can change its width; recompute its
cell placement before publishing the next snapshot. Keep the last editable
cluster's provenance and apply the bounded stored-prefix rule below. Layout
reflow joins only soft-wrapped normal lines and
preserves anchors, attributes, clusters, recorded revisions, and explicit
hard boundaries. Enforce the captured physical-row quota before publishing;
retirement records eligible Text without LF. Alternate resize clips/pads,
does not reflow, and parks normal content until its eventual return/reflow.

### Pinned emoji recognition

The recognized repertoire is the union of the Emoji 17 RGI set, every emoji-style
pair in the pinned `emoji-variation-sequences.txt`, and the multi-scalar
minimally-qualified/unqualified forms listed in `emoji-test.txt`. Include the
RGI sequence and ZWJ files; do not generate additional missing-selector variants
or recognize a known substring of an otherwise unsupported sequence. A bare
text-default symbol remains under the ordinary width rule even when listed as
an unqualified singleton. Supported VS16 pairs select two cells. Any VS15 in
the spelling being measured selects ordinary width, which can itself be two.

For recognition lookup only, remove the maximal trailing run whose scalars
have both General_Category Mn/Mc/Me and Grapheme_Cluster_Break Extend/SpacingMark,
excluding the full Variation_Selector property and U+20E3 COMBINING ENCLOSING
KEYCAP. The entire remaining core must match the repertoire. Do not remove
interior marks, selectors, joiners, modifiers, tags or keycap structure to
manufacture a match. Lookup never changes stored content or normalizes text.
An unmatched sequence uses the ordinary maximum of non-mark base widths;
isolated-mark and zero-width FEFF rules retain their special meanings.

Reevaluate from the retained original spelling after every stored extension,
including intermediate shrink/growth within one receive chunk. For example,
`00A9 FE0F 0301` is two cells, while `00A9 FE0F 0301 FE0F` and
`00A9 FE0F 200D 00A9` use ordinary width. `2764 FE0F FE0E` uses ordinary width.
Chunk/page ends do not freeze recognition. The bounded-prefix exception below
means that omitted continuations beyond scalar 256 do not change this geometry.
Font shaping and the OS Unicode category database are not independent rule
authorities. Data provenance and independent verification belong to the
[shared Rust grapheme design](native-terminal-core.md#shared-grapheme-operations).

### Bounded stored spelling and complete-stream segmentation

Retain at most the first 256 Unicode scalars of each stored cluster. Consume
further continuations without ending the session, inserting a replacement or
ellipsis, manufacturing a break at scalar 257, or keeping an unbounded archive.
This is a per-cluster storage guard, not a line-length or outgoing-input limit.
Keep a separate bounded incremental break state and advance it over every
eligible scalar, including omitted ones. Start the next stored cluster at the
next real grapheme break or existing VT continuation cutoff. Repeatedly testing
the truncated prefix against the next scalar is insufficient: a retained ZWJ
must not absorb a later independent cluster after its omitted continuation.

The source-local last-graphic/REP spelling, each editable-grid projection and
serialized Text-position state remain independent. One owner's saturation must
not stop the whole source operation: other projections, recording eligibility
and full-stream break state still advance. Width, recognition and drawing-aid
eligibility use each owner's retained prefix through scalar 256; omitted
VS15/VS16 or other suffixes do not alter its stored geometry. Estimate and
Execute use the same classification from their independent states.

Screen, scrollback, copy, UIA, screen/history observations, search and normal-
screen Text recording use the corresponding retained spelling. Alternate-screen
Text and Terminal recording still receive their original eligible scalar or
operation stream; Raw still receives original terminal-payload bytes. Storage
saturation must not bypass those independent paths. Existing recording interval,
source-provenance and I/O-failure rules still apply, with no backfill or claim
of file durability. REP replays its source-local retained spelling through the
existing resolved-text path and cannot reconstruct omitted continuations.

Reflow, selection and observations use authoritative stored spans. Freshly
segmenting exported concatenated prefixes may produce different spans and must
not redefine the stored ones. Add no invisible separators to copied text.
UIA's full-result promise covers retained text and permits no additional
provider-time truncation. Serialized Text position remains bounded and does not
discard stream text; emit TAB only when its expansion is provably faithful,
otherwise use the existing final-spaces/text representation.

This omission is normal continued processing, without a per-scalar warning,
new dialog, fatal incomplete status or recording failure. Actual resource,
transport, recording and provider failures retain their existing outcomes.
Successful outbound paste preserves the complete validated input; the display
prefix is never its payload. Local echo can use a bounded stored prefix of that
exact submitted input. See [input and recording](session-and-recording.md) for
the source-size cap, confirmation and scalar/codec-safe Stop, and
[resource accounting](resources-and-validation.md)
for shared-source/application admission, existing target input-work bounds and
observation capacities.

### Presentation and accessibility

Use Consolas 11 pt with installed fallback and fixed row/cell metrics; absent
fonts are visible nonfatal fallback status. Keep required shaping while
disabling optional programming ligatures. Fit glyphs to assigned cells instead
of widening columns. IME composition is an uncommitted overlay anchored at the
caret, excluded from transport/logs until one commit; consume the corresponding
key event so it cannot send another Enter. Follow caret position/DPI for the
candidate UI. Tab switching, disconnect, and focus movement to another terminal
pane (including within the same tab) cancel uncommitted composition and send
nothing. Menu/dialog focus changes use Windows IME semantics, without generating
an extra commit or inventing remote input. Q184/Q195 give each pane independent
scrolling, selection, and search navigation over the shared terminal model;
query/case and temporary zoom remain tab-shared. See the
[split-view contract](broadcast-and-split-views.md).

Expose UIA Text/TextRange selection, caret, visible ranges, and retained text
with stable content anchors and ordinary logical boundary positions. Alternate
mode exposes only its active content. Evicted ranges collapse at the nearest
surviving boundary; stale ranges never read freed buffers. GetText(-1) returns
the complete requested range; a nonnegative maximum follows UIA's contract.
Reserve snapshot memory before copying; if unavailable, return an explicit
provider failure, never implicit truncation or invented pagination. Coalesce
text events, no default live
announcement of every incoming line, and no output-triggered focus change.

UIA range movement and expansion use row positions and the terminal snapshot's
stored character spans. A one-character move must work even when copying the
entire document would exceed the 64 MiB UIA snapshot budget. For `TextUnit.Line`,
each displayed terminal row is a line, including a row ending in a soft wrap.
A soft wrap introduces a navigation position without adding a character to the
text returned by UIA, copied to the clipboard, or searched. See
[Microsoft's UI Automation text units](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-uiautomationtextunits#line).

`FindText` searches within the requested range without copying that whole
range into a string. Preserve forward and backward searches, the caller's
case choice, matches spanning a soft wrap, and matches containing an explicit
line break. For a command that wraps across three terminal rows, verify that
line movement visits each row, search finds text across a wrap in either
direction, and `GetText` returns the original command. With a document whose
full text exceeds the copy budget, verify a one-character move and a search
for a short phrase alongside the explicit failure of a full-document read.

## Keyboard and mouse

Use Tera Term's shipped IBMKEYB.CNF baseline: normal arrows CSI A/B/C/D,
application arrows SS3 A/B/C/D; Home/End CSI 1~/4~; Insert/Delete/PgUp/PgDn
CSI 2~/3~/5~/6~; Shift+Tab CSI Z; F1-F4 SS3 P/Q/R/S; F5-F12 CSI
15~/17~/18~/19~/20~/21~/23~/24~. Numeric keypad sends ordinary characters;
application keypad uses SS3 p-q-r-s-t-u-v-w-x-y for digits 0-9, n for decimal,
M for Enter, j/k/m/o for multiply/plus/minus/divide. Plain Tab sends HT;
Escape sends ESC. Printable input follows the selected strict transmit codec.
Ctrl letters use ordinary ASCII C0 mappings; Alt printable input uses an ESC
prefix, except AltGr/IME text remains printable. Modified navigation/function
keys use xterm CSI parameter 1 + Shift(1)+Alt(2)+Ctrl(4); do not enable modifyOtherKeys
or enhanced keyboard protocols initially. Accepted local commands take priority.

Enter/Backspace and local shortcuts retain Q88 and the interaction draft's
protocol defaults; fixed SSH echo Off and temporary Telnet/serial behavior
remain. Ctrl+Shift+C/V are local copy/paste; Ctrl+C/V remain remote input.
Right-click opens the local menu only while remote reporting is off or Shift
selected local gesture ownership; otherwise report it remotely. Menu-based
zoom has no new Ctrl+wheel binding.

Mouse 9 sends presses only; 1000 adds release/wheel; 1002 adds drag motion;
1003 adds all cell motion. Latest enabled tracking mode wins; disabling it
leaves tracking off. 1006 chooses SGR format and never enables tracking alone.
Classic reports are CSI M plus encoded button/x/y, one-based cells clamped to
223; SGR is CSI <button;x;y M, with m for release. Add standard Shift/Alt/Ctrl
bits 4/8/16, motion 32, wheel 64/65. Coalesce motion within one cell. Apply the
accepted Shift local-selection override at gesture start, retain that owner,
and cancel the remote gesture on protocol mode/generation change; never invent
an old-mode release in a new mode. No pixel/urxvt/UTF-8-coordinate extensions.

Broadcast Q182/Q190 amend coordinate handling: skip unsupported or out-of-grid
press/motion and clamp matching release only at actual button-up. Do not end
the gesture at grid exit. The [broadcast contract](broadcast-and-split-views.md)
also covers protocol bounds, capture, and same-session pane coordinates.

## Encoding providers

The editor and profile validator support 47 identities. Profile validation
rejects unsupported encoding identifiers. Win32 strict conversion defines the supported Windows/OEM
code pages and KOI8-R. Undefined printable units yield U+FFFD on receive.
CP932 A0, FD, FE and FF are undefined as standalone bytes. Strict send
preflight checks that the library's output decodes to the exact original text.

Windows ICU supplies ISO-8859-10/11/14, EUC-JP and JIS. EUC-JP uses
`euc-jp-2007` for the ASCII, SS2 kana and two-byte repertoire. A well-shaped
EUC SS3 unit yields one U+FFFD. ICU selects the byte mapping for each accepted
character, including U+FF5E as A1 C1 and U+2460 as AD A1.

JIS uses ICU's Japanese ISO-2022 converters. Each direction's kana setting
selects ICU version 3 for SO/SI or version 4 for eight-bit kana. ICU owns
Roman and Kanji designations, repertoire selection, and transmit closure.
Saved `kanjiIn` and `kanjiOut` values are read for profile compatibility and
have no effect on transmission. The JIS codec keeps receive state independent
per source across chunks. TabTty routes VT controls to the terminal parser.
The CP932 receive SO/SI option remains a terminal wire interpretation that
routes the invoked kana bytes to the CP932 converter.

For legacy multibyte decoding, a well-shaped undefined/unsupported unit consumes
its whole unit and produces one U+FFFD. On an invalid continuation, replace
the pending prefix once and reprocess the offending byte as a new start/control.
At EOF replace one pending character prefix once; chunk end is not EOF. Thus
EUC 8F A1 A1 -> one replacement; 8F A1 0A -> replacement then LF. An unfinished
charset/VT designation is an incomplete control, not a printable character.
UTF-8 uses maximal-subpart replacement: E3 81 1B -> replacement then ESC;
C0 AF -> two replacements. Single-byte undefined positions produce one
replacement. All outgoing units use strict preflight; rejects cannot alter live
encoder state. No automatic encoding detection or UTF-16 is introduced.

## Verification and sources

After authorization, verify control aliases/reset-state tables, less/top/vim,
all codec chunk splits, malformed-to-C0 resynchronization, exact Roman four-
character round trips, extension duplicates, unsupported repertoires, emoji/
combining/edge edits/reflow, all key/mouse modes, IME once-only commit, and
Narrator workflows in English/Japanese. No such runtime verification has run.

Source facts and explicit differences are in `../drafts/technical-contract-review.md`.
Mapping/segmentation references:
[Windows ICU](https://learn.microsoft.com/en-us/windows/win32/intl/international-components-for-unicode--icu-),
[RFC 1468](https://www.rfc-editor.org/rfc/rfc1468.html),
[Microsoft CP932 map](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP932.TXT),
[Microsoft EUC-JP](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/EUCJPEncoding.cs.html),
[Unicode 17](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-3/),
[UAX29 rev47](https://www.unicode.org/reports/tr29/tr29-47.html),
[UAX11 rev44](https://www.unicode.org/reports/tr11/tr11-44.html),
[UTS51 rev29](https://www.unicode.org/reports/tr51/tr51-29.html).
Cell-width adaptations and internal limits are TabTty decisions, not claims
that those standards define every terminal-cell behavior.
