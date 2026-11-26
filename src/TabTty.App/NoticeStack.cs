using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static TabTty.App.App;

namespace TabTty.App;

/// <summary>Ordered notices for one view. The owner supplies the available bounds and the identity of each incident.</summary>
internal sealed class NoticeStack : Grid
{
    // Paint an opaque card without intercepting terminal input in its padding or behind passive text.
    private sealed class Card : Border
    {
        internal Card()
        {
            Padding = new Thickness(6, 3, 6, 3);
            Margin = new Thickness(0, 0, 0, 2);
            BorderThickness = new Thickness(1);
            HorizontalAlignment = HorizontalAlignment.Right;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        }

        protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) => null;
    }

    private sealed class Entry(object key)
    {
        internal readonly object Key = key;
        internal readonly Card Frame = new();
        internal Card? Compact;
        internal bool KeepVisible;
        internal TextBlock? Message;
        internal Button? Ok;
        internal Action? Acknowledge;
        internal TimeSpan? Remaining;
        internal long? VisibleSince;
    }

    private readonly List<Entry> entries = [];
    private readonly StackPanel direct = new();
    private readonly StackPanel overflowItems = new();
    private readonly Button overflowButton = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ScrollViewer overflow = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly DispatcherTimer timer = new();
    private bool expanded, hintsActive = true;
    private int overflowCount;
    internal bool HasItems => entries.Count > 0;

    public NoticeStack()
    {
        MaxWidth = 440;
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        Children.Add(direct);
        Children.Add(overflowButton);
        Children.Add(overflow);
        SetRow(overflowButton, 1);
        SetRow(overflow, 2);
        overflow.Content = overflowItems;
        overflowButton.Visibility = overflow.Visibility = Visibility.Collapsed;
        overflowButton.Click += (_, _) =>
        {
            expanded = !expanded;
            InvalidateMeasure();
        };
        timer.Tick += (_, _) => UpdateHintClocks();
        LayoutUpdated += (_, _) => { if (entries.Any(item => item.Remaining.HasValue)) UpdateHintClocks(); };
        IsVisibleChanged += (_, _) => UpdateHintClocks();
        Unloaded += (_, _) => UpdateHintClocks();
    }

    public bool HintsActive
    {
        get => hintsActive;
        set
        {
            if (hintsActive == value) return;
            hintsActive = value;
            foreach (var item in entries.Where(item => item.Remaining.HasValue))
                item.Frame.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateHintClocks();
            InvalidateMeasure();
        }
    }

    private Entry Get(object key)
    {
        var item = entries.Find(item => Equals(item.Key, key));
        if (item is not null) return item;
        item = new Entry(key);
        entries.Add(item);
        direct.Children.Add(item.Frame);
        InvalidateMeasure();
        return item;
    }

    public void ShowContent(object key, FrameworkElement content, bool keepVisible = false, FrameworkElement? compactActions = null)
    {
        var item = Get(key);
        if (item.Frame.Child == content && item.KeepVisible == keepVisible && item.Compact?.Child == compactActions) return;
        if (item.Message is not null) ResetPresentation(item);
        item.Frame.Child = content;
        item.KeepVisible = keepVisible;
        if (compactActions is not null)
        {
            item.Compact ??= new Card();
            item.Compact.Child = compactActions;
        }
        else if (item.Compact is { } compact)
        {
            Detach(compact);
            item.Compact = null;
        }
        InvalidateMeasure();
    }

    public void ShowReport(object key, string message, Action acknowledge)
    {
        var item = Get(key);
        if (item.Ok is null)
        {
            ResetPresentation(item);
            var body = new StackPanel();
            item.Message = NoticeText();
            item.Ok = new Button { HorizontalAlignment = HorizontalAlignment.Right, Content = Text("OK") };
            var captured = item.Ok;
            captured.Click += (_, _) => { if (item.Ok == captured) item.Acknowledge?.Invoke(); };
            body.Children.Add(item.Message);
            body.Children.Add(item.Ok);
            item.Frame.Child = body;
        }
        item.Acknowledge = acknowledge;
        UpdateMessage(item.Message!, message);
        AutomationProperties.SetHelpText(item.Ok, message);
        InvalidateMeasure();
    }

    public void ShowHint(object key, string message, TimeSpan visibleDuration)
    {
        if (visibleDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(visibleDuration));
        var item = Get(key);
        if (item.Remaining is null)
        {
            ResetPresentation(item);
            item.Frame.Child = item.Message = NoticeText();
            item.Frame.Visibility = HintsActive ? Visibility.Visible : Visibility.Collapsed;
            item.Remaining = visibleDuration;
        }
        UpdateMessage(item.Message!, message);
        InvalidateMeasure();
    }

    public void UpdateHintText(object key, string message)
    {
        if (entries.Find(item => Equals(item.Key, key)) is { Remaining: not null, Message: { } label })
            UpdateMessage(label, message);
    }

    private static TextBlock NoticeText()
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
        label.SetResourceReference(TextBlock.ForegroundProperty, "WindowForeground");
        AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
        label.IsVisibleChanged += (_, _) =>
        {
            if (label.IsVisible) UIElementAutomationPeer.CreatePeerForElement(label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
        return label;
    }

    private static void UpdateMessage(TextBlock label, string message)
    {
        if (label.Text == message) return;
        label.Text = message;
        UIElementAutomationPeer.FromElement(label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void Remove(object key)
    {
        if (entries.Find(item => Equals(item.Key, key)) is not { } item) return;
        entries.Remove(item);
        Detach(item.Frame);
        ResetPresentation(item);
        if (!entries.Any(entry => entry.Remaining.HasValue)) timer.Stop();
        InvalidateMeasure();
    }

    private static void ResetPresentation(Entry item)
    {
        item.Message = null;
        item.Ok = null;
        item.Acknowledge = null;
        item.Remaining = null;
        item.VisibleSince = null;
        item.KeepVisible = false;
        item.Frame.Child = null;
        item.Frame.Visibility = Visibility.Visible;
        if (item.Compact is { } compact)
        {
            Detach(compact);
            compact.Child = null;
            item.Compact = null;
        }
    }

    public void UpdateLabels()
    {
        foreach (var item in entries)
            if (item.Ok is { } ok) ok.Content = Text("OK");
        UpdateOverflowLabel();
    }

    private void UpdateOverflowLabel()
    {
        string text = string.Format(Text(expanded ? "NoticeStackHide" : "NoticeStackShow"), overflowCount);
        if (Equals(overflowButton.Content, text)) return;
        overflowButton.Content = text;
        AutomationProperties.SetName(overflowButton, text);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Measure full cards even when the list is collapsed. Content and localization can change their size.
        var available = new Size(constraint.Width, double.PositiveInfinity);
        double height = 0;
        foreach (var item in entries)
        {
            item.Frame.Measure(available);
            height += item.Frame.DesiredSize.Height;
        }
        bool compact = height > constraint.Height;
        int directIndex = 0, overflowIndex = 0;
        foreach (var item in entries)
        {
            bool listed = compact && (!item.KeepVisible || item.Compact is not null);
            if (listed) Place(item.Frame, overflowItems, overflowIndex++);
            else Place(item.Frame, direct, directIndex++);
            if (item.Compact is not { } actions) continue;
            if (listed) Place(actions, direct, directIndex++);
            else Detach(actions);
        }
        overflowCount = overflowItems.Children.Cast<UIElement>().Count(element => element.Visibility == Visibility.Visible);
        if (overflowCount == 0) expanded = false;
        overflowButton.Visibility = overflowCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        overflow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateOverflowLabel();
        return base.MeasureOverride(constraint);
    }

    private static void Place(FrameworkElement element, Panel parent, int index)
    {
        if (index < parent.Children.Count && ReferenceEquals(parent.Children[index], element)) return;
        Detach(element);
        parent.Children.Insert(index, element);
    }

    private static void Detach(FrameworkElement element) => (element.Parent as Panel)?.Children.Remove(element);

    private void UpdateHintClocks()
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan? next = null;
        foreach (var item in entries.Where(item => item.Remaining.HasValue).ToArray())
        {
            TimeSpan remaining = item.Remaining!.Value;
            if (item.VisibleSince is long started) remaining -= Stopwatch.GetElapsedTime(started, now);
            if (remaining <= TimeSpan.Zero)
            {
                Remove(item.Key);
                continue;
            }
            item.Remaining = remaining;
            item.VisibleSince = null;
            if (!HintsActive || !IsLoaded || !IsVisible || !item.Frame.IsVisible || item.Frame.ActualHeight <= 0 ||
                item.Frame.Parent is null) continue;
            if (item.Frame.Parent == overflowItems)
            {
                double top = item.Frame.TranslatePoint(new Point(), overflowItems).Y - overflow.VerticalOffset;
                if (top < -0.5 || top + item.Frame.ActualHeight > overflow.ViewportHeight + 0.5) continue;
            }
            item.VisibleSince = now;
            if (next is null || remaining < next) next = remaining;
        }
        if (next is null) timer.Stop();
        else
        {
            timer.Interval = next.Value;
            timer.Start();
        }
    }
}
