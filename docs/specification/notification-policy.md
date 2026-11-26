# User notification policy

This policy governs notifications in the Windows app. The inventory records the starting point at `f37d1c4`. [日本語版](notification-policy-ja-jp.md) · [Notification inventory before migration](../drafts/notification-inventory.md)

## Choosing a surface

| What the user needs | Surface | Lifetime and example |
| --- | --- | --- |
| Check a current condition or progress | Left side of the affected view's bar, or right side of each affected main window's bar | Reflect the actual state until it changes. Connection, recording, paste progress and AI control can change independently. All panes in a terminal tab share its fixed 30 DIP bottom row. |
| Act on an ongoing activity at a time of their choosing | Floating notice with action buttons | Keep available actions visible while applicable. Examples include taking control from AI, ending sharing and pausing or stopping a macro run. |
| Understand an unexpected change in interaction | Timed floating notice | Explain the change and how to regain ordinary behavior. When mouse input switches to the host, give the existing six-second hint. Start its visible time when the user can see it and discard it if the routing changes back. |
| Learn about an important event after it happened | Floating notice with **OK** for a specific view, or a report accessible from the right side of the bar for a window or the application | Keep it until acknowledged. View-owned examples include a later recording write failure, uncertain delivery of input to one terminal and a successful reconnect to a different destination. **OK** records that the user saw the report. Recovery or retry requires its own action. |
| Give an answer before an operation can proceed | Decision or input dialog | Identify the target and wait for a choice or input for that operation. Examples include host-key trust, granting AI permission, paste review, an SFTP overwrite and a macro question. Preserve cancellation and safe defaults. |

Ordinary success should be visible in the resulting state, for example **Vault: Unlocked**. Preserve information the user needs later, such as an archive path. Search, field validation, user-opened detail views and per-file results are parts of their workflows. Tab-header indicators identify which tab needs attention. A close-time report required while its owner still exists, and a startup error before a suitable owner exists, can use a dialog. Preserve the SFTP close report's counts, paths and full detail. [File-operation close reporting](file-operations.md)

## Status bar scope

Choose the side of the bar by asking what the information describes. Keep each indicator independently readable as other indicators change. [Implementation plan #247](https://github.com/TabTty/TabTty/issues/247)

| Scope | Placement | Information |
| --- | --- | --- |
| One tab, pane, editor or operation owned by that view | Left side of the owning view's bar | Connection and input state, local echo, mouse destination, AI sharing or control, highlight warnings, macro state and result, and recording state and part number. A management view's status and delayed file-operation progress also belong on its left. |
| A window or multiple windows | Right side, once per affected main window | Broadcast scope, ready and selected target counts, the scope selected for the next start, no selection, no ready targets and broadcast cleanup. Window-scoped broadcast appears in its participating window. Cross-window broadcast appears once in each participating window. |
| The application's selected vault | Right side, always, once in each main window | Show **Vault: Unlocked** while the selected vault is unlocked in this application run. Show **Vault: Not unlocked** otherwise, including when the vault is uninitialized. Read the shared state at startup and after each unlock, relock or selection change. Displaying it requires neither initialization nor a password prompt. A separate vault file opened for editing has its own local state. |

Keep the window's right side available with a terminal, a management or settings view, or no tab visible. Switching or moving tabs and splitting the window preserve a single copy of each applicable wider state in that window. Use floating notices only for a specific affected view. Window and application status and results are presented through the right side, including in a main window with no terminal.

A paste belongs to its source tab. Show its progress on the left there, including progress for each target of a broadcast paste. The right side continues to show the current broadcast mode and target counts. Show broadcast outcomes, including a cancelled remote mouse gesture, on the right in the affected windows. If delivery fails or its extent is uncertain, retain a report with each target and what is known about delivery to it. Keep it accessible from the right until acknowledged, even after the mode ends. Acknowledgement in one window clears that report across its presentations. Arrival leaves keyboard focus and active tabs in place.

For a menu command, identify the affected tab, file, view, window or application state when the command starts. Use that scope for progress and results even when the user changes tabs before completion. A file save belongs to its editor. A main-menu archive or restore of the selected vault belongs to the wider operation. Make the outcome and any archive path available from the right. An operation on the file opened in a particular vault editor reports there. Preserve dialogs for genuine decisions and input, such as approving an overwrite or entering a master password.

## Stacking and ownership

- Use one compact floating presentation at the upper right of the owning content area. A terminal tab owns one stack across its split panes, including for a macro run before connection. Its notices move with the tab. An editor or transfer window owns its own notices.
- Put the **oldest item at the top**. Append each distinct new item immediately below the last. Update an ongoing activity or a repeated report of the same incident in place. When an item ends or is acknowledged, close its gap and keep the others in order. A change in severity or a progress update does not move an item.
- Items must not cover each other, terminal tabs, split dividers or search controls. Changes to the stack preserve terminal dimensions, row and column counts, scroll position and input focus. Passive text lets pointer input through. Buttons are reachable by keyboard and UI Automation. Localize labels in English and Japanese and use the active application theme.
- When all items cannot fit, keep active intervention controls such as **Take control**, **End sharing** and **Stop macro** directly reachable. Show a visible count and an expandable, scrollable list for the remaining notices, in the same chronological order. Keep every unacknowledged report accessible. Count a hint's display time only while its text is visible.
- Associate actions with the specific tab, connection attempt, run or file operation that produced them. Retire an action when its target ends. If an incident recovers before acknowledgement, show its outcome in the existing item. Its status bar reflects the current condition.

## Inactive tabs

For attention purposes, a tab is inactive when it is not the user's current tab, including a visible tab in another split group. Focusing a control within the current tab keeps that tab active. Decide whether to announce an event when it occurs. Switching tabs or moving a tab carries its pending notices forward without issuing another alert. Work in a background window must also leave the foreground user's input undisturbed.

| Event in an inactive tab | What happens immediately | When the user responds |
| --- | --- | --- |
| Ordinary progress, an optional control or a timed hint | Update the tab's state. Show a still-relevant hint when its tab becomes active. | Selecting the tab exposes its current state and available actions. |
| Important event requiring acknowledgement | Mark the tab and also show a **modeless notification window** in its owning window's context. Identify the tab and event and offer **Show tab** and **OK**. Keep the tab's floating notice available. | **Show tab** navigates to the source. **OK** in either place acknowledges the same report and clears its unread indication. The underlying condition remains visible while true. |
| Decision or input required | Present a dialog that identifies the tab, destination or operation without waiting for the user to find the tab. Mark it as awaiting a response. | The user deliberately activates the dialog to answer, or cancels under the existing operation rules. Reopening from the tab leads to the same request. |

Arrival must not activate a tab or window, redirect text typed for another terminal, or treat a pending Enter as approval. Background reports for one owning window may share one notice window with oldest-first entries. A single request has one pending answer or acknowledgement across its surfaces. An answered, cancelled or expired request cannot be applied again after a tab move or reconnect. Ongoing terminal input and broadcast continue during post-event reports. A decision holds only the work that depends on its answer wherever the existing operation allows.

## Adding a notification

A request from a minimized window leaves that window minimized. Its decision dialog can be reached from the taskbar. Choosing **Show tab** in a background report restores the source window and selects the tab.

Choose the owner before choosing the control. A terminal feature uses its `TerminalTab`, including across pane splits and moves between windows. A management operation uses its issuing `FormView`. For a deferred command from a main-window menu, capture its actual target and scope when the command starts. Use `MainWindow.CaptureReport()` when the current view owns the report.

| At the call site | How to use the shared framework |
| --- | --- |
| A condition or progress changes | Derive the status from the underlying operation. Use `FormView.BeginProgress(resourceKey)` for delayed form progress and dispose its returned handle when the work finishes. |
| An interaction mode needs an explanation | Call `Notices.ShowHint(key, localizedText, visibleDuration)`. Remove the key if the explanation stops applying. The stack counts visible time. |
| An ongoing activity offers actions | Call `Notices.ShowContent(key, content, keepVisible: true, compactActions: buttons)`. Update under the same key and call `Remove(key)` when the activity ends. Bind every button to the live run or session. Provide compact controls when the full card contains detail that can overflow. |
| An important event has already occurred in a view | Call `Reports.Report(incidentKey, localizedText)`. Use a key that identifies the occurrence, such as the connection generation and recording part. To update its outcome only while it awaits acknowledgement, call `Reports.UpdateIfPending`. The framework coordinates its floating card, background window and unread indication. |
| A window-wide or application-wide operation has a result | Make the result and its retained details accessible from the right side in the applicable windows. Preserve one acknowledgement for each report. |
| An operation needs an answer | Create an operation-specific dialog and await `TerminalTab.PresentDecisionAsync(dialog, token)`. Bind the request to the operation's lifetime, keep the safe default, and validate the target before applying the answer. A shared vault unlock tracks all waiting tabs through `TabDecision`. |

For example, a recording write failure changes the current recording state and publishes one incident. Explicit recovery updates the pending incident with the new part number. Pressing **OK** resolves that incident across its surfaces. Future features can follow the same pattern without managing window focus, duplicate reports or stack coordinates. [NoticeStack](../../src/TabTty.App/NoticeStack.cs) · [ViewNotifications](../../src/TabTty.App/ViewNotifications.cs) · [Tab decisions](../../src/TabTty.App/TerminalTab.Decisions.cs) · [FormView](../../src/TabTty.App/FormView.cs)

## Migration from the previous implementation

This table records the earlier migration from `f37d1c4`. The status bar scope above specifies the subsequent layout change.

| Area | Behavior after migration from `f37d1c4` |
| --- | --- |
| Shared floating layout | The earlier migration placed mouse hints, AI controls and the broadcast fallback in the upper-right stack while keeping search available. The status bar scope above supersedes that broadcast fallback. [Mouse routing](../../src/TabTty.App/TerminalTab.Mouse.cs) · [AI controls](../../src/TabTty.App/TerminalTab.Ai.cs) · [Historical broadcast fallback](../../src/TabTty.App/MainWindow.xaml) |
| Status and recording | The status bar shows recording state independently of terminal input. Failure to open a part or make its first write still fails the connection. A later write failure keeps the terminal usable, marks recording stopped and raises one persistent report. [Terminal tab](../../src/TabTty.App/TerminalTab.cs) · [Recording contract](session-and-recording.md) |
| Reconnect, credential and input failures | A changed destination produces one report with both endpoints. An inactive tab also announces it in a modeless window. A fatal saved-credential failure shows the failed state and one reason. AI and broadcast failures report the target and the known extent of delivery. [Terminal tab](../../src/TabTty.App/TerminalTab.cs) · [Broadcast input](../../src/TabTty.App/BroadcastInput.cs) |
| AI and macro controls | AI actions update in place. A macro has its own run-bound intervention controls. The end reason and source position remain available in the status bar after completion. Macro questions use an answering dialog. [AI collaboration](ai-collaboration.md) · [Macro controls](../../src/TabTty.App/TerminalTab.Macro.cs) |
| Background delivery | Unread and awaiting-answer indicators identify the tab. Reports and decisions follow it between windows and share one acknowledgement or answer. Background arrival preserves the current keyboard target. [Tab headers](../../src/TabTty.App/MainWindow.cs) · [Reconnect tests](../../tests/TabTty.App.Tests/BroadcastAndSplitViews.cs) |
| Management views | Forms show delayed progress in the owning status bar after 500 ms. Routine success is visible in the resulting state. Archive destinations, partial results, field errors, drafts and recovery actions remain readable. Important file failures wait for acknowledgement. [Form status](../../src/TabTty.App/FormView.cs) · [Vault operations](../../src/TabTty.App/VaultFileController.cs) · [Management views](persistence-and-windows.md) |
| SFTP and closure | The transfer window retains per-item results and its pre-close report. Overwrite and deletion decisions use dialogs. An early startup error can use an ownerless dialog. [Transfer window](../../src/TabTty.App/FileTransferWindow.cs) · [Startup](../../src/TabTty.App/App.cs) |

## Acceptance checks

The reconnect tests cover input continuity and moves between windows. The floating layout checks exercise simultaneous notices and search. [Reconnect and input](../../tests/TabTty.App.Tests/BroadcastAndSplitViews.cs) · [AI and search](../../tests/TabTty.App.Tests/AiCollaborationTests.cs)

1. Start AI sharing, switch mouse routing to the host, then cause a later recording write failure in the same tab. From the top, see AI actions, the hint and the warning. A repeated event for the same failed part stays in its original position. When the hint expires, the warning moves up. After **OK**, recording still shows stopped and the AI actions remain available.
2. Repeat in a narrow split with search open, long Japanese text and sufficient notices to overflow. Check keyboard access, both themes, terminal selection, mouse reporting and unchanged grid and scroll position. Older reports remain readable and AI takeover stays available.
3. While typing in tab A, cause a recording failure in inactive tab B. The notice window identifies B and leaves input in A. Move B to a different window and acknowledge from either surface. Only B's report clears. Repeat with a host-key or macro question. Enter typed in A never answers B.
4. Reconnect to the same and then to a changed destination. Only the latter raises the report. Verify ordinary terminal and broadcast input while it is open. Confirm that a genuine trust or overwrite question still waits for a decision and that dismissing a report starts no retry.
5. Open two main windows, split one and switch the other to a management view. Check the selected vault indicator once on the right of both windows at startup, after a first-use unlock in either window, after relocking and after changing the selected vault. The management view's own progress is readable on its left at the same time. Check the right side in a window displaying no terminal.
6. Exercise AI sharing, host mouse tracking, a macro, highlight suspension and recording in one terminal tab. Each applicable condition is on the left of that tab and follows it on a move. Start window-scoped and then cross-window broadcast. Check scope and target counts once on the right of each participating window, while per-target paste progress appears only on the source tab's left.
7. Cancel a broadcast mouse gesture and check the outcome on the right. Cause a broadcast delivery failure and end the mode. Open the report from the right, read each target's outcome and acknowledge it once across its presentations. Verify that arrival leaves input in the active tab. Start a selected-vault archive or restore from the main menu, switch tabs, then confirm that the result remains accessible from the right. Repeat with a file operation in a vault editor and check its result in the owning view.
