using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void CheckTabHeaderClicks(App app)
    {
        RequireDesktopInput();
        var window = new MainWindow(app) { Width = 760, ShowInTaskbar = false, Topmost = true };
        GetCursorPos(out var original);
        try
        {
            for (int index = 0; index < 3; index++)
                window.AddTerminalTab(new TerminalTab($"tab-input-{index}.ttty"));
            window.OpenForm("Settings", _ => { });
            window.Show();
            var group = (TabControl)window.FindName("tabs");
            var tabs = group.Items.Cast<TabItem>().ToArray();
            group.SelectedIndex = 0;
            DrainInput();
            for (int click = 0; click < 8; click++)
                ClickAndCheck(tabs[1 - group.SelectedIndex]);

            tabs[0].Focus();
            for (int index = 1; index < tabs.Length; index++) PressAndCheck(Key.Right, tabs[index]);
            PressAndCheck(Key.Right, tabs[^1]);
            var strip = (ScrollViewer)group.Template.FindName("tabStripScroller", group);
            if (strip.HorizontalOffset <= 0)
                throw new InvalidOperationException("Switching to a hidden tab did not scroll it into view.");
            for (int index = tabs.Length - 2; index >= 0; index--) PressAndCheck(Key.Left, tabs[index]);
            PressAndCheck(Key.Left, tabs[0]);
            ClickAndCheck(tabs[0]);

            void ClickAndCheck(TabItem expected)
            {
                ClickTabBarElement(expected);
                var pane = FindControls<TerminalSurface>((TerminalTab)expected.Content).Single();
                WaitForTabBarInput(() => group.SelectedItem == expected && pane.IsKeyboardFocusWithin,
                    "Clicking a terminal tab did not select it and focus its pane.");
                if (Mouse.Captured == group)
                    throw new InvalidOperationException("A normal tab click left the pointer captured by the tab group.");
            }

            void PressAndCheck(Key key, TabItem expected)
            {
                PressTabBarKey(key);
                WaitForTabBarInput(() => group.SelectedItem == expected && expected.IsKeyboardFocused,
                    $"{key} did not select tab {Array.IndexOf(tabs, expected)} and retain header focus.");
            }
        }
        finally
        {
            window.Close();
            SetCursorPos(original.X, original.Y);
        }
    }

    private static void ClickTabBarElement(FrameworkElement element)
    {
        MovePointerToTabBarElement(element);
        MouseEvent(0x0002, 0, 0, 0, 0);
        MouseEvent(0x0004, 0, 0, 0, 0);
        DrainInput();
    }

    private static void MovePointerToTabBarElement(FrameworkElement element)
    {
        element.UpdateLayout();
        var point = element.PointToScreen(new Point(element.ActualWidth / 3, element.ActualHeight / 2));
        if (!SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)))
            throw new InvalidOperationException("The test could not move the pointer onto the tab bar.");
        DrainInput();
    }

    private static void PressTabBarKey(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        const uint extendedKey = 0x0001, keyUp = 0x0002;
        byte virtualKey = (byte)KeyInterop.VirtualKeyFromKey(key);
        uint flags = key is Key.Left or Key.Right or Key.PageUp or Key.PageDown ? extendedKey : 0;
        bool control = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (control)
            KeyEvent((byte)KeyInterop.VirtualKeyFromKey(Key.LeftCtrl), 0, 0, 0);
        if (shift)
            KeyEvent((byte)KeyInterop.VirtualKeyFromKey(Key.LeftShift), 0, 0, 0);
        try
        {
            if (modifiers != ModifierKeys.None)
                WaitForTabBarInput(() => Keyboard.Modifiers == modifiers,
                    "The shortcut modifiers did not reach the window.");
            KeyEvent(virtualKey, 0, flags, 0);
            KeyEvent(virtualKey, 0, flags | keyUp, 0);
        }
        finally
        {
            if (shift)
                KeyEvent((byte)KeyInterop.VirtualKeyFromKey(Key.LeftShift), 0, keyUp, 0);
            if (control)
                KeyEvent((byte)KeyInterop.VirtualKeyFromKey(Key.LeftCtrl), 0, keyUp, 0);
        }
        if (modifiers != ModifierKeys.None)
            WaitForTabBarInput(() => Keyboard.Modifiers == ModifierKeys.None, "The shortcut modifiers did not release.");
        DrainInput();
    }

    private static void DrainInput() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void WaitForTabBarInput(Func<bool> ready, string message)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            DrainInput();
            if (ready()) return;
            Thread.Sleep(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(2));
        throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event", ExactSpelling = true)]
    private static extern void KeyEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

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
            var first = new TabItem { Header = "First" };
            var last = new TabItem { Header = "Last" };
            group.Items.Add(first);
            group.Items.Add(last);
            group.SelectedItem = first;
            root.UpdateLayout();
            double originalLeft = last.TranslatePoint(new Point(), panel).X;
            double between = originalLeft + last.ActualWidth / 4;
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
            target.OpenForm("HighlightSets", _ => { });
            target.OpenForm("ProfileCatalog", _ => { });
            TabItem a = (TabItem)sourceTabs.Items[0], b = (TabItem)sourceTabs.Items[1], c = (TabItem)sourceTabs.Items[2];
            TabItem d = (TabItem)targetTabs.Items[0], e = (TabItem)targetTabs.Items[1];
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
            CheckConnectedTabFocus(target, targetTabs, d, b, e, sessionTab);
            await ChangeKeyboardShortcutsAsync(target, targetTabs, b, e, sessionTab, directory, deadline.Token);
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

    private static void CheckConnectedTabFocus(MainWindow window, TabControl group, TabItem first,
        TabItem header, TabItem last, TerminalTab terminal)
    {
        window.Activate();
        var list = (Button)group.Template.FindName("tabListButton", group);
        if (!GetCursorPos(out var pointer)) throw new InvalidOperationException("Could not read the pointer position.");
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (VisualCheckForegroundWindow() != handle) ClickTabBarElement(header);
            WaitForTabBarInput(() => VisualCheckForegroundWindow() == handle, "The connected tab window did not acquire desktop input.");
            group.SelectedItem = first;
            first.Focus();
            WaitForTabBarInput(() => first.IsKeyboardFocused, "Could not focus the first tab header.");
            PressTabBarKey(Key.Right);
            WaitForTabBarInput(() => group.SelectedItem == header && header.IsKeyboardFocused,
                "Entering the connected tab with Right did not keep focus on its header.");
            PressTabBarKey(Key.Right);
            WaitForTabBarInput(() => group.SelectedItem == last && last.IsKeyboardFocused,
                "Right did not continue past the connected tab.");

            var pane = FindControls<TerminalSurface>(terminal).Single();
            MovePointerToTabBarElement(list);
            MouseEvent(0x0800, 0, 0, 120, 0);
            WaitForTabBarInput(() => group.SelectedItem == header && pane.IsKeyboardFocusWithin,
                "Scrolling the mouse wheel did not focus the newly selected terminal pane.");

            last.Focus();
            ClickTabBarElement(list);
            WaitForTabBarInput(() => list.ContextMenu?.IsOpen == true, "The tab list did not open.");
            var connection = (MenuItem)list.ContextMenu.Items[1];
            connection.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, connection));
            list.ContextMenu.IsOpen = false;
            WaitForTabBarInput(() => group.SelectedItem == header && pane.IsKeyboardFocusWithin,
                "Picking the connected tab from the list did not focus its pane.");

            last.Focus();
            ClickTabBarElement(header);
            WaitForTabBarInput(() => group.SelectedItem == header && pane.IsKeyboardFocusWithin,
                "Clicking the connected tab did not focus its terminal pane.");
            PressTabBarKey(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
            if (!pane.IsKeyboardFocusWithin || group.SelectedItem != header)
                throw new InvalidOperationException("The retired shortcut changed the focused terminal tab or pane.");

            ExpectKeyboardSelection(Key.Tab, ModifierKeys.Control, last);
            ExpectKeyboardSelection(Key.Tab, ModifierKeys.Control, first);
            ExpectKeyboardSelection(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, last);
            ExpectKeyboardSelection(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, header);
        }
        finally { SetCursorPos(pointer.X, pointer.Y); }

        void ExpectKeyboardSelection(Key key, ModifierKeys modifiers, TabItem expected)
        {
            PressTabBarKey(key, modifiers);
            WaitForTabBarInput(() => group.SelectedItem == expected && expected.IsKeyboardFocused,
                "The tab shortcut did not select and focus the expected tab header.");
        }
    }

    private static async Task ChangeKeyboardShortcutsAsync(MainWindow window, TabControl group, TabItem header,
        TabItem next, TerminalTab terminal, string directory, CancellationToken cancellationToken)
    {
        OpenSettings(window);
        window.Activate();
        window.UpdateLayout();
        var form = (FormView)group.SelectedContent;
        var categories = FindControls<ListBox>(form).Single();
        categories.SelectedItem = categories.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "SettingsKeyboardShortcuts"));
        window.UpdateLayout();
        Change("NextTabShortcut", "Ctrl+PageDown");
        Change("PreviousTabShortcut", null);
        Change("Copy", null);
        Change("Paste", "Ctrl+Shift+P");
        Change("Search", "Ctrl+Shift+G");
        var save = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("Save")));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, save));
        while (form.IsBusy) await Task.Delay(10, cancellationToken);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!.AsObject();
        var saved = KeyboardShortcuts.Read(settings);
        if (saved[ShortcutAction.NextTab] != "Ctrl+PageDown" ||
            saved[ShortcutAction.PreviousTab] is not null || saved[ShortcutAction.Copy] is not null ||
            saved[ShortcutAction.Paste] != "Ctrl+Shift+P" || saved[ShortcutAction.Search] != "Ctrl+Shift+G")
            throw new InvalidOperationException("Settings did not save the changed and disabled shortcuts.");

        group.SelectedItem = header;
        window.UpdateLayout();
        DrainInput();
        header.Focus();
        const ModifierKeys controlShift = ModifierKeys.Control | ModifierKeys.Shift;
        PressTabBarKey(Key.Tab, ModifierKeys.Control);
        PressTabBarKey(Key.Tab, controlShift);
        if (!header.IsKeyboardFocused || group.SelectedItem != header)
            throw new InvalidOperationException("A remapped or disabled Ctrl+Tab shortcut still switched tabs.");
        PressTabBarKey(Key.PageDown, ModifierKeys.Control);
        WaitForTabBarInput(() => next.IsKeyboardFocused && group.SelectedItem == next,
            "The reassigned next-tab shortcut did not move to the next header.");

        group.SelectedItem = header;
        window.UpdateLayout();
        terminal.FocusPane();
        var search = FindControls<TextBox>(terminal).Single(box => AutomationProperties.GetName(box) == App.Text("Search"));
        PressTabBarKey(Key.F, controlShift);
        if (search.IsVisible) throw new InvalidOperationException("The old search shortcut opened search.");
        PressTabBarKey(Key.G, controlShift);
        WaitForTabBarInput(() => search.IsKeyboardFocused, "The reassigned search shortcut did not open terminal search.");

        void Change(string label, string? binding)
        {
            var choice = FindControls<ComboBox>(form).Single(box =>
                AutomationProperties.GetLabeledBy(box) is Label { Content: TextBlock text } && text.Text == App.Text(label));
            choice.SelectedIndex = binding is null ? 0 : choice.Items.IndexOf(binding);
        }
    }
}
