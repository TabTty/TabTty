using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TabTty.App;

internal static partial class Program
{
    private static async Task CheckMaximizedWorkAreaAsync(App app)
    {
        const int topResizeBorder = 12, caption = 2, client = 1;
        Rect workArea = SystemParameters.WorkArea;
        var window = CreateWindow();
        try
        {
            window.Show();
            OpenSettings(window);
            var settings = (FormView)((TabControl)window.FindName("tabs")).SelectedContent;
            Rect normal = await ContentBoundsAsync(window);
            var maximizeButton = (Button)window.FindName("maximizeButton");
            nint handle = new WindowInteropHelper(window).Handle;
            const uint nonClientDoubleClick = 0x00a3;
            nint normalTop = TopCoordinates(normal);
            CheckHit(window, normalTop, topResizeBorder);

            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            nint maximizedTop = TopCoordinates(await CheckMaximizedAsync(window));
            CheckEditorStatusLayout(window, settings, "Settings");
            nint hit = CheckHit(window, maximizedTop, caption);
            CheckHit(window, Coordinates(maximizeButton.PointToScreen(
                new Point(maximizeButton.ActualWidth / 2, maximizeButton.ActualHeight / 2))), client);
            SendMessageW(handle, nonClientDoubleClick, hit, maximizedTop);
            Rect restored = await ContentBoundsAsync(window);
            if (window.WindowState != WindowState.Normal || !SameBounds(restored, normal))
                throw new InvalidOperationException($"Restored content {restored} does not match the original bounds {normal}.");
            CheckHit(window, normalTop, topResizeBorder);
            CheckEditorStatusLayout(window, settings, "Settings");
        }
        finally { window.Close(); }

        var startupWindow = CreateWindow();
        try
        {
            startupWindow.WindowState = WindowState.Maximized;
            startupWindow.Show();
            nint startupTop = TopCoordinates(await CheckMaximizedAsync(startupWindow));
            CheckHit(startupWindow, startupTop, caption);
        }
        finally { startupWindow.Close(); }

        MainWindow CreateWindow()
        {
            var target = new MainWindow(app)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = workArea.Left + 40,
                Top = workArea.Top + 40,
                Width = 640,
                Height = 480,
            };
            target.SourceInitialized += (_, _) =>
            {
                nint handle = new WindowInteropHelper(target).Handle;
                if (GetPropW(handle, "NonRudeHWND") != 1)
                    throw new InvalidOperationException("The taskbar can treat the window as fullscreen when shown.");
            };
            return target;
        }

        async Task<Rect> CheckMaximizedAsync(MainWindow target)
        {
            Rect bounds = await ContentBoundsAsync(target);
            var toDevice = PresentationSource.FromVisual(target)!.CompositionTarget!.TransformToDevice;
            var workAreaPixels = new Rect(toDevice.Transform(workArea.TopLeft), toDevice.Transform(workArea.BottomRight));
            if (target.WindowState != WindowState.Maximized || !SameBounds(bounds, workAreaPixels))
                throw new InvalidOperationException($"Maximized content {bounds} does not fit the monitor work area {workAreaPixels}.");
            return bounds;
        }

        static nint CheckHit(MainWindow window, nint point, int expected)
        {
            const uint nonClientHitTest = 0x0084;
            nint result = SendMessageW(new WindowInteropHelper(window).Handle, nonClientHitTest, 0, point);
            if (result != expected)
                throw new InvalidOperationException($"{window.WindowState} window returned hit {result}, expected {expected} at {point:X}.");
            return result;
        }

        static async Task<Rect> ContentBoundsAsync(MainWindow window)
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            return new Rect(content.PointToScreen(new Point()),
                content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight)));
        }

        static bool SameBounds(Rect left, Rect right) =>
            Math.Abs(left.Left - right.Left) <= 1 && Math.Abs(left.Top - right.Top) <= 1 &&
            Math.Abs(left.Right - right.Right) <= 1 && Math.Abs(left.Bottom - right.Bottom) <= 1;

        static nint TopCoordinates(Rect bounds) => Coordinates(new Point(bounds.Left + bounds.Width / 2, bounds.Top));

        static nint Coordinates(Point screenPoint) =>
            unchecked((int)((uint)(ushort)Math.Round(screenPoint.X) | ((uint)(ushort)Math.Round(screenPoint.Y) << 16)));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetPropW(nint window, string property);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
