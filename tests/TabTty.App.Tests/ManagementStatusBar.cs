using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TabTty.App;

internal static partial class Program
{
    private static async Task AcknowledgeWindowReportAsync(MainWindow owner, params string[] details)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var open = (Button)owner.FindName("windowReportButton");
        while (!deadline.IsCancellationRequested)
        {
            Window? review = owner.OwnedWindows.Cast<Window>().SingleOrDefault(window =>
                window.IsVisible && window.Title == App.Text("WindowReportsTitle"));
            if (review is null && open.IsVisible && open.IsEnabled)
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (review is not null)
            {
                var ok = FindControls<Button>(review).SingleOrDefault(button =>
                    Equals(button.Content, App.Text("OK")) &&
                    details.All(text => AutomationProperties.GetHelpText(button).Contains(text, StringComparison.OrdinalIgnoreCase)));
                if (ok is { IsVisible: true, IsEnabled: true })
                {
                    string message = AutomationProperties.GetHelpText(ok);
                    var values = FindControls<DialogText>((DependencyObject)ok.Parent).Where(text =>
                        text.IsVisible && text.IsReadOnly && text.Focusable).ToArray();
                    if (values.Length == 0 || (values.Length == 1 ? message != values[0].Text :
                        !values.Skip(1).All(value => AutomationProperties.GetLabeledBy(value) is TextBlock label &&
                            message.Contains(label.Text, StringComparison.Ordinal) && message.Contains(value.Text, StringComparison.Ordinal))))
                        throw new InvalidOperationException("The report did not expose its full selectable details.");
                    ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
            }
            await Task.Delay(15, deadline.Token);
        }
        throw new InvalidOperationException("The expected details were unavailable from the window status bar.");
    }

    private static void CheckVaultStatus(MainWindow window, bool unlocked)
    {
        string state = App.Text(unlocked ? "VaultStatusUnlocked" : "VaultStatusLocked");
        var indicator = (TextBlock)window.FindName("vaultStatus");
        window.UpdateLayout();
        if (!indicator.IsVisible || indicator.Text != state || AutomationProperties.GetName(indicator) != state ||
            FindControls<TextBlock>(window).Count(text => text.IsVisible && text.Text == state) != 1 ||
            indicator.TranslatePoint(new Point(indicator.ActualWidth, indicator.ActualHeight), window).X < window.ActualWidth - 20)
            throw new InvalidOperationException($"The window did not show its selected vault's state once on the right: {state}.");
    }

    private static async Task CheckSharedStatusRowAsync(MainWindow window)
    {
        window.OpenForm("Settings", _ => { });
        var form = (FormView)((TabControl)window.FindName("tabs")).SelectedContent;
        var local = form.Body.Children.OfType<Border>().Single(child => DockPanel.GetDock(child) == Dock.Bottom &&
            child.Child is TextBlock);
        var status = (TextBlock)local.Child;
        var shared = (FrameworkElement)window.FindName("windowStatusBar");
        using (form.BeginProgress("SavingChanges"))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (status.Text.Length == 0) await Task.Delay(20, timeout.Token);
            window.UpdateLayout();
            var left = local.TransformToAncestor(window).TransformBounds(new Rect(local.RenderSize));
            var message = status.TransformToAncestor(window).TransformBounds(new Rect(status.RenderSize));
            var right = shared.TransformToAncestor(window).TransformBounds(new Rect(shared.RenderSize));
            if (status.Text != App.Text("SavingChanges") || message.Width < 1 || right.Width < 1 ||
                Math.Abs(left.Top - right.Top) > 1 || Math.Abs(left.Bottom - right.Bottom) > 1 ||
                !left.Contains(message) || message.Right > right.Left + 1)
                throw new InvalidOperationException($"The form's progress {message} and window status {right} did not share the bottom row {left}.");
            CheckVaultStatus(window, unlocked: false);
        }
        form.RequestClose(() => true);
    }

    private static void CheckVaultStatusSeparator(MainWindow window, FormView form)
    {
        var root = (FrameworkElement)window.Content;
        var local = form.Body.Children.OfType<Border>().Single(child =>
            DockPanel.GetDock(child) == Dock.Bottom && child.Child is TextBlock);
        var indicator = (TextBlock)window.FindName("vaultStatus");
        var left = local.TransformToAncestor(root).TransformBounds(new Rect(local.RenderSize));
        var vault = indicator.TransformToAncestor(root).TransformBounds(new Rect(indicator.RenderSize));
        int leftX = (int)(left.Left + left.Width / 2);
        int vaultX = (int)(vault.Left + vault.Width / 2);
        var bitmap = RenderLayout(root, window.Background);
        int top = (int)Math.Floor(left.Top);
        var background = Pixel(leftX, top + 4);
        int dividerY = Enumerable.Range(top - 2, 5).FirstOrDefault(y => Pixel(leftX, y) != background, -1);
        if (dividerY < 0)
            throw new InvalidOperationException($"The profile status row has no visible top divider near y={top}.");
        var divider = Pixel(leftX, dividerY);
        var right = Pixel(vaultX, dividerY);
        var edge = Pixel(bitmap.PixelWidth - 2, dividerY);
        if (divider != right || edge != divider)
            throw new InvalidOperationException(
                $"The profile status divider at y={dividerY} did not span the window: {divider} at x={leftX}, " +
                $"{right} above the vault at x={vaultX}, {edge} at the right edge.");
        var statusBounds = new Rect(0, dividerY + 1, root.ActualWidth, root.ActualHeight - dividerY - 1);
        if (vault.Width <= 0 || vault.Height <= 0 || !statusBounds.Contains(vault))
            throw new InvalidOperationException($"The vault at {vault} exceeded the profile status bounds {statusBounds}.");

        Color Pixel(int x, int y)
        {
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }
    }

    // Read the actual report and acknowledge its own button, including every expected detail.
    private static async Task AcknowledgeReportAsync(FrameworkElement owner, params string[] details)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (;;)
        {
            foreach (var stack in FindControls<NoticeStack>(owner).Where(stack => stack.IsVisible))
            {
                var ok = FindControls<Button>(stack).SingleOrDefault(button =>
                    Equals(button.Content, App.Text("OK")) &&
                    details.All(text => AutomationProperties.GetHelpText(button).Contains(text, StringComparison.OrdinalIgnoreCase)));
                if (ok is { IsVisible: true, IsEnabled: true })
                {
                    ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
                var more = FindControls<Button>(stack).FirstOrDefault(button => button.IsVisible &&
                    button.Content is string caption && caption == string.Format(App.Text("NoticeStackShow"), 1));
                more?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            await Task.Delay(15, deadline.Token);
        }
    }

    private static async Task CheckManagementStatusBarAsync()
    {
        var form = new FormView();
        var search = new TextBox();
        var list = new ListView();
        form.AddField("SearchProfiles", search);
        form.AddFillingDisplay("ProfileGroups", list);
        form.AddStatusBar();
        var bar = form.Body.Children.OfType<Border>().Single();
        var status = (TextBlock)bar.Child;

        var bounds = new Rect(0, 0, 360, 280);
        var initial = Layout();
        if (status.Text.Length != 0 || initial.Bar.Bottom != bounds.Bottom || initial.Bar.Height == 0)
            throw new InvalidOperationException("An empty status bar did not reserve a line at the bottom.");
        using (form.BeginProgress("SavingChanges"))
        {
            if (status.Text.Length != 0) throw new InvalidOperationException("Fast operation showed a status.");
        }
        await Task.Delay(550);
        if (status.Text.Length != 0) throw new InvalidOperationException("Completed operation showed a late status.");

        using (form.BeginProgress("CheckingPreviousSave"))
        {
            if (status.Text.Length != 0) throw new InvalidOperationException("Progress appeared before the delay.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (status.Text.Length == 0) await Task.Delay(20, timeout.Token);
            if (status.Text != App.Text("CheckingPreviousSave") || initial != Layout())
                throw new InvalidOperationException("Progress showed the wrong message or rearranged the form.");
        }
        if (status.Text.Length != 0 || initial != Layout())
            throw new InvalidOperationException("Clearing progress rearranged the form or left stale text.");
        const string multiLine = "Vault unavailable.\nOpen vault management to create it.";
        form.SetStatusMessage(multiLine);
        if (status.Text.Contains('\n') || !Equals(status.ToolTip, multiLine) ||
            AutomationProperties.GetName(status) != multiLine || initial != Layout())
            throw new InvalidOperationException("A multiline status resized the bar or lost its full text.");

        (Rect Search, Rect List, Rect Bar) Layout()
        {
            form.Measure(bounds.Size);
            form.Arrange(bounds);
            form.UpdateLayout();
            return (Position(search), Position(list), Position(bar));
        }

        Rect Position(FrameworkElement element) =>
            element.TransformToAncestor(form).TransformBounds(new Rect(element.RenderSize));
    }
}
