namespace TabTty.App;

internal sealed record ReportField(string Caption, string Value, bool Framed = false, bool Inline = false)
{
    internal DialogField Create() => new(Caption, new DialogText(Value, Framed ? 250 : null), inline: Inline);
    internal static string Describe(IEnumerable<ReportField> fields) => string.Join("\n\n", fields.Select(item =>
        string.Format(App.Text("DialogFieldCaption"), item.Caption) + (item.Inline ? " " : "\n") + item.Value));
}

internal sealed record WindowReportContent(string Summary, ReportField[] Fields)
{
    internal string Details => ReportField.Describe(Fields);
}

internal sealed class WindowStatusReport(object key, MainWindow? window, WindowReportContent content)
{
    internal object Key { get; } = key;
    internal MainWindow? Window { get; } = window;
    internal WindowReportContent Content { get; set; } = content;
}

/// <summary>Holds window and application reports on the UI dispatcher until acknowledged.</summary>
internal sealed class WindowStatusReports
{
    private readonly List<WindowStatusReport> reports = [];
    internal event Action? Changed;

    internal IReadOnlyList<WindowStatusReport> Pending(MainWindow window) =>
        reports.Where(report => report.Window is null || report.Window == window).ToArray();

    internal Action<object, string> Capture(MainWindow? window, string summary)
    {
        var report = CaptureFields(window, summary);
        return (key, details) => report(key, [new(App.Text("DialogDetails"), details, Framed: true)]);
    }

    internal Action<object, ReportField[]> CaptureFields(MainWindow? window, string summary)
    {
        var operation = new object();
        return (key, fields) => Report((operation, key), window, new(summary, fields));
    }

    internal void Report(object key, MainWindow? window, WindowReportContent content)
    {
        if (window is { IsClosed: true }) return;
        var existing = reports.Find(report => report.Window == window && Equals(report.Key, key));
        if (existing is null) reports.Add(new WindowStatusReport(key, window, content));
        else
        {
            if (existing.Content.Summary == content.Summary && existing.Content.Fields.SequenceEqual(content.Fields)) return;
            existing.Content = content;
        }
        Changed?.Invoke();
    }

    internal void Acknowledge(WindowStatusReport report)
    {
        if (reports.Remove(report)) Changed?.Invoke();
    }

    internal void WindowClosed(MainWindow window)
    {
        reports.RemoveAll(report => report.Window == window);
    }
}
