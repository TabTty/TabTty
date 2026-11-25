# Broadcast input and split views

Specification confirmed and ready for implementation, 2026-11-14. The user
accepted the final shared-understanding review in Q199 of the
[feature interview](../drafts/broadcast-and-split-issue.md), completing Q162-Q199.
This includes Q197's all-selection reset and its next-Start timing under
Q181/Q189. Implementation was requested after the specification was confirmed.
See [implementation and validation status](implementation-status.md#broadcast-and-split-view-validation-2026-11-14)
for the checks completed and remaining desktop acceptance work.

Published as [GitHub issue #1](https://github.com/TabTty/TabTty/issues/1)
with `ready-for-agent` after the user confirmed the testing seams: WPF
application integration as the primary boundary, existing session/wire
substitution for deterministic failure cases, and native Windows validation
for IME/capture handoff. Publication does not authorize implementation.

Architecture follow-up Q21-Q27 is accepted and consolidated in the
[broadcast-input operation module design](broadcast-input-operations.md),
including final shared understanding. The design is ready for implementation.
It refines operation ownership and the caller interface; it does not report an
implemented refactor.

## Ownership and scope

One terminal tab owns one session and belongs to one tab group in one window.
A group has its own tab bar and selected content. Inner terminal panes share
that tab's session, terminal model, retained output, receive processing, and
recording group. They do not create additional connections or log records.
Broadcast addresses a selected session once, regardless of pane count.
See [ADR 0002](../adr/0002-two-level-splits-with-one-session-per-tab.md).

Add the optional application setting `broadcastAcrossWindows`, boolean,
default `false`. Persist the preference, not active modes, target checks,
first-use flags, or layouts. Settings/editor tabs are neither input sources
nor targets. The setting controls the scope captured by the next Start:

| Saved preference | Starting and stopping |
| --- | --- |
| Disabled | Start one independent mode in the invoking window. Other windows may have their own modes. Ordinary Stop ends that local mode, or the whole global mode if a previously started global mode is still active. |
| Enabled | Start one application-wide mode after all old local modes have stopped. Ordinary Stop ends every active mode across windows, including old local modes. There is no separate Stop All command. |

Saving the setting never changes an active mode's input scope or automatically
restarts it. Q197 invalidates remembered selection when the saved scope changes;
the reset applies at the next Start, not to an active mode or frozen operation.
Its changed Stop-command behavior applies immediately. Show the
effective active scope separately from the next-start preference when they
differ. Disable global Start until old local modes stop; do not silently merge
them. A global mode and a local mode cannot overlap.

An input-capable terminal pane in the effective scope can originate input even
when its tab is unchecked. Only checked, input-ready sessions receive it;
the source receives it only if checked. Selected disconnected/connecting tabs
are counted but excluded from new operations. Never queue input for readiness
or replay it after reconnect. Zero eligible recipients sends nothing and is
visible, rather than falling back to sending to the source.

## Selection lifetime and first use

The first local Start in each window checks all its existing terminal tabs,
including retained disconnected tabs. Later Starts with no intervening scope
setting change restore the checks, including an intentionally empty selection,
regardless of which tab invokes Start. Stop hides the checkboxes and preserves
their values for this application run, subject to Q197's scope-change reset.
Disconnect/reconnect in the same tab and same-window group moves preserve them.
When a successful reconnect changes the transport, host and port, SSH account, or
serial port, show a topmost modeless notification with the tab name and both
destinations. Show it without activating it or moving keyboard focus. Terminal
and broadcast input continue while it is open. OK dismisses the notice.
The first connection and reconnects to the same destination do not show this dialog.
New terminal tabs default to checked. They can receive input from the next operation
once ready. Closing a tab deletes its remembered selection.

A committed cross-window move sets the moved terminal tab's check to ON.
Moving it back sets it ON again; there is no per-window remembered value for
that tab. Cancellation/failure before transfer commit does not change checks.
During a frozen operation, this reset affects subsequent operations only.

Q197 replaces selection preservation across window-local/application-wide
scope changes: reset all terminal-tab selections and start fully selected.
There are no separate remembered selections for the two scope modes.

Under accepted Q199, apply that reset at each affected scope's next Start under
Q181, without changing active recipients under Q189:

| Transition | Selection on the next Start |
| --- | --- |
| Local to global | After old local modes stop, check every terminal tab in every window, including previously used windows and previously unchecked tabs. |
| Global to local | After the global mode stops, each window's next local Start checks all of its terminal tabs. Starting one window does not start other windows. Every window's old selection is invalidated, not restored on its later Start. |
| Stop/Start without a scope setting change | Preserve the current selection, including zero checks. Ordinary mode On/Off is not a scope change. |

A successful save that changes the scope marks selection reset pending for
all windows. Saving the same value or cancelling/failed saving does not.
Switching and saving back before starting still leaves a reset pending; do not
recover a discarded selection. An already-active old mode retains its current
selection until stopped. At Start, apply the pending reset to the starting
scope before admitting input; never reset an unrelated active local mode.
Clear each window's pending reset when it participates in that new Start.

An application's first global Start checks all existing terminal tabs. With
the scope unchanged, a new window joins an already-active global mode. New
and transferred terminal tabs start checked. Joining counts as that window's
first participation; subsequent Starts
do not introduce a delayed first-use reset. Window first-use/reset state is
runtime-only and disappears when that window closes or the application exits.

## Commands and indication

Terminal-tab context menus and the main menu provide Start, Stop, Select All,
and Clear All under Broadcast Input (`同時送信`). Checkboxes appear only on
terminal tabs in participating windows while a mode is active. Bulk selection
uses the effective scope, includes hidden tabs, and cannot bypass an operation's
selection lock. Count ready-selected/total-selected targets, such as `3/4`,
in the single bottom status row of a visible terminal tab in each participating
window. If the window shows no terminal, float the count over the window
content. Status changes preserve the allocated terminal layout. Distinguish zero
selection from selected targets that are all unready. Disconnected status and
target checks are separate indications.

All commands, checkboxes, split dividers, group/pane activation, and tab moves
must have keyboard and UI Automation access. Extend the existing menu-based
movement commands to named groups as well as windows; do not reserve new fixed
terminal shortcuts. Checkboxes expose name, checked state, and temporary
unavailability. Keep English/Japanese UI strings in locale resources and
internal identities independent of translated labels.

## Input admission, ordering, and failures

Broadcast semantic terminal text, committed IME text, terminal keys, paste,
and source-classified remote mouse input. Resolve encoding, Enter/Backspace,
key modes, bracketed paste, mouse format, local echo, and logging separately
for each target. Do not copy one session's encoded bytes to other sessions.
Local selection/scroll/search/copy/zoom, connection actions/BREAK, resizing,
automatic terminal replies, authentication-dialog input, and received output
are outside broadcast. Fan-out must not recursively broadcast its deliveries.
Manual login typed into a terminal remains ordinary terminal input.

Each operation captures the effective mode and eligible recipient identities
with their connection generations. Dispatch each recipient once through its
existing input owner. Preflight all required conversions and reserve bounded
admission before releasing any user payload. If preflight fails, send none of
that operation. After sending begins, an eligible recipient's conversion,
admission, or send failure stops the affected broadcast mode, cancels unsent
work where possible, and preserves checks. Report per-recipient outcomes as
not sent, submitted/possibly delivered, or failed; transport submission does
not prove remote execution. Do not promise rollback, automatic replay, or
simultaneous remote execution. A generation change never redirects old input
to the new connection.

Serialize input admission for a mode and preserve each session's ordering
against its other permitted input and automatic replies. Do not create a
second writer for broadcasting or wait while holding multiple session locks.
Ordinary per-session input budgets remain 2 MiB and 256 queue units, including
preflight and in-flight ownership; broadcast does not multiply those allowances.
Paste source/common-plan storage is shared in the existing application envelope;
per-target private work shares the existing input allowance and send owner. A slow
recipient may delay bounded work; an admission/send failure uses the same
visible stop policy rather than silently dropping that recipient's input.
The initial deadline is 30 seconds for broadcast preflight and individual
non-paste sends. Paste writes use the connection profile's wait setting.

## Paste and frozen operations

Bind the paste to the active mode, input source and eligible recipient sessions
when the user invokes Paste, before requesting text from the Windows clipboard.
Keep the recipient selection locked while the clipboard provider prepares its
data. Stop discards a late clipboard result. If a single-session paste is waiting
for clipboard data or confirmation when broadcast starts, it cannot be sent
through the newly active mode. Capture the configured source-size maximum at
invocation and share the immutable source allocation across recipients.
Accepted whole-codebase architecture Q24-Q27 retain the configurable 512 MiB
default per-paste maximum measured as actual UTF-16 source-text bytes, captured
at operation start, alongside original Q26's shared-budget policy. Q28-Q29's
independent aggregate policy is superseded. Apply the same
[clipboard-paste contract](session-and-recording.md#clipboard-paste) as the
single-session path. For otherwise valid text within the captured maximum, show one
confirmation with the frozen target list and shared character/line pacing fields
when any CR/LF occurs or the source exceeds 1,024 bytes. Newline-free text at or
below 1,024 bytes keeps the direct path; source-maximum equality alone does not
force confirmation. Full text remains accessible through bounded
scalar-safe preview pages. Keep 0-10000 ms delays and application-run-only
Remember transitions, including size-confirmed newline-free text. Direct small
input uses remembered character delay if enabled and changes no remembered values.

Validate the complete payload and strict encoder finalization for every recipient
before sending any payload/start marker; encoded outputs have no separate size
cap. Use bounded conversion work rather than retaining one full output per target.
Reserve each target's remaining bounded send/Stop/healthy closure peak under
[resource accounting](resources-and-validation.md). Target-private conversion,
preflight and transmit work share the existing 2 MiB/256-unit input allowance
and existing closure reservation policy. The shared source/common plan is not
charged again per target. Broaden the existing 4 GiB application envelope to
cover terminal state plus owned paste source/work/copies, counting physical
sharing once; keep the 2 GiB per-session terminal-state bound. Failed admission
uses existing reject/defer behavior and sends no prefix/start marker. Keep held
send/closure capacity, release resources only when actually done, and add no
independent aggregate setting, available-memory percentage or waiting mechanism.

The 30-second broadcast preparation deadline begins after confirmation, or when
preparation begins if no confirmation is required. Exclude time spent in the
dialog; do not restart the deadline on retries. Check expiry/cancellation during
work and again after the final target's conversion immediately before first-send
commitment. Expiry leaves every target not sent. The configured source maximum
does not promise completion within that deadline.

Freeze recipients from preparation/preview through completion or cancellation;
show that target selection is temporarily unavailable. A target that becomes
ready later cannot join. Revalidate generation/readiness before commit; do not
quietly change the confirmed list. Cancelled preview sends nothing. Show
per-target progress and one Stop for the whole paste. Reject additional user
terminal input during paste under the existing paste rule. Each target's
framing and unsent-tail cancellation remain session-owned. Stop closes valid
started frames where possible and does not undo delivered payload. Apply the
same Stop rule inside short or long graphemes: the next complete scalar or
required indivisible codec/control unit, without splitting CRLF. Internal work
chunks add no pacing/count completion, and normal success sends the exact full
input independently of each target's local-echo retained prefix.

Mouse button gestures likewise freeze their selection until all buttons in the
gesture finish or cancellation completes. A target's press recipients are
recorded separately for each button. Selection editing and bulk commands unlock
after the operation ends. Stopping broadcast cancels its active operation.
Ordinary keys, wheel reports, and unbuttoned motion take a fresh selection
snapshot for each operation and do not create a persistent selection lock.

### Operation lifetime and presentation

The BroadcastInput module owns the complete preview-to-completion sequence;
UI callers do not pair operation Begin/End calls. Full Stop ends the mode
immediately; Stop Paste cancels the paste while leaving the mode active.
After effects start, show cleanup pending until admitted work, required frame
and native cleanup, and resource ownership actually end. Cancellation and
deadline expiry alone do not establish completion.

Preview-only cancellation invalidates answers and preference/progress updates,
requests dialog closure, and releases broadcast admission without waiting for
an inert UI callback. Outstanding session/native work is still owned until
actual release. Late callbacks cannot affect replacement operations.

Preserve existing scope-specific Start conflicts and whole-operation AI-grant
eligibility. Ordinary input after full Stop uses each session's existing checks;
draining alone does not extend active local-scope movement barriers. The module
design specifies the named interface, ordered transitions, and verified native
handoff through production/test adapters.

## Mouse coordinates and capture

Determine local/remote gesture ownership at the source, honoring the tab's mouse routing and
source reporting mode. Use logical terminal cell coordinates, including the
source pane's view offset, rather than coordinates relative to the small pane.
Local scrollbars and retained-history browsing never generate remote mouse
reports. Do not turn a local gesture into remote input because a target has
tracking enabled. Respect each target's supported reporting type and mode.

Press/motion outside a target's grid is skipped, with no ratio mapping or
edge clamping. A recipient that never received a button's press cannot join
that drag midway. Leaving the grid does not terminate its gesture; valid
re-entry resumes motion. At actual physical button-up, send the matching
release to valid press recipients and clamp that release coordinate into each
target's current grid. Example: a press at column 50 followed by motion and
release at column 70 gives a 60-column target no motion at 70, then a release
at 60 at the actual release time. It is not released early at the edge.

Protocol representability is separate from grid bounds. Classic reports have
the existing 223-cell encoding limit; SGR reports use numeric coordinates.
For broadcast, an unrepresentable press/motion is unsupported and skipped,
rather than sent at another cell. Release coordinates are bounded by both the
current grid and that format's representable range. No invented releases for
wheel/press-only protocols, changed reporting generations, or new connections.
This is a broadcast amendment to the single-session encoding contract's
general classic-coordinate clamping; see [terminal modes](terminal-and-encodings.md).

Keep an operation owner independent of the pane's UI host. Check mouse capture
success; do not silently begin an untracked gesture. Unexpected capture loss
cancels the gesture, with best-effort matching release cleanup at the last
known valid position only while its connection/mode still permits it. Mark
this as cancellation, not an observed physical release, and never infer a
missed release's exact time/location from current button state. Ordinary
grid exit is not capture loss. An intentional capture transfer is not itself
cancellation if the operation owner verifies the handoff.

[WPF Mouse.Capture](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.mouse.capture?view=windowsdesktop-10.0)
supports directing input to an element outside its bounds and reports capture
success. It does not establish an atomic, lossless cross-window handoff.
The design must preserve gesture identity and observe button-up exactly once
through transfer, checking actual capture outcomes before disposing the old
host. During drag-and-drop, ordinary
[Mouse.GetPosition](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.mouse.getposition?view=windowsdesktop-10.0)
is documented as unreliable; use a supported drag-event or screen-coordinate
path and convert through the current pane geometry. Runtime verification of
these paths is an implementation gate, not a completed result.

## Moves, closure, and scope boundaries

Q193 allows movement during an operation within its effective scope: between
any windows during global broadcast, and between groups in the same window
during local broadcast. Moving a tab into or out of a local scope with an
active frozen operation waits for completion/Stop. Check both affected windows;
an unrelated window's operation does not block an unrelated move.

Retain recipient/session/generation identities through allowed movement.
Moving an unchecked tab between global windows checks it for future input but
does not add it to the active paste/gesture. Moving the input source likewise
does not create a new input operation. The tab carries all its panes and
existing state; do not reconnect, duplicate receive processing, or restart logs.

Prepare the destination, validate input/capture ownership, commit insertion,
then remove the source placement and close an empty source window. A failed
or cancelled move leaves the original ownership intact. If a capture handoff
cannot be maintained, use explicit gesture cancellation and report it; do not
silently discard a press/release or claim successful uninterrupted transfer.
Never dispose the last input host before the handoff decision.

Ordinary close confirmations still apply to live/connecting sessions; window
close combines them once. Cancelling starts no new close-induced effects from
that request; it does not undo a separately approved close already in progress
or independent peer loss. The [closure design](tab-and-window-closure.md)
defines how later requests join existing target closure. Confirmed close
cancels affected unsent input, establishes the receive cutoff, and drains or
reports recording cleanup under [session and recording](session-and-recording.md).
Preserve associated [SFTP close/transfer rules](file-transfer.md). Split removal
alone has none of these disconnect effects.

**Close all disconnected tabs** covers every tab group in the current window.
An exclamation mark on yellow marks an abnormal disconnection, such as a failed
or timed-out attempt or a communication error that ended an established
connection. These tabs stay open for the user to read the reason and are
eligible for the bulk command. An ongoing attempt, including one waiting for
authentication, stays open. Normal disconnections follow the automatic-close
preference in the [window contract](persistence-and-windows.md).

Under architecture Q10, the bulk command and automatic disconnected-tab removal
close the affected transfer windows as
explicit tab/window closure does. The targets need no connection-ending
warning. Existing cleanup, reporting, and wait/loss choices still apply.
Disconnect or peer loss preserves transfer results when the terminal tab is retained. The
[closure design](tab-and-window-closure.md) records these pending implementation
changes.

Closing tabs leaves the window and its tab groups open, including after manual,
automatic, or bulk closure of the final tab. Moving the final tab to another
window closes the source after the destination commits, preserving the
application run and the vault unlock state. Terminal and standalone editor
windows count toward the [application lifetime](persistence-and-windows.md#windows-activation-and-modal-order).

## Two split layouts

Each terminal window has a recursive binary layout of tab groups. Each terminal tab has
its own recursive binary layout of panes. Both support left/right and top/bottom
division, initially equal, with draggable boundaries. Add an empty group for an
outer split, or a same-session pane at the source pane's output position for an
inner split. Do not implicitly connect or clone a tab. Settings/editor tabs
may move to any group but have no inner split operation.

Removing an inner pane only removes that view and collapses its split branch;
the last pane cannot be removed this way. Closing a tab remains separate.
Empty groups persist until explicitly removed. New tabs use the window's
active group. External profile connection requests use the last active group
of the destination terminal window. Each new terminal window starts with one
group and no inner splits. Both layouts are run-local.

Remove an empty group immediately and focus the first remaining group in layout
order. When removing a group with tabs, move them directly to the only remaining
group in that window. If several groups remain, offer them as destinations
identified by position and selected-tab label. Use the ordinary tab move
operation to append the tabs in their current order, committing each move
individually and preserving all sessions and checks.
If a move fails, stop there. Previously moved tabs stay in the destination. The
source group stays open with the failed tab and the remaining tabs in order.
After all moves succeed, focus the destination and collapse the source branch.
The final group cannot be removed.

Expose distinct group-split and terminal-pane-split commands through the main
menu and appropriate tab/group context menus; enable them according to the
invoked content. A terminal's remote-owned right-click remains remote input.
Provide named menu destinations for pane/group activation and movement, with
focus indication, without consuming ordinary terminal navigation keys.

## Shared grid and independent views

Size the session grid from the whole tab content rectangle as if there were
no inner dividers/panes, using the shared font/zoom. Inner splits, divider moves,
pane focus, and pane scrolling do not change that grid. Outer group/window
resizing and shared font/zoom changes may resize it through the existing
normal-screen reflow/alternate-screen clipping contract. Do not count individual
pane scrollbars as reductions to this hypothetical unsplit rectangle.
Each pane aligns its rows to the top of its viewport. At the bottom of its
scrollback, any fractional row height below the last complete row uses the
terminal background. While browsing, this space can show part of the next row.
A terminal tab split into multiple panes shares one fixed-height status bar.

Each pane has its own horizontal/vertical position, text selection, and current
search result/navigation anchor. Query/case and temporary zoom are tab-shared.
Use existing stable content anchors through edits/reflow; eviction clamps each
affected anchor to surviving content. Never create a per-pane retained buffer
or change saved retention to preserve an old view. The tab's search controls
operate on its active pane and do not move another pane's current result.
The search bar overlays the top of the terminal area. Opening or closing it
preserves the terminal grid and each pane's viewport size.

Provide vertical/horizontal scrollbars when needed; they affect only the local
pane. Accepted keyboard/paste input reveals the cursor in the operated pane;
cancelled/rejected input, local navigation, and broadcast delivery to other
sessions do not move their browsing panes. Live-following panes continue
following output; panes browsing history keep their anchors on new output.
Search navigation moves only the pane where it is invoked.

Bind IME composition to its originating pane/session. Moving focus to another
terminal pane, even within the same tab, cancels uncommitted composition and
sends nothing. Tab switching/disconnect likewise cancel; menus/dialogs retain
Windows IME behavior. Once-only commit, local caret/candidate positioning,
and exclusion of uncommitted text from logs remain unchanged.

Give each pane a distinct accessible identity and its own UIA selection and
visible range, backed by the shared content. Expose active-pane focus and
readable group/pane names. Retain existing UIA snapshot limits, retained-range
semantics, and no-default-live-announcement behavior. Output never steals focus.

## Minimum sizes and resource pressure

Q196 delegates numeric usability guards to the engineering specification.
Initial values: terminal drawing viewport at least 160 by 96 DIPs and enough
space for two columns/one row at the effective font size; group shell at least
240 by 160 DIPs and its actual tab/content chrome. Add applicable scrollbar,
divider, and control sizes rather than hiding required controls. Each tree's
minimum is calculated recursively: sums along the split axis, maximum across
it. Settings controls retain their own scrolling/minimum requirements.
These are initial engineering values, not measured usability results.

Reject new splitting if both children cannot fit in the region currently
allocated to the split. Do not enlarge the layout merely to admit more splits.
For an existing layout, shrinking its window or increasing its font must retain
the tree and minimum allocations; provide scrolling over the resulting virtual
layout extent. Clamp divider ratios to child minima. Layout-level scrolling
changes which region is visible, not terminal dimensions. Recompute grids only
when allocated tab content dimensions or shared font metrics actually change.
Keep the documented outer-window minimum of 480 by 320 DIPs; the inspected
520-wide implementation discrepancy is not an approved spec change.

Shared terminal buffers are charged once. Pane anchors and view/layout caches
are charged to existing application budgets; adding panes cannot multiply the
per-session retained-row quota or input budget. Rendering remains fairly
scheduled with the existing per-visible-tab frame target, sharing one session
snapshot across visible panes. Search/highlight/UIA work retains application
limits. Reject a layout mutation before commit if allocation cannot be admitted;
keep the existing layout usable. See [resource limits](resources-and-validation.md).

For both tab groups and terminal panes, the layout holds one reservation for
each live split. A single undivided view has no split reservation. When a split
collapses, its reservation is immediately available for another split, whichever
side survives. A surviving subtree keeps the reservations for its own splits.

## Acceptance cases

| Case | Required observation |
| --- | --- |
| Scope preference | Defaults local; active input scope/selection and frozen recipients survive a save; next Start uses the saved scope and pending all-selection reset. |
| Stop after saving global | Ordinary Stop ends all old local modes; no separate Stop All item. |
| First use and restart | First local/global use checks existing terminal tabs in scope. Unchanged-scope restart preserves empty/partial selection regardless of invoking tab. Scope change invalidates all windows' old selections: next global Start checks all tabs, next local Start in each window checks its tabs. |
| Reset timing and new windows | Same-value save does not reset; changing/saving back leaves reset pending. No reset affects an ongoing operation. New windows in an unchanged active global mode start with new and moved terminal tabs checked. |
| Source versus targets | Unchecked source sends only to checked ready targets; settings inputs and zero-target input send nothing. |
| Tab identity | Reconnect preserves checks without replay; new tab starts checked; cross-window move/back checks ON, same-window move preserves; cancelled move does not reset. |
| Shared panes | Several panes of a checked tab cause one delivery, one receive update, and one recording event. |
| Per-target semantics | Different Enter/codec/key/bracket modes produce each target's expected operation, with its normal echo/log rules. |
| Failure boundaries | Preflight failure sends nowhere; post-start failure reports submitted/uncertain targets, stops the affected mode, and never automatically resends. |
| Paste | One required confirmation, full preflight, bounded per-target progress, common pacing and Stop; late-ready/newly checked tabs never join. |
| Scope-preserving moves | Global cross-window and local same-window moves succeed during paste/drag without changing recipients; local cross-boundary moves wait. |
| Last-tab transfer | Destination exists before source closes; session, gesture/paste owner, logs, vault, and all panes survive without duplication. |
| Mouse edge and re-entry | Narrow target skips out-of-grid motion, resumes on re-entry, and gets release clamped only at physical button-up. |
| Mouse identity | No release to a non-press recipient; overlapping buttons, press-only/wheel modes, generation changes, classic limits, capture failure/loss, and Stop have explicit outcomes. |
| Mouse host transfer | Capture success and one physical release verified across same/different window moves, source closure, DPI changes, and drag-and-drop; no reliance on unreliable GetPosition. |
| Split creation/removal | Both axes and both levels work recursively. An outer split creates an empty group and an inner split shares the session. An empty group can be removed immediately. For a group with tabs, removal moves them directly to the sole destination or prompts for a choice among several. Tabs keep their order, sessions, and checks. Focus moves to the destination or the first remaining group. The final group stays open. |
| Split budget after removal | At each level, split A into A and B. Close A in one run and B in another. In each run the split charge returns to baseline and can fund an immediate new split. In a nested layout with A alongside a C/D split, closing A leaves exactly one split charge. Use the current per-split charge (8 KiB today) when checking the budget. |
| Grid versus view | Inner split/focus/scroll leaves grid unchanged; outer resize/shared zoom can resize; per-pane offsets/selections/current matches remain independent. |
| IME | Same-tab cross-pane focus cancels uncommitted text; commit once, later Enter normal; menus/dialogs retain Windows semantics. |
| Empty lifetime | Manual, automatic, and bulk closure leave the window and its groups open when the last tab closes. The empty groups accept new tabs. |
| Size pressure | Too-small new split disabled; existing layout remains reachable by scrolling after resize/font/DPI changes; no automatic merge or lost tab. |
| Accessibility/localization | Keyboard-only and Narrator reach all groups/panes/targets, command labels distinguish actions in English/Japanese, theme does not change fixed terminal defaults. |
| Resource fairness | More visible panes do not duplicate terminal state, expand retention/input limits, or starve hidden receiving/logging sessions. |

Record runtime evidence in the implementation status document. Native Windows
input and the multi-pane performance cases require their own verification.

Architecture follow-up cases B01-B12 in the
[operation module design](broadcast-input-operations.md#migration-and-verification)
exercise the complete production operation interface, including preview
cancellation, stale native events, actual cleanup ownership, and separate
placement/gesture outcomes. Preserve the acceptance cases above and native
Windows verification. These additions specify future checks, not executed results.
