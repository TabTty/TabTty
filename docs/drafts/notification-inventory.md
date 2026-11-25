# User notifications in the Windows app

This inventory records what a user can see, where it appears, what replaces or clears it, and whether a response is required. It is a static review of the WPF checkout at [f37d1c4](https://github.com/TabTty/TabTty/tree/f37d1c4b95a36433913c7357322111310e62e0a6) on 2026-11-26. Runtime appearance and timing were not exercised.

The [Japanese notification matrix](notification-inventory-ja-jp.md) groups these findings by event and identifies which surfaces accept a response.
The [user notification policy](../specification/notification-policy.md) specifies the target behavior and the changes needed from this baseline.

## Terminal tab and window

| Surface | What appears and for how long |
| --- | --- |
| Bottom status bar | All panes of a terminal tab share one reserved 30 DIP row. Its main text shows Connecting, Connected, Disconnected or Failed. One replaceable detail carries SSH connection progress, input errors, recording pause or resume, and single-target paste counts or cancellation. For example, successful terminal input can clear the recording pause message while recording is still paused. Telnet's unencrypted warning, local echo and font fallback can accompany the text. Truncated indicators expose details on hover or keyboard focus. [Layout][bar] · [Status text][status] · [SSH progress][ssh] · [Recording][record] · [Input completion][input] · [Paste][paste] |
| Recording failures | Startup failure appears as a failed connection. A later write failure adds a detail to the existing connection status and allows explicit recovery. Open issue #144 describes an older build that waited for more terminal traffic. This checkout subscribes to recording-part failures and dispatches an update for the current session. [Issue #144](https://github.com/TabTty/TabTty/issues/144) · [Failure event][failure] · [Startup failure][startup] |
| Other tab indicators | Independent fields show a missing highlight rule set or suspended rule, mouse routing while the host requests tracking, and AI read-only sharing, control or takeover. AI details include the counterpart and any handoff message. Tab-header icons also identify disconnection, failure and AI state when another tab is selected. When enabled, the visual bell flashes a visible terminal's status bar for 150 ms. [Highlight][highlight] · [Mouse][mouse] · [AI state][ai] · [Header icons][icons] · [Bell event][bellEvent] · [Bell timer][bell] |
| Broadcast input | Window-level text includes scope, ready/checked target counts and progress or cleanup details. It occupies the selected terminal's bar, or one visible terminal's bar in another group. With no visible terminal, the window shows it over the lower-left corner. It can coexist with tab-local state. [Broadcast text][broadcast] · [Placement][placement] · [Window overlay][fallback] |

## Overlays and the reconnect window

- When the active pane starts sending mouse input to the host, a lower-right hint explains how to regain local mouse use. It ignores pointer input, expires after six seconds and can disappear sooner when host routing or presentation ends. The routing indicator stays in the bottom bar while tracking is requested. [Hint and routing][mouse]
- During AI sharing, **End AI sharing** floats at the upper right. **Take control from AI** appears there while the AI controls input. Search opens a separate upper-left overlay with its own feedback and close action. It sits below the AI actions when both are visible. [AI actions][ai] · [Search positioning and dismissal][search]
- A successful reconnect to a changed destination opens a separate topmost window with the tab name, previous destination, new destination and **OK**. It opens without taking focus, including for a background tab. Reception continues. The notice follows the tab between windows and closes when the session ends. [Reconnect notice][reconnect] · [Window flags][flags] · [Window ownership](../specification/persistence-and-windows.md)

## Dialogs, editors and transfers

- **Acknowledgement.** Registering file associations and explicitly unlocking an existing vault show OK-only modals on success. File activation errors can open a dialog before the invoking process has a terminal window. When a saved SSH credential is missing or has the wrong type and the connection attempt fails for that reason, the failed tab shows the reason. A visible owner window also presents it in a vault error dialog. **SSH messages** and **Connection details** open read-only dialogs on request. [File association][association] · [Vault unlock][unlock] · [Activation error][activation] · [Credential failure][startup] · [SSH messages][messages] · [Connection details][details]
- **Decision or input.** Host-key review, password challenges, paste review, AI consent and closing active connections ask the user to decide before work proceeds. Profile trust and changed recording-drive mappings use a modeless prompt that waits only for the affected operation. In this checkout a macro also has a top panel for run state, recovery actions and plaintext-password notices, and can open a modeless prompt that waits for its run. Macros remain outside initial-release scope. [SSH interaction][auth] · [Paste review][review] · [AI consent][consent] · [Closure][closure] · [Profile prompts][trust] · [Macro panel][macro] · [Macro dialog][macroDialog] · [Release scope](future-macro-design.md)
- **Forms.** The profile manager and editor, vault management tab, standalone vault editor and credential edit dialog reserve a bottom line even when empty. Profile save/recheck and credential edit saves show progress after 500 ms of work. The credential editor owns its save feedback while the parent stays quiet. Profile validation appears inline. An unresolved profile save has a footer message and recovery button. Settings also uses inline file-operation progress. Issue #149 is still open. [Form status and timing][form] · [Editor footer][footer] · [Validation][validation] · [Settings][settings] · [Profile and vault contract](../specification/persistence-and-windows.md) · [Issue #149](https://github.com/TabTty/TabTty/issues/149)
- **File transfer.** The transfer window's footer shows operation status, byte progress and per-item results. Results remain available after disconnect while the tab and window survive. Overwrite, skip, cancel and permanent-delete decisions use dialogs. [Transfer footer][transfer] · [Transfer choices][choices] · [Result lifetime](../specification/file-transfer.md#transfer-window-and-operation-ownership)

### Issue #147 and the fixed status bar

Issue #147 addressed changes in terminal size when status appeared, including AI handoff details. Its original placement covered connection status, paste progress, highlight warnings, AI state and broadcast input. On 2026-11-22, commit `da8a2fb` implemented the compact top overlays and referenced #147. Later that day, commit `cdcf4a9` revised the specification and implementation. Connection and AI state occupy a fixed 30 DIP bottom row. The AI takeover and end-sharing buttons float at the upper right. Hovering or focusing the AI state reveals the handoff message. Regression tests check that sharing, showing the takeover button and receiving a handoff message preserve the terminal area. As of 2026-11-26 the issue remains open with its original wording. [Issue #147](https://github.com/TabTty/TabTty/issues/147) · [Original implementation](https://github.com/TabTty/TabTty/commit/da8a2fb22559903900a7e81084425a0d1da1dd2e) · [Revised design and tests](https://github.com/TabTty/TabTty/commit/cdcf4a9e7943dda341d915122fff918cd729ff82) · [AI collaboration](../specification/ai-collaboration.md) · [Split views](../specification/broadcast-and-split-views.md)

[bar]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L160-L195
[status]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L956-L1014
[ssh]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L307-L323
[record]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L853-L878
[input]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L1603-L1609
[paste]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L1473-L1507
[failure]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L397-L439
[startup]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L459-L490
[highlight]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L1035-L1048
[mouse]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.Mouse.cs#L13-L96
[ai]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.Ai.cs#L35-L110
[icons]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MainWindow.cs#L1144-L1164
[bell]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L105-L114
[bellEvent]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L379-L394
[broadcast]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/BroadcastInput.cs#L165-L182
[placement]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MainWindow.Splits.cs#L351-L363
[fallback]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MainWindow.xaml#L184-L189
[search]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L1144-L1173
[reconnect]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L1016-L1032
[flags]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/ThemedMessageBox.cs#L30-L43
[unlock]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/VaultFileController.cs#L48-L61
[association]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MainWindow.cs#L839-L847
[activation]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/App.cs#L152-L164
[messages]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L943-L954
[details]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L812-L822
[auth]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L626-L685
[review]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.cs#L1537-L1580
[consent]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.Ai.cs#L157-L185
[closure]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MainWindow.cs#L957-L981
[trust]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/ProfileCatalog.Trust.cs#L259-L298
[macro]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/TerminalTab.Macro.cs#L22-L111
[macroDialog]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MacroDialogWindow.cs#L93-L109
[form]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/FormView.cs#L66-L141
[footer]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/FormView.cs#L719-L744
[validation]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/ProfileEditor.cs#L191-L225
[settings]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/MainWindow.cs#L702-L706
[transfer]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/FileTransferWindow.cs#L65-L79
[choices]: https://github.com/TabTty/TabTty/blob/f37d1c4/src/TabTty.App/FileTransferWindow.cs#L462-L486
