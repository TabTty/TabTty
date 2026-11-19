using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal static class LocalMacroFile
{
    internal static bool IsOrdinaryFile(SafeFileHandle handle) => GetFileType(handle) == 1 &&
        (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.Device)) == 0;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(SafeFileHandle handle);

    internal static unsafe SafeFileHandle? TryOpen(string path)
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
            const uint readAndSynchronize = 0x00100081;
            const uint openOptions = 0x00400060; // NO_RECALL | NON_DIRECTORY | SYNCHRONOUS_IO_NONALERT
            int status = NtOpenFile(out SafeFileHandle file, readAndSynchronize, ref attributes, out _, 1, openOptions);
            if (status < 0 || file.IsInvalid)
            {
                file.Dispose();
                if (status is unchecked((int)0xC0000034) or unchecked((int)0xC000003A))
                    throw new FileNotFoundException();
                return null;
            }

            try
            {
                const FileAttributes recall = (FileAttributes)(0x00040000 | 0x00400000);
                const FileAttributes unsupported = FileAttributes.ReparsePoint | FileAttributes.Offline | recall;
                if ((File.GetAttributes(file) & unsupported) == 0) return file;
            }
            catch (IOException) { }
            file.Dispose();
            return null;
        }
    }

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
