using System.Windows;
using System.Windows.Interop;

namespace TabTty.App;

internal static class NativeKeyState
{
    public static bool KeypadEnter { get; private set; }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is not { } source) return;
            source.AddHook(Observe);
        };
    }

    private static nint Observe(nint hwnd, int message, nint key, nint flags, ref bool handled)
    {
        if (message is 0x0100 or 0x0104)
            KeypadEnter = key == 13 && ((long)flags & 0x01000000) != 0;
        return 0;
    }
}
