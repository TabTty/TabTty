using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private MenuItem? windowReportsMenu;
    private Window? reportReview;
    private readonly StackPanel reportRows = new();

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
        private readonly TextBox details = new()
        {
            IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        };
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
        var scroll = new ScrollViewer { Content = reportRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var review = new Window
        {
            Owner = this, Icon = WindowIcons.App, ShowInTaskbar = false,
            Width = Math.Min(560, SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height * 0.75),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = scroll
        };
        review.SetResourceReference(BackgroundProperty, "WindowBackground");
        review.SetResourceReference(ForegroundProperty, "WindowForeground");
        review.Closed += (_, _) =>
        {
            scroll.Content = null;
            reportRows.Children.Clear();
            reportReview = null;
        };
        reportReview = review;
        UpdateWindowReports();
        review.Show();
    }
}
