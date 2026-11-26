using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TabTty.Core;

namespace TabTty.App;

internal static class ProfileLocation
{
    public static string Normalize(string path) => Normalize(path, keepResolvedParent: false);

    internal static string VaultOpeningPath(string path) => Normalize(path, keepResolvedParent: true);

    internal static bool SameVaultOpeningPath(string first, string second)
    {
        first = VaultOpeningPath(first);
        second = VaultOpeningPath(second);
        if (first == second) return true;
        if (!string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return false;
        string? parent = Path.GetDirectoryName(first);
        if (parent is null || parent != Path.GetDirectoryName(second)) return false;
        using var directory = CreateFileW(ExtendedLookup(parent), 0x80 /* FILE_READ_ATTRIBUTES */, 7, 0,
            3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, 0);
        return !directory.IsInvalid &&
            (!GetFileInformationByHandleEx(directory, 23 /* FileCaseSensitiveInfo */, out uint flags, sizeof(uint)) ||
                (flags & 1) == 0);
    }

    private static string Normalize(string path, bool keepResolvedParent)
    {
        string full = OrdinaryWindowsPath.Resolve(path);
        string root = Path.GetPathRoot(full)!;
        string normalized = root.ToUpperInvariant();
        // Resolve each component at its opening location, retaining any link in the path.
        var components = full[root.Length..].Split(Path.DirectorySeparatorChar);
        for (int i = 0; i < components.Length; i++)
        {
            string candidate = Path.Combine(normalized, components[i]);
            nint handle = FindFirstFileW(ExtendedLookup(candidate), out FindData data);
            if (handle == -1)
                return keepResolvedParent ? Path.Combine(normalized, string.Join(Path.DirectorySeparatorChar, components, i,
                    components.Length - i)) : full;
            FindClose(handle);
            normalized = Path.Combine(normalized, data.Name);
        }
        return normalized;
    }

    private static string ExtendedLookup(string path) => path.Length < 260 ? path : path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle directory, int infoClass, out uint flags, uint size);

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
