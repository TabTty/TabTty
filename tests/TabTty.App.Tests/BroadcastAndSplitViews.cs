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
        var window = new MainWindow(app);
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
            tab.BroadcastChecked = false;
            await CheckConnection(second, selected: false);
        }
        finally
        {
            tab.Disconnect();
            await tab.Completion;
            window.Close();
            await SetAutoClose(autoClose);
        }

        Task SetAutoClose(bool enabled) => app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, enabled, app.VaultPath, app.SettingsRevision);

        async Task CheckConnection(TcpListener endpoint, bool selected, TcpListener? previous = null)
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Connected(string _) => ready.TrySetResult(tab.BroadcastChecked);
            tab.Connected += Connected;
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
                    try
                    {
                        if (previous is null && notice is null) return;
                        if (previous is null || notice is not { Topmost: true, ShowActivated: false, IsActive: false } ||
                            !window.IsEnabled || notice.Title != App.Text("ReconnectDestinationChanged"))
                            throw new InvalidOperationException("The destination change notification appeared at the wrong time or below the owner.");
                        string before = string.Format(App.Text("AiTelnetTarget"), previous.LocalEndpoint);
                        string after = string.Format(App.Text("AiTelnetTarget"), endpoint.LocalEndpoint);
                        string body = string.Format(App.Text("ReconnectDestinationDetails"), "Same name", before, after);
                        if (!FindControls<TextBlock>(notice).Any(label => label.Text == body) || FindControls<Button>(notice).Count() != 1)
                            throw new InvalidOperationException("The reconnect notification did not identify the old and new destinations.");
                        var pane = FindControls<TerminalSurface>(tab).Single();
                        SendText(pane, "a");
                        var input = new byte[2];
                        await peer.GetStream().ReadExactlyAsync(input.AsMemory(0, 1), deadline.Token);
                        app.Broadcast.Start(window);
                        if (!app.Broadcast.IsActive(window))
                            throw new InvalidOperationException("Broadcast could not start while the notification was visible.");
                        SendText(pane, "b");
                        await peer.GetStream().ReadExactlyAsync(input.AsMemory(1), deadline.Token);
                        if (!input.AsSpan().SequenceEqual("ab"u8) || !notice.IsVisible)
                            throw new InvalidOperationException("The open notification interrupted terminal or broadcast input.");
                        app.Broadcast.Stop(window);
                        FindControls<Button>(notice).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (notice.IsVisible || !tab.BroadcastChecked)
                            throw new InvalidOperationException("Acknowledging the notification changed broadcast selection.");
                    }
                    finally { if (notice?.IsVisible == true) notice.Close(); }
                }, DispatcherPriority.ApplicationIdle, deadline.Token).Task.Unwrap();
                tab.Disconnect();
                await tab.Completion.WaitAsync(deadline.Token);
            }
            finally { tab.Connected -= Connected; }
        }
    }

    private static async Task BroadcastAndSplitViews(App app, string directory)
    {
        await CheckBroadcastReconnectAsync(app, directory);
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
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            if (second.Available != 0) throw new InvalidOperationException("A discarded composition was sent after pane focus moved.");

            var broadcast = FindMenu(host, "ConnectionMenu", "BroadcastInput");
            Click(Child(broadcast, "BroadcastStart"));
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
            string currentDestination = string.Format(App.Text("AiTelnetTarget"), $"127.0.0.1:{port}");
            string[] expectedRecipients = [$"{currentDestination} — UTF-8", $"{currentDestination} — CP932", $"{currentDestination} — Outside"];
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
            Click(Child(FindMenu(host, "ConnectionMenu", "BroadcastInput"), "StopPaste"));
            if (await sending.WaitAsync(deadline.Token) != BroadcastInput.PasteOutcome.Stopped || new[] { first, second, third }.Any(peer => peer.Available != 0))
                throw new InvalidOperationException("Stop Paste did not cancel the unsent tail in every window.");

            foreach (var peer in new[] { first, second, third })
                await peer.GetStream().WriteAsync("\u001b[?1002h\u001b[?1006h"u8.ToArray(), deadline.Token);
            foreach (TerminalTab terminal in terminals)
            {
                while (!(await terminal.InputSession!.ObserveAiAsync((_, state) => state.Mouse.SgrCoordinates, deadline.Token)))
                    await Task.Delay(10, deadline.Token);
            }
            app.Broadcast.CaptureFailed(host);
            if (!app.Broadcast.Status(host).Contains(App.Text("BroadcastGestureCancelled"), StringComparison.Ordinal))
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

            var status = (TextBlock)host.FindName("broadcastStatus");
            if (!status.Text.Contains(App.Text("BroadcastFailed"), StringComparison.Ordinal))
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
            string path = Path.Combine(directory, $"broadcast-{codec}-{Guid.NewGuid():N}.ttty");
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

        Task SaveScope(bool acrossWindows) => app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs,
            app.VaultPath, app.SettingsRevision, acrossWindows);

        async Task WaitForStart(MainWindow window)
        {
            var commands = FindMenu(window, "ConnectionMenu", "BroadcastInput");
            do { await window.Dispatcher.InvokeAsync(() => OpenSubmenu(commands), DispatcherPriority.ApplicationIdle, deadline.Token); }
            while (!Child(commands, "BroadcastStart").IsEnabled);
        }

        static MenuItem Child(ItemsControl menu, string key) => menu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, App.Text(key)));
        static MenuItem FindMenu(MainWindow window, string top, string child) =>
            Child(Child((Menu)window.FindName("mainMenu"), top), child);
        static void Click(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
        static void ClickButton(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        static Button SplitButton(TabControl group, string name, string labelKey)
        {
            var button = (Button)group.Template.FindName(name, group);
            string label = App.Text(labelKey);
            if (button.Content is not System.Windows.Shapes.Path || !Equals(button.ToolTip, label) ||
                System.Windows.Automation.AutomationProperties.GetName(button) != label)
                throw new InvalidOperationException("A tab-group button omitted its icon or localized label.");
            return button;
        }
        static void OpenSubmenu(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
    }

    private sealed class DeferredComposition(TerminalSurface source) :
        TextComposition(InputManager.Current, source, "discarded", TextCompositionAutoComplete.Off)
    {
        // The integration test explicitly delivers the late completion event after focus moves.
        public override void Complete() { }
    }
}
