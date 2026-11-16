# Confirmed window lifecycle

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q114-Q116 accepted as recommended on 2026-11-08, following the amended
acceptance of Q109-Q113. Specification only; no implementation is
authorized. These are TabTty-specific choices for its accepted multi-window
model, not claims about Tera Term behavior or Windows API guarantees.

## Q114: Geometry persistence and display changes

Retain the last active terminal window's usable normal bounds,
monitor association, and normal/maximized state as the next launch's geometry.
Persist normal restore bounds even while maximized. Do not persist minimized
as the launch state. Start with one window; do not restore the prior window/tab
collection or connections. Keep profile-selection windows usable without a
session. First-launch default dimensions depend on the remaining font/layout
specification and are not silently decided here.

When explicitly creating another window, use the source window's normal size
and a small positional offset, fitting it into the selected monitor's usable
work area. A tab detached by dragging creates its window near the drop location
on that monitor and preserves the live session. Do not create a maximized
detached window just because its source was maximized.

If a saved monitor disappears or saved bounds are unusable, choose an available
monitor and clamp/reduce bounds to keep the title bar and essential controls
reachable. Apply the destination monitor's scaling so text and controls retain
a usable logical size. Terminal cell-count changes use the already accepted
resize/logging rules (Q106); no synthetic log rows or restored sessions.

Exact placement fallback, minimum layout dimensions, and per-monitor DPI API
handling remain technical details to verify; this is requested user-visible
behavior, not a claim that current code implements it.

## Q115: Activation while minimized or displaying a dialog

Preserve the last-active surviving terminal-window routing rule. Restore
a minimized target to its previous normal/maximized state and open
the requested profile in a new tab. Request activation through ordinary Windows
behavior; do not promise unrestricted foreground stealing.

If the target is occupied by a modal interaction, keep the file-open request
pending and draw attention to that interaction instead of hiding it, dismissing
it, or placing another dialog on top. Resume the pending open when that blocking
interaction ends and the application is still running. Keep arrival order for
multiple requests; each requested file creates its own tab. Dialog cancellation
does not constitute permission to accept its action. Cancellation of an
individual connection applies to that attempt, not unrelated queued opens.

One in-progress vault-unlock interaction serves connections waiting for that
shared vault. Do not stack simultaneous master-password dialogs. Failed or
cancelled unlock ends those waiting credential-use attempts without automatic
repeat prompting; a later explicit connection may request unlock again. This
preserves the accepted first-successful-unlock-until-exit policy.

Opening files does not create a profile approval dialog. At execution time,
read each file and apply only the accepted basic loading checks, ordinary
host-key/vault operations, and runtime error handling. Queuing does not make
unsaved editor settings available to a connection. If the user finishes exiting
the app, pending in-memory opens must not keep a hidden app alive or start new
connections after exit. Detailed close/activation races and queue bounds remain
technical work; they must preserve this user-visible behavior.

## Q116: Tab movement without dragging

Provide tab-menu actions for Move to New Window and Move to Another Window,
accessible with keyboard navigation as well as pointer interaction. Present
existing windows with distinguishable labels using a local window number and
current tab/profile information; no separate persistent window-name feature.
Do not add fixed terminal-keyboard shortcuts until the remaining key map is
resolved, to avoid silently consuming keys intended for the remote terminal.

Move the existing session, retained output, log/pause state, and tab state in
the same way as dragging. Do not reconnect or ask for credentials. Moving the
last tab closes only the empty source window, with transfer protected from
the final-window-exit path. If a destination disappears before the move is
committed, leave the tab in the source and report that the move could not be
completed; do not close or duplicate the session.

Q186 adds one deliberate state exception: a committed cross-window move sets
the terminal tab's broadcast check ON, including a move back. Same-window group
moves preserve checks. Q193 permits moves during a frozen broadcast operation
within its effective scope (global: across windows; local: within the window),
without changing that operation's recipients. A move across an active local
operation's boundary waits for completion/Stop. The tab carries all its panes;
the [broadcast/split contract](../specification/broadcast-and-split-views.md)
defines input ownership and source-window closure ordering.

## Remaining work

These decisions do not settle exact modal ownership and error surfaces, close/
activation/transfer ordering, durable geometry timing, queue/resource bounds,
default font/layout dimensions, or all keyboard bindings. They do not reopen
the accepted tab ownership, single app-run vault, explicit close confirmation,
manual reconnect, or trusted-profile activation requirements.
