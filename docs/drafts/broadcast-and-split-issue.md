# Specify broadcast input and split terminal views

Status: specification confirmed and ready for implementation on 2026-11-14,
following final shared-understanding acceptance in Q199. The interview opened
on 2026-11-12 and resolves Q162-Q199. The
[consolidated contract](../specification/broadcast-and-split-views.md) records
the accepted behavior, technical details, and acceptance cases. Historical
withdrawn recommendations below are not requirements. Both features join the
initial release under Q162; implementation and prototyping still require a
separate explicit request.

Published through the user-invoked `to-spec` skill as
[GitHub issue #1](https://github.com/TabTty/TabTty/issues/1), labelled
`ready-for-agent`. The user separately confirmed the testing seams before
publication: existing WPF application integration plus the existing
session/wire boundary for deterministic failures, with native IME/capture
validation. The [publication source](broadcast-and-split-github-issue.md)
contains the seven-section issue specification and 74 user stories. The
published body and label were read back and verified against that source.

The user subsequently requested implementation. See
[implementation and validation status](../specification/implementation-status.md#broadcast-and-split-view-validation-2026-11-14)
for the results and the remaining desktop acceptance cases.

## Confirmed request

- Provide broadcast input resembling RLogin: direct the same user operations
  to multiple selected connections.
- Show checkboxes on terminal tabs while using broadcast input; checked tabs
  identify its targets. Settings-related tabs are never eligible targets.
- Provide Start/Stop and Select All/Clear All through both the terminal-tab
  context menu and main menu, under `同時送信` in Japanese. Show the active mode,
  effective scope, and ready-selected/total-selected counts in participating
  windows. Keep English/Japanese labels in locale resources.
- Provide window-level tab groups with separate tab bars and splitting inside
  a terminal tab. Q165's final answer on 2026-11-13 selects multiple views of
  the same session inside a tab. Different sessions remain different tabs.
  Q172 applies repeated left/right and top/bottom splitting to both levels.
- Provide a setting to allow broadcast target selection across all TabTty
  windows. Default it to disabled, restricting target selection to the same
  window. Enabling it allows selection across windows, including hidden tabs.
  When disabled, each window owns its broadcast mode and target set. When
  enabled, one mode and target set is shared application-wide. Q181 captures
  the effective scope at broadcast Start: saving a changed setting does not
  change an active mode's scope. After Stop, the next Start uses the saved
  setting; when already stopped, the next Start uses it directly. This is an
  explicit exception to the usual immediate application-setting timing.
  Q187 requires existing local modes to stop before global Start. Once the
  saved setting selects all windows, the ordinary Stop Broadcast command stops
  all windows' broadcasts, including modes still using their earlier local
  scope. Do not add a separate Stop All Broadcasts command.
- Broadcast terminal text/committed IME input, terminal keys, paste, and mouse
  operations directed to the remote terminal. Do not synchronize local
  scrolling or selection. Connection operations/BREAK, resize, automatic
  terminal replies, and authentication-dialog input remain outside broadcast.
- Separate the source of broadcast input from its targets: input from an
  unchecked terminal tab goes only to checked targets, not to that source.
  Zero targets means no transmission and a visible zero-target state.
- Remember the selected broadcast targets across mode stop/start within the
  application run when the scope setting is unchanged. Q197 resets all tab
  selections on a scope-mode change, so the new mode starts fully selected;
  it supersedes selection preservation across that change. Clear remembered
  selection when the application exits.
  Selection survives disconnect/reconnect and movement within one window;
  Q186 resets it to CHECKED whenever a move to another window commits. Moving
  back resets it again; do not restore the previous window's unchecked state.
  This supersedes Q178's selection-preservation rule for cross-window moves;
  only input-ready sessions receive new input, with no replay. Closing a tab
  removes its selection; new tabs start unchecked. Q179 initializes the first
  broadcast selection with ALL terminal tabs in scope checked, not only the
  invoked tab. Later starts restore the remembered state regardless of which
  tab invokes Start, including an unchecked invoking tab. Start/Stop operations
  do not change between first and subsequent use. Q180 chooses first-use
  initialization separately for each window rather than once per app run.
  Q197 rejects preserving used windows' selections when changing to global
  scope. The consolidated contract reconciles the reset with Q181's next-Start
  timing and Q189's active-operation recipient protection, accepted in Q199.
- Interpret equivalent operations using each target's settings and terminal
  modes; retain per-session local-echo and logging behavior.
- Both layout levels support equal initial division and draggable dividers.
  Adding panes neither creates a connection nor duplicates receipt/logging;
  broadcast selection identifies the tab's session once, not each pane.
- Q173 derives the shared terminal grid from the tab's whole content area as
  though it had no inner splits. Inner-divider movement and pane focus changes
  do not resize the terminal. Outer-group/window geometry and the existing
  per-tab font/zoom rules can change the grid. Individual panes display a
  portion of that shared grid, with independent browsing under Q184/Q195.
- Q184 keeps browsing position, text selection, and search navigation separate
  per pane. Input/search moves the operated pane only, and new output does not
  reset a pane browsing retained history. Search query/case and temporary zoom
  are shared by the panes of one tab. Shared zoom retains the existing grid
  resize behavior; pane-local presentation-only zoom is not selected.
- Splitting a tab group creates an empty group. Splitting inside a tab creates
  another view at the source view's current output position. Opening a new
  connection still requires a saved profile and an explicit connection action.
- Removing an inner split closes that view only; the final view cannot be
  removed through split removal. Removing an outer group moves its tabs to a
  user-selected group in the same window under Q198. Preserve order while
  appending the moved tabs, then focus that destination. These operations do
  not disconnect sessions. Closing a
  terminal tab remains a separate operation with the existing confirmation.
- Settings and profile-editor tabs may be placed/moved in any tab group.
  They cannot be split internally and are excluded from broadcast input.
- Do not save split layouts across application runs. Start with one tab group
  and no inner splits, preserving the existing window-geometry persistence and
  no-tab/no-connection-restoration policies.
- Preserve empty tab groups until explicit split removal. New tabs open in the
  active group. External profile activation uses the last active group of the
  window selected by the existing last-active-window rule. Q192 closes a window
  once all of its tabs are gone, including after transfer; empty groups do not
  keep that window alive. Settings/editor tabs count as tabs.
- Send broadcast mouse input using the same terminal row/column in each target,
  respecting that target's reporting mode. Skip disabled/unsupported reports
  and out-of-grid press/motion coordinates instead of remapping or clamping
  them. Q190 adds a release-only exception: at actual button-up, clamp the
  release coordinate into each matching press recipient's current grid. Do
  not end a drag merely because it crosses a target's grid edge.
- On an eligible target's input conversion/admission/send failure, stop
  broadcast, keep checks, and report the outcomes. Preflight failure sends
  none of that operation; failure after sending starts stops remaining work
  where possible without rollback or automatic replay. Already-unready tabs
  remain excluded, rather than triggering a failure merely by being selected.
- Broadcast paste uses one required newline confirmation with the target list,
  one immutable payload, common pacing controls, per-target progress, and Stop
  for the whole paste. Validate for every target before any send; apply its
  encoding and bracketed-paste mode. No-newline paste retains the existing
  no-confirmation rule. Stop cancels unsent work without undoing delivered data.
- Freeze targets and disable checkbox changes during a paste or mouse drag.
  Restore target editing after completion/cancellation. Ending broadcast also
  cancels its in-progress operation; ordinary keys capture current selection
  for each operation. Q193 allows tab moves that stay within the operation's
  effective scope: any window in global mode, same-window groups in local mode.
  Cross-window resets under Q186 affect subsequent operations, not the frozen
  recipient set of an in-progress paste/gesture.
- Cancel uncommitted IME input when focus moves to another terminal pane,
  including within the same tab. Existing menu/dialog IME semantics remain.
- Give panes independent vertical/horizontal scrollbars when needed. Scrolling
  is local. Input reveals the cursor in the operated pane without moving other
  panes that are browsing history.
- Reject an additional split if usable minimum sizes cannot fit. If resizing
  makes an existing layout too large, retain it and allow layout-level
  scrolling; do not automatically remove or merge groups/panes.

## Reference evidence

Reviewed RLogin's primary documentation on 2026-11-12. Reference behavior is
evidence for the interview, not an automatic TabTty requirement.

- [RLogin FAQ 5.11](https://kmiya-culti.github.io/RLogin/faq.html#SPLIT)
  describes multiple connections arranged in split regions and moving
  connections between regions by dragging tabs. Ordinary splits can exist
  empty and survive connection closure; a split created by connecting can
  disappear when that connection closes. Layout-only and connection-inclusive
  saved states are separate options.
- [RLogin's main documentation](https://kmiya-culti.github.io/RLogin/), sections
  2.1, 2.3, 2.4, and 3.2, also describes a distinct same-connection split view,
  tab-checkbox broadcast selection, and optional broadcast restrictions or
  expansion to other RLogin instances. It does not establish a complete
  contract for paste, IME, terminal replies, or authentication input.

Do not conflate multiple connections displayed together with multiple views
of one terminal session, or infer that RLogin tab transfer preserves the same
live connection in the manner required by TabTty.

### RLogin comparison requested on 2026-11-13

Rechecked the official FAQ and main documentation. These are reference facts,
not accepted TabTty semantics.

| Mechanism | Connection and view behavior |
| --- | --- |
| Layout splits and split-and-connect | Regions host connections; split-and-connect establishes another connection, even when reusing the same destination. Ordinary regions survive connection closure; split-and-connect regions close with their connection. See FAQ 5.11. |
| Scrollbar split handle | Two vertical views share one connection. The reported terminal grid stays at its pre-split size; view positions are independent. The focused view follows the cursor during input/output. See main documentation 2.3. |
| New display window | A clone shares the connection; switching the active clone changes the reported terminal size to that view. See main documentation 2.3. |

The main documentation does not establish separate tab bars per split region,
or the exact connection lifetime when a shared view closes. Those details
must not be asserted as verified RLogin behavior.

## Round 1 outcomes (Q162-Q165)

Q162-Q164 are accepted on 2026-11-12, including the user's correction to Q163.
Q165's initial answer was withdrawn, reviewed against RLogin, and replaced on
2026-11-13 with same-session views inside a tab.

| Question | Decision | Outcome |
| --- | --- | --- |
| Q162 | Release scope | Include both features in the initial release; finish the specification before a separate implementation request. |
| Q163 | Broadcast reach | Make cross-window target selection a setting, disabled by default. Disabled means the same window only; enabled allows all application windows. The initial recommendation of always-window-local broadcasting is superseded, and unconditional cross-window selection is superseded by the subsequent setting requirement. |
| Q164 | Meaning of the same operation | Include terminal text/committed IME input, terminal keys such as Enter/arrows/Ctrl+C, paste, and remote mouse operations. Exclude local scrolling/selection, connection operations/BREAK, resize, automatic terminal replies, and authentication-dialog input. The initial recommendation to exclude remote mouse operations is superseded. |
| Q165 | Split-display model | Final answer accepted on 2026-11-13 after the RLogin comparison: panes inside one terminal tab show the SAME session. Different sessions use different tabs. This supersedes the initial exclusion of same-session views and resolves the subsequent hold. |

Q164 preserves authentication-dialog isolation. It does not
claim that manually entered terminal text can be identified as a password;
existing manual-input and session-log rules continue to apply.

## Round 2 outcomes (Q166-Q171)

Answers received on 2026-11-13. Preserve partial answers without treating
unanswered parts of a compound recommendation as accepted.

| Question | Decision | Outcome |
| --- | --- | --- |
| Q166 | Broadcast grouping | Accepted: with cross-window selection disabled, each window owns its mode and target set. When enabled, one application-wide mode and target set is shared by all windows. Defer multiple independently named/custom groups. |
| Q167 | Input source versus target | Accepted: a focused terminal tab can originate input independently of its checkbox. Send only to checked targets; the source receives only if checked. With zero targets, send nothing and indicate that state. Settings/form input is never a source. |
| Q168 | Entry and selection lifetime | Amended: remember the selected targets until application exit, including across broadcast stop/start; reset on application exit. Clearing on mode exit is rejected. Q197 later adds resetting selection on scope-setting changes, while unchanged-scope Stop/Start still preserves it. First-use selection and the additional main-menu entry were not settled by this lifetime-specific answer. |
| Q169 | Equivalent operations | Accepted: interpret the same operation independently using each target's encoding, Enter setting, and terminal key modes. Retain each session's local-echo/logging policies rather than copying the source's encoded bytes. |
| Q170 | Tab bars and panes | Replaced: provide window-level groups with their own tab bars AND splitting inside a tab. The one-tab-bar-per-window recommendation is rejected. Q165's final answer resolves the inner panes as same-session views. |
| Q171 | Layout shape | Accepted: repeated side-by-side/top-bottom splits, equal initial sizes, and draggable dividers. Q172 subsequently applies this to BOTH layout levels. |

## Revised split model accepted (Q165/Q172)

After the requested reference comparison, the user selected same-session views
inside a tab and repeated splitting at both levels, explicitly including
top/bottom splits for tab groups. See
[ADR 0002](../adr/0002-two-level-splits-with-one-session-per-tab.md).

| Question | Decision | Accepted outcome |
| --- | --- | --- |
| Q165, final | Session identity inside a split tab | Multiple views of the same session. Different sessions remain in different tabs. |
| Q172 | Which layout levels support repeated splitting | BOTH outer tab groups and inner terminal panes. Tab groups support top/bottom as well as left/right splits; the earlier two-group example is not a limit. |

## Round 3 outcomes (Q173-Q179)

Q173-Q178 are accepted. Q173's hold ended when the user accepted the explained
recommendation. Q179 is accepted with all tabs initially selected and unchanged
Start/Stop operations across first and subsequent uses. Its scope-initialization
edge cases and the optional main-menu entry remain to be completed.

| Question | Decision | Outcome / pending recommendation |
| --- | --- | --- |
| Q173 | Shared terminal grid | Accepted after rationale: derive the shared grid from the whole tab content area as though inner panes were not split. Inner dividers/focus do not resize it; outer group/window resizing does. The focused-pane-size alternative is not selected. |
| Q174 | Contents after a split | Accepted: an outer split creates an empty tab group; an inner split adds a view of the same session, initially at the source view's output position. Splitting alone never opens another connection. |
| Q175 | Removing a split | Accepted: close an inner view without disconnecting; disable inner-split removal for the last view. Removing a group moves its tabs to a neighboring group. Closing a terminal tab remains a separate connection-ending action. |
| Q176 | Settings tabs | Accepted: settings/profile-editor tabs can be placed and moved in any group, but have no internal splits and no broadcast eligibility. |
| Q177 | Layout persistence | Accepted: do not save split layouts; the next application run starts with one group and no inner splits. Preserve window geometry and the existing no-tab/no-connection-restoration policy. |
| Q178 | Target lifetime | Accepted with Q186's later amendment: selection survives disconnect/reconnect and movement within one window, but cross-window movement resets it to CHECKED. Unready tabs receive nothing; readiness admits new input only. Tab closure removes membership; a new tab starts unchecked even for the same profile. |
| Q179 | First-use selection and entry points | Amended: initially check ALL terminal tabs in scope. Later starts restore remembered checks, regardless of the tab invoking Start; invoking Start from a previously unchecked tab does not select it. First and later uses share the same Start/Stop operations. Q180 fixes per-window initialization, Q186 amends moved-tab state, and Q191 accepts the main-menu entry. |

Accepted Q173 rationale: a whole-tab grid keeps remote application layout
and history wrapping stable while the user splits or focuses views to inspect
output. Each smaller view can show only a portion of that grid, potentially
requiring horizontal as well as vertical browsing. A focused-pane grid better
fits a full-screen interactive program within the active view, but changing
focus between differently sized panes resizes the shared session. Follow the
existing resize/reflow/retirement rules for actual grid changes. Neither option
changes the remote session into multiple independent terminal screens.

## Round 4 outcomes (Q180-Q185)

Q180-Q185 are accepted or amended as below. Q181's effective boundary is
the next broadcast Start after an active mode ends. Q184's hold was resolved
on 2026-11-13 when the user accepted the recommendation after its rationale.

| Question | Decision | Outcome |
| --- | --- | --- |
| Q180 | First-use boundary | Amended: initialize all checks on each window's first broadcast start. The once-per-application recommendation is rejected. Q186 resets cross-window moves to checked; detailed global-scope initialization remains to be completed. |
| Q181 | Cross-window setting transition | Clarified: apply the saved scope setting at the next broadcast Start. If broadcasting when saved, finish that mode through Stop before restarting with the new setting; the current mode retains its old scope. If stopped when saved, apply it on the next Start. Neither saving nor the next input operation changes an active mode's scope. |
| Q182 | Remote mouse mapping | Accepted with Q190's release exception: broadcast only gestures classified as remote by the source. Use the same terminal cell coordinates for each target, interpreted using its mouse modes. Skip unsupported event kinds, disabled tracking, and out-of-grid press/motion; do not proportionally remap. At actual release, Q190 clamps release coordinates for matching press recipients. |
| Q183 | Send failure | Accepted: stop broadcast on a selected eligible target's input conversion/admission/send failure, retaining checks and reporting outcomes. A preflight failure rejects that operation for all targets before sending. After sending has begun, stop remaining work where possible; do not roll back or automatically replay delivered/uncertain input. Tabs already unready at target selection remain excluded under Q178. |
| Q184 | Independent view state | Accepted after rationale: independent browsing positions, selection, and search navigation per pane; input/search moves only the operated pane and new output does not reset history browsing. Query/case and temporary zoom remain shared per tab. |
| Q185 | Empty groups and new-tab placement | Accepted with Q192's window-lifetime boundary: keep an empty group until explicit split removal while its window still has tabs. Open new tabs in the active group; external profile activation targets the last active group of the existing last-active-window destination. A window with no tabs closes. |

Q183 provides no atomic cross-connection delivery guarantee. A connection can
fail after successful preflight, and remote receipt/execution cannot be rolled
back. Mouse operations deliberately excluded under Q182 are not send failures.
Paste cancellation boundaries and target-set changes were subsequently settled
in Q188-Q190 and consolidated in the feature contract.

## Accepted Q184 rationale and remaining scope details

Q181's clarification selects next Start, not next input. Keep active scope
separate from the saved next-start preference. Q187 requires old local modes
to end before global Start and makes ordinary Stop application-wide when the
saved preference selects global scope. Q197 later resets selection across scope
changes; its next-Start timing integration is accepted in Q199.

Q184 rationale: existing Q117 makes temporary zoom tab-local and changes the
terminal grid as the font changes. Sharing that zoom across same-session views
keeps one text size and one cell-size basis for the whole-tab grid selected in
Q173. It also keeps the same output at the same visual scale across views.
The cost is that a user cannot enlarge only one history pane. Independent
per-pane zoom is feasible; a possible design would make it presentation-only,
with terminal-grid sizing based on a separate shared baseline. That would
explicitly amend Q117's zoom-driven resize behavior, not merely add another
zoom button. The user accepted shared zoom after this comparison. The
presentation-only per-pane alternative is not selected.

## Round 5 outcomes (Q186-Q191)

Answered on 2026-11-14. Q186/Q187 are amended; Q188/Q189/Q191 are accepted.
Q190's revised release-only clamping recommendation was accepted after the
example and source review. It amends Q182 only for matching button releases.

| Question | Decision | Outcome |
| --- | --- | --- |
| Q186 | Selection after window transfer | Amended: a tab becomes CHECKED when moved to another window. Moving it back resets it to checked again, rather than recovering that window's old unchecked state. This supersedes cross-window selection preservation in Q178 and the Q186 recommendation. Apply the reset at committed transfer; a cancelled/failed move retains its original state. Same-window group moves remain ordinary selection-preserving moves. |
| Q187 | Active modes with a different scope | Amended: existing local broadcasts must stop before global Start. After saving all-window scope, the ordinary Stop Broadcast command stops broadcasts across all windows. Do NOT add a separate Stop All Broadcasts item. The saved next-start scope changes this Stop behavior even while old local modes remain active. |
| Q188 | Broadcast paste interaction | Accepted: one required newline-paste confirmation containing the target list, one immutable payload, shared pacing controls, per-target progress, and Stop for the whole paste. Apply each target's encoding/bracket mode and whole-paste preflight. No-newline paste retains the existing no-confirmation rule. |
| Q189 | Target changes during an operation | Accepted: freeze the target set and temporarily disable checkbox changes for an active paste or mouse drag. Completion/cancellation restores editing. Ending broadcast cancels its in-progress work; ordinary keys use the selection captured for that operation. |
| Q190 | Release outside a target grid | Accepted revised recommendation: retain the gesture when it leaves a target grid; continue skipping out-of-grid press/motion under Q182, but at physical button-up send a release to targets that received the press, clamping that release's current coordinates to each target's grid. Valid re-entry can resume motion before release. Existing reporting-mode/generation checks apply. The earlier recommendation to release immediately at the last valid cell is withdrawn. |
| Q191 | Entry points and status | Accepted: Start/Stop and Select All/Clear All through the terminal-tab context menu and main menu. Show mode/effective scope and ready-selected/total-selected counts in participating windows. Selection commands act within the effective scope; Q187 separately defines the broadened Stop behavior. |

These questions do not promise cross-connection execution synchronization.
Paste/gesture cleanup and group scheduling must preserve per-session ordering,
bounded ownership, generation isolation, and the accepted no-replay policy.

## Accepted Q190 release-coordinate exception

This concerns remote mouse reporting, not local text selection or scrolling.
Assume source A is 100 columns wide, target B is 60 columns wide, both targets
are checked, both support the relevant mouse reports, and row 5 exists in both.

| Physical gesture on A | A's remote application | B's reports if every out-of-grid event is simply skipped |
| --- | --- | --- |
| Press at column 50, row 5 | Press at (50, 5) | Press at (50, 5) |
| Drag to column 59 | Move to (59, 5) | Move to (59, 5) |
| Drag to column 70 | Move to (70, 5) | No event: column 70 does not exist |
| Release at column 70 | Release at (70, 5) | No event: column 70 does not exist |

B then received a press without its corresponding release, which can leave a
remote application treating the button as held. This table illustrates applying
the proposed broadcast filter to every event; it does NOT describe the current
single-tab implementation.

The initial Q190 recommendation released B early at its last actually delivered
valid cell, (59, 5). After the user's question, that recommendation is withdrawn
in favor of waiting for the actual button release and clamping its coordinates.
In the example, B receives release at (60, 5) when the physical button is
released at (70, 5), rather than release at (59, 5) on leaving its grid.

Amendment accepted on 2026-11-14:

- Do not end a target's drag solely because the pointer crosses its grid edge.
- Preserve Q182's exclusion of out-of-grid press/motion reports. A target that
  received the press may resume valid motion if the pointer re-enters before
  release; a target that never received the press does not join mid-gesture.
- At physical button-up, send a release to those matching press recipients,
  clamping the current release coordinate to each target's current grid.
  This is a narrow release exception to Q182's no-clamping rule, not a general
  change to broadcast mouse mapping.
- Keep connection, gesture identity, and reporting-mode generation checks;
  do not send an unsupported release or an old gesture into a changed mode or
  new connection. Cancellation and capture loss require separate cleanup.

### Single-tab source evidence reviewed on 2026-11-14

The normal single-button path in
[TerminalSurface.cs](../../src/TabTty.App/TerminalSurface.cs) captures the
mouse at lines 95-102, reports the current position on mouse-up at lines
105-112, and clamps both motion and release coordinates at lines 482-494.
The move handler at lines 437-445 has no grid-exit termination check. Release
is not suppressed by the same-cell motion coalescing. The core
[TerminalMouse.cs](../../src/TabTty.Core/TerminalMouse.cs) also bounds encoded
coordinates to the session grid.

Thus the current normal single-tab path waits for mouse-up instead of sending
an early release on grid exit. It also clamps out-of-grid motion, unlike Q182's
accepted broadcast-motion exclusion. These are static source findings, not
runtime verification. Capture success/loss and overlapping buttons are not
fully guarded by the current handler; do not infer a guarantee for those
exceptional paths. Mode changes and disconnect cancel gestures separately.

## Round 6 outcomes (Q192-Q196)

Answered on 2026-11-14. Q192/Q193 amend the recommendations; Q194-Q196 are
accepted. The separate technical contract will consolidate these outcomes.

| Question | Decision | Accepted outcome |
| --- | --- | --- |
| Q192 | Window with no remaining tabs | Close a window when all its tabs are gone, including after closing or transferring the last tab. Empty groups do not keep it alive. The proposed empty-window retention is rejected; count terminal and settings/editor tabs together. |
| Q193 | Tab movement during active input | Permit tab moves during sending within the effective broadcast scope: global mode permits cross-window moves; window-local mode permits moves within that window. Moving outside a local scope waits for completion or Stop. Preserve the operation's frozen recipient identities; a cross-window check reset affects the next operation. |
| Q194 | IME across same-session panes | Cancel uncommitted composition without transmission when focus moves to another terminal pane, including one in the same tab. Retain the existing Windows IME treatment for menus/dialogs and normal handling of already committed text. |
| Q195 | Pane scrolling | Provide independent vertical/horizontal scrollbars when needed. Scrollbar gestures are local. Input reveals the terminal cursor in the operated pane; other panes retain their browsing position under Q184. |
| Q196 | Layout size pressure | Reject additional splits that cannot meet a minimum usable region size. If shrinking a window makes an existing layout too large, preserve it and provide scrolling over the layout instead of automatically merging/deleting groups or panes. Numeric minimums are an engineering specification within this policy. |

The older profile draft says closing the last terminal tab closes its window,
while the current persistence contract keeps windows open after automatic or
bulk tab closure. Current code also preserves a window if settings forms remain.
Q192 now resolves these differences: removing the final tab closes the window,
regardless of removal path; remaining settings/editor tabs keep it alive. Window
creation/activation preparation is distinct from removing the final tab, so an
initial window can be constructed before its initial content is attached.

## Round 7 outcomes (Q197-Q198)

Answered on 2026-11-14. Q197 replaces the recommendation; Q198 accepts it.

| Question | Decision | Outcome |
| --- | --- | --- |
| Q197 | Selection across scope-mode changes | Amended: changing between window-local and application-wide broadcast resets every tab's selection and starts fully selected. The proposal to preserve previously used windows' selections across the change is rejected. Ordinary Stop/Start without a scope setting change retains selection under Q168/Q179. |
| Q198 | Destination on group removal | Accepted: offer other groups in the same window, labeled by position/selected tab. Append the removed group's tabs in order to the chosen group, focus it, and collapse the removed branch without disconnecting or changing checks. The last group cannot be removed. |

Q197 states the reset rule, not a change to Q181's next-Start scope timing or
Q189's recipient freeze. The consolidated contract, accepted in Q199, applies reset
at the next Start: global checks all windows; local checks all tabs in each
window when that window next starts. Saving a scope change invalidates every
window's old selection, but does not mutate an active mode's recipients.
Saving the same value does not reset; changing and saving back leaves reset
pending. Q199 confirms these timing details as part of the complete feature
specification.

## Final shared-understanding review (Q199)

Accepted on 2026-11-14. The user confirms the consolidated specification, including the
reset timing above: changed-scope Start resets all selections; unchanged-scope
Stop/Start preserves them; saving a change and then saving back still leaves
a reset pending. Global-to-local resets are applied when each window next
starts, without automatically starting other windows or changing active input.
Q198's explicit group destination, all other accepted feature behavior, and
the contract's labeled engineering guards form the confirmed specification.
The design frontier is closed and this feature is ready for implementation.
No implementation or prototype is authorized by this confirmation; begin only
after a separate explicit user request.

## Design tree and consolidation coverage

| Branch | Accepted decisions and current coverage |
| --- | --- |
| Scope, source/target, selection | Q163-Q169/Q178-Q181/Q186-Q187/Q191/Q197/Q199; the contract incorporates reset to all-selected on scope change with confirmed next-Start timing. |
| Input conversion, paste, failures | Q164/Q169/Q183/Q188-Q189; contract defines preflight, bounded admission, frozen recipients, ordered dispatch, and visible outcomes without rollback/replay. |
| Mouse | Q182/Q189/Q190; contract covers same-cell mapping, per-button press recipients, actual-release clamping, protocol bounds, generation isolation, and capture handling. |
| Moves and closure | Q175/Q178/Q186/Q192-Q193; contract defines scope-preserving moves, ownership handoff, destination-before-source closure, and existing close/log/SFTP boundaries. |
| Two split levels | Q165/Q170-Q177/Q198; ADR fixes one session per tab and whole-tab grid; group removal uses an explicitly selected destination. |
| View state and navigation | Q184/Q185/Q194-Q195; contract defines independent positions/selections/current matches, shared query/zoom, IME cancellation, keyboard/menu and UIA access. |
| Size/resource pressure | Q196; contract supplies delegated initial numeric guards, recursive minima, overflow scrolling, and existing shared resource accounting. |
| Readiness | Consolidated acceptance matrix and cross-document amendments are complete. Q199 confirms shared understanding, including Q197 timing. Specification ready; implementation requires a separate explicit request. |

The [consolidated contract](../specification/broadcast-and-split-views.md) is
the completed feature contract. Q199 accepts the integrated behavior, including
Q197's reset timing and Q198's group-removal destination. Runtime acceptance
cases remain implementation work, not completed verification evidence.

## Existing constraints to preserve or explicitly reconsider

- Preserve one session per terminal tab under final Q165. The tab contains one
  or more views and belongs to one group at a time. Moving existing content
  preserves its live session state and logging.
- Connections require saved profiles. Reconnecting does not replay input.
- Preserve the current terminal-input, local-echo, receive-log, resize, and
  authentication contracts unless a specific amendment is agreed.
- Preserve the existing application language/theme policies and keyboard and
  accessibility requirements.
- Keep macros deferred; broadcast input must not require a macro runtime.
- The earlier Q1-Q161 specification handoff and subsequent implementation
  evidence remain distinct from readiness of this proposed feature extension.

Related documents: [connection profiles](connection-profiles-issue.md),
[authentication](authentication-issue.md),
[future macros](future-macro-design.md),
[session and recording](../specification/session-and-recording.md),
[terminal and encodings](../specification/terminal-and-encodings.md), and
[persistence and windows](../specification/persistence-and-windows.md).

## Repository evidence for subsequent rounds

Read-only inspection on 2026-11-12 found no existing broadcast or split-display
implementation. The current main window displays one selected tab through a
single tab control. Terminal tabs and settings forms are already distinct kinds
of content. This is implementation evidence, not a constraint that determines
the new layout design.

- Existing input operations distinguish text, Enter, and terminal keys. Target
  settings and terminal modes determine their encodings; one source's bytes
  are not necessarily another target's equivalent operation.
- Connected/input-ready is distinct from a connection attempt being active.
  Input is not queued before readiness or replayed after reconnect.
- IME commits once; uncommitted composition is not terminal input. Copy,
  selection, search, and zoom are local operations.
- Paste has immutable text, whole-paste validation, confirmation where
  required, per-session encoding and bracketed-paste handling, and explicit
  cancellation. Additional user input is rejected during a paste.
- Automatic terminal replies and protocol authentication are separate from
  user input. Manual terminal login text follows ordinary input rules.
- Existing resource limits and failure outcomes apply per session. Q178/Q183
  and the consolidated contract define unavailable/slow/failing-member policy;
  simultaneous remote execution cannot be inferred from fan-out.

The GitHub issue list for `TabTty/TabTty` was empty at inspection. This
interview has not published an issue or changed remote state.

### Mouse and layout evidence for subsequent decisions

- Existing remote mouse support covers tracking modes 9/1000/1002/1003 and
  classic/SGR coordinates. Shift chooses local gesture ownership at the start;
  it does not duplicate one gesture into local and remote effects.
- Current mouse emission requires the source session to be connected and have
  remote tracking enabled. Coordinates are terminal cells, with existing
  per-session clamping; Q182/Q190 now define the broadcast exception.
- Mouse mode/generation changes cancel a remote gesture. Paste suppresses
  interactive mouse input. A cross-target gesture must preserve these boundaries
  rather than reusing another session's generation identity.
- Existing keyboard-accessible window-movement menus avoid adding fixed keys
  that consume terminal input. Split-pane navigation has no selected binding.
- The existing terminal grid minimum is two columns by one row; that is not a
  usability minimum for a new pane. Q196 and the consolidated contract add
  initial engineering viewport/group minima without changing grid limits.
- Inspection found an existing outer-window minimum-size discrepancy: the
  persistence contract specifies 480 by 320 DIPs, while WindowPlacement uses
  520 by 320 DIPs. This is an existing code/spec difference, not an accepted
  change to the new split-layout specification; no implementation was changed.
