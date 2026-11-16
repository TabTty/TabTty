# Confirmed terminal external effects, links, and bell scope

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q152-Q154 recommendations accepted on 2026-11-09. Specification only;
no implementation, prototype, installation,
runtime experiment, or publication is authorized.

Read with `terminal-interaction-issue.md`, `terminal-display-proposals.md`,
`teraterm-behavior-baseline.md`, `connection-profiles-issue.md`, and
`receive-logging-issue.md`. Apply the same initial behavior to SSH, Telnet, and
serial, on normal and alternate screens. Remote terminal output is not an
application UI command or proof of identity.

## Q152: Exclude remote clipboard and outer-window changes initially

Consume recognized received clipboard read/write requests without
executing them. In particular, OSC 52 must neither read/send the local clipboard
nor replace its contents. Do not offer an initial enable switch or a per-request
approval dialog. User-initiated Copy/Paste and their existing confirmation rules
are unchanged. Receiving terminal data must not initiate a local paste.

Likewise, do not allow received title/icon-name or outer-window operations to
rename tabs/windows, move/resize/minimize/maximize them, raise them, or change
focus. Keep the profile's display name as the tab's identifying label, with
application-owned connection/logging status. Do not substitute a peer-supplied
title into profile names, file names, or saved settings. Initial scope has no
remote-title preference or separate remote-title history.

This excludes outer-window effects, not ordinary terminal cursor movement,
screen editing, alternate-screen switching, or already selected terminal modes.
Read-only terminal capability/cursor/grid replies retain their separate bounded
protocol contract; rejecting OS window manipulation does not disable all terminal
queries. The exact supported query and legacy column-mode matrix remains work.

Consume recognized complete controls without displaying their parameters or
opening warning dialogs for every occurrence. Unknown, oversized, or incomplete
control recovery still requires the bounded parser contract. This is an explicit
initial capability policy, not a claim that authenticated SSH output is harmless
or that `.ttty` profile activation needs a new approval gate.

## Q153: Leave links as text in the initial release

Provide no automatic URL detection or clickable terminal hyperlinks initially.
Recognize and consume OSC 8 hyperlink wrappers without activating their target;
render the enclosed ordinary text using normal terminal behavior. Do not launch
the browser, a file, a registered URI handler, or another application through a
terminal click or remote control. Do not add an Open link menu action initially.

Visible URLs and labels remain selectable/copyable as ordinary text. Copying a
label copies its visible characters, not a hidden OSC 8 destination. Do not
create special link colors/underlines merely because a wrapper was received;
ordinary received SGR attributes and highlight rules still apply. A future link
feature can separately specify actual-target display and allowed activation
schemes without changing the terminal text model.

## Q154: Use a quiet visual bell with an application-wide Off option

Use a persistent application setting with two initial choices: Visual
(default) and Off. Apply changes immediately to all tabs; do not add per-profile
overrides or sound-file settings. Initial scope has no audible bell, Windows
toast, taskbar flashing, forced focus, or full-terminal inversion/flash.

In Visual mode, a received BEL creates a localized accessible bell indicator on
its tab. If that tab is selected in the active application window, show it for
about one second. Otherwise retain one indicator until that tab is selected in
an active window. Selecting a tab or activating its window acknowledges the
pending indicator. Moving it to another window preserves the pending state;
disconnect does not silently clear an unread indicator, while closing the tab
removes it.

Coalesce repeated BEL events into one indicator, without a counter, queued
animations, or one UI notification per byte. While an active one-second indicator
is visible, further bells do not extend its deadline; after expiry a new BEL may
show it again. Off immediately removes pending indicators and ignores new bell
effects. Expose the indicator's state/name to accessibility without repeatedly
announcing every received BEL.

Only BEL parsed as a bell operation triggers this behavior. A BEL serving as an
OSC string terminator is not also a bell notification. Do not infer a bell from
printable text, connection errors, or a generated log event. Notification UI does
not change terminal cells, terminal theme defaults, or logging eligibility.

## Logging and lifecycle boundaries

Preserve existing recording policy independently of these accepted UI effects.
Raw retains exact eligible received terminal-payload bytes, including these
controls and their payloads, even when no side effect is executed. Text excludes
the nonprinting controls and retains eligible visible text under its screen-mode
rules. Terminal excludes BEL, title/window/clipboard actions and hyperlink
metadata, retaining eligible text/display operations. Do not inject alert labels,
URLs hidden in OSC payloads, timestamps, or newline markers into Text/Terminal.

These choices do not add file transfers, shell integration, macro execution,
remote process launch, or an application-message history. Resource bounds and
malformed-control recovery remain technical specification work; no terminal
feature has been implemented or tested.

## Reference review on 2026-11-09

Tera Term 5.7.1's shipped INI disables remote clipboard access but permits
title replacement and window-control sequences, and enables audible BEL.
Its clipboard-access notification is distinct from a documented per-request
approval flow. Q152 preserves the disabled clipboard default while adopting
a narrower title/window capability. Q154's quiet tab indicator is a TabTty
product choice, not Tera Term's default behavior.

Microsoft documents received title controls separately from cursor controls,
OSC termination by BEL, embedded hyperlinks, and configurable bell effects.
Windows Terminal's documented bell default is audible as well. OSC 8 associates
a URI with visible text; automatic URL detection is a separate feature. Ignoring
link metadata does not require discarding its visible label. These facts inform
the decisions; none requires adopting another terminal's implementation.

- [Tera Term 5.7.1 shipped settings](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/release/TERATERM.INI)
- [Tera Term control-sequence settings](https://teratermproject.github.io/manual/5/en/menu/setup-additional-ctrlseq.html)
- [xterm control definitions](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
- [Microsoft title/OSC controls](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences#window-title)
- [Microsoft embedded hyperlink support](https://devblogs.microsoft.com/commandline/windows-terminal-preview-1-4-release/)
- [Microsoft bell settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-advanced#bell-notification-style)

Before implementation, complete numeric string/control limits, parser recovery,
query replies, resource ownership, and accessible indicator layout. The external
effect decisions must stay distinct from `.ttty` activation trust and ordinary
terminal rendering. Initial implementation readiness has not yet been reached.
