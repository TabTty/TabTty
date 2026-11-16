# Confirmed terminal display and accessibility

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q117-Q120 accepted on 2026-11-08, with Q119 amended to provide only
the default cursor behavior and no user-selectable cursor settings initially.
Q114-Q116 are accepted. This is specification work, not authorization to
implement, prototype, install fonts, or perform runtime validation.

## Q117: Font settings and temporary zoom

Use Consolas at 12 pt as the initial primary terminal font. Express the
UI size in points (12 pt is 16 WPF device-independent pixels), independently
of monitor scaling. Use app-wide defaults with optional connection-profile
overrides for family and size. Restrict the primary font choice to suitable
installed fixed-pitch fonts and keep UI fonts separate. Saved changes apply
on the next connection, preserving the accepted settings snapshot policy.

Use installed fallback fonts for characters absent from the primary font.
Consolas alone does not cover Japanese. Prefer a suitable installed Japanese
font where needed, but do not require MS Gothic to exist on every Windows 11
installation. Do not install/download fonts or language packs automatically.
If a configured font is absent, retain its requested name in the saved profile,
use the local fallback, and indicate the actual fallback in settings/status
without making the font shortage a connection-blocking confirmation.

Separate glyph selection/shaping from terminal cell allocation. Fallback must
not change the number of cells occupied, later column positions, or row height
in response to each new glyph. Fit rendering into the assigned cell area;
preserve original decoded text even if only a missing-glyph replacement can
be shown. Do not claim universal Unicode glyph coverage from stock Windows.
Disable optional programming ligatures that combine separate characters such
as `!=`; keep required script shaping and combining-mark rendering.

Provide menu actions to increase/decrease/reset the current tab's font size.
This is temporary per-tab presentation state, preserved across tab movement,
discarded when the tab closes, and not written to the app/profile defaults.
Reset restores the font size captured for that session. Do not silently reserve
new Ctrl+wheel/terminal key sequences; keyboard access through menus is enough
until the key/mouse map is finalized. Zoom keeps outer window bounds and changes
the grid size; run the same resize/reflow, peer-size notification, and logging
rules as Q106, including real quota retirement when applicable.

The split-view extension's Q184 (accepted on 2026-11-13) retains this per-tab
zoom and shares it across the tab's same-session panes. Pane browsing positions
and selections remain independent. Q173 derives the common terminal grid from
the whole tab content area without its inner splits; changing pane focus or
inner dividers does not resize that grid. See
[broadcast input and split views](broadcast-and-split-issue.md).

The specific font/12 pt default and built-in temporary zoom are TabTty choices.
Tera Term v5.7.1 uses environment-dependent font defaults, not a universal
Consolas or 12 pt default. Numeric font/zoom limits and initial/minimum window
dimensions remain layout/resource specifications.

## Q118: Unicode clusters, cell width, and unavailable glyphs

Use a documented, version-pinned Unicode cell-width policy separate from
font measurement. Keep the accepted ambiguous-width setting: one cell by
default, with a per-profile two-cell override, independent of UI language.
Ordinary narrow characters use one cell; full-width/wide characters use two.
Nonspacing combining marks, such as an accent or decomposed Japanese voicing
mark, extend their base character instead of adding columns. Spacing marks and
script-specific clusters need the detailed width matrix below.

Treat supported emoji-presentation sequences, including recognized ZWJ and
modifier sequences, as a two-cell cluster. Use Unicode grapheme boundaries as
an input to clustering, not as proof that every grapheme has one glyph or a
fixed width. Variation selectors and joiners belong to their sequence; do not
count UTF-16 code units as columns or infer columns from glyph pixel advances.
This emoji rule is an explicit TabTty choice, not a claim of exact Tera Term
behavior for every sequence or every remote application's width calculation.

Keep clusters whole for the already accepted selection/copy/paste and reflow
operations. A wide cluster that does not fit at the right edge follows the
terminal's wrapping mode without splitting its glyph or logical contents.
Do not normalize or replace stored text merely to simplify drawing. Missing
fonts/combined emoji glyphs may require a bounded fallback depiction, but the
assigned cells and original Unicode content remain. Color emoji appearance is
not an initial acceptance requirement; correct content/width and usable fallback
are. No terminal image protocol is introduced.

Exact Unicode data version, legacy-encoding width compatibility, isolated marks,
malformed/unrecognized sequences, incremental clusters across receive chunks,
and VT edits into a cluster still require a technical behavior matrix. The
width selection must not silently depend on the Windows font version. This
decision does not reopen the agreed Tera Term encoding inventory or claim
matching terminal/remote widths for all locales and applications.

## Q119: Cursor appearance and focus

Use a blinking block cursor as the fixed local default, matching the reference's
ordinary initial cursor choice. The user excluded cursor selection from the
initial release: provide no app/profile controls or persisted fields for cursor
shape or blinking. Use Windows caret blink timing and respect system-disabled
blinking, including when received controls request a blinking cursor. The
earlier local shape and blink override proposal is superseded.

Honor supported received cursor visibility/style controls for the current
session; this protocol behavior is distinct from the excluded user settings.
Reconnection restores the fixed local default before new peer controls apply.
Preserve peer-requested hiding. When the terminal loses
focus, stop blinking and use an outline indication for a visible cursor so it
cannot be confused with the input target. Render the cursor with sufficient
contrast over terminal colors, highlights, search, and selection while keeping
the underlying character identifiable. No user-configurable terminal default
background/foreground colors or theme variants are introduced.

Exact style-control coverage, cursor contrast rules, and interaction with IME
composition remain technical visual/behavior acceptance work. Blink timing
and focus rendering must not change terminal contents or recording eligibility.

## Q120: Initial accessibility scope

Include keyboard and Windows Narrator access in the initial release.
All surrounding UI, profile/settings editing, tab movement, search, and dialogs
must have keyboard focus/navigation, accessible names, and usable focus cues.
Tab names/status must convey connection/error state without relying only on color.

Expose actual retained terminal text, selection, caret, and visible ranges to
Windows UI Automation so a screen reader can inspect output on request. The
normal buffer includes retained scrollback; the alternate screen exposes its
current contents, not a hidden restored normal buffer. Do not expose off-screen
vault metadata or application-managed secrets through the terminal surface.
Actual remotely displayed text keeps its usual visibility semantics; no promise
of heuristic password redaction is introduced.

Do not automatically speak every arriving line by default or steal screen-reader
focus on output. Coalesce change notifications and bound range queries to avoid
making large output unusable. Initial acceptance includes deliberate text
navigation with Narrator and keyboard operation of the principal workflows.
Do not claim complete application-specific full-screen navigation for every
remote program or support verified with every assistive technology.

Custom WPF rendering does not provide this automatically. A suitable automation
peer and text/range patterns are part of the future implementation work. Exact
event/range limits and verification scenarios remain specifications; no runtime
accessibility verification has been performed in this documentation phase.

## Primary facts reviewed on 2026-11-08

Microsoft lists Consolas in Windows 11 and identifies it as fixed pitch, with
documented coverage that does not make it a standalone Japanese terminal font.
MS Gothic is fixed pitch but Microsoft deployment documentation lists it in
Japanese Supplemental Fonts; the Windows 11 inventory is not sufficient to
promise universal availability. Yu Gothic UI is not a fixed-pitch primary font.
The product therefore uses installed fallback without extra installation.

Unicode distinguishes grapheme clustering, East Asian Width properties, and
actual glyph rendering. East Asian Width is not a ready-made complete modern
terminal width algorithm; unsupported emoji sequences can have multiple glyphs.
The accepted two-cell emoji policy is TabTty's explicit adaptation.

The pinned Tera Term installer also varies ambiguous/emoji width defaults by
Windows UI language. The previously accepted TabTty ambiguous-width default
remains one regardless of UI language; the baseline document corrects its earlier
overbroad description of Tera Term's default. The reference's distributed INI
selects block, blinking enabled, and unfocused outline enabled.

- [Tera Term 5.7.1 installer font/width defaults](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/teraterm.iss#L440)
- [Tera Term 5.7.1 cursor defaults](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/release/TERATERM.INI#L168)
- [Tera Term 5.7.1 width/combining handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L2454)
- [Windows caret blink timing](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcaretblinktime)
- [Windows 11 font list](https://learn.microsoft.com/en-us/typography/fonts/windows_11_font_list)
- [Consolas](https://learn.microsoft.com/en-us/typography/font-list/consolas)
- [Windows optional fonts](https://learn.microsoft.com/en-us/windows/deployment/windows-missing-fonts)
- [MS Gothic](https://learn.microsoft.com/en-us/typography/font-list/ms-gothic)
- [Yu Gothic](https://learn.microsoft.com/en-us/typography/font-list/yu-gothic)
- [WPF font-family fallback](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.fontfamily?view=windowsdesktop-10.0)
- [WPF font-size units](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.control.fontsize?view=windowsdesktop-10.0)
- [Unicode grapheme boundaries, UAX 29](https://unicode.org/reports/tr29/)
- [Unicode East Asian Width, UAX 11](https://unicode.org/reports/tr11/)
- [Unicode emoji sequences, UTS 51](https://unicode.org/reports/tr51/)
- [WPF custom control UI Automation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/ui-automation-of-a-wpf-custom-control)
- [UI Automation Text and TextRange](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange)
