using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TabTty.App;

internal sealed partial class WindowFrameAccent
{
    private const uint DefaultColor = 0xffffffff; // DWMWA_COLOR_DEFAULT
    private const int BorderColorAttribute = 34; // DWMWA_BORDER_COLOR
    private const int SystemColorChanged = 0x0015;
    private const int SettingChanged = 0x001a;
    private const int ThemeChanged = 0x031a;
    private const int ColorizationChanged = 0x0320;
    private static readonly ConditionalWeakTable<HwndSource, WindowFrameAccent> Frames = new();
    private readonly Window window;
    private readonly HwndSource source;
    private uint appliedColor = DefaultColor;
    private bool refreshPending;

    internal static void Register()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Window { AllowsTransparency: false } window && PresentationSource.FromVisual(window) is HwndSource source)
            Frames.GetValue(source, hwndSource => new WindowFrameAccent(window, hwndSource));
    }

    private WindowFrameAccent(Window window, HwndSource source)
    {
        this.window = window;
        this.source = source;
        window.Activated += OnWindowStateChanged;
        window.Deactivated += OnWindowStateChanged;
        window.StateChanged += OnWindowStateChanged;
        source.AddHook(Observe);
        Apply();
    }

    private void OnWindowStateChanged(object? sender, EventArgs args) => Apply();

    private nint Observe(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((message is SystemColorChanged or SettingChanged or ThemeChanged or ColorizationChanged) && !refreshPending)
        {
            refreshPending = true;
            _ = source.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                refreshPending = false;
                Apply();
            }));
        }
        return 0;
    }

    private void Apply()
    {
        if (source.IsDisposed) return;
        uint color = window.IsActive && window.WindowState == WindowState.Normal && !SystemParameters.HighContrast
            ? ReadAccentColor() : DefaultColor;
        if (color != appliedColor && DwmSetWindowAttribute(source.Handle, BorderColorAttribute, in color, sizeof(uint)) >= 0)
            appliedColor = color;
    }

    private static uint ReadAccentColor()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            // The DWM accent stores alpha above the three COLORREF bytes.
            if (settings?.GetValue("ColorPrevalence") is 1 && settings.GetValue("AccentColor") is int packed)
                return unchecked((uint)packed) & 0x00ffffff;
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException) { }
        return DefaultColor;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in uint value, int size);
}
