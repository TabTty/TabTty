using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;

namespace TabTty.App;

internal static class WindowPlacement
{
    public const double MinimumWidth = 480;

    public static void Validate(JsonNode? value)
    {
        if (value is null) return;
        if (value is not JsonObject item || item["monitor"]?.GetValue<string>() is not { Length: > 0 and <= 256 } ||
            item["dpiX"]?.GetValue<double>() is not (>= 1 and <= 10000) ||
            item["dpiY"]?.GetValue<double>() is not (>= 1 and <= 10000) || item["maximized"]?.GetValue<bool>() is not { })
            throw new InvalidDataException(App.Text("LocalStateInvalid"));
        _ = ReadRect(item["normal"] ?? throw new InvalidDataException(App.Text("LocalStateInvalid")));
        _ = ReadRect(item["workArea"] ?? throw new InvalidDataException(App.Text("LocalStateInvalid")));
    }

    public static Rect Constrain(Rect normal, Rect work)
    {
        double width = Math.Min(work.Width, Math.Max(MinimumWidth, normal.Width));
        double height = Math.Min(work.Height, Math.Max(320, normal.Height));
        return new Rect(Math.Clamp(normal.Left, work.Left, work.Right - width),
            Math.Clamp(normal.Top, work.Top, work.Bottom - height), width, height);
    }

    public static void Restore(Window window)
    {
        JsonNode? saved;
        try { saved = LocalState.Read()["lastWindow"]; }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception)) { saved = null; }
        Rect desired = new(100, 100, 1000, 700);
        Monitor monitor = GetMonitor(0);
        bool maximized = false;
        if (saved is JsonObject node)
        {
            try
            {
                Rect prior = ReadRect(node["normal"]!);
                Rect oldWork = ReadRect(node["workArea"]!);
                string device = node["monitor"]!.GetValue<string>();
                EnumDisplayMonitors(0, 0, (nint handle, nint _, ref NativeRect rectangle, nint data) =>
                {
                    Monitor candidate = GetMonitor(handle);
                    if (candidate.Device == device) monitor = candidate;
                    return true;
                }, 0);
                desired = new Rect(monitor.Work.Left + prior.Left - oldWork.Left,
                    monitor.Work.Top + prior.Top - oldWork.Top, prior.Width, prior.Height);
                maximized = node["maximized"]!.GetValue<bool>();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NullReferenceException)
            { desired = new Rect(monitor.Work.Left + 40, monitor.Work.Top + 40, 1000, 700); }
        }
        Apply(window, Constrain(desired, monitor.Work));
        if (maximized) window.WindowState = WindowState.Maximized;
    }

    public static void KeepOnScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        Monitor monitor = GetMonitor(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2));
        Apply(window, Constrain(new Rect(window.Left, window.Top, window.Width, window.Height), monitor.Work));
    }

    public static void FitMaximizedWindowToWorkArea(nint window, nint parameters)
    {
        if (!IsZoomed(window)) return;
        const uint noSize = 0x0001, noMove = 0x0002;
        var position = Marshal.PtrToStructure<WindowPosition>(parameters);
        if ((position.Flags & (noMove | noSize)) == (noMove | noSize)) return;

        // Use the proposed position to select the monitor when a maximized window moves.
        if (!GetWindowRect(window, out var bounds)) return;
        int left = (position.Flags & noMove) == 0 ? position.X : bounds.Left;
        int top = (position.Flags & noMove) == 0 ? position.Y : bounds.Top;
        int width = (position.Flags & noSize) == 0 ? position.Width : bounds.Right - bounds.Left;
        int height = (position.Flags & noSize) == 0 ? position.Height : bounds.Bottom - bounds.Top;
        var proposed = new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfoW(MonitorFromRect(in proposed, 2), ref info)) return;

        NativeRect work = GetMaximizedWorkArea(info);
        position.X = work.Left;
        position.Y = work.Top;
        position.Width = work.Right - work.Left;
        position.Height = work.Bottom - work.Top;
        position.Flags &= ~(noMove | noSize);
        Marshal.StructureToPtr(position, parameters, false);
    }

    private static NativeRect GetMaximizedWorkArea(MonitorInfo info)
    {
        const uint getState = 0x0004, autoHide = 0x0001, getAutoHideBar = 0x000b;
        const uint left = 0, top = 1, right = 2, bottom = 3;
        const int revealPixels = 2;
        var bar = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        NativeRect work = info.Work;
        if ((SHAppBarMessage(getState, ref bar) & autoHide) == 0) return work;

        // The work area can include an auto-hidden taskbar's edge. Leave its reveal strip accessible.
        bar.Bounds = info.Monitor;
        if (work.Left < info.Monitor.Left + revealPixels && HasAutoHideBar(left)) work.Left = info.Monitor.Left + revealPixels;
        if (work.Top < info.Monitor.Top + revealPixels && HasAutoHideBar(top)) work.Top = info.Monitor.Top + revealPixels;
        if (work.Right > info.Monitor.Right - revealPixels && HasAutoHideBar(right)) work.Right = info.Monitor.Right - revealPixels;
        if (work.Bottom > info.Monitor.Bottom - revealPixels && HasAutoHideBar(bottom)) work.Bottom = info.Monitor.Bottom - revealPixels;
        return work;

        bool HasAutoHideBar(uint edge)
        {
            bar.Edge = edge;
            return SHAppBarMessage(getAutoHideBar, ref bar) != 0;
        }
    }

    public static void PlaceDetached(Window target, Window source, Point screenPixels)
    {
        Monitor monitor = GetMonitor(MonitorFromPoint(new NativePoint { X = (int)screenPixels.X, Y = (int)screenPixels.Y }, 2));
        Rect normal = source.WindowState == WindowState.Normal
            ? new Rect(source.Left, source.Top, source.ActualWidth, source.ActualHeight) : source.RestoreBounds;
        var desired = new Rect(screenPixels.X * 96 / monitor.DpiX, screenPixels.Y * 96 / monitor.DpiY,
            Finite(normal) ? normal.Width : 1000, Finite(normal) ? normal.Height : 700);
        Apply(target, Constrain(desired, monitor.Work));
    }

    public static void Save(Window window)
    {
        Rect normal = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        if (normal.IsEmpty || !Finite(normal)) return;
        Monitor monitor = GetMonitor(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2));
        LocalState.Change(state => state["lastWindow"] = new JsonObject
        {
            ["normal"] = WriteRect(normal), ["monitor"] = monitor.Device,
            ["workArea"] = WriteRect(monitor.Work), ["dpiX"] = monitor.DpiX, ["dpiY"] = monitor.DpiY,
            ["maximized"] = window.WindowState == WindowState.Maximized,
        });
    }

    private static void Apply(Window window, Rect normal)
    {
        window.MinWidth = Math.Min(MinimumWidth, normal.Width);
        window.MinHeight = Math.Min(320, normal.Height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = normal.Left;
        window.Top = normal.Top;
        window.Width = normal.Width;
        window.Height = normal.Height;
    }

    private static bool Finite(Rect rect) => double.IsFinite(rect.Left) && double.IsFinite(rect.Top) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0;

    private static Rect ReadRect(JsonNode node)
    {
        Rect value = new(node["left"]!.GetValue<double>(), node["top"]!.GetValue<double>(),
            node["width"]!.GetValue<double>(), node["height"]!.GetValue<double>());
        if (!Finite(value) || Math.Abs(value.Left) > 1e8 || Math.Abs(value.Top) > 1e8 ||
            value.Width > 1e8 || value.Height > 1e8) throw new ArgumentException("Invalid window bounds.");
        return value;
    }

    private static JsonObject WriteRect(Rect rect) => new()
    {
        ["left"] = rect.Left, ["top"] = rect.Top, ["width"] = rect.Width, ["height"] = rect.Height,
    };

    private static Monitor GetMonitor(nint handle)
    {
        if (handle == 0) handle = MonitorFromPoint(new NativePoint(), 1);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfoW(handle, ref info)) throw new IOException("Monitor information is unavailable.");
        uint dpiX = 96, dpiY = 96;
        if (GetDpiForMonitor(handle, 0, out uint x, out uint y) == 0 && x != 0 && y != 0) { dpiX = x; dpiY = y; }
        return new Monitor(info.Device, new Rect(info.Work.Left * 96d / dpiX, info.Work.Top * 96d / dpiY,
            (info.Work.Right - info.Work.Left) * 96d / dpiX, (info.Work.Bottom - info.Work.Top) * 96d / dpiY), dpiX, dpiY);
    }

    private sealed record Monitor(string Device, Rect Work, uint DpiX, uint DpiY);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public nint Window;
        public uint CallbackMessage, Edge;
        public NativeRect Bounds;
        public nint Parameter;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rectangle, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(in NativeRect rect, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
}
