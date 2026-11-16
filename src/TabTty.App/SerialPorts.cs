using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TabTty.App;

internal static class SerialPorts
{
    private sealed record Port(string Name, string Label);

    public static ComboBox Picker(string saved = "")
    {
        var box = new ComboBox { IsEditable = true, DisplayMemberPath = nameof(Port.Label), Text = saved };
        Refresh(box);
        return box;
    }

    public static string SelectedName(ComboBox box) => box.SelectedItem is Port choice && box.Text == choice.Label
        ? choice.Name : box.Text;

    public static void Refresh(ComboBox box)
    {
        string previous = SelectedName(box);
        var ports = Read();
        box.ItemsSource = ports;
        Port? selected = ports.FirstOrDefault(port => string.Equals(port.Name, previous, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) box.SelectedItem = selected;
        else box.Text = previous.Length != 0 ? previous : ports.FirstOrDefault()?.Name ?? "";
    }

    private static Port[] Read()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var registry = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
        {
            if (registry is not null)
                foreach (string key in registry.GetValueNames())
                    if (registry.GetValue(key) is string port) names.TryAdd(port, port);
        }
        Guid portClass = new("4d36e978-e325-11ce-bfc1-08002be10318");
        nint devices = SetupDiGetClassDevsW(ref portClass, 0, 0, 2);
        if (devices != -1)
        {
            try
            {
                for (uint index = 0; index < 10000; index++)
                {
                    var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                    if (!SetupDiEnumDeviceInfo(devices, index, ref device)) break;
                    nint key = SetupDiOpenDevRegKey(devices, ref device, 1, 0, 1, 0x0001);
                    if (key == -1) continue;
                    using var handle = new SafeRegistryHandle(key, ownsHandle: true);
                    using var registry = RegistryKey.FromHandle(handle);
                    if (registry.GetValue("PortName") is not string name) continue;
                    var buffer = new byte[8192];
                    if (SetupDiGetDeviceRegistryPropertyW(devices, ref device, 12, out uint type,
                        buffer, (uint)buffer.Length, out uint size) && type == 1 && size <= buffer.Length)
                        names[name] = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
                    else names.TryAdd(name, name);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
        }
        return names.Where(pair => pair.Key.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(pair.Key.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && id != 0)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new Port(pair.Key, pair.Value == pair.Key ? pair.Key : $"{pair.Key} — {pair.Value}")).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid Class; public uint Instance; public nuint Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid type, nint enumerator, nint owner, uint flags);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint devices, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll")]
    private static extern nint SetupDiOpenDevRegKey(nint devices, ref DeviceInfo device, uint scope, uint profile, uint type, uint access);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint devices, ref DeviceInfo device, uint property,
        out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
}
