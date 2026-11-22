using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void CheckTabPreviewLayout(App app)
    {
        var window = new MainWindow(app);
        try
        {
            var root = (FrameworkElement)window.Content;
            var group = (TabControl)window.FindName("tabs");
            var incoming = new TabItem { Header = "Incoming" };
            var size = new Size(120, 30);
            root.Measure(new Size(1000, 500));
            root.Arrange(new Rect(0, 0, 1000, 500));
            root.UpdateLayout();
            var panel = (TabHeaderPanel)group.Template.FindName("tabHeaderPanel", group);
            panel.Preview(null, null, size);
            root.UpdateLayout();
            if (panel.GapBounds is not Rect empty || empty.Left != 0 || empty.Width != size.Width || group.Items.Count != 0)
                throw new InvalidOperationException("An empty group must reserve and show its first slot before insertion.");

            panel.Preview(null, null, default);
            var first = new TabItem { Header = "First", Width = 100 };
            var last = new TabItem { Header = "Last", Width = 150 };
            group.Items.Add(first);
            group.Items.Add(last);
            group.SelectedItem = first;
            root.UpdateLayout();
            double originalLeft = last.TranslatePoint(new Point(), panel).X;
            double between = first.DesiredSize.Width + last.DesiredSize.Width / 4;
            if (panel.HeaderAfterDrop(between, incoming) != last)
                throw new InvalidOperationException("The proposed insertion did not use the headers' actual widths.");
            panel.Preview(null, last, size);
            root.UpdateLayout();
            if (panel.GapBounds is not Rect gap || gap.Width != size.Width || gap.Left < first.ActualWidth - 1 ||
                last.TranslatePoint(new Point(), panel).X < gap.Right - 1 || group.SelectedItem != first ||
                panel.HeaderAfterDrop(between, incoming) != last || group.Items.Contains(incoming))
                throw new InvalidOperationException("Preview moved the stable slot, selected tab, or tab ownership.");
            panel.Preview(null, null, default);
            root.UpdateLayout();
            if (panel.GapBounds is not null || Math.Abs(last.TranslatePoint(new Point(), panel).X - originalLeft) > 1)
                throw new InvalidOperationException("Leaving the bar did not restore its original geometry.");
        }
        finally { window.Close(); }
    }

    private static async Task CheckTabInteractions(App app, string directory)
    {
        TabStripOverflowButtons(app);
        CheckTabPreviewLayout(app);
        RequireDesktopInput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string path = Path.Combine(directory, "drag-session.ttty");
        await ConnectionProfile.CreateTelnet("B", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .SaveNewAsync(path, deadline.Token);

        Rect work = SystemParameters.WorkArea;
        var source = CreateDragWindow(app, work.Left + 30, work.Top + 24);
        var target = CreateDragWindow(app, source.Left, source.Top + source.Height + 10);
        var sessionTab = new TerminalTab(path);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionTab.Connected += _ => ready.TrySetResult();
        try
        {
            var sourceTabs = (TabControl)source.FindName("tabs");
            var targetTabs = (TabControl)target.FindName("tabs");
            source.OpenForm("Settings", _ => { });
            source.AddTerminalTab(sessionTab);
            source.OpenForm("ProfileCatalog", _ => { });
            target.OpenForm("Settings", _ => { });
            target.OpenForm("ProfileCatalog", _ => { });
            TabItem a = (TabItem)sourceTabs.Items[0], b = (TabItem)sourceTabs.Items[1], c = (TabItem)sourceTabs.Items[2];
            TabItem d = (TabItem)targetTabs.Items[0], e = (TabItem)targetTabs.Items[1];
            foreach (var header in new[] { a, b, c, d, e }) header.Width = 120;
            sourceTabs.SelectedItem = b;
            target.Show();
            source.Show();
            source.UpdateLayout();
            target.UpdateLayout();
            var inactiveHeader = (FrameworkElement)a.Header;
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            inactiveHeader.RaiseEvent(press);
            if (press.Handled) throw new InvalidOperationException("Starting a potential drag consumed a normal tab press.");
            press.RoutedEvent = Mouse.MouseDownEvent;
            inactiveHeader.RaiseEvent(press);
            sourceTabs.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            if (sourceTabs.SelectedItem != a || Mouse.Captured == sourceTabs)
                throw new InvalidOperationException("Clicking an inactive tab must select it and release the drag capture.");
            sourceTabs.SelectedItem = b;
            sessionTab.Start();
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
            if (await Task.WhenAny(accepting, sessionTab.Completion) != accepting)
                throw new InvalidOperationException($"The loopback session could not connect: {sessionTab.ConnectionStatus}");
            using var peer = await accepting;
            await peer.GetStream().ReadExactlyAsync(new byte[15], deadline.Token);
            await ready.Task.WaitAsync(deadline.Token);
            var session = sessionTab.InputSession ?? throw new InvalidOperationException("The drag fixture needs a live session.");

            var sourcePanel = HeaderPanel(sourceTabs);
            var targetPanel = HeaderPanel(targetTabs);
            Point pastC = ScreenPoint(c, 0.60), beforeA = ScreenPoint(a, 0.25);
            Begin(source, sourceTabs, b);
            source.UpdateTabDrag(pastC);
            double gap = GapCenter(sourcePanel);
            if (gap <= HeaderCenter(c, sourcePanel))
                throw new InvalidOperationException("Dragging B past C did not move C ahead of the gap.");
            source.UpdateTabDrag(pastC);
            if (Math.Abs(gap - GapCenter(sourcePanel)) > 0.5)
                throw new InvalidOperationException("The insertion gap moved under a stationary pointer.");
            source.UpdateTabDrag(beforeA);
            if (GapCenter(sourcePanel) >= HeaderCenter(a, sourcePanel))
                throw new InvalidOperationException("Reversing the drag did not open the gap before A.");
            ExpectOrder(sourceTabs, a, b, c);
            await peer.GetStream().WriteAsync("\u001b[?1006h"u8.ToArray(), deadline.Token);
            while (!await session.ObserveAiAsync((_, state) => state.Mouse.SgrCoordinates, deadline.Token))
                await Task.Delay(10, deadline.Token);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(b),
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            b.RaiseEvent(escape);
            if (!escape.Handled || Mouse.Captured == sourceTabs)
                throw new InvalidOperationException("Escape did not cancel the tab gesture and release its capture.");
            ExpectNoGap(sourcePanel);
            ExpectOrder(sourceTabs, a, b, c);

            Begin(source, sourceTabs, b);
            source.UpdateTabDrag(pastC);
            source.EndTabDrag(pastC);
            ExpectOrder(sourceTabs, a, c, b);
            ExpectNoGap(sourcePanel);

            Point between = ScreenPoint(d, 0.60);
            Begin(source, sourceTabs, b);
            source.UpdateTabDrag(between);
            ExpectBetween(d, e, targetPanel);
            ExpectNoGap(sourcePanel);
            var cover = new Window
            {
                Left = target.Left, Top = target.Top, Width = target.Width, Height = 120,
                WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, AllowDrop = true,
            };
            try
            {
                cover.Show();
                source.UpdateTabDrag(between);
                ExpectNoGap(targetPanel);
            }
            finally { cover.Close(); }
            source.UpdateTabDrag(between);
            ExpectBetween(d, e, targetPanel);
            var plus = (Button)targetTabs.Template.FindName("newTabButton", targetTabs);
            Point onPlus = ScreenPoint(plus, 0.5);
            source.UpdateTabDrag(onPlus);
            ExpectNoGap(targetPanel);
            source.EndTabDrag(onPlus);
            ExpectOrder(sourceTabs, a, c, b);
            ExpectOrder(targetTabs, d, e);

            Begin(source, sourceTabs, b);
            Point desktop = source.PointToScreen(new Point(-12, -12));
            source.UpdateTabDrag(desktop);
            source.EndTabDrag(desktop);
            await source.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            var detached = app.Windows.OfType<MainWindow>().Single(w => w != source && w != target);
            var detachedTabs = (TabControl)detached.FindName("tabs");
            ExpectOrder(sourceTabs, a, c);
            ExpectOrder(detachedTabs, b);
            detached.Left = source.Left;
            detached.Top = source.Top;
            detached.Height = source.Height;
            detached.Topmost = true;
            detached.UpdateLayout();
            Begin(detached, detachedTabs, b);
            detached.UpdateTabDrag(between);
            ExpectBetween(d, e, targetPanel);
            ExpectOrder(detachedTabs, b);
            if (detached.IsClosing) throw new InvalidOperationException("Preview closed the last tab's window early.");
            detached.EndTabDrag(between);
            ExpectOrder(targetTabs, d, b, e);
            if (!detached.IsClosing || sessionTab.InputSession != session || !target.TerminalTabs.Contains(sessionTab))
                throw new InvalidOperationException("Moving the last tab did not preserve its session and retire the source window.");
            await peer.GetStream().WriteAsync("\u001b[?1006l"u8.ToArray(), deadline.Token);
            while (await session.ObserveAiAsync((_, state) => state.Mouse.SgrCoordinates, deadline.Token))
                await Task.Delay(10, deadline.Token);
        }
        finally
        {
            foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) window.CancelTabDrag();
            sessionTab.Disconnect();
            await sessionTab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var window in app.Windows.OfType<MainWindow>().Where(w => !w.IsClosing).ToArray()) window.Close();
        }

        static MainWindow CreateDragWindow(App app, double left, double top) => new(app)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = left, Top = top, Width = 840, Height = 320, ShowInTaskbar = false, Topmost = true,
        };
        static TabHeaderPanel HeaderPanel(TabControl tabs) =>
            (TabHeaderPanel)tabs.Template.FindName("tabHeaderPanel", tabs);
        static Point ScreenPoint(FrameworkElement header, double x) =>
            header.PointToScreen(new Point(header.ActualWidth * x, header.ActualHeight / 2));
        static double HeaderCenter(TabItem header, TabHeaderPanel panel) =>
            header.TranslatePoint(new Point(header.ActualWidth / 2, 0), panel).X;
        static void Begin(MainWindow window, TabControl group, TabItem tab)
        {
            window.UpdateLayout();
            if (!window.BeginTabDrag(group, tab, ScreenPoint(tab, 0.5)))
                throw new InvalidOperationException("The tab header did not start its drag.");
        }
        static double GapCenter(TabHeaderPanel panel)
        {
            panel.UpdateLayout();
            if (panel.GapBounds is not Rect gap || gap.Width <= 0)
                throw new InvalidOperationException("The bar did not show an insertion gap.");
            return gap.Left + gap.Width / 2;
        }
        static void ExpectNoGap(TabHeaderPanel panel)
        {
            panel.UpdateLayout();
            if (panel.GapBounds is not null) throw new InvalidOperationException("A stale insertion gap remained on the bar.");
        }
        static void ExpectBetween(TabItem left, TabItem right, TabHeaderPanel panel)
        {
            double gap = GapCenter(panel);
            if (gap <= HeaderCenter(left, panel) || gap >= HeaderCenter(right, panel))
                throw new InvalidOperationException("The destination did not preview inserting B between D and E.");
        }
        static void ExpectOrder(TabControl group, params TabItem[] order)
        {
            if (!group.Items.Cast<TabItem>().SequenceEqual(order))
                throw new InvalidOperationException("The tab group has an unexpected order or a different tab instance.");
        }
    }
}
