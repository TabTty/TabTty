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
            if (window.WindowState != WindowState.Normal || !SameBounds(restored, normal))
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

        async Task CheckMaximizedAsync(MainWindow target)
        {
            Rect bounds = await ContentBoundsAsync(target);
            var toDevice = PresentationSource.FromVisual(target)!.CompositionTarget!.TransformToDevice;
            var workAreaPixels = new Rect(toDevice.Transform(workArea.TopLeft), toDevice.Transform(workArea.BottomRight));
            if (target.WindowState != WindowState.Maximized || !SameBounds(bounds, workAreaPixels))
                throw new InvalidOperationException($"Maximized content {bounds} does not fit the monitor work area {workAreaPixels}.");
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
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetPropW(nint window, string property);
}
