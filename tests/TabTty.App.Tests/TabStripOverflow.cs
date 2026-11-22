using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TabTty.App;

internal static partial class Program
{
    private static void TabStripOverflowButtons(App app)
    {
        var window = new MainWindow(app);
        try
        {
            var root = (FrameworkElement)window.Content;
            var group = (TabControl)window.FindName("tabs");
            for (int index = 0; index < 5; index++)
                group.Items.Add(new TabItem { Header = $"Tab {index + 1}", Width = 220 });
            group.SelectedIndex = 0;
            const double narrowWidth = 900;
            Layout(narrowWidth);
            var strip = (ScrollViewer)group.Template.FindName("tabStripScroller", group);
            var buttons = (StackPanel)group.Template.FindName("tabScrollButtons", group);
            var left = (Button)group.Template.FindName("scrollTabsLeftButton", group);
            var right = (Button)group.Template.FindName("scrollTabsRightButton", group);
            var add = (Button)group.Template.FindName("newTabButton", group);
            double fitWidth = narrowWidth + strip.ScrollableWidth - buttons.ActualWidth;
            ExpectEdges(false, true);
            if (AutomationProperties.GetName(left) != App.Text("ScrollTabsLeft") ||
                AutomationProperties.GetName(right) != App.Text("ScrollTabsRight") ||
                Math.Abs(left.TranslatePoint(new Point(left.ActualWidth, 0), right).X) > 0.5 ||
                Math.Abs(right.TranslatePoint(new Point(right.ActualWidth, 0), add).X) > 0.5)
                throw new InvalidOperationException("The named left and right scroll buttons must be beside + in order.");

            Click(right);
            ExpectEdges(true, true);
            for (int index = 0; index < group.Items.Count && right.IsEnabled; index++) Click(right);
            ExpectEdges(true, false);
            Click(left);
            ExpectEdges(true, true);
            Layout(fitWidth - 1);
            if (buttons.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The scroll buttons must appear even when only one DIP is clipped.");
            Layout(fitWidth);
            ExpectEdges(false, false);
            var headers = (TabHeaderPanel)strip.Content;
            headers.Preview(null, (TabItem)group.Items[0], new Size(220, 34));
            root.UpdateLayout();
            if (buttons.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A provisional insertion gap brought up scroll buttons during a drag.");
            headers.Preview(null, null, default);
            root.UpdateLayout();
            Layout(fitWidth - 1);
            group.Items.RemoveAt(group.Items.Count - 1);
            root.UpdateLayout();
            ExpectEdges(false, false);

            var split = (Button)group.Template.FindName("splitGroupsSideButton", group);
            split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, split));
            root.UpdateLayout();
            var second = FindControls<TabControl>(root).Single(candidate => candidate != group);
            for (int index = 0; index < 5; index++)
                second.Items.Add(new TabItem { Header = $"Other {index + 1}", Width = 220 });
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
                bool visible = buttons.Visibility == Visibility.Visible;
                if (visible != (moreLeft || moreRight) || left.IsEnabled != moreLeft || right.IsEnabled != moreRight ||
                    (fades?[0].Color.A == 0) != moreLeft || (fades?[^1].Color.A == 0) != moreRight)
                    throw new InvalidOperationException($"Scroll buttons and fades disagree at {strip.HorizontalOffset} of {strip.ScrollableWidth}.");
            }
        }
        finally { window.Close(); }
    }
}
