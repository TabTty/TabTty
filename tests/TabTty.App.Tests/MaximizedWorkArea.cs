using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;

internal static partial class Program
{
    private static async Task CheckMaximizedWorkAreaAsync(App app)
    {
        Rect workArea = SystemParameters.WorkArea;
        var window = CreateWindow();
        try
        {
            window.Show();
            Rect normal = await ContentBoundsAsync(window);
            var maximizeButton = (Button)window.FindName("maximizeButton");

            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await CheckMaximizedAsync(window);

            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Rect restored = await ContentBoundsAsync(window);
            if (window.WindowState != WindowState.Normal || !SameBounds(restored, normal, 1))
                throw new InvalidOperationException($"Restored content {restored} does not match the original bounds {normal}.");
        }
        finally { window.Close(); }

        var startupWindow = CreateWindow();
        try
        {
            startupWindow.WindowState = WindowState.Maximized;
            startupWindow.Show();
            await CheckMaximizedAsync(startupWindow);
        }
        finally { startupWindow.Close(); }

        MainWindow CreateWindow() => new(app)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = workArea.Left + 40,
            Top = workArea.Top + 40,
            Width = 640,
            Height = 480,
        };

        async Task CheckMaximizedAsync(MainWindow target)
        {
            Rect bounds = await ContentBoundsAsync(target);
            // An auto-hidden taskbar can need a two-pixel inset within the reported work area.
            var roundingTolerance = workArea;
            roundingTolerance.Inflate(1, 1);
            if (target.WindowState != WindowState.Maximized || !roundingTolerance.Contains(bounds) ||
                !SameBounds(bounds, workArea, 2))
                throw new InvalidOperationException($"Maximized content {bounds} does not fit the monitor work area {workArea}.");
        }

        static async Task<Rect> ContentBoundsAsync(MainWindow window)
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            var fromDevice = PresentationSource.FromVisual(content)!.CompositionTarget!.TransformFromDevice;
            return new Rect(fromDevice.Transform(content.PointToScreen(new Point())),
                fromDevice.Transform(content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight))));
        }

        static bool SameBounds(Rect left, Rect right, double tolerance) =>
            Math.Abs(left.Left - right.Left) <= tolerance && Math.Abs(left.Top - right.Top) <= tolerance &&
            Math.Abs(left.Right - right.Right) <= tolerance && Math.Abs(left.Bottom - right.Bottom) <= tolerance;
    }
}
