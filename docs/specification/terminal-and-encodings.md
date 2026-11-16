# Initial terminal and encoding contract

Implementation allocation update, 2026-11-16: the
[complete Rust terminal design](native-terminal-core.md) implements the
behavior below without managed fallback, including all supported encodings
and input modes. That target has not been implemented; this update changes
ownership, not the accepted terminal behavior or excluded extensions.

Specification baseline, 2026-11-10. Completes the reference-based choices under
Q103/Q117-Q128/Q144-Q145/Q149-Q154. Tera Term 5.7.1 is the behavior reference;
explicit TabTty adaptations below and prior user decisions take precedence.
This is neither implemented compatibility nor a claim of every xterm feature.

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
Selection is white on 0050A0; search is black on FFD54F, current match outlined
white. Cursor uses a black or white contrasting fill/outline and opposite glyph
foreground as needed; focus loss gives an outline, and peer hiding still wins.

Pin Unicode 17.0.0, UAX29 revision 47, UAX11 revision 44, Emoji 17/UTS51 revision
29 for segmentation/width data. Do not derive cell width from fonts or whichever
Unicode data happens to ship in the OS. Initially render through a TabTty-owned
WPF control with TextFormatter/DrawingContext and installed font fallback;
renderer changes cannot alter this cell contract. No web renderer or third-party
terminal control is introduced.

| Case | Cell/content rule |
| --- | --- |
| Ordinary Unicode cluster | Maximum width of its non-mark base scalars: Wide/Fullwidth 2, Ambiguous profile 1/2, other 1 |
| Recognized emoji sequence | Two cells, including supported presentation/ZWJ/modifier sequences; text variation selects the text-width rule |
| Combining/variation/joiner continuation | Extend the preceding eligible cluster without normalizing text; chunk boundaries do not end clusters |
| Isolated mark | One cell with a dotted-circle rendering aid; stored/copied/logged content excludes the synthetic dotted circle |
| Interior/repeated U+FEFF | Preserve in content/copy/logs as a zero-width format; only Q126's first complete UTF-8 signature per source is consumed |
| Unrecognized grapheme/ZWJ sequence | Preserve scalars, use the ordinary maximum-base rule; no promise of one combined glyph |
| Legacy decoded text | Same Unicode width policy after exact mapping; no extra hidden locale/OS byte-width override |
| Wide cluster at last column | With wrap, move the complete cluster to the next row; without wrap, reserve the last two cells by moving its start left one column; never store half a cluster |
| Edit/erase touches part of a cluster | Affect the entire cluster and leave cleared cells; no orphan continuation cell or split Unicode content |

Text-changing operations advance revisions; pure attribute/layout changes do
not. An incrementally extended cluster can change its width; recompute its
cell placement before publishing the next snapshot. Keep the last editable
cluster's provenance; growth past the resource cap is explicit failure, not
silent truncation. Layout reflow joins only soft-wrapped normal lines and
preserves anchors, attributes, clusters, recorded revisions, and explicit
hard boundaries. Enforce the captured physical-row quota before publishing;
retirement records eligible Text without LF. Alternate resize clips/pads,
does not reflow, and parks normal content until its eventual return/reflow.

Use Consolas 12 pt with installed fallback and fixed row/cell metrics; absent
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

## Exact Japanese codec policy

Keep all 48 accepted codec identities. Use official Microsoft mappings/codecs
only where their behavior matches; use the accepted Unicode mapping data for
ISO-8859-10/11/14/16. CP932 maps 8160 -> U+FF5E, 5C -> U+005C, 7E -> U+007E,
and 9B5B -> U+5ABC. No best-fit/normalization/width substitutions.

For JIS/EUC, define a Windows-compatible 94x94 table from defined CP932 two-byte
positions with leads 81-9F/E0-EF using the ordinary Shift-JIS to row/cell mapping.
This includes standard 0208 and documented Microsoft extensions in rows
13/89-92. Exclude undefined cells and F0-F9 private-use mappings outside that
grid. FA-FC duplicate Unicode characters transmit through their matching
adopted cell, not direct row/cell conversion. Reverse mapping selects the
smallest row/cell for duplicate Unicode values after single-byte/kana choices.

Vectors: U+FF5E -> JIS 2141 / EUC A1C1; U+2460 -> 2D21 / ADA1;
U+7E8A (CP932 ED40/FA5C) -> 7921 / F9A1; U+2170 (EEEF/FA40) -> 7C71 / FCF1.
Do not map U+301C to U+FF5E. This is an explicit Windows-compatible extension
profile, not strict ISO-2022-JP mail encoding or byte round-trip identity.

JIS ESC ( B selects ASCII; ESC ( J selects Roman with 5C=U+00A5 and
7E=U+203E. Transmit exact Unicode: backslash/tilde require ASCII, yen/overline
require Roman. Leaving Kanji emits the configured OUT B/J, followed by another
designation if the next exact character needs it; preserve this order even
when redundant. IN @/B selects the accepted 0208 table (no separate historical
1978 glyph map). Default B/B remains. Recognize ESC $ @ / ESC $ B / ESC $ ( B;
ESC $ ( D and O/P/Q select unsupported two-byte repertoires until redesignation.
Do not delegate these states blindly to Microsoft's ISO-2022 decoder.

Japanese eight-bit kana is A1-DF in CP932/JIS; selected SO/SI mode invokes
7-bit kana 21-5F and returns to the previous relevant single-byte state.
EUC kana always uses SS2 8E A1-DF; SS3 8F followed by two A1-FE bytes is one
unsupported 0212 unit. JIS transmit kana uses its independent accepted
eightBit/soSi choice. Other encodings do not gain these kana interpretations.

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
[RFC 1468](https://www.rfc-editor.org/rfc/rfc1468.html),
[Microsoft CP932 map](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WINDOWS/CP932.TXT),
[Microsoft EUC-JP](https://source.dot.net/System.Text.Encoding.CodePages/System/Text/EUCJPEncoding.cs.html),
[Unicode 17](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-3/),
[UAX29 rev47](https://www.unicode.org/reports/tr29/tr29-47.html),
[UAX11 rev44](https://www.unicode.org/reports/tr11/tr11-44.html),
[UTS51 rev29](https://www.unicode.org/reports/tr51/tr51-29.html).
Cell-width adaptations and internal limits are TabTty decisions, not claims
that those standards define every terminal-cell behavior.
