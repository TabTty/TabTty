using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabTty.App;

internal sealed class FloatingTabHeader : IDisposable
{
    private readonly HwndSource surface;
    private readonly double grip;

    internal FloatingTabHeader(TabItem header, Point screenPoint)
    {
        var size = header.RenderSize;
        var dpi = VisualTreeHelper.GetDpi(header);
        grip = Math.Clamp(header.PointFromScreen(screenPoint).X, 0, size.Width);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(header), null, new Rect(size));
        bitmap.Render(drawing);
        bitmap.Freeze();

        var parameters = new HwndSourceParameters("TabTty floating tab header")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x08000000 | 0x00000080 | 0x00000020, // no activate, tool window, transparent to input
            UsesPerPixelOpacity = true,
            Width = bitmap.PixelWidth,
            Height = bitmap.PixelHeight,
            PositionX = (int)screenPoint.X,
            PositionY = (int)screenPoint.Y,
        };
        surface = new HwndSource(parameters)
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            RootVisual = new Image { Source = bitmap, Width = size.Width, Height = size.Height, IsHitTestVisible = false },
        };
        Follow(screenPoint);
    }

    internal void Follow(Point point)
    {
        var scale = surface.CompositionTarget!.TransformToDevice;
        // Keep the pointer just above the artwork so native hit testing sees
        // the exposed window even when it belongs to a different process.
        SetWindowPos(surface.Handle, new nint(-1), (int)(point.X - grip * scale.M11), (int)point.Y + 4,
            0, 0, 0x0001 | 0x0010 | 0x0040); // no size, no activation, show
    }

    public void Dispose() => surface.Dispose();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
