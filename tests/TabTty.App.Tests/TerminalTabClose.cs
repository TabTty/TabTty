using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
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

            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (reviews == 0) await Task.Delay(15, deadline.Token);
            if (reviews != 1 || terminal.InputSession is null || !tabs.Items.Contains(item))
                throw new InvalidOperationException("Declining a repeated tab close changed the live connection or removed the tab.");

            accept = true;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await terminal.Completion.WaitAsync(deadline.Token);
            while (tabs.Items.Contains(item)) await Task.Delay(15, deadline.Token);
            if (reviews != 2 || terminal.IsRunning || !host.IsVisible)
                throw new InvalidOperationException("The accepted tab close did not complete once and preserve the other tab.");
        }
        finally
        {
            dialogTimer.Stop();
            terminal.Disconnect();
            await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            host.Close();
        }
    }
}
