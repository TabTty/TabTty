using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TabTty.App;

internal sealed class TabStripScroller : ScrollViewer
{
    protected override Size MeasureOverride(Size constraint)
    {
        // The scroll presenter measures its content with infinite width.
        if (Content is TabHeaderPanel headers) headers.SetAvailableWidth(constraint.Width);
        return base.MeasureOverride(constraint);
    }
}

// A tab drag changes only header layout. The TabControl keeps its selection,
// content presenter and session host until the move commits.
internal sealed class TabHeaderPanel : TabPanel
{
    private const double MinimumTabWidth = 120;
    private const double MaximumTabWidth = 240;
    private double availableWidth = double.PositiveInfinity;
    private double tabWidth;
    private TabItem? held;
    private TabItem? before;
    private Size gapSize;
    internal Rect? GapBounds { get; private set; }
    internal double AddedPreviewWidth => held is null ? gapSize.Width : 0;

    internal void SetAvailableWidth(double width)
    {
        if (availableWidth == width) return;
        availableWidth = width;
        InvalidateMeasure();
    }

    internal TabItem? HeaderAfterDrop(double x, TabItem dragged)
    {
        // Use the original header midpoints. Hit testing shifted headers would
        // switch the proposed order back and forth under a stationary pointer.
        double left = 0;
        foreach (TabItem header in InternalChildren)
        {
            if (header.Visibility == Visibility.Collapsed) continue;
            if (header != dragged && x < left + tabWidth / 2) return header;
            left += tabWidth;
        }
        return null;
    }

    internal void Preview(TabItem? heldHeader, TabItem? followingHeader, Size space)
    {
        if (held == heldHeader && before == followingHeader && gapSize == space) return;
        held = heldHeader;
        before = followingHeader;
        gapSize = space;
        GapBounds = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        int count = InternalChildren.Cast<TabItem>().Count(header => header.Visibility != Visibility.Collapsed);
        tabWidth = count == 0 ? 0 : Math.Clamp(availableWidth / count, MinimumTabWidth, MaximumTabWidth);
        double height = gapSize.Height;
        foreach (TabItem header in InternalChildren)
        {
            header.Measure(new Size(tabWidth, constraint.Height));
            height = Math.Max(height, header.DesiredSize.Height);
        }
        return new Size(tabWidth * count + AddedPreviewWidth, height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        double previewWidth = held is not null && gapSize.Width > 0 ? tabWidth : gapSize.Width;
        double x = 0;
        foreach (TabItem header in InternalChildren)
        {
            if (header.Visibility == Visibility.Collapsed) continue;
            if (header == held)
            {
                header.Arrange(new Rect(-tabWidth - 1, 0, tabWidth, arrangeSize.Height));
                continue;
            }
            if (header == before) MakeSpace();
            header.Arrange(new Rect(x, 0, tabWidth, arrangeSize.Height));
            x += tabWidth;
        }
        if (before is null) MakeSpace();
        return arrangeSize;

        void MakeSpace()
        {
            if (previewWidth == 0) return;
            GapBounds = new Rect(x, 0, previewWidth, arrangeSize.Height);
            x += previewWidth;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (GapBounds is not { Width: > 2, Height: > 2 } gap) return;
        gap.Inflate(-1, -1);
        drawingContext.PushOpacity(0.18);
        drawingContext.DrawRoundedRectangle((Brush)FindResource("WindowForeground"), null, gap, 4, 4);
        drawingContext.Pop();
    }
}
