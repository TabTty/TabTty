using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;
using ShapePath = System.Windows.Shapes.Path;

internal static partial class Program
{
    private static void SendText(TerminalSurface pane, string input) => pane.RaiseEvent(new TextCompositionEventArgs(
        Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, pane, input))
        { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });

    private static async Task CheckBroadcastReconnectAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        string path = Path.Combine(directory, "broadcast-reconnect.ttty");
        await ConnectionProfile.CreateTelnet("Same name", "127.0.0.1", ((IPEndPoint)first.LocalEndpoint).Port)
            .SaveNewAsync(path, deadline.Token);
        var tab = new TerminalTab(path);
        bool autoClose = app.AutoCloseDisconnectedTabs;
        await SetAutoClose(false);
        bool originalHasForeground = true;
        var window = new MainWindow(app, () => originalHasForeground);
        window.Show();
        window.AddTerminalTab(tab);
        try
        {
            await CheckConnection(first, selected: true);
            await CheckConnection(first, selected: true);
            var profile = await ConnectionProfile.LoadAsync(path, deadline.Token);
            var edit = System.Text.Json.Nodes.JsonNode.Parse(profile.ToEditorJson())!;
            edit["connection"]!["port"] = ((IPEndPoint)second.LocalEndpoint).Port;
            await profile.Edit(edit.ToJsonString()).SaveEditAsync(deadline.Token);
            await CheckConnection(second, selected: true, previous: first);
            var reverse = System.Text.Json.Nodes.JsonNode.Parse((await ConnectionProfile.LoadAsync(path, deadline.Token)).ToEditorJson())!;
            reverse["connection"]!["port"] = ((IPEndPoint)first.LocalEndpoint).Port;
            var saved = await ConnectionProfile.LoadAsync(path, deadline.Token);
            await saved.Edit(reverse.ToJsonString()).SaveEditAsync(deadline.Token);
            await CheckConnection(first, selected: true, previous: second, background: true);
            tab.BroadcastChecked = false;
            await CheckConnection(first, selected: false);
        }
        finally
        {
            tab.Disconnect();
            await tab.Completion;
            window.Close();
            await SetAutoClose(autoClose);
        }

        Task SetAutoClose(bool enabled) => app.SaveSettingsAsync(app.SettingsRevision, new(AutoClose: enabled));

        async Task CheckConnection(TcpListener endpoint, bool selected, TcpListener? previous = null, bool background = false)
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Connected(string _) => ready.TrySetResult(tab.BroadcastChecked);
            tab.Connected += Connected;
            MainWindow? destination = null;
            if (background)
            {
                originalHasForeground = false;
                destination = new MainWindow(app, () => !originalHasForeground);
                destination.Show();
                destination.Activate();
            }
            else window.Activate();
            try
            {
                tab.Start();
                using var peer = await endpoint.AcceptTcpClientAsync(deadline.Token);
                await peer.GetStream().ReadExactlyAsync(new byte[15], deadline.Token);
                if (await ready.Task.WaitAsync(deadline.Token) != selected)
                    throw new InvalidOperationException("Reconnecting changed the user's broadcast selection.");
                await window.Dispatcher.InvokeAsync(async () =>
                {
                    var notice = window.OwnedWindows.OfType<Window>().SingleOrDefault(dialog => dialog.IsVisible);
                    if (previous is null)
                    {
                        if (notice is not null || tab.Reports.HasReports)
                            throw new InvalidOperationException("An unchanged destination raised a report.");
                        return;
                    }
                    string body = string.Format(App.Text("ReconnectDestinationDetails"), Path.GetFileNameWithoutExtension(path),
                        string.Format(App.Text("AiTelnetTarget"), previous.LocalEndpoint),
                        string.Format(App.Text("AiTelnetTarget"), endpoint.LocalEndpoint));
                    bool shown = FindControls<TextBlock>(tab.Notices).Any(label => label.Text == body);
                    if (!shown || tab.Reports.HasUnread != background || (notice is not null) != background)
                        throw new InvalidOperationException("Changed destination was not routed to its active or background surfaces.");
                    if (notice is not null && (notice.ShowActivated || notice.IsActive || !window.IsEnabled ||
                            !FindControls<DialogText>(notice).Any(value => value.Text == previous.LocalEndpoint.ToString() &&
                                System.Windows.Automation.AutomationProperties.GetLabeledBy(value) is TextBlock label &&
                                label.Text == string.Format(App.Text("ReconnectPreviousDestination"), App.Text("AiTelnetTargetLabel")) + ":") ||
                            !FindControls<DialogText>(notice).Any(value => value.Text == endpoint.LocalEndpoint.ToString() &&
                                System.Windows.Automation.AutomationProperties.GetLabeledBy(value) is TextBlock label &&
                                label.Text == string.Format(App.Text("ReconnectCurrentDestination"), App.Text("AiTelnetTargetLabel")) + ":")))
                        throw new InvalidOperationException("A background report lost its target or disturbed the active window.");
                    var pane = FindControls<TerminalSurface>(tab).Single();
                    SendText(pane, "a");
                    var input = new byte[2];
                    await peer.GetStream().ReadExactlyAsync(input.AsMemory(0, 1), deadline.Token);
                    app.Broadcast.Start(window);
                    SendText(pane, "b");
                    await peer.GetStream().ReadExactlyAsync(input.AsMemory(1), deadline.Token);
                    app.Broadcast.Stop(window);
                    if (!input.AsSpan().SequenceEqual("ab"u8) || !tab.Reports.HasReports)
                        throw new InvalidOperationException("The report interrupted normal or broadcast input.");
                    if (notice is not null)
                    {
                        var move = FindMenu(window, "WindowMenu", "MoveTab");
                        OpenSubmenu(move);
                        Click(move.Items.OfType<MenuItem>().Last());
                        if (!window.IsClosing || !destination!.TerminalTabs.Contains(tab) || notice.Owner != destination || !notice.IsVisible)
                            throw new InvalidOperationException("The unread report did not follow its tab.");
                        window = destination;
                        FindControls<Button>(notice).Single(button => Equals(button.Content, App.Text("NoticeShowTab")))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (!tab.Reports.HasUnread) throw new InvalidOperationException("Navigation acknowledged an incident.");
                    }
                    FindControls<Button>(tab.Notices).Single(button => Equals(button.Content, App.Text("OK")))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (notice?.IsVisible == true || tab.Reports.HasReports || !tab.BroadcastChecked)
                        throw new InvalidOperationException("Acknowledgement did not resolve both surfaces consistently.");
                }, DispatcherPriority.ApplicationIdle, deadline.Token).Task.Unwrap();
                tab.Disconnect();
                await tab.Completion.WaitAsync(deadline.Token);
            }
            finally { tab.Connected -= Connected; }
        }
    }

    private static async Task BroadcastAndSplitViews(App app, string directory)
    {
        await CheckWindowReportScopeAsync(app);
        await CheckBroadcastPasteCleanupAsync(app, directory);
        await CheckBroadcastPasteFailurePrefixAsync(app, directory);
        await CheckBroadcastPasteFailurePrefixAsync(app, directory, bracketed: true);
        await CheckBroadcastDeliveryFailureAsync(app, directory);
        await CheckMouseRoutingAsync(app, directory);
        await CheckBroadcastReconnectAsync(app, directory);
        await CheckRecordingNoticesAsync(app, directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = new MainWindow(app);
        var other = new MainWindow(app);
        var terminals = new List<TerminalTab>();
        bool originalScope = app.BroadcastAcrossWindows;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new TaskCompletionSource<TerminalPaste?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            host.Show();
            other.Show();
            var (source, firstPeer) = await Connect(host, "UTF-8", "utf-8", (_, _, _) =>
            {
                captureStarted.SetResult();
                return clipboard.Task;
            });
            using var first = firstPeer;
            if (!FindControls<TextBlock>(source).Any(label => label.IsVisible &&
                label.Text.Contains(App.Text("TelnetUnencrypted"), StringComparison.Ordinal)))
                throw new InvalidOperationException("The connected Telnet tab omitted its clear-text transport status.");
            var (target, secondPeer) = await Connect(host, "CP932", "cp932");
            using var second = secondPeer;
            var (outside, thirdPeer) = await Connect(other, "Outside", "utf-8");
            using var third = thirdPeer;

            var initial = (TabControl)host.FindName("tabs");
            host.UpdateLayout();
            var side = SplitButton(initial, "splitGroupsSideButton", "SplitGroupsSide");
            var stack = SplitButton(initial, "splitGroupsStackButton", "SplitGroupsStack");
            var unsplit = SplitButton(initial, "removeTabGroupButton", "RemoveTabGroup");
            if (unsplit.IsEnabled || stack.TranslatePoint(new Point(), initial).X >= side.TranslatePoint(new Point(), initial).X ||
                side.TranslatePoint(new Point(), initial).X >= unsplit.TranslatePoint(new Point(), initial).X)
                throw new InvalidOperationException("The initial tab bar did not show the split controls in order.");
            ClickButton(side);
            host.UpdateLayout();
            var right = FindControls<TabControl>(host).Single(group => group != initial);
            if (right.TranslatePoint(new Point(), host).X <= initial.TranslatePoint(new Point(), host).X || !unsplit.IsEnabled)
                throw new InvalidOperationException("The side-by-side button did not create a group to the right.");
            ClickButton(SplitButton(right, "splitGroupsStackButton", "SplitGroupsStack"));
            host.UpdateLayout();
            var bottom = FindControls<TabControl>(host).Single(group => group != initial && group != right);
            if (bottom.TranslatePoint(new Point(), host).Y <= right.TranslatePoint(new Point(), host).Y)
                throw new InvalidOperationException("The top-and-bottom button did not split the group that owns it.");
            var emptyRemove = SplitButton(bottom, "removeTabGroupButton", "RemoveTabGroup");
            ClickButton(emptyRemove);
            host.UpdateLayout();
            if (emptyRemove.ContextMenu is not null || FindControls<TabControl>(host).Count() != 2)
                throw new InvalidOperationException("The remove button did not close an empty group directly.");

            ClickButton(SplitButton(right, "splitGroupsStackButton", "SplitGroupsStack"));
            host.UpdateLayout();
            bottom = FindControls<TabControl>(host).Single(group => group != initial && group != right);
            var groups = FindMenu(host, "WindowMenu", "TabGroups");
            OpenSubmenu(groups);
            Click(Child(groups, "FocusTabGroup").Items.OfType<MenuItem>().First());
            var tabToMerge = (TabItem)initial.SelectedItem;
            var moveToSplitGroup = FindMenu(host, "WindowMenu", "MoveTab");
            OpenSubmenu(moveToSplitGroup);
            string bottomName = System.Windows.Automation.AutomationProperties.GetName(bottom);
            Click(moveToSplitGroup.Items.OfType<MenuItem>().Single(item =>
                System.Windows.Automation.AutomationProperties.GetName(item) == bottomName));
            var removeButton = SplitButton(bottom, "removeTabGroupButton", "RemoveTabGroup");
            ClickButton(removeButton);
            var destinations = removeButton.ContextMenu;
            if (destinations is not { IsOpen: true } || destinations.Items.Count != 2)
                throw new InvalidOperationException("Removing a group with tabs did not offer both destinations.");
            try { Click(destinations.Items.OfType<MenuItem>().Last()); }
            finally { destinations.IsOpen = false; }
            host.UpdateLayout();
            if (!right.Items.Contains(tabToMerge))
                throw new InvalidOperationException("Group removal did not move the tab to the chosen destination.");
            var singleDestination = SplitButton(right, "removeTabGroupButton", "RemoveTabGroup");
            ClickButton(singleDestination);
            host.UpdateLayout();
            if (singleDestination.ContextMenu is not null || FindControls<TabControl>(host).Count() != 1 ||
                !ReferenceEquals(initial.Items[^1], tabToMerge))
                throw new InvalidOperationException("The remove button did not move the tab directly to the sole remaining group.");

            ClickButton(side);
            host.UpdateLayout();
            right = FindControls<TabControl>(host).Single(group => group != initial);
            ClickButton(SplitButton(right, "splitGroupsStackButton", "SplitGroupsStack"));
            host.UpdateLayout();
            OpenSubmenu(groups);
            var remove = Child(groups, "RemoveTabGroup");
            if (remove.HasItems)
                throw new InvalidOperationException("The group menu offered destinations for an empty group.");
            Click(remove);
            host.UpdateLayout();
            if (FindControls<TabControl>(host).Count() != 2)
                throw new InvalidOperationException("The group menu did not close an empty group directly.");
            OpenSubmenu(groups);
            Click(Child(groups, "FocusTabGroup").Items.OfType<MenuItem>().Last());
            OpenSubmenu(groups);
            remove = Child(groups, "RemoveTabGroup");
            Click(remove);
            host.UpdateLayout();
            if (FindControls<TabControl>(host).Count() != 1 || host.TerminalTabs.Count() != 2 || unsplit.IsEnabled)
                throw new InvalidOperationException("Group removal changed the window's sessions.");

            var panes = FindMenu(host, "WindowMenu", "TerminalPanes");
            OpenSubmenu(panes);
            Click(Child(panes, "SplitPanesSide"));
            host.UpdateLayout();
            var views = FindControls<TerminalSurface>(target).ToArray();
            if (views.Length != 2 || target.InputSession is null)
                throw new InvalidOperationException("The terminal split did not keep two views on one connection.");
            var composition = new DeferredComposition(views[1]);
            views[1].RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            OpenSubmenu(panes);
            Click(Child(panes, "FocusPane").Items.OfType<MenuItem>().First());
            views[1].RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            var lateResult = new DeferredComposition(views[0], TextCompositionAutoComplete.On);
            views[0].RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, lateResult)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            views[0].RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, lateResult)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            if (second.Available != 0) throw new InvalidOperationException("A discarded composition was sent after pane focus moved.");

            var broadcast = FindMenu(host, "ConnectionMenu", "BroadcastInput");
            host.UpdateLayout();
            Size paneSize = target.TerminalArea.RenderSize;
            Click(Child(broadcast, "BroadcastStart"));
            host.UpdateLayout();
            var broadcastStatus = (TextBlock)host.FindName("broadcastStatus");
            if (target.TerminalArea.RenderSize != paneSize || !broadcastStatus.IsVisible ||
                broadcastStatus.Text != app.Broadcast.Status(host) || FindControls<TextBlock>(target).Contains(broadcastStatus))
                throw new InvalidOperationException("Broadcast status did not stay in the window's fixed row.");
            var otherBroadcast = FindMenu(other, "ConnectionMenu", "BroadcastInput");
            if (!source.BroadcastChecked || !target.BroadcastChecked || !outside.BroadcastChecked || app.Broadcast.IsActive(other))
                throw new InvalidOperationException("New tabs must be checked by default and respect the active window scope.");
            var tabBar = (TabControl)host.FindName("tabs");
            var headers = tabBar.Items.OfType<TabItem>().ToArray();
            var sourceCheck = ((DockPanel)headers[0].Header).Children.OfType<CheckBox>().Single();
            string destination = string.Format(App.Text("AiTelnetTarget"), $"127.0.0.1:{port}");
            if (sourceCheck.ToolTip as string != $"{destination} — UTF-8" ||
                !System.Windows.Automation.AutomationProperties.GetName(sourceCheck).Contains(destination))
                throw new InvalidOperationException("The broadcast checkbox did not identify its connection.");
            sourceCheck.IsChecked = false;
            sourceCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, sourceCheck));
            tabBar.SelectedItem = headers[0];
            host.UpdateLayout();
            var sourceView = FindControls<TerminalSurface>(source).Single();
            await VerifyClipboardRoutingAsync(source, first, second, third);
            SendText(sourceView, "あ");
            var actual = new byte[2];
            await second.GetStream().ReadExactlyAsync(actual, deadline.Token);
            if (!actual.SequenceEqual(new byte[] { 0x82, 0xa0 }) || first.Available != 0 || third.Available != 0)
                throw new InvalidOperationException("Broadcast did not use the checked session's codec and scope.");

            Click(Child(broadcast, "BroadcastStop"));
            await WaitForStart(host);
            Click(Child(broadcast, "BroadcastStart"));
            if (source.BroadcastChecked) throw new InvalidOperationException("Stop/Start lost the remembered selection.");
            Click(Child(otherBroadcast, "BroadcastStart"));
            var keyboardTarget = Keyboard.FocusedElement;
            var selected = tabBar.SelectedItem;
            app.Broadcast.CaptureFailed(host);
            WindowReportContent cancellation = app.StatusReports.Pending(host).Single().Content;
            if (!cancellation.Summary.Contains(App.Text("BroadcastGestureCancelled"), StringComparison.Ordinal) ||
                !cancellation.Details.Contains(App.Text("BroadcastNotSent"), StringComparison.Ordinal) ||
                app.StatusReports.Pending(other).Count != 0 || Keyboard.FocusedElement != keyboardTarget ||
                tabBar.SelectedItem != selected || source.Reports.HasReports)
                throw new InvalidOperationException("Local capture failure was misrouted or moved the user's input.");
            await AcknowledgeWindowReportAsync(host, cancellation.Details);
            await VerifyCompetingPastesAsync(source, first, target, second, outside, third);
            await SaveScope(true);
            if (source.BroadcastChecked || !app.Broadcast.IsLocal(host))
                throw new InvalidOperationException("Saving scope changed an active local broadcast.");
            broadcast = FindMenu(host, "ConnectionMenu", "BroadcastInput");
            Click(Child(broadcast, "BroadcastStop"));
            if (app.Broadcast.IsActive(host) || app.Broadcast.IsActive(other))
                throw new InvalidOperationException("Ordinary Stop did not stop the old local modes.");
            await WaitForStart(host);
            Click(Child(broadcast, "BroadcastStart"));
            if (!source.BroadcastChecked || !target.BroadcastChecked || !outside.BroadcastChecked)
                throw new InvalidOperationException("The first global Start did not reset all selections.");

            // The mode budget rejects this operation before any target can receive bytes.
            var earlierReports = app.StatusReports.Pending(host);
            keyboardTarget = Keyboard.FocusedElement;
            selected = tabBar.SelectedItem;
            bool queued = await app.Broadcast.Send(host, new(TerminalInputKind.Text, new string('x', 1_048_577)), deadline.Token)!;
            WindowStatusReport full = app.StatusReports.Pending(other).Single();
            if (queued || app.Broadcast.IsActive(host) || !app.StatusReports.Pending(host).Contains(full) ||
                Keyboard.FocusedElement != keyboardTarget || tabBar.SelectedItem != selected ||
                !full.Content.Fields.Contains(new(App.Text("OperationReportReason"), App.Text("InputQueueFull"))) ||
                !new[] { source, target, outside }.All(tab => full.Content.Details.Contains(
                    $"{TerminalTab.DialogTargetLabel(tab.InputSession!)} — {App.Text("BroadcastNotSent")}", StringComparison.Ordinal)) ||
                FindControls<TextBlock>(source.Notices).Any(label => label.Text.Contains(App.Text("InputQueueFull"), StringComparison.Ordinal)))
                throw new InvalidOperationException("Queue rejection did not leave one shared report with each unsent target.");
            await AcknowledgeWindowReportAsync(other, full.Content.Details);
            if (!app.StatusReports.Pending(host).SequenceEqual(earlierReports) || app.StatusReports.Pending(other).Count != 0)
                throw new InvalidOperationException("Acknowledging a global report left a copy or cleared a different local report.");
            await WaitForStart(host);
            Click(Child(broadcast, "BroadcastStart"));

            var waitingAnswer = new TaskCompletionSource<BroadcastInput.PasteDecision?>();
            var previewEntered = new TaskCompletionSource<BroadcastInput.PastePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            var defaults = app.PasteDefaults;
            using var origin = new CancellationTokenSource();
            using var previewSource = TerminalPaste.Create("cancelled\n");
            Task<BroadcastInput.PasteOutcome> cancelledPreview = app.Broadcast.PasteAsync(host, source,
                previewSource, (preview, _) =>
                {
                    previewEntered.SetResult(preview);
                    return waitingAnswer.Task;
                }, origin: origin.Token);
            var shown = await previewEntered.Task.WaitAsync(deadline.Token);
            origin.Cancel();
            var expectedRecipients = new[] { "UTF-8", "CP932", "Outside" }
                .Select(name => string.Format(App.Text("AiTelnetTargetValue"), $"127.0.0.1:{port}", name));
            if (!shown.Targets.Order().SequenceEqual(expectedRecipients.Order()))
                throw new InvalidOperationException("Unexpected paste recipients: " + string.Join(", ", shown.Targets));
            if (await cancelledPreview.WaitAsync(deadline.Token) != BroadcastInput.PasteOutcome.Stopped)
                throw new InvalidOperationException("The obsolete preview did not release its operation.");
            previewSource.Dispose();
            using (previewSource.HoldSource()) { }
            if (await app.Broadcast.PasteAsync(host, source, TerminalPaste.Create("shared"),
                (_, _) => throw new InvalidOperationException("Single-line paste must not prompt."), new(0, 0, false))
                != BroadcastInput.PasteOutcome.Submitted)
                throw new InvalidOperationException("The replacement paste did not complete.");
            waitingAnswer.SetResult(new(9000, 9000, true));
            if (app.PasteDefaults != defaults) throw new InvalidOperationException("An obsolete answer changed paste defaults.");
            foreach (TcpClient peer in new[] { first, second, third })
            {
                byte[] payload = new byte[6];
                await peer.GetStream().ReadExactlyAsync(payload, deadline.Token);
                Equal(Encoding.ASCII.GetString(payload), "shared");
            }

            var outsideCheck = ((DockPanel)((TabItem)((TabControl)other.FindName("tabs")).SelectedItem).Header)
                .Children.OfType<CheckBox>().Single();
            outsideCheck.IsChecked = false;
            outsideCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, outsideCheck));
            var sending = app.Broadcast.PasteAsync(host, source, TerminalPaste.Create("XY"),
                (_, _) => throw new InvalidOperationException("Single-line paste must not prompt."), new(10_000, 0, false));
            foreach (TcpClient peer in new[] { first, second })
            {
                byte[] payload = new byte[1];
                await peer.GetStream().ReadExactlyAsync(payload, deadline.Token);
                if (payload[0] != 'X') throw new InvalidOperationException("The paced paste began with unexpected bytes.");
            }
            var move = FindMenu(other, "WindowMenu", "MoveTab");
            OpenSubmenu(move);
            Click(move.Items.OfType<MenuItem>().Last());
            if (!other.IsClosing || !host.TerminalTabs.Contains(outside) || !outside.BroadcastChecked || third.Available != 0)
                throw new InvalidOperationException("The global move did not preserve the paste targets and reset the moved check.");
            var destinationTabs = (TabControl)host.FindName("tabs");
            destinationTabs.SelectedItem = destinationTabs.Items.OfType<TabItem>().Single(item => item.Content == outside);
            while (!ShowsPasteProgress(source)) await Task.Delay(20, deadline.Token);
            if (ShowsPasteProgress(outside))
                throw new InvalidOperationException("Paste progress followed the selection to a tab outside the captured recipients.");
            Click(Child(FindMenu(host, "ConnectionMenu", "BroadcastInput"), "StopPaste"));
            if (await sending.WaitAsync(deadline.Token) != BroadcastInput.PasteOutcome.Stopped || new[] { first, second, third }.Any(peer => peer.Available != 0))
                throw new InvalidOperationException("Stop Paste did not cancel the unsent tail in every window.");
            if (ShowsPasteProgress(source)) throw new InvalidOperationException("The initiating tab retained stopped paste progress.");

            static bool ShowsPasteProgress(TerminalTab terminal) => FindControls<TextBlock>(terminal)
                .Any(label => label.Text.Contains("1/2", StringComparison.Ordinal));

            foreach (var peer in new[] { first, second, third })
                await peer.GetStream().WriteAsync("\u001b[?1002h\u001b[?1006h"u8.ToArray(), deadline.Token);
            foreach (TerminalTab terminal in terminals)
            {
                while (!(await terminal.InputSession!.ObserveAiAsync((_, state) => state.Mouse.SgrCoordinates, deadline.Token)))
                    await Task.Delay(10, deadline.Token);
            }
            app.Broadcast.CaptureFailed(host);
            if (!app.StatusReports.Pending(host).Any(report =>
                report.Content.Summary.Contains(App.Text("BroadcastGestureCancelled"), StringComparison.Ordinal)))
                throw new InvalidOperationException("The failed capture did not report cancellation.");
            app.Broadcast.StartMouse(host, source, sourceView, 10);
            await Mouse(10, 0);
            await Mouse(10, 2);
            await Mouse(10, 0, release: true);
            if (app.Broadcast.CanSelect(host)) throw new InvalidOperationException("The first button-up released the entire gesture.");
            await Mouse(10, 2, release: true);
            app.Broadcast.StopMouse(sourceView, 10);
            app.Broadcast.StartMouse(host, source, sourceView, 11);
            await Mouse(11, 0);
            app.Broadcast.CaptureLost(sourceView, 10);
            app.Broadcast.StopMouse(sourceView, 10);
            await Mouse(10, 0, release: true);
            if (app.Broadcast.CanSelect(host)) throw new InvalidOperationException("A stale capture ended the replacement gesture.");
            app.Broadcast.CaptureLost(sourceView, 11);
            app.Broadcast.StopMouse(sourceView, 11);
            while (!app.Broadcast.CanSelect(host)) await Task.Delay(10, deadline.Token);
            if (!app.Broadcast.CanSelect(host)) throw new InvalidOperationException("The last button-up did not release admission.");
            foreach (var peer in new[] { first, second, third })
            {
                const string expectedMouse = "\u001b[<0;2;2M\u001b[<2;2;2M\u001b[<0;2;2m\u001b[<2;2;2m\u001b[<0;2;2M\u001b[<0;2;2m";
                var payload = new byte[expectedMouse.Length];
                await peer.GetStream().ReadExactlyAsync(payload, deadline.Token);
                Equal(Encoding.ASCII.GetString(payload), expectedMouse);
            }

            var movedHost = new MainWindow(app);
            try
            {
                movedHost.Show();
                app.Broadcast.StartMouse(host, source, sourceView, 12);
                await Mouse(12, 0);
                if (app.Broadcast.MoveTerminalTab(host, movedHost, source, () => false).Placed ||
                    app.Broadcast.CanSelect(host))
                    throw new InvalidOperationException("A placement failure ended an intact gesture.");
                tabBar.SelectedItem = headers[0];
                host.UpdateLayout();
                var moveSource = FindMenu(host, "WindowMenu", "MoveTab");
                OpenSubmenu(moveSource);
                Click(moveSource.Items.OfType<MenuItem>().Last());
                if (!movedHost.TerminalTabs.Contains(source) || host.TerminalTabs.Contains(source))
                    throw new InvalidOperationException("The gesture source did not move to the other window.");
                await Mouse(12, 0, release: true);
                await app.Broadcast.RouteMouseAsync(movedHost, sourceView, 12, new(0, 1, 1, true, false, TerminalModifiers.None, 0));
                app.Broadcast.StopMouse(sourceView, 12);
                foreach (var peer in new[] { first, second, third })
                {
                    const string expected = "\u001b[<0;2;2M\u001b[<0;2;2m";
                    byte[] seen = new byte[expected.Length];
                    await peer.GetStream().ReadExactlyAsync(seen, deadline.Token);
                    Equal(Encoding.ASCII.GetString(seen), expected);
                    if (peer.Available != 0) throw new InvalidOperationException("Duplicate button-up produced more input.");
                }
                var lastTabMove = FindMenu(movedHost, "WindowMenu", "MoveTab");
                OpenSubmenu(lastTabMove);
                Click(lastTabMove.Items.OfType<MenuItem>().Last());
                if (!movedHost.IsClosing || !host.TerminalTabs.Contains(source))
                    throw new InvalidOperationException("The last source tab did not retire its old host.");
                SendText(sourceView, "😀");
                while (app.Broadcast.IsActive(host))
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            }
            finally
            {
                TerminalTab[] movedTabs = movedHost.TerminalTabs.ToArray();
                foreach (var movedTab in movedTabs) movedTab.Disconnect();
                await Task.WhenAll(movedTabs.Select(tab => tab.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
                if (!movedHost.IsClosing) movedHost.Close();
            }
            while (app.Broadcast.IsActive(host))
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            if (app.Broadcast.IsActive(host) || first.Available != 0 || second.Available != 0 || third.Available != 0)
                throw new InvalidOperationException("A conversion failure did not stop broadcast before sending.");
            Task<bool> Mouse(long capture, int button, bool release = false) => app.Broadcast.RouteMouseAsync(
                host, sourceView, capture, new(button, 1, 1, release, false, TerminalModifiers.None, 0));

            if (!app.StatusReports.Pending(host).Any(report => report.Content.Summary.Contains(App.Text("BroadcastFailed"), StringComparison.Ordinal)) ||
                !((Button)host.FindName("windowReportButton")).IsVisible)
                throw new InvalidOperationException("The broadcast failure was not visible.");
        }
        finally
        {
            foreach (var terminal in terminals) terminal.Disconnect();
            await Task.WhenAll(terminals.Select(terminal => terminal.Completion));
            if (!host.IsClosing) host.Close();
            if (!other.IsClosing) other.Close();
            await SaveScope(originalScope);
        }

        async Task<(TerminalTab Tab, TcpClient Peer)> Connect(MainWindow window, string name, string codec,
            Func<Window, long, CancellationToken, Task<TerminalPaste?>>? capture = null)
        {
            string path = Path.Combine(directory, name + ".ttty");
            await ConnectionProfile.CreateTelnet(name, "127.0.0.1", port, receiveEncoding: codec).SaveNewAsync(path, deadline.Token);
            var terminal = new TerminalTab(path, null, captureClipboard: capture);
            terminals.Add(terminal);
            window.AddTerminalTab(terminal);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.Connected += _ => ready.TrySetResult();
            terminal.Start();
            var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await peer.GetStream().ReadExactlyAsync(new byte[15], deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            window.UpdateLayout();
            return (terminal, peer);
        }

        async Task VerifyClipboardRoutingAsync(TerminalTab source, TcpClient first, TcpClient second, TcpClient third)
        {
            Task paste = source.PasteAsync();
            await captureStarted.Task.WaitAsync(deadline.Token);
            app.Broadcast.Select(host, source, true);
            if (source.BroadcastChecked)
                throw new InvalidOperationException("The broadcast recipients changed during clipboard capture.");
            clipboard.SetResult(TerminalPaste.Create("A"));
            await paste.WaitAsync(deadline.Token);
            var received = new byte[1];
            await second.GetStream().ReadExactlyAsync(received, deadline.Token);
            if (received[0] != 'A' || first.Available != 0 || third.Available != 0)
                throw new InvalidOperationException("The clipboard text was sent beyond the original recipients.");

            foreach (bool stopBroadcast in new[] { true, false })
            {
                captureStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                clipboard = new(TaskCreationOptions.RunContinuationsAsynchronously);
                paste = source.PasteAsync();
                await captureStarted.Task.WaitAsync(deadline.Token);
                if (stopBroadcast) app.Broadcast.Stop(host);
                else app.Broadcast.Start(host);
                clipboard.SetResult(TerminalPaste.Create("B"));
                await paste.WaitAsync(deadline.Token);
                if (first.Available != 0 || second.Available != 0 || third.Available != 0)
                    throw new InvalidOperationException("Changing broadcast mode redirected a pending paste.");
                if (stopBroadcast) await WaitForStart(host);
            }
        }

        async Task VerifyCompetingPastesAsync(TerminalTab source, TcpClient first, TerminalTab target, TcpClient second,
            TerminalTab outside, TcpClient third)
        {
            using var shared = TerminalPaste.Create("XY");
            app.Broadcast.SetAll(host, true);
            using (var pressure = await target.InputSession!.PrepareInputAsync(
                new TerminalInput(TerminalInputKind.Text, new string('p', 512 * 1024)), null, deadline.Token))
            {
                var outsidePaste = app.Broadcast.PasteAsync(other, outside, shared, NoPreview,
                    new BroadcastInput.PasteDecision(10_000, 0, false), deadline.Token);
                try
                {
                    var prefix = new byte[1];
                    await third.GetStream().ReadExactlyAsync(prefix, deadline.Token);
                    var refused = await app.Broadcast.PasteAsync(host, source, shared, NoPreview,
                        new BroadcastInput.PasteDecision(0, 0, false), deadline.Token);
                    if (prefix[0] != 'X' || refused != BroadcastInput.PasteOutcome.Stopped ||
                        first.Available != 0 || second.Available != 0 || outsidePaste.IsCompleted)
                        throw new InvalidOperationException("One exhausted target did not refuse the whole local broadcast.");
                }
                finally { app.Broadcast.StopPaste(other); }
                if (await outsidePaste != BroadcastInput.PasteOutcome.Stopped || third.Available != 0)
                    throw new InvalidOperationException("Stopping the independent paste sent its uncommitted suffix.");
            }
            if (!app.Broadcast.IsActive(host))
            {
                await WaitForStart(host);
                app.Broadcast.Start(host);
                app.Broadcast.SetAll(host, true);
            }
            var sent = await app.Broadcast.PasteAsync(host, source, shared, NoPreview,
                new BroadcastInput.PasteDecision(0, 0, false), deadline.Token);
            var received = new byte[2];
            await first.GetStream().ReadExactlyAsync(received, deadline.Token);
            bool firstExact = received.AsSpan().SequenceEqual("XY"u8);
            await second.GetStream().ReadExactlyAsync(received, deadline.Token);
            if (sent != BroadcastInput.PasteOutcome.Submitted || !firstExact ||
                !received.AsSpan().SequenceEqual("XY"u8) || third.Available != 0)
                throw new InvalidOperationException("Released capacity did not restore exact paste to the local targets.");
            app.Broadcast.Select(host, source, false);

            static Task<BroadcastInput.PasteDecision?> NoPreview(BroadcastInput.PastePreview _, CancellationToken token) =>
                throw new InvalidOperationException("A two-character paste unexpectedly opened a preview.");
        }

        Task SaveScope(bool acrossWindows) => app.SaveSettingsAsync(app.SettingsRevision, new(Broadcast: acrossWindows));

        async Task WaitForStart(MainWindow window)
        {
            var commands = FindMenu(window, "ConnectionMenu", "BroadcastInput");
            do { await window.Dispatcher.InvokeAsync(() => OpenSubmenu(commands), DispatcherPriority.ApplicationIdle, deadline.Token); }
            while (!Child(commands, "BroadcastStart").IsEnabled);
        }

        static void ClickButton(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        static Button SplitButton(TabControl group, string name, string labelKey)
        {
            var button = (Button)group.Template.FindName(name, group);
            string label = App.Text(labelKey);
            bool hasIcon = name == "removeTabGroupButton"
                ? button.Content is Grid { Children: [ShapePath, ShapePath { StrokeDashArray.Count: > 0 }] }
                : button.Content is ShapePath;
            if (!hasIcon || !Equals(button.ToolTip, label) ||
                System.Windows.Automation.AutomationProperties.GetName(button) != label)
                throw new InvalidOperationException("A tab-group button omitted its icon or localized label.");
            return button;
        }
    }

    private static async Task CheckWindowReportScopeAsync(App app)
    {
        var window = new MainWindow(app) { Width = WindowPlacement.MinimumWidth, ShowActivated = false };
        try
        {
            window.Show();
            app.Broadcast.Start(window);
            var first = app.StatusReports.CaptureFields(window, "First file operation");
            var second = app.StatusReports.CaptureFields(window, "Second file operation");
            var verified = new ReportField(App.Text("DialogDetails"), "First operation verified");
            first("result", [new(App.Text("DialogDetails"), "Awaiting verification")]);
            second("result", [new(App.Text("DialogDetails"), "Later operation")]);
            first("result", [verified]);
            var reports = app.StatusReports.Pending(window);
            if (reports.Count != 2 || reports[0].Content.Fields.Single().Value != "First operation verified" ||
                reports[1].Content.Fields.Single().Value != "Later operation")
                throw new InvalidOperationException("Two operations sharing a result key were merged or reordered.");

            window.UpdateLayout();
            var mode = (TextBlock)window.FindName("broadcastStatus");
            var summary = (Button)window.FindName("windowReportButton");
            var vault = (TextBlock)window.FindName("vaultStatus");
            if (!mode.IsVisible || mode.ActualWidth <= 0 || !summary.IsVisible || summary.ActualWidth <= 0 ||
                !vault.IsVisible || vault.ActualWidth <= 0 ||
                mode.TranslatePoint(new Point(mode.ActualWidth, 0), window).X > summary.TranslatePoint(new Point(), window).X ||
                summary.TranslatePoint(new Point(summary.ActualWidth, 0), window).X > vault.TranslatePoint(new Point(), window).X ||
                vault.TranslatePoint(new Point(vault.ActualWidth, 0), window).X > window.ActualWidth)
                throw new InvalidOperationException("Reports, broadcast or vault status were clipped or overlapped at minimum window width.");
            summary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var viewer = window.OwnedWindows.Cast<Window>().Single(candidate => candidate.IsVisible &&
                candidate.Title == App.Text("WindowReportsTitle"));
            viewer.MaxHeight = 240;
            viewer.UpdateLayout();
            var viewerLayout = (DialogLayout)viewer.Content;
            var close = viewerLayout.Actions.Children.OfType<Button>().Single(button => Equals(button.Content, App.Text("CloseDialog")));
            var count = FindControls<TextBlock>(viewer).Single(text => text.Text == string.Format(App.Text("OperationReportCount"), 2));
            var details = FindControls<DialogText>(viewer).Single(text => text.Text == reports[0].Content.Fields.Single().Value);
            if (System.Windows.Automation.AutomationProperties.GetLabeledBy(count) is not TextBlock countLabel ||
                countLabel.Text != App.Text("PendingOperationReports") + ":" ||
                System.Windows.Automation.AutomationProperties.GetLabeledBy(details) is not TextBlock detailsLabel ||
                detailsLabel.Text != App.Text("DialogDetails") + ":" ||
                !FindControls<DialogText>(viewer).Any(text => text.Text == reports[0].Content.Summary))
                throw new InvalidOperationException("The result viewer did not separate the count, outcome and copyable details from their labels.");
            void CheckRows(bool inline) =>
                CheckDialogFieldLayout(viewer, inline, [count, .. FindControls<DialogText>(viewer)]);
            CheckRows(true);
            string caption = string.Format(App.Text("DialogFieldCaption"), verified.Caption);
            if (reports[0].Content.Details != $"{caption} {verified.Value}")
                throw new InvalidOperationException("The short report used a different caption format in plain text.");
            var backup = new ReportField(App.Text("BackupFilePath"), "copy.tttykey", Framed: true);
            first("result", [verified, backup]);
            CheckRows(false);
            string backupCaption = string.Format(App.Text("DialogFieldCaption"), backup.Caption);
            if (reports[0].Content.Details != $"{caption}\n{verified.Value}\n\n{backupCaption}\n{backup.Value}")
                throw new InvalidOperationException("The framed report mixed caption formats in plain text.");
            first("result", [verified]);
            CheckRows(true);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (viewer.IsVisible || app.StatusReports.Pending(window).Count != 2)
                throw new InvalidOperationException("Closing the result viewer acknowledged a report.");
            await AcknowledgeWindowReportAsync(window, reports[0].Content.Details);
            if (app.StatusReports.Pending(window).Single() != reports[1])
                throw new InvalidOperationException("Acknowledging the older report cleared another operation's report.");
        }
        finally
        {
            app.Broadcast.Stop(window);
            window.Close();
        }
        if (app.StatusReports.Pending(window).Count != 0)
            throw new InvalidOperationException("Closing the window retained its local results.");
    }

    private static async Task CheckBroadcastPasteCleanupAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var host = new MainWindow(app);
        var peer = new MainWindow(app) { ShowActivated = false };
        var wire = new HeldBroadcastWrite();
        await using var session = new TerminalSession(wire, ConnectionProfile.CreateTelnet("Cleanup", "localhost"), null, 100);
        var source = new TerminalTab(Path.Combine(directory, "cleanup.ttty"));
        typeof(TerminalTab).GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(source, session);
        host.AddTerminalTab(source);
        try
        {
            host.Show();
            peer.Show();
            app.Broadcast.Start(host);
            using var paste = TerminalPaste.Create("AB");
            Task<BroadcastInput.PasteOutcome> sending = app.Broadcast.PasteAsync(host, source, paste, (_, _) =>
                throw new InvalidOperationException("A single-line paste requested confirmation."), new(0, 0, false), deadline.Token);
            await wire.Started.Task.WaitAsync(deadline.Token);
            app.Broadcast.StopPaste(host);
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            string cleanup = App.Text("BroadcastCleanupPending");
            var state = (TextBlock)host.FindName("broadcastStatus");
            if (sending.IsCompleted || !state.Text.Contains(cleanup, StringComparison.Ordinal) ||
                app.Broadcast.Status(peer).Contains(cleanup, StringComparison.Ordinal) ||
                FindControls<TextBlock>(source).Any(label => label.Text.Contains(cleanup, StringComparison.Ordinal)))
                throw new InvalidOperationException("Cleanup for an admitted paste escaped its window or ended before the write.");
            wire.Finish.TrySetResult();
            await sending.WaitAsync(deadline.Token);
            if (!app.Broadcast.IsActive(host) || state.Text.Contains(cleanup, StringComparison.Ordinal))
                throw new InvalidOperationException("Stop Paste ended the mode or retained completed cleanup.");
        }
        finally
        {
            wire.Finish.TrySetResult();
            app.Broadcast.Stop(host);
            typeof(TerminalTab).GetProperty(nameof(TerminalTab.IsRunning))!.SetValue(source, false);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Closed += (_, _) => closed.TrySetResult();
            host.Close();
            peer.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class HeldBroadcastWrite : ISessionWire
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            Started.TrySetResult();
            await Finish.Task;
        }
        public void Dispose() { }
    }

    private static async Task CheckBroadcastPasteFailurePrefixAsync(App app, string directory, bool bracketed = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new MainWindow(app, () => true);
        host.Show();
        var goodWire = new PacedPasteWire(written, failing, fail: false);
        var badWire = new PacedPasteWire(written, failing, fail: true);
        var (source, good) = Add("Healthy", goodWire);
        var (_, bad) = Add("Failing", badWire);
        await using var first = good;
        await using var second = bad;
        try
        {
            if (bracketed)
                await good.ProcessPayloadAsync(TerminalReceiveSource.Data, "\u001b[?2004h"u8.ToArray(), deadline.Token);
            host.SelectNotificationTab(source);
            app.Broadcast.Start(host);
            using var paste = TerminalPaste.Create("AB");
            var result = await app.Broadcast.PasteAsync(host, source, paste, (_, _) =>
                throw new InvalidOperationException("The fixture unexpectedly requested a paste preview."),
                new(5000, 0, false), deadline.Token);
            string expected = bracketed ? "\u001b[200~\u001b[201~" : "A";
            if (result != BroadcastInput.PasteOutcome.Stopped || Encoding.ASCII.GetString(goodWire.Bytes.ToArray()) != expected)
                throw new InvalidOperationException("The fixture did not stop the healthy recipient at the intended boundary.");

            host.UpdateLayout();
            string confirmed = bracketed ? App.Text("BroadcastPasteFramingOnly") : string.Format(App.Text("ConfirmedInputPrefix"), 1);
            string label = TerminalTab.DialogTargetLabel(good);
            var report = app.StatusReports.Pending(host).Select(item => item.Content).Single(item =>
                item.Details.Contains(label, StringComparison.Ordinal) && item.Details.Contains(confirmed, StringComparison.Ordinal));
            if (!report.Details.Split('\n').Any(line => line.Contains(label, StringComparison.Ordinal) &&
                line.Contains(confirmed, StringComparison.Ordinal) && !line.Contains(App.Text("BroadcastNotSent"), StringComparison.Ordinal)))
                throw new InvalidOperationException("The incident reported a confirmed paste write as unsent.");
            await AcknowledgeWindowReportAsync(host, report.Details);
            if (goodWire.Bytes.Count != expected.Length || badWire.Bytes.Count != 0)
                throw new InvalidOperationException("Acknowledging a broadcast incident resent input.");
            Console.WriteLine(bracketed ? "PASS BroadcastPasteFailureReportsFramingOnly" : "PASS BroadcastPasteFailurePreservesRecipientProgress");
        }
        finally
        {
            app.Broadcast.Stop(host);
            foreach (var terminal in host.TerminalTabs)
                typeof(TerminalTab).GetProperty(nameof(TerminalTab.IsRunning))!.SetValue(terminal, false);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Closed += (_, _) => closed.TrySetResult();
            host.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        (TerminalTab, TerminalSession) Add(string name, PacedPasteWire wire)
        {
            var tab = new TerminalTab(Path.Combine(directory, name + ".ttty"));
            var terminal = new TerminalSession(wire, ConnectionProfile.CreateTelnet(name, "127.0.0.1"), null, 100);
            typeof(TerminalTab).GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(tab, terminal);
            host.AddTerminalTab(tab);
            return (tab, terminal);
        }
    }

    private sealed class PacedPasteWire(TaskCompletionSource written, TaskCompletionSource failing, bool fail) : ISessionWire
    {
        internal List<byte> Bytes { get; } = [];

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (fail)
            {
                await written.Task.WaitAsync(token);
                await Task.Delay(100, token);
                failing.TrySetResult();
                throw new IOException("Synthetic paste failure.");
            }
            Bytes.AddRange(buffer.ToArray());
            written.TrySetResult();
            if (buffer.Span.SequenceEqual("\u001b[200~"u8))
            {
                await failing.Task.WaitAsync(token);
                await Task.Delay(350, token);
            }
        }

        public void Dispose() { }
    }

    private static async Task CheckBroadcastDeliveryFailureAsync(App app, string directory)
    {
        foreach (bool serial in new[] { true, false })
        foreach (bool isPaste in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var host = new MainWindow(app, () => true);
            string name = serial ? "Zero serial" : "Half IAC";
            var tab = new TerminalTab(Path.Combine(directory, name + ".ttty"));
            using var wire = new IncompleteBroadcastWire(serial);
            var profile = serial ? ConnectionProfile.CreateSerial(name, new("COM254")) :
                ConnectionProfile.CreateTelnet(name, "127.0.0.1", transmitEncoding: "iso-8859-1");
            await using var session = new TerminalSession(wire, profile, null, 100);
            typeof(TerminalTab).GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(tab, session);
            host.AddTerminalTab(tab);
            host.Show();
            host.SelectNotificationTab(tab);
            app.Broadcast.Start(host);
            try
            {
                string text = serial ? "A" : "ÿ";
                bool sent;
                if (isPaste)
                {
                    using var paste = TerminalPaste.Create(text);
                    var result = await app.Broadcast.PasteAsync(host, tab, paste, (_, _) =>
                        throw new InvalidOperationException("Unexpected paste preview."), new(0, 0, false), deadline.Token);
                    sent = result != BroadcastInput.PasteOutcome.Stopped;
                }
                else sent = await app.Broadcast.Send(host, new(TerminalInputKind.Text, text), deadline.Token)!;
                string label = TerminalTab.DialogTargetLabel(session);
                var report = app.StatusReports.Pending(host).Select(item => item.Content).Single(item => item.Details.Contains(label, StringComparison.Ordinal));
                string outcome = serial ? App.Text("BroadcastNotSent") : App.Text("NoticeBroadcastUncertain");
                string other = serial ? App.Text("NoticeBroadcastUncertain") : App.Text("BroadcastNotSent");
                if (sent || !report.Details.Contains(outcome, StringComparison.Ordinal) || report.Details.Contains(other, StringComparison.Ordinal) ||
                    wire.Calls != (serial ? 1 : 2) || wire.Accepted != (serial ? null : (byte)0xFF))
                    throw new InvalidOperationException("The report did not preserve the outcome of a zero-payload write.");
                await AcknowledgeWindowReportAsync(host, report.Details);
                if (wire.Calls != (serial ? 1 : 2)) throw new InvalidOperationException("Acknowledging an incident resent input.");
            }
            finally
            {
                app.Broadcast.Stop(host);
                typeof(TerminalTab).GetProperty(nameof(TerminalTab.IsRunning))!.SetValue(tab, false);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Closed += (_, _) => closed.TrySetResult();
                host.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Console.WriteLine("PASS BroadcastDistinguishesNoSubmissionFromAnUncertainWrite");
    }

    private sealed class IncompleteBroadcastWire(bool serial, bool refuseRemainder = false) : ISessionWire
    {
        internal int Calls { get; private set; }
        internal byte? Accepted { get; private set; }
        public ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (++Calls != 1)
            {
                if (refuseRemainder) return ValueTask.FromResult(0);
                throw new IOException("Synthetic write failure.");
            }
            if (serial) return ValueTask.FromResult(0);
            Accepted = buffer.Span[0];
            return ValueTask.FromResult(1);
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => throw new InvalidOperationException("Expected counted write.");
        public void Dispose() { }
    }

    private static MenuItem Child(ItemsControl menu, string key) => menu.Items.OfType<MenuItem>()
        .Single(item => Equals(item.Header, App.Text(key)));
    private static MenuItem FindMenu(MainWindow window, string top, string child) =>
        Child(Child((Menu)window.FindName("mainMenu"), top), child);
    private static void Click(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
    private static void OpenSubmenu(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));

    private sealed class DeferredComposition(TerminalSurface source,
        TextCompositionAutoComplete completion = TextCompositionAutoComplete.Off) :
        TextComposition(InputManager.Current, source, "discarded", completion)
    {
        // The integration test explicitly delivers the late completion event after focus moves.
        public override void Complete() { }
    }
}
