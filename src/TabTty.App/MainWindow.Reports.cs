using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private MenuItem? windowReportsMenu;
    private Window? reportReview;
    private Button? reportClose;
    private readonly StackPanel reportRows = new();
    private readonly TextBlock reportCount = new() { Margin = new Thickness(14, 8, 14, 4), TextWrapping = TextWrapping.Wrap };

    private void UpdateWindowReports()
    {
        var pending = app.StatusReports.Pending(this);
        string summary = pending.Count switch
        {
            0 => "",
            1 => pending[0].Summary,
            _ => string.Format(Text("WindowReportSummary"), pending.Count, pending[^1].Summary)
        };
        windowReportSummary.Text = summary.ReplaceLineEndings(" ");
        windowReportButton.ToolTip = summary;
        AutomationProperties.SetName(windowReportButton, summary);
        windowReportButton.Visibility = pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (windowReportsMenu is not null) windowReportsMenu.IsEnabled = pending.Count > 0;
        UpdateStatusWidths();
        if (reportReview is null) return;
        if (pending.Count == 0) { reportReview.Close(); return; }
        reportReview.Title = Text("WindowReportsTitle");
        if (reportClose is not null) reportClose.Content = Text("CloseDialog");
        reportCount.Text = string.Format(Text("PendingOperationReports"), pending.Count);

        // Preserve focus and text selection when another report arrives or is acknowledged.
        var existing = reportRows.Children.OfType<ReportRow>().ToDictionary(row => row.Report);
        foreach (var report in pending)
        {
            if (!existing.Remove(report, out var row))
            {
                row = new ReportRow(report, app.StatusReports);
                reportRows.Children.Add(row);
            }
            row.Update();
        }
        foreach (var row in existing.Values) reportRows.Children.Remove(row);
    }

    private sealed class ReportRow : StackPanel
    {
        internal WindowStatusReport Report { get; }
        private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly DialogText details = new("", 250);
        private readonly Button ok = new() { MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right };

        internal ReportRow(WindowStatusReport report, WindowStatusReports reports)
        {
            Report = report;
            Margin = new Thickness(14, 8, 14, 8);
            Children.Add(heading);
            Children.Add(details);
            Children.Add(ok);
            ok.Click += (_, _) => reports.Acknowledge(report);
        }

        internal void Update()
        {
            heading.Text = Report.Summary;
            heading.Visibility = Report.Details.StartsWith(Report.Summary, StringComparison.Ordinal)
                ? Visibility.Collapsed : Visibility.Visible;
            if (details.Text != Report.Details) details.Text = Report.Details;
            AutomationProperties.SetName(details, Report.Summary);
            ok.Content = Text("OK");
            AutomationProperties.SetHelpText(ok, Report.Details);
        }
    }

    private void OpenWindowReports(object sender, RoutedEventArgs args) => ShowWindowReports();

    private void ShowWindowReports()
    {
        if (reportReview is { } open) { open.Activate(); return; }
        if (app.StatusReports.Pending(this).Count == 0) return;
        var body = new StackPanel { Children = { reportCount, reportRows } };
        var layout = new DialogLayout(body, new Thickness(0, 0, 8, 8));
        var review = new Window
        {
            Owner = this, Icon = WindowIcons.App, ShowInTaskbar = false,
            Width = Math.Min(560, SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout
        };
        DialogLayout.SetWindowBounds(review);
        review.MaxHeight = Math.Min(review.MaxHeight, Math.Max(160, SystemParameters.WorkArea.Height * 0.75));
        var close = DialogLayout.Action(Text("CloseDialog"));
        reportClose = close;
        close.Click += (_, _) => review.Close();
        review.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape) return;
            args.Handled = true;
            review.Close();
        };
        layout.Actions.Children.Add(close);
        review.SetResourceReference(BackgroundProperty, "WindowBackground");
        review.SetResourceReference(ForegroundProperty, "WindowForeground");
        review.Closed += (_, _) =>
        {
            body.Children.Clear();
            reportRows.Children.Clear();
            reportReview = null;
            reportClose = null;
        };
        reportReview = review;
        UpdateWindowReports();
        review.Show();
    }
}
