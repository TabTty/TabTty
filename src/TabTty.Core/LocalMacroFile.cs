using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal static class LocalMacroFile
{
    internal static string OpenedLocation(SafeFileHandle handle, string path)
    {
        // TryOpen excludes reparse points and drive redirects. The handle supplies
        // the stored spelling without traversing the original path a second time.
        const int volumeNameNone = 4;
        var name = new StringBuilder(260);
        int size = GetFinalPathNameByHandleW(handle, name, name.Capacity, volumeNameNone);
        if (size >= name.Capacity && size <= 32768)
        {
            name.EnsureCapacity(size);
            size = GetFinalPathNameByHandleW(handle, name, name.Capacity, volumeNameNone);
        }
        string drive = path[..2].ToUpperInvariant();
        return size > 0 && size < name.Capacity && name[0] == '\\'
            ? drive + name.ToString() : drive + path[2..];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder name, int size, int flags);

    internal static bool IsOrdinaryFile(SafeFileHandle handle) => GetFileType(handle) == 1 &&
        (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.Device)) == 0;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(SafeFileHandle handle);

    internal static SafeFileHandle? TryOpen(string path) =>
        InspectLocal(path, FileShare.Read, static metadata =>
        {
            // Request data access only on this already inspected local object.
            const uint openReparsePoint = 0x00200000;
            var file = ReOpenFile(metadata, 0x80000000, FileShare.Read, openReparsePoint);
            if (!file.IsInvalid) return file;
            file.Dispose();
            return null;
        });

    internal static string? StoredLocalPath(string path) =>
        InspectLocal(path, FileShare.ReadWrite | FileShare.Delete, metadata => OpenedLocation(metadata, path));

    private static T? InspectLocal<T>(string path, FileShare share, Func<SafeFileHandle, T?> inspect) where T : class
    {
        // Windows can disguise Cloud Files placeholders as ordinary files for
        // the calling application. Inspect the handle with placeholders exposed.
        const sbyte exposePlaceholders = 2;
        sbyte previous = RtlSetThreadPlaceholderCompatibilityMode(exposePlaceholders);
        if (previous < 0) return null;
        try { return InspectLocalHandle(path, share, inspect); }
        finally { RtlSetThreadPlaceholderCompatibilityMode(previous); }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);

    private static unsafe T? InspectLocalHandle<T>(string path, FileShare share, Func<SafeFileHandle, T?> inspect) where T : class
    {
        // Resolve only a direct local volume mapping. A drive name can also
        // resolve to a redirector or to another path (SUBST).
        if (path.Length < 4 || path[1] != ':' || path[2] != '\\' ||
            path[3..].Split('\\').Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ')))
            return null;
        Span<char> mapping = stackalloc char[1024];
        uint count;
        fixed (char* buffer = mapping)
            count = QueryDosDevice(path[..2], buffer, (uint)mapping.Length);
        if (count == 0) return null;
        int end = mapping[..(int)count].IndexOf('\0');
        const string volumePrefix = @"\Device\HarddiskVolume";
        if (end <= volumePrefix.Length ||
            !mapping[..volumePrefix.Length].Equals(volumePrefix, StringComparison.OrdinalIgnoreCase) ||
            mapping[volumePrefix.Length..end].ContainsAnyExceptInRange('0', '9'))
            return null;

        // Open the resolved device directly. OBJ_DONT_REPARSE applies to the
        // full name, including ancestors, and avoids a check-then-reopen race.
        string name = string.Concat(mapping[..end], path.AsSpan(2));
        if (name.Length >= ushort.MaxValue / sizeof(char)) return null;
        fixed (char* chars = name)
        {
            ushort bytes = (ushort)(name.Length * sizeof(char));
            var nativeName = new UnicodeString { Length = bytes, MaximumLength = bytes, Buffer = (nint)chars };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                Name = (nint)(&nativeName),
                Attributes = 0x1040, // OBJ_DONT_REPARSE | OBJ_CASE_INSENSITIVE
            };
            const uint attributesAndSynchronize = 0x00100080;
            const uint openOptions = 0x00600060; // NO_RECALL | OPEN_REPARSE_POINT | NON_DIRECTORY | SYNCHRONOUS_IO_NONALERT
            int status = NtOpenFile(out SafeFileHandle metadata, attributesAndSynchronize, ref attributes, out _, (uint)share, openOptions);
            using (metadata)
            {
                if (status < 0 || metadata.IsInvalid)
                {
                    if (status is unchecked((int)0xC0000034) or unchecked((int)0xC000003A))
                        throw new FileNotFoundException();
                    return null;
                }
                try
                {
                    const FileAttributes recall = (FileAttributes)(0x00040000 | 0x00400000);
                    const FileAttributes unsupported = FileAttributes.ReparsePoint | FileAttributes.Offline | recall;
                    if ((File.GetAttributes(metadata) & unsupported) != 0) return null;
                    return inspect(metadata);
                }
                catch (IOException) { }
                return null;
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, FileShare share, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length, MaximumLength;
        internal nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal nint RootDirectory, Name;
        internal uint Attributes;
        internal nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint Status, Information;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe uint QueryDosDevice(string name, char* target, uint length);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtOpenFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, uint share, uint options);
}
