using System.IO;
using System.Runtime.InteropServices;
using TabTty.Core;

namespace TabTty.App;

internal static class ProfileLocation
{
    public static string Normalize(string path)
    {
        string full = OrdinaryWindowsPath.Resolve(path);
        string root = Path.GetPathRoot(full)!;
        string normalized = root.ToUpperInvariant();
        // Resolve each component at its opening location, retaining any link in the path.
        foreach (string component in full[root.Length..].Split(Path.DirectorySeparatorChar))
        {
            string candidate = Path.Combine(normalized, component);
            string lookup = candidate.Length < 260 ? candidate : candidate.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + candidate[2..] : @"\\?\" + candidate;
            nint handle = FindFirstFileW(lookup, out FindData data);
            if (handle == -1) return full;
            FindClose(handle);
            normalized = Path.Combine(normalized, data.Name);
        }
        return normalized;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindFirstFileW(string path, out FindData data);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }
}
