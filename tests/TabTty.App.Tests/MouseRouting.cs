using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckMouseRoutingAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string path = Path.Combine(directory, "mouse-routing.ttty");
        await ConnectionProfile.CreateTelnet("Mouse routing", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .SaveNewAsync(path, token);

        var tab = new TerminalTab(path);
        var window = new MainWindow(app, hasForeground: () => true);
        window.AddTerminalTab(tab);
        window.Show();
        window.SelectNotificationTab(tab);
        window.UpdateLayout();
        tab.FocusPane();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            tab.Start();
            using var peer = await listener.AcceptTcpClientAsync(token);
            var stream = peer.GetStream();
            await stream.ReadExactlyAsync(new byte[15], token);
            var surface = FindControls<TerminalSurface>(tab).Single();
            var header = (FrameworkElement)FindControls<TabItem>(window).Single(item => item.Content == tab).Header;
            var tabMenu = header.ContextMenu;
            var paneMenu = surface.ContextMenu;
            tabMenu.PlacementTarget = header;
            tabMenu.IsOpen = true;
            try
            {
                CheckMenuDidNotAnimate(tabMenu);
                var submenu = tabMenu.Items.OfType<MenuItem>().First(item => item.HasItems);
                await OpenMenuWithoutAnimationAsync(submenu);
            }
            finally { tabMenu.IsOpen = false; }
            if (FindCommand(tabMenu, "UseMouseInTabTty") is not null)
                throw new InvalidOperationException("The tab offered local mouse routing before the host requested tracking.");
            await stream.WriteAsync("\u001b[?1049h\u001b[?1000h\u001b[?1006h"u8.ToArray(), token);
            await WaitForTrackingAsync(true);
            if (!surface.CanReportMouse || !Shows("MouseDestinationHost") || !Shows("MouseSentToHostNotice") ||
                FindCommand(paneMenu, "SendMouseToHost") is not null)
                throw new InvalidOperationException($"Default host routing: mouse={surface.CanReportMouse}, " +
                    $"status={Shows("MouseDestinationHost")}, hint={Shows("MouseSentToHostNotice")}, " +
                    $"current={window.IsCurrentView(tab)}, activeWindow={window.IsActive}, " +
                    $"selected={((TabControl)window.FindName("tabs")).SelectedContent == tab}, clock={tab.Notices.HintsActive}.");

            Invoke(tabMenu, "UseMouseInTabTty");
            if (surface.CanReportMouse || !Shows("MouseDestinationTabTty") || Shows("MouseSentToHostNotice"))
                throw new InvalidOperationException("The tab menu did not switch mouse routing to TabTty.");

            await stream.WriteAsync("\u001b[?1000l"u8.ToArray(), token);
            await WaitForTrackingAsync(false);
            if (FindCommand(paneMenu, "SendMouseToHost") is not null ||
                FindCommand(tabMenu, "SendMouseToHost") is not null || Shows("MouseDestinationTabTty"))
                throw new InvalidOperationException("A menu offered host mouse routing without tracking.");

            await stream.WriteAsync("\u001b[?1000h"u8.ToArray(), token);
            await WaitForTrackingAsync(true);
            if (surface.CanReportMouse || FindCommand(tabMenu, "SendMouseToHost") is null)
                throw new InvalidOperationException("A tracking-mode change discarded the tab's local preference.");
            Invoke(paneMenu, "SendMouseToHost");
            if (!surface.CanReportMouse || !Shows("MouseDestinationHost") || !Shows("MouseSentToHostNotice"))
                throw new InvalidOperationException("The pane menu did not restore host routing and show its notice.");

            while (Shows("MouseSentToHostNotice")) await Task.Delay(20, token);
            tab.UpdateLabels();
            if (Shows("MouseSentToHostNotice"))
                throw new InvalidOperationException("An update repeated the notice while host routing was still active.");

            await stream.WriteAsync("\u001b[?1000l"u8.ToArray(), token);
            await WaitForTrackingAsync(false);
            await stream.WriteAsync("\u001b[?1000h"u8.ToArray(), token);
            await WaitForTrackingAsync(true);
            if (!surface.CanReportMouse || !Shows("MouseSentToHostNotice"))
                throw new InvalidOperationException("The notice did not reappear when the host re-enabled mouse tracking.");

            async Task WaitForTrackingAsync(bool enabled)
            {
                while (surface.MouseTrackingRequested != enabled) await Task.Delay(20, token);
            }

            bool Shows(string key) => FindControls<TextBlock>(tab).Any(label => label.IsVisible && label.Text == App.Text(key));
        }
        finally
        {
            tab.Disconnect();
            await tab.Completion;
            window.Close();
        }

        static MenuItem? FindCommand(ContextMenu menu, string key)
        {
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            return menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, App.Text(key)));
        }

        static void Invoke(ContextMenu menu, string key)
        {
            var item = FindCommand(menu, key) ?? throw new InvalidOperationException($"Missing mouse command: {key}");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        }
    }
}
