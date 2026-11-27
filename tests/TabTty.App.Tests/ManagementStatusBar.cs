using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            var right = shared.TransformToAncestor(window).TransformBounds(new Rect(shared.RenderSize));
            if (status.Text != App.Text("SavingChanges") || left.Width < 1 || right.Width < 1 ||
                Math.Abs(left.Bottom - right.Bottom) > 1 || left.Right > right.Left + 1)
                throw new InvalidOperationException($"The form's progress and window status did not share the bottom row: {left}, {right}.");
            CheckVaultStatus(window, unlocked: false);
        }
        form.RequestClose(() => true);
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
