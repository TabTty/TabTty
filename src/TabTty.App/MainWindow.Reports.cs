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
    private readonly TextBlock reportCount = new() { TextWrapping = TextWrapping.Wrap };
    private DialogField? reportCountField;

    private void UpdateWindowReports()
    {
        var pending = app.StatusReports.Pending(this);
        string summary = pending.Count switch
        {
            0 => "",
            1 => pending[0].Content.Summary,
            _ => string.Format(Text("WindowReportSummary"), pending.Count, pending[^1].Content.Summary)
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
        reportCountField?.SetCaption(Text("PendingOperationReports"));
        reportCount.Text = string.Format(Text("OperationReportCount"), pending.Count);

        // Preserve focus and text selection when another report arrives or is acknowledged.
        var existing = reportRows.Children.OfType<ReportRow>().ToDictionary(row => row.Report);
        foreach (var report in pending)
        {
            if (!existing.Remove(report, out var row))
            {
                row = new ReportRow(this, report, app.StatusReports);
                reportRows.Children.Add(row);
            }
            row.Update();
        }
        foreach (var row in existing.Values) reportRows.Children.Remove(row);
    }

    private sealed class ReportRow : StackPanel
    {
        internal WindowStatusReport Report { get; }
        private readonly DialogText summary = new("");
        private readonly DialogField summaryField;
        private readonly StackPanel fields = new();
        private readonly Button ok = new() { MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Button recheck = new() { HorizontalAlignment = HorizontalAlignment.Right };
        private readonly MainWindow owner;
        private WindowReportContent? displayed;

        internal ReportRow(MainWindow owner, WindowStatusReport report, WindowStatusReports reports)
        {
            this.owner = owner;
            Report = report;
            Margin = new Thickness(0, 0, 0, 8);
            summaryField = new DialogField(Text("OperationReportSummaryLabel"), summary);
            Children.Add(summaryField);
            Children.Add(fields);
            Children.Add(recheck);
            Children.Add(ok);
            ok.Click += (_, _) => reports.Acknowledge(report);
            recheck.Click += async (_, _) =>
            {
                if (FindStore() is not { } store) return;
                recheck.IsEnabled = false;
                try
                {
                    string[] paths = await JsonStoreRecovery.RecheckAsync(owner.app, store);
                    reports.Acknowledge(report);
                    owner.app.Modal(() => ThemedMessageBox.Show(owner,
                        paths.Length == 0 ? Text("StoreRechecked") : string.Join('\n', paths), Text("RecheckFilePublication")));
                }
                catch (Exception error) when (ProfileCatalog.IsFileProblem(error))
                {
                    owner.app.Modal(() => ThemedMessageBox.Show(owner, error.Message, Text("RecheckFilePublication"),
                        icon: MessageBoxImage.Error));
                }
                finally { recheck.IsEnabled = true; }
            };
        }

        private JsonStoreRecovery.Store? FindStore() => owner.app.RecheckableStores().FirstOrDefault(store =>
            TabTty.Core.VerifiedFilePublication.IsUnresolved(store.Path) &&
            Report.Content.Fields.Any(field => string.Equals(field.Value, store.Path, StringComparison.OrdinalIgnoreCase)));

        internal void Update()
        {
            var content = Report.Content;
            summaryField.SetCaption(Text("OperationReportSummaryLabel"));
            ok.Content = Text("OK");
            recheck.Content = Text("RecheckFilePublication");
            recheck.Visibility = FindStore() is null ? Visibility.Collapsed : Visibility.Visible;
            if (ReferenceEquals(displayed, content)) return;
            displayed = content;
            summary.Text = content.Summary;
            fields.Children.Clear();
            foreach (var field in content.Fields) fields.Children.Add(field.Create());
            AutomationProperties.SetHelpText(ok, content.Fields.Length > 0 ? content.Details : content.Summary);
        }
    }

    private void OpenWindowReports(object sender, RoutedEventArgs args) => ShowWindowReports();

    private void ShowWindowReports()
    {
        if (reportReview is { } open) { open.Activate(); return; }
        if (app.StatusReports.Pending(this).Count == 0) return;
        reportCountField ??= new DialogField(Text("PendingOperationReports"), reportCount);
        var body = new StackPanel { Children = { reportCountField, reportRows } };
        var layout = new DialogLayout(body, new Thickness(14, 8, 14, 8));
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
            reportRows.Children.Clear();
            body.Children.Clear();
            reportReview = null;
            reportClose = null;
        };
        reportReview = review;
        UpdateWindowReports();
        review.Show();
    }
}
