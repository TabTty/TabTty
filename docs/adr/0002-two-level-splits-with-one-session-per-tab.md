# Two levels of splitting with one terminal session per tab

Accepted on 2026-11-13 in Q165/Q170/Q172 of the
[broadcast and split-view interview](../drafts/broadcast-and-split-issue.md).
Window-level tab groups each have a tab bar, while splitting inside a terminal
tab creates additional views of that tab's existing session. Both levels allow
repeated side-by-side and top/bottom splits.

This separates arranging different connections from viewing different portions
of one connection's output. We retain one session per terminal tab instead of
putting different sessions inside one tab: the tab remains the identity for
movement and broadcast selection. Additional panes do not add connections,
receive pipelines, or recording groups, and a broadcast operation reaches each
selected session once regardless of its number of panes. A session's single
visual owner means its containing tab, not a restriction to one pane.

Q173 subsequently fixes the shared terminal grid to the tab's full content
area as though its inner panes were not split. This keeps inner splitting and
focus changes from resizing the remote terminal, at the cost of each smaller
pane showing only a portion of that grid. Outer geometry and per-tab font/zoom
changes still follow the existing resize contract. The focused-pane-sized
alternative was considered and not selected.

Q184 retains one temporary zoom per tab, shared by its panes, rather than
independent presentation-only magnification. This preserves one font-size
basis for the shared grid while keeping browsing positions, selection, and
search navigation independent per pane.

The [broadcast/split contract](../specification/broadcast-and-split-views.md)
consolidates interaction details and is confirmed in Q199 on 2026-11-14.
Implementation requires a separate explicit user request.
