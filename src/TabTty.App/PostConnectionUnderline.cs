using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TabTty.App;

// Draw over the existing text layout so editing, selection, scrolling and undo remain native.
internal sealed class PostConnectionUnderline : FrameworkElement
{
    private readonly TextBox source;
    private static readonly Pen errorPen = MakePen();
    internal int Start { get; private set; }
    internal int Length { get; private set; }

    internal PostConnectionUnderline(TextBox source)
    {
        this.source = source;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "PostConnectionUnderline");
    }

    internal void Mark(int start, int length)
    {
        Start = start;
        Length = length;
        Visibility = Visibility.Visible;
        InvalidateVisual();
    }

    internal void Clear() => Visibility = Visibility.Collapsed;

    private static Pen MakePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xcf, 0x4b, 0x47)), 1.5);
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (source.LineCount < 1 || Start > source.Text.Length) return;
        int end = Math.Min(Start + Length, source.Text.Length);
        int first = Math.Max(source.GetFirstVisibleLineIndex(), source.GetLineIndexFromCharacterIndex(Start));
        int last = Math.Min(source.GetLastVisibleLineIndex(), source.GetLineIndexFromCharacterIndex(Math.Max(Start, end - 1)));
        if (first < 0 || last < first) return;
        Rect viewport = new(RenderSize);
        if (source.Template.FindName("PART_ContentHost", source) is ScrollViewer viewer &&
            viewer.Template.FindName("PART_ScrollContentPresenter", viewer) is FrameworkElement presenter)
            viewport = new(presenter.TranslatePoint(new Point(), this), presenter.RenderSize);
        drawing.PushClip(new RectangleGeometry(viewport));
        Pen pen = SystemParameters.HighContrast ? new Pen(SystemColors.WindowTextBrush, 1.5) : errorPen;
        for (int i = first; i <= last; i++)
        {
            int lineStart = source.GetCharacterIndexFromLineIndex(i);
            int lineEnd = Math.Min(end, lineStart + source.GetLineLength(i));
            while (lineEnd > lineStart && source.Text[lineEnd - 1] is '\r' or '\n') lineEnd--;
            Rect left = source.GetRectFromCharacterIndex(Math.Max(Start, lineStart));
            Rect right = source.GetRectFromCharacterIndex(lineEnd);
            if (left.IsEmpty || right.IsEmpty) continue;
            double x = Math.Max(viewport.Left, Math.Min(left.X, right.X));
            double limit = Math.Min(viewport.Right, Math.Max(left.X + 4, right.X));
            double y = left.Bottom - 1;
            for (; x < limit; x += 4)
            {
                drawing.DrawLine(pen, new Point(x, y), new Point(Math.Min(x + 2, limit), y - 1.5));
                if (x + 2 < limit)
                    drawing.DrawLine(pen, new Point(x + 2, y - 1.5), new Point(Math.Min(x + 4, limit), y));
            }
        }
        drawing.Pop();
    }
}
