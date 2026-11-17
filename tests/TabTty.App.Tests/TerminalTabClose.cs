using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckTerminalClosure(App app, string directory)
    {
        await CheckTerminalTabClose(app, directory);
        await CheckStaleTabCloseApproval(app, directory);
        await CheckWindowJoinsTerminalTabClose(app, directory, acceptWindow: false);
        await CheckWindowJoinsTerminalTabClose(app, directory, acceptWindow: true);
    }

    private static async Task CheckStaleTabCloseApproval(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string profile = Path.Combine(directory, "stale-tab-close.ttty");
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await ConnectionProfile.CreateTelnet("Stale tab close", "127.0.0.1", port).SaveNewAsync(profile, deadline.Token);
        var terminal = new TerminalTab(profile);
        var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        host.OpenForm("Settings", _ => { });
        host.AddTerminalTab(terminal);
        host.Show();
        var tabs = (TabControl)host.FindName("tabs");
        var item = (TabItem)tabs.SelectedItem;
        var close = ((DockPanel)item.Header).Children.OfType<Button>().Single();
        try
        {
            using TcpClient oldPeer = await ConnectAsync();
            object oldGeneration = terminal.ConnectionGeneration;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Button approve = await WaitForDialogButtonAsync("Yes");
            oldPeer.Client.Shutdown(SocketShutdown.Send);
            await terminal.Completion.WaitAsync(deadline.Token);
            using TcpClient newPeer = await ConnectAsync();
            if (ReferenceEquals(oldGeneration, terminal.ConnectionGeneration))
                throw new InvalidOperationException("The close fixture did not establish a replacement connection.");

            approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            (await WaitForDialogButtonAsync("OK")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await newPeer.GetStream().WriteAsync("\u001b[5n"u8.ToArray(), deadline.Token);
            var reply = new byte[4];
            await newPeer.GetStream().ReadExactlyAsync(reply, deadline.Token);
            if (!tabs.Items.Contains(item) || !reply.AsSpan().SequenceEqual("\u001b[0n"u8))
                throw new InvalidOperationException("Stale close approval affected the replacement connection.");
        }
        finally
        {
            foreach (Window dialog in host.OwnedWindows.Cast<Window>().ToArray())
                if (dialog.IsVisible) dialog.Close();
            terminal.Disconnect();
            await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (host.IsVisible) host.Close();
        }

        async Task<TcpClient> ConnectAsync()
        {
            terminal.Start();
            TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            try
            {
                await peer.GetStream().ReadExactlyAsync(new byte[15], deadline.Token);
                while (terminal.InputSession is null) await Task.Delay(10, deadline.Token);
                return peer;
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        async Task<Button> WaitForDialogButtonAsync(string key)
        {
            while (true)
            {
                var button = host.OwnedWindows.Cast<Window>().Where(dialog => dialog.IsVisible)
                    .SelectMany(FindControls<Button>).SingleOrDefault(candidate => Equals(candidate.Content, App.Text(key)));
                if (button is not null) return button;
                await Task.Delay(10, deadline.Token);
            }
        }
    }

    private static async Task CheckTerminalTabClose(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string profile = Path.Combine(directory, "tab-close.ttty");
        await ConnectionProfile.CreateTelnet("Tab close", "127.0.0.1", port).SaveNewAsync(profile, deadline.Token);
        var terminal = new TerminalTab(profile);
        var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        host.OpenForm("Settings", _ => { });
        host.AddTerminalTab(terminal);
        host.Show();
        var tabs = (TabControl)host.FindName("tabs");
        var item = (TabItem)tabs.SelectedItem;
        var close = ((DockPanel)item.Header).Children.OfType<Button>().Single();
        bool accept = false;
        int reviews = 0;
        var dialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        dialogTimer.Tick += (_, _) =>
        {
            var dialog = host.OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible);
            if (dialog is null) return;
            var answer = FindControls<Button>(dialog).Single(button => Equals(button.Content, App.Text(accept ? "Yes" : "No")));
            reviews++;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            answer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        dialogTimer.Start();
        terminal.Start();
        try
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await peer.GetStream().ReadExactlyAsync(new byte[15], deadline.Token);
            while (terminal.InputSession is null) await Task.Delay(15, deadline.Token);
            TerminalSession owner = terminal.InputSession!;
            await peer.GetStream().WriteAsync("retained 界\u001b[5n"u8.ToArray(), deadline.Token);
            var reply = new byte[4];
            await peer.GetStream().ReadExactlyAsync(reply, deadline.Token);
            if (!reply.AsSpan().SequenceEqual("\u001b[0n"u8))
                throw new InvalidOperationException("The connected test tab did not receive its complete output.");
            var held = (TerminalSnapshot)await owner.GetOutputAsync(deadline.Token);

            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (reviews == 0) await Task.Delay(15, deadline.Token);
            if (reviews != 1 || terminal.InputSession is null || !tabs.Items.Contains(item))
                throw new InvalidOperationException("Declining a repeated tab close changed the live connection or removed the tab.");

            long closedBefore = Interlocked.Read(ref NativeTerminalCoreDiagnostics.ClosedModels);
            accept = true;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await terminal.Completion.WaitAsync(deadline.Token);
            while (tabs.Items.Contains(item)) await Task.Delay(15, deadline.Token);
            if (reviews != 2 || terminal.IsRunning || !host.IsVisible)
                throw new InvalidOperationException("The accepted tab close did not complete once and preserve the other tab.");
            if (Interlocked.Read(ref NativeTerminalCoreDiagnostics.ClosedModels) <= closedBefore)
                throw new InvalidOperationException("The visual close did not release the native terminal model.");
            if (!held.Contains("retained 界") || held.GetCells(0).IsEmpty)
                throw new InvalidOperationException("The held snapshot lost its output after visual removal.");
            try
            {
                await owner.GetOutputAsync(deadline.Token);
                throw new InvalidOperationException("A removed tab's session still published new snapshots.");
            }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            dialogTimer.Stop();
            terminal.Disconnect();
            await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            host.Close();
        }
    }

    private static async Task CheckWindowJoinsTerminalTabClose(App app, string directory, bool acceptWindow)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string profile = Path.Combine(directory, $"joined-close-{acceptWindow}.ttty");
        await ConnectionProfile.CreateTelnet("Joined close", "127.0.0.1", port).SaveNewAsync(profile, deadline.Token);

        var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        var first = new TerminalTab(profile);
        var second = new TerminalTab(profile);
        host.AddTerminalTab(first);
        host.AddTerminalTab(second);
        host.Show();
        int reviews = 0;
        int cleanupChoices = 0;
        bool firstHeldThroughReview = false;
        bool secondReviewedWhileFirstPending = false;
        Window? cleanupDialog = null;
        var dialogs = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        dialogs.Tick += (_, _) =>
        {
            Window? dialog = host.OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible);
            if (dialog is null) return;
            if (deadline.IsCancellationRequested)
            {
                dialog.Close();
                return;
            }
            var wait = FindControls<Button>(dialog).SingleOrDefault(button => Equals(button.Content, App.Text("ContinueWaiting")));
            if (wait is not null)
            {
                if (ReferenceEquals(dialog, cleanupDialog)) return;
                cleanupDialog = dialog;
                cleanupChoices++;
                ((IInvokeProvider)new ButtonAutomationPeer(wait).GetPattern(PatternInterface.Invoke)).Invoke();
                return;
            }
            bool approve = ++reviews == 1 || acceptWindow;
            if (reviews == 2) secondReviewedWhileFirstPending = !first.Completion.IsCompleted;
            FindControls<Button>(dialog).Single(button => Equals(button.Content, App.Text(approve ? "Yes" : "No")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        try
        {
            using TcpClient peerA = await ConnectAsync(first);
            using TcpClient peerB = await ConnectAsync(second);
            var tabs = (TabControl)host.FindName("tabs");
            var item = tabs.Items.OfType<TabItem>().Single(tab => ReferenceEquals(tab.Content, first));
            first.Disconnected += RequestWindowCloseWhileFirstFinishes;
            dialogs.Start();
            ((DockPanel)item.Header).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (tabs.Items.Contains(item)) await Task.Delay(10, deadline.Token);
            if (acceptWindow)
            {
                while (host.IsVisible) await Task.Delay(10, deadline.Token);
                if (second.IsRunning) throw new InvalidOperationException("Window closed before its second tab finished.");
            }
            else if (!host.IsVisible || second.InputSession is null)
                throw new InvalidOperationException("Refusing the window review affected its previously unreviewed tab.");
            if (reviews != 2 || !firstHeldThroughReview || !secondReviewedWhileFirstPending)
                throw new InvalidOperationException("The later window review did not join the in-progress terminal-tab close.");
            if (cleanupChoices != (acceptWindow ? 1 : 0))
                throw new InvalidOperationException("The joined tab did not keep its one pending cleanup decision.");
        }
        finally
        {
            first.Disconnected -= RequestWindowCloseWhileFirstFinishes;
            dialogs.Stop();
            first.Disconnect();
            second.Disconnect();
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            if (host.IsVisible) host.Close();
        }

        async Task<TcpClient> ConnectAsync(TerminalTab terminal)
        {
            terminal.Start();
            TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await peer.GetStream().ReadExactlyAsync(new byte[15], deadline.Token);
            while (terminal.InputSession is null) await Task.Delay(10, deadline.Token);
            return peer;
        }

        void RequestWindowCloseWhileFirstFinishes()
        {
            host.Close();
            var frame = new DispatcherFrame();
            var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            probe.Tick += (_, _) =>
            {
                if ((reviews < 2 || (acceptWindow && cleanupChoices == 0)) && !deadline.IsCancellationRequested) return;
                firstHeldThroughReview = !first.Completion.IsCompleted && host.IsVisible;
                frame.Continue = false;
            };
            probe.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { probe.Stop(); }
        }
    }
}
