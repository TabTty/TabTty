using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TabTty.App;

internal static partial class Program
{
    private static void CheckActiveWindowHeader(App app)
    {
        RequireDesktopInput();
#pragma warning disable WPF0001 // WPF Fluent theme integration.
        var previous = app.ThemeMode;
        var window = app.CreateWindow(restorePlacement: false);
        window.Width = 1100;
        window.Height = 650;
        window.ShowInTaskbar = false;
        window.Topmost = true;
        var peer = app.CreateWindow(restorePlacement: false);
        peer.ShowInTaskbar = false;
        try
        {
            app.ThemeMode = ThemeMode.Dark;
            window.OpenForm("Settings", _ => { });
            window.Show();
            peer.Show();
            var root = (FrameworkElement)window.Content;
            var host = (Border)window.FindName("groupHost");
            var titleBar = (Border)window.FindName("titleBarBackground");
            var group = (TabControl)window.FindName("tabs");
            var tabBar = TabBar(group);
            var peerTabBar = TabBar((TabControl)peer.FindName("tabs"));
            var exampleAccent = Color.FromRgb(0xA6, 0x38, 0x0C);

            Activate(window);
            window.SetActiveHeaderAccent(exampleAccent);
            Layout();
            Color darkShade = CheckBand(tabBar, 0);
            var activeBrush = tabBar.Background;
            int darkDifference = Brightness(((SolidColorBrush)window.Background).Color) - Brightness(darkShade);
            if (darkShade.R <= darkShade.G || darkShade.G <= darkShade.B)
                throw new InvalidOperationException("The dark header did not pick up the Windows accent.");
            if (titleBar.IsVisible || HasShade(peerTabBar))
                throw new InvalidOperationException("The single tab bar shade was not confined to the active window.");

            window.Width = 600;
            Layout();
            if (host.Margin.Top <= 0) throw new InvalidOperationException("The narrow window did not separate its caption.");
            CheckBand(tabBar, host.Margin.Top);
            if (titleBar.IsVisible)
                throw new InvalidOperationException("The narrow window shaded its separate caption without a split.");

            window.Width = 1100;
            Layout();
            var split = (Button)group.Template.FindName("splitGroupsStackButton", group);
            split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, split));
            Layout();
            var second = FindControls<TabControl>(root).Single(candidate => candidate != group);
            var secondTabBar = TabBar(second);
            CheckBand(titleBar, 0);
            CheckBand(tabBar, host.Margin.Top);
            CheckBand(secondTabBar, second.TranslatePoint(new Point(), root).Y);
            if (host.Margin.Top < titleBar.ActualHeight)
                throw new InvalidOperationException("The split tab bars overlapped the shaded title bar.");

            Activate(peer);
            if (HasShade(titleBar) || HasShade(tabBar) || HasShade(secondTabBar) || !HasShade(peerTabBar))
                throw new InvalidOperationException("Changing the active window did not move the header shading.");
            Activate(window);
            if (titleBar.Background != activeBrush || tabBar.Background != activeBrush || secondTabBar.Background != activeBrush)
                throw new InvalidOperationException("Reactivating the window did not reuse its prepared header brush.");

            Activate(peer);
            window.SetActiveHeaderAccent(null);
            if (HasShade(titleBar) || HasShade(tabBar) || HasShade(secondTabBar))
                throw new InvalidOperationException("Updating the accent shaded an inactive window.");
            Activate(window);
            if (CheckBand(titleBar, 0) == darkShade)
                throw new InvalidOperationException("Reactivating the window did not pick up the updated accent.");

            app.ThemeMode = ThemeMode.Light;
            window.SetActiveHeaderAccent(exampleAccent);
            Layout();
            CheckBand(tabBar, host.Margin.Top);
            CheckBand(secondTabBar, second.TranslatePoint(new Point(), root).Y);
            CheckLightBackground(titleBar);

            var remove = (Button)second.Template.FindName("removeTabGroupButton", second);
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, remove));
            Layout();
            if (host.Margin.Top != 0 || titleBar.IsVisible)
                throw new InvalidOperationException("Removing the split did not restore the shared tab bar.");
            CheckLightBackground(tabBar);

            app.ThemeMode = ThemeMode.Dark;
            Layout();
            CheckBand(tabBar, 0);

            void Layout()
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            Color CheckBand(Border header, double top)
            {
                if (!header.IsVisible || header.IsHitTestVisible ||
                    Math.Abs(header.TranslatePoint(new Point(), root).Y - top) > 0.5 ||
                    header.Background is not SolidColorBrush { Color: var shade })
                    throw new InvalidOperationException("The active shade did not follow the intended header row.");
                var background = ((SolidColorBrush)window.Background).Color;
                var pixel = SampleAt(top + header.ActualHeight - 2);
                if (Brightness(shade) >= Brightness(background) || pixel != shade)
                    throw new InvalidOperationException($"Expected the {shade} header over {background}, sampled {pixel}.");
                return shade;
            }

            void CheckLightBackground(Border header)
            {
                Color active = CheckBand(header, 0);
                Activate(peer);
                int difference = Brightness(SampleAt(header.ActualHeight - 2)) - Brightness(active);
                if (difference <= 0 || difference >= darkDifference)
                    throw new InvalidOperationException("Activation did not produce a subtler shade in the light theme.");
                Activate(window);
            }

            static int Brightness(Color color) => color.R + color.G + color.B;
            static bool HasShade(Border header) => header.Background is SolidColorBrush { Color.A: > 0 };
            static Border TabBar(TabControl tabs) => (Border)tabs.Template.FindName("tabBarBackground", tabs);

            Color SampleAt(double y)
            {
                var visual = new DrawingVisual();
                var pixel = new Rect(0, 0, 1, 1);
                using (DrawingContext drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(window.Background, null, pixel);
                    drawing.DrawRectangle(new VisualBrush(root)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(Math.Floor(root.ActualWidth / 2), y, 1, 1),
                    }, null, pixel);
                }
                var bitmap = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                byte[] bytes = new byte[4];
                bitmap.CopyPixels(bytes, 4, 0);
                return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
            }
        }
        finally
        {
            window.Close();
            peer.Close();
            app.ThemeMode = previous;
        }
#pragma warning restore WPF0001

        static void Activate(MainWindow target)
        {
            target.Activate();
            target.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (!target.IsActive) throw new InvalidOperationException("The test could not activate the target window.");
        }
    }

    private static void TabStripOverflowButtons(App app)
    {
        var window = new MainWindow(app);
        try
        {
            var root = (FrameworkElement)window.Content;
            var group = (TabControl)window.FindName("tabs");
            foreach (string key in new[] { "Settings", "ProfileCatalog", "Vault", "HighlightSets", "ConnectionType" })
                window.OpenForm(key, _ => { });
            group.SelectedIndex = 0;
            const double narrowWidth = 760;
            Layout(narrowWidth);
            var strip = (ScrollViewer)group.Template.FindName("tabStripScroller", group);
            var list = (Button)group.Template.FindName("tabListButton", group);
            var left = (Button)group.Template.FindName("scrollTabsLeftButton", group);
            var right = (Button)group.Template.FindName("scrollTabsRightButton", group);
            var add = (Button)group.Template.FindName("newTabButton", group);
            double fitWidth = narrowWidth + strip.ScrollableWidth - left.ActualWidth - right.ActualWidth;
            ExpectEdges(false, true);
            ExpectWidths(120);
            CheckTabHeaderLabels(window, (TabItem)group.Items[0], (TabItem)group.Items[1], expectedWidth: 120);
            if (AutomationProperties.GetName(left) != App.Text("ScrollTabsLeft") ||
                AutomationProperties.GetName(right) != App.Text("ScrollTabsRight") ||
                Math.Abs(list.TranslatePoint(new Point(list.ActualWidth, 0), left).X) > 0.5 ||
                Math.Abs(left.TranslatePoint(new Point(left.ActualWidth, 0), strip).X) > 0.5 ||
                Math.Abs(strip.TranslatePoint(new Point(strip.ActualWidth, 0), right).X) > 0.5 ||
                Math.Abs(right.TranslatePoint(new Point(right.ActualWidth, 0), add).X) > 0.5)
                throw new InvalidOperationException("The named scroll buttons must sit between the tab list and the strip, and between the strip and +.");

            Click(right);
            ExpectEdges(true, true);
            for (int index = 0; index < group.Items.Count && right.IsEnabled; index++) Click(right);
            ExpectEdges(true, false);
            Click(left);
            ExpectEdges(true, true);
            Layout(fitWidth - 1);
            if (left.Visibility != Visibility.Visible || right.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The scroll buttons must appear even when only one DIP is clipped.");
            Layout(fitWidth);
            ExpectEdges(false, false);
            ExpectWidths(120);
            var headers = (TabHeaderPanel)strip.Content;
            headers.Preview(null, (TabItem)group.Items[0], new Size(240, 34));
            root.UpdateLayout();
            if (left.Visibility != Visibility.Collapsed || right.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A provisional insertion gap brought up scroll buttons during a drag.");
            headers.Preview(null, null, default);
            root.UpdateLayout();
            Layout(fitWidth - 1);
            group.Items.RemoveAt(group.Items.Count - 1);
            root.UpdateLayout();
            ExpectEdges(false, false);
            ExpectWidths((120 * 5 - 1) / 4d);
            Layout(1500);
            ExpectEdges(false, false);
            ExpectWidths(240);
            Layout(narrowWidth);

            var split = (Button)group.Template.FindName("splitGroupsSideButton", group);
            split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, split));
            root.UpdateLayout();
            var second = FindControls<TabControl>(root).Single(candidate => candidate != group);
            for (int index = 0; index < 5; index++)
                second.Items.Add(new TabItem { Header = $"Other {index + 1}" });
            second.SelectedIndex = 0;
            root.UpdateLayout();
            var otherStrip = (ScrollViewer)second.Template.FindName("tabStripScroller", second);
            var otherRight = (Button)second.Template.FindName("scrollTabsRightButton", second);
            double originalOffset = strip.HorizontalOffset;
            otherRight.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, otherRight));
            root.UpdateLayout();
            if (otherStrip.HorizontalOffset <= 0 || strip.HorizontalOffset != originalOffset || second.SelectedIndex != 0)
                throw new InvalidOperationException("Each split group must scroll its own headers independently.");
            Layout(window.MinWidth);
            if (otherStrip.ViewportWidth < 48 || otherRight.ActualWidth < 24)
                throw new InvalidOperationException("A minimum-width tab group lost its draggable header area or scroll button.");

            void Layout(double width)
            {
                var size = new Size(width, 600);
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
            }

            void ExpectWidths(double width)
            {
                foreach (TabItem header in group.Items)
                {
                    var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(header);
                    if (Math.Abs(slot.Width - width) > 0.01 || header.ActualWidth > slot.Width + 0.01)
                        throw new InvalidOperationException($"Expected equally sized {width}-DIP tabs, found slot {slot.Width} and header {header.ActualWidth}.");
                }
            }

            void Click(Button button)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                root.UpdateLayout();
                if (group.SelectedIndex != 0)
                    throw new InvalidOperationException("Scrolling the tab headers changed the selected tab.");
            }

            void ExpectEdges(bool moreLeft, bool moreRight)
            {
                var fades = (strip.OpacityMask as LinearGradientBrush)?.GradientStops;
                var visibility = moreLeft || moreRight ? Visibility.Visible : Visibility.Collapsed;
                if (left.Visibility != visibility || right.Visibility != visibility ||
                    left.IsEnabled != moreLeft || right.IsEnabled != moreRight ||
                    (fades?[0].Color.A == 0) != moreLeft || (fades?[^1].Color.A == 0) != moreRight)
                    throw new InvalidOperationException($"Scroll buttons and fades disagree at {strip.HorizontalOffset} of {strip.ScrollableWidth}.");
            }
        }
        finally { window.Close(); }
    }
}
