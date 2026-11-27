using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using static TabTty.App.App;

namespace TabTty.App;

/// <summary>A named value that shares its caption layout with every visible field in its window.</summary>
internal sealed class DialogField : Panel
{
    internal const double Spacing = 16;
    private const double InlineGap = 4;
    private static readonly ConditionalWeakTable<Window, FieldGroup> groups = new();
    private readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap };
    private readonly FrameworkElement value;
    private readonly FrameworkElement labelTarget;
    private FieldGroup? group;
    private bool requiresStack;
    private bool stacked;

    internal DialogField(string caption, FrameworkElement value, FrameworkElement? labelTarget = null)
    {
        this.value = value;
        Margin = new Thickness(0, 0, 0, Spacing);
        SetCaption(caption);
        Children.Add(this.caption);
        Children.Add(value);
        this.labelTarget = labelTarget ?? value;
        AutomationProperties.SetLabeledBy(this.labelTarget, this.caption);
        SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = this.labelTarget });
        IsVisibleChanged += (_, _) => SetGroup(IsVisible ? Window.GetWindow(this) : null);
        if (this.labelTarget is DialogText text) text.TextChanged += (_, _) => InvalidateMeasure();
    }

    internal void SetCaption(string name)
    {
        string text = string.Format(Text("DialogFieldCaption"), name);
        if (caption.Text == text) return;
        caption.Text = text;
        InvalidateMeasure();
    }

    internal static bool HasLineBreak(string text) => text.AsSpan().ContainsAny("\r\n\v\f\u0085\u2028\u2029");

    private void SetGroup(Window? window)
    {
        var next = window is null ? null : groups.GetValue(window, static owner => new FieldGroup(owner));
        if (next == group) return;
        group?.Members.Remove(this);
        if (requiresStack) group?.ChangeStackCount(-1);
        group = next;
        group?.Members.Add(this);
        group?.ChangeStackCount(requiresStack ? 1 : 0);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        bool needsStack = !FitsOneLine(available);
        if (needsStack != requiresStack)
        {
            requiresStack = needsStack;
            group?.ChangeStackCount(needsStack ? 1 : -1);
        }
        stacked = requiresStack || group?.Stacked != false;
        if (stacked)
        {
            caption.Measure(available);
            value.Measure(available);
            return new Size(Math.Max(caption.DesiredSize.Width, value.DesiredSize.Width),
                caption.DesiredSize.Height + value.DesiredSize.Height);
        }
        return new Size(caption.DesiredSize.Width + InlineGap + value.DesiredSize.Width,
            Math.Max(caption.DesiredSize.Height, value.DesiredSize.Height));
    }

    private bool FitsOneLine(Size available)
    {
        string? text = labelTarget switch
        {
            DialogText { IsFramed: false } display => display.Text,
            TextBlock display => display.Text,
            _ => null,
        };
        if (text is null || HasLineBreak(text) || HasLineBreak(caption.Text)) return false;
        var natural = new Size(double.PositiveInfinity, available.Height);
        caption.Measure(natural);
        value.Measure(natural);
        return caption.DesiredSize.Width + InlineGap + value.DesiredSize.Width <= available.Width;
    }

    protected override Size ArrangeOverride(Size size)
    {
        double height = caption.DesiredSize.Height;
        if (stacked)
        {
            caption.Arrange(new Rect(0, 0, size.Width, height));
            value.Arrange(new Rect(0, height, size.Width, Math.Max(0, size.Height - height)));
        }
        else
        {
            double width = caption.DesiredSize.Width;
            caption.Arrange(new Rect(0, 0, width, height));
            value.Arrange(new Rect(width + InlineGap, 0, value.DesiredSize.Width, size.Height));
        }
        return size;
    }

    // Pinned fields, scrolling content and separate reports all belong to the same window.
    private sealed class FieldGroup(Window window)
    {
        internal HashSet<DialogField> Members { get; } = [];
        private int stackCount;
        private bool updatePending;
        internal bool Stacked { get; private set; } = true;

        internal void ChangeStackCount(int delta)
        {
            stackCount += delta;
            if (updatePending || Stacked == (stackCount > 0)) return;
            updatePending = true;
            window.LayoutUpdated += ApplyLayout;
        }

        // A later sibling can require two lines after the parent has already measured the first.
        // Change the shared decision between layout passes so every parent allocates the new height.
        private void ApplyLayout(object? sender, EventArgs args)
        {
            window.LayoutUpdated -= ApplyLayout;
            updatePending = false;
            bool next = stackCount > 0;
            if (Stacked == next) return;
            Stacked = next;
            foreach (var member in Members) member.InvalidateMeasure();
        }
    }
}
