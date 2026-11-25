using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static TabTty.App.App;

namespace TabTty.App;

/// <summary>Owns incidents for one view, including the two presentations of a background incident.</summary>
internal sealed class ViewNotifications(NoticeStack stack, Func<Window?> host, Func<bool> active,
    Func<string> name, Action select, string navigationLabel = "NoticeShowTab") : IDisposable
{
    private sealed class Incident(bool background)
    {
        public bool Background { get; } = background;
        public long Order { get; } = NoticeStack.ReserveOrder();
        public TextBlock? Label;
        public FrameworkElement? Row;
        public Button? Navigate;
        public Button? Acknowledge;
        public string Message = "";
    }

    private readonly Dictionary<object, Incident> incidents = [];
    private NoticeStack presentation = stack;
    private Window? window;
    private StackPanel? rows;
    private bool announceWhenHosted;
    private bool disposed;
    public event Action? Changed;
    public bool HasUnread => incidents.Values.Any(item => item.Background);
    public bool HasReports => incidents.Count != 0;

    public void Report(object key, string message)
    {
        if (disposed) return;
        bool added = false;
        if (!incidents.TryGetValue(key, out var item))
        {
            item = new Incident(!active());
            incidents.Add(key, item);
            added = true;
        }
        item.Message = message;
        presentation.ShowReport(item, message, () => Acknowledge(key, item), item.Order);
        if (item.Background)
        {
            if (item.Label is null) AddRow(key, item);
            item.Label!.Text = message;
            if (added)
            {
                announceWhenHosted = true;
                HostChanged();
            }
        }
        if (added) Changed?.Invoke();
    }

    public void UpdateIfPending(object key, string message)
    {
        if (incidents.ContainsKey(key)) Report(key, message);
    }

    public void PresentIn(NoticeStack next)
    {
        if (next == presentation) return;
        foreach (var (key, item) in incidents)
        {
            presentation.Remove(item);
            next.ShowReport(item, item.Message, () => Acknowledge(key, item), item.Order);
        }
        presentation = next;
    }

    private void Acknowledge(object key, Incident item)
    {
        if (incidents.GetValueOrDefault(key) != item) return;
        incidents.Remove(key);
        presentation.Remove(item);
        if (item.Row is not null) rows?.Children.Remove(item.Row);
        if (!HasUnread) CloseWindow();
        Changed?.Invoke();
    }

    private void AddRow(object key, Incident item)
    {
        rows ??= new StackPanel();
        var row = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        item.Row = row;
        item.Label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        row.Children.Add(item.Label);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button AddAction(string label, Action action)
        {
            var button = new Button { MinWidth = 72, Margin = new Thickness(6, 6, 0, 0) };
            SetLabel(button, label);
            button.Click += (_, _) => action();
            actions.Children.Add(button);
            return button;
        }
        item.Navigate = AddAction(navigationLabel, () => { if (incidents.GetValueOrDefault(key) == item) select(); });
        item.Acknowledge = AddAction("OK", () => Acknowledge(key, item));
        row.Children.Add(actions);
        rows.Children.Add(row);
    }

    public void UpdateLabels()
    {
        if (window is not null) window.Title = string.Format(Text("NoticeForView"), name());
        foreach (var item in incidents.Values)
        {
            if (item.Navigate is { } show) SetLabel(show, navigationLabel);
            if (item.Acknowledge is { } ok) SetLabel(ok, "OK");
        }
    }

    private static void SetLabel(Button button, string key)
    {
        button.Content = Text(key);
        AutomationProperties.SetName(button, Text(key));
    }

    public void HostChanged()
    {
        if (host() is not { IsVisible: true } owner || rows is null || window is null && !announceWhenHosted) return;
        Window? visibleOwner = owner.WindowState == WindowState.Minimized ? null : owner;
        if (window is null)
        {
            window = new Window
            {
                Icon = WindowIcons.App, ShowActivated = false,
                Width = Math.Min(440, SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height * 0.65),
                WindowStartupLocation = visibleOwner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "WindowForeground");
            var dialog = window;
            dialog.Closed += (_, _) =>
            {
                if (dialog.Content is ScrollViewer scroller) scroller.Content = null;
                if (window == dialog) window = null;
            };
        }
        if (window.Owner != visibleOwner) window.Owner = visibleOwner;
        window.ShowInTaskbar = visibleOwner is null;
        window.Title = string.Format(Text("NoticeForView"), name());
        announceWhenHosted = false;
        if (!window.IsVisible) window.Show();
    }

    private void CloseWindow()
    {
        announceWhenHosted = false;
        window?.Close();
        rows = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var item in incidents.Values) presentation.Remove(item);
        incidents.Clear();
        CloseWindow();
        Changed = null;
    }
}
