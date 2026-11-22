using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TabTty.App;

// A tab drag changes only header layout. The TabControl keeps its selection,
// content presenter and session host until the move commits.
internal sealed class TabHeaderPanel : TabPanel
{
    private TabItem? held;
    private TabItem? before;
    private Size gapSize;
    internal Rect? GapBounds { get; private set; }
    internal double AddedPreviewWidth => held is null ? gapSize.Width : 0;

    internal TabItem? HeaderAfterDrop(double x, TabItem dragged)
    {
        // Use the original header midpoints. Hit testing shifted headers would
        // switch the proposed order back and forth under a stationary pointer.
        double left = 0;
        foreach (TabItem header in InternalChildren)
        {
            double width = header.DesiredSize.Width;
            if (header != dragged && x < left + width / 2) return header;
            left += width;
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
        Size measured = base.MeasureOverride(constraint);
        if (AddedPreviewWidth == 0) return measured;
        return new Size(measured.Width + AddedPreviewWidth, Math.Max(measured.Height, gapSize.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (held is null && gapSize.Width == 0) return base.ArrangeOverride(arrangeSize);
        double x = 0;
        foreach (TabItem header in InternalChildren)
        {
            double width = header.DesiredSize.Width;
            if (header == held)
            {
                header.Arrange(new Rect(-width - 1, 0, width, arrangeSize.Height));
                continue;
            }
            if (header == before) MakeSpace();
            header.Arrange(new Rect(x, 0, width, arrangeSize.Height));
            x += width;
        }
        if (before is null) MakeSpace();
        return arrangeSize;

        void MakeSpace()
        {
            if (gapSize.Width == 0) return;
            GapBounds = new Rect(x, 0, gapSize.Width, arrangeSize.Height);
            x += gapSize.Width;
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
