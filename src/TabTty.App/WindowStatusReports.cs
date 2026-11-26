namespace TabTty.App;

internal sealed class WindowStatusReport(object key, MainWindow? window, string summary, string details)
{
    internal object Key { get; } = key;
    internal MainWindow? Window { get; } = window;
    internal string Summary { get; set; } = summary;
    internal string Details { get; set; } = details;
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
        var operation = new object();
        return (key, details) => Report((operation, key), window, summary, details);
    }

    internal void Report(object key, MainWindow? window, string summary, string details)
    {
        if (window is { IsClosed: true }) return;
        var existing = reports.Find(report => report.Window == window && Equals(report.Key, key));
        if (existing is null) reports.Add(new WindowStatusReport(key, window, summary, details));
        else
        {
            if (existing.Summary == summary && existing.Details == details) return;
            existing.Summary = summary;
            existing.Details = details;
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
