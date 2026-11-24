using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private (Button Button, Point Origin, bool ClickHandled)? captionMenuPress;

    private void OnCaptionMenuPressed(object sender, MouseButtonEventArgs e) =>
        captionMenuPress = ((Button)sender, e.GetPosition(this), false);

    private void OnCaptionMenuMoved(object sender, MouseEventArgs e)
    {
        if (captionMenuPress is not { } press || press.Button != sender ||
            !press.Button.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - press.Origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - press.Origin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;

        Point screen = PointToScreen(position);
        captionMenuPress = null;
        e.Handled = true;
        CloseApplicationMenu(restoreFocus: false);
        if (press.Button.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        press.Button.ReleaseMouseCapture();

        // Windows handles the same move, restore and snap behavior as a title-bar press.
        const uint nonClientLeftButtonDown = 0x00a1;
        const int caption = 2;
        int coordinates = ((int)screen.X & 0xffff) | ((int)screen.Y << 16);
        SendMessageW(source.Handle, nonClientLeftButtonDown, caption, coordinates);
    }

    private void OnCaptionMenuReleased(object sender, MouseButtonEventArgs e)
    {
        if (captionMenuPress is not { } press || press.Button != sender) return;
        captionMenuPress = null;
        if (press.ClickHandled)
        {
            e.Handled = true;
            press.Button.ReleaseMouseCapture();
        }
    }

    private void OnCaptionMenuLostCapture(object sender, MouseEventArgs e)
    {
        if (captionMenuPress is { } press && press.Button == sender && !press.Button.IsMouseCaptured)
            captionMenuPress = null;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
