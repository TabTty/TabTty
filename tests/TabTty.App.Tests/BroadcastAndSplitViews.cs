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
    private static async Task BroadcastAndSplitViews(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = new MainWindow(app);
        var other = new MainWindow(app);
        var terminals = new List<TerminalTab>();
        bool originalScope = app.BroadcastAcrossWindows;
        try
        {
            host.Show();
            other.Show();
            var (source, firstPeer) = await Connect(host, "UTF-8", "utf-8");
            using var first = firstPeer;
            var (target, secondPeer) = await Connect(host, "CP932", "cp932");
            using var second = secondPeer;
            var (outside, thirdPeer) = await Connect(other, "Outside", "utf-8");
            using var third = thirdPeer;

            var groups = FindMenu(host, "WindowMenu", "TabGroups");
            OpenSubmenu(groups);
            Click(Child(groups, "SplitGroupsSide"));
            host.UpdateLayout();
            if (FindControls<TabControl>(host).Count() != 2)
                throw new InvalidOperationException("The window did not create a second tab group.");
            OpenSubmenu(groups);
            Click(Child(groups, "SplitGroupsStack"));
            host.UpdateLayout();
            if (FindControls<TabControl>(host).Count() != 3)
                throw new InvalidOperationException("The second group could not be split on the other axis.");
            OpenSubmenu(groups);
            var remove = Child(groups, "RemoveTabGroup");
            Click(remove.Items.OfType<MenuItem>().Last());
            host.UpdateLayout();
            OpenSubmenu(groups);
            remove = Child(groups, "RemoveTabGroup");
            Click(remove.Items.OfType<MenuItem>().Single());
            host.UpdateLayout();
            if (FindControls<TabControl>(host).Count() != 1 || host.TerminalTabs.Count() != 2)
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
            if (!source.BroadcastChecked || !target.BroadcastChecked || outside.BroadcastChecked)
                throw new InvalidOperationException("First local use did not select just the window's tabs.");
            var tabBar = (TabControl)host.FindName("tabs");
            var headers = tabBar.Items.OfType<TabItem>().ToArray();
            var sourceCheck = ((DockPanel)headers[0].Header).Children.OfType<CheckBox>().Single();
            sourceCheck.IsChecked = false;
            sourceCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, sourceCheck));
            tabBar.SelectedItem = headers[0];
            host.UpdateLayout();
            var sourceView = FindControls<TerminalSurface>(source).Single();
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
            SaveScope(true);
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
            var defaults = app.PasteDefaults;
            Task<BroadcastInput.PasteOutcome> cancelledPreview = app.Broadcast.PasteAsync(host, source,
                TerminalPaste.Create("cancelled\n"), (_, _) => waitingAnswer.Task);
            app.Broadcast.StopPaste(host);
            if (await cancelledPreview.WaitAsync(deadline.Token) != BroadcastInput.PasteOutcome.Stopped)
                throw new InvalidOperationException("The obsolete preview did not release its operation.");
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
            SaveScope(originalScope);
        }

        async Task<(TerminalTab Tab, TcpClient Peer)> Connect(MainWindow window, string name, string codec)
        {
            string path = Path.Combine(directory, $"broadcast-{codec}-{Guid.NewGuid():N}.ttty");
            await ConnectionProfile.CreateTelnet(name, "127.0.0.1", port, receiveEncoding: codec).SaveNewAsync(path, deadline.Token);
            var terminal = new TerminalTab(path);
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

        void SaveScope(bool acrossWindows) => app.SaveSettings(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs,
            app.VaultPath, app.SettingsRevision, acrossWindows);

        async Task WaitForStart(MainWindow window)
        {
            var commands = FindMenu(window, "ConnectionMenu", "BroadcastInput");
            do { await window.Dispatcher.InvokeAsync(() => OpenSubmenu(commands), DispatcherPriority.ApplicationIdle, deadline.Token); }
            while (!Child(commands, "BroadcastStart").IsEnabled);
        }

        static void SendText(TerminalSurface pane, string input) => pane.RaiseEvent(new TextCompositionEventArgs(
            Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, pane, input))
        { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        static MenuItem Child(ItemsControl menu, string key) => menu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, App.Text(key)));
        static MenuItem FindMenu(MainWindow window, string top, string child) =>
            Child(Child((Menu)window.FindName("mainMenu"), top), child);
        static void Click(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
        static void OpenSubmenu(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
    }

    private sealed class DeferredComposition(TerminalSurface source) :
        TextComposition(InputManager.Current, source, "discarded", TextCompositionAutoComplete.Off)
    {
        // The integration test explicitly delivers the late completion event after focus moves.
        public override void Complete() { }
    }
}
