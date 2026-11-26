using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal readonly record struct FileObjectIdentity(uint Volume, ulong Index)
{
    internal static FileObjectIdentity Read(SafeFileHandle handle)
    {
        return Identity(ReadInformation(handle));
    }

    internal static string[] NamespaceKeys(string path)
    {
        string name = Path.GetFileName(path);
        string directory = Path.GetDirectoryName(path) ?? throw new IOException("A publication requires a file path.");
        using var parent = OpenAttributes(InteropPath(directory), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (parent.IsInvalid) throw NativeError("The publication directory could not be identified.");
        FileObjectIdentity folder = Read(parent);
        var keys = new List<string>(3) { $"D:{folder.Volume:X8}:{folder.Index:X16}:{name}" };

        using var file = OpenAttributes(InteropPath(path), 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (file.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() is 2 or 3) return [.. keys];
            throw NativeError("The publication file could not be identified.");
        }
        FileObjectIdentity identity = Read(file);
        keys.Add($"F:{identity.Volume:X8}:{identity.Index:X16}");
        return [.. keys];
    }

    internal static bool DeleteIfSame(string path, FileObjectIdentity expected, long? expectedLength = null,
        byte[]? expectedDigest = null, int maximumBytes = int.MaxValue)
    {
        const uint deleteAccess = 0x10000;
        const uint readAccess = 0x80000000;
        using var handle = OpenAttributes(InteropPath(path), deleteAccess | (expectedDigest is null ? 0 : readAccess),
            1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() is 2 or 3) return true;
            throw NativeError("The publication candidate could not be opened for cleanup.");
        }
        var info = ReadInformation(handle);
        if (Identity(info) != expected)
        {
            if (expectedLength is not null) throw new IOException("The parked original changed before cleanup.");
            return false;
        }
        if (expectedLength is { } length && (((ulong)info.SizeHigh << 32) | info.SizeLow) != (ulong)length)
            throw new IOException("The parked original changed before cleanup.");
        if (expectedDigest is not null && (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;
        using var contents = expectedDigest is null ? null : new FileStream(handle, FileAccess.Read);
        if (contents is not null &&
            (contents.Length > maximumBytes || !SHA256.HashData(contents).AsSpan().SequenceEqual(expectedDigest)))
            return false;
        MarkForDeletion(handle);
        return true;
    }

    internal static void MarkForDeletion(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
            throw NativeError("The file could not be removed.");
    }

    internal static bool MoveIfSame(string path, FileObjectIdentity expected, string target, bool replace,
        byte[]? expectedBytes = null)
    {
        const uint deleteAccess = 0x10000;
        const uint readAccess = 0x80000000;
        using var handle = OpenAttributes(InteropPath(path), deleteAccess | (expectedBytes is null ? 0 : readAccess),
            1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError("The staged file could not be opened for publication.");
        var info = ReadInformation(handle);
        if (Identity(info) != expected || (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;
        using var contents = expectedBytes is null ? null : new FileStream(handle, FileAccess.Read);
        if (expectedBytes is not null)
        {
            if (contents!.Length != expectedBytes.Length) return false;
            var bytes = new byte[expectedBytes.Length];
            contents.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(expectedBytes)) return false;
        }

        // FILE_RENAME_INFO contains a flags field, an aligned directory handle (NULL for an absolute path),
        // a DWORD byte count and the UTF-16 destination.
        string fullName = InteropPath(target);
        int lengthOffset = 2 * IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] rename = new byte[nameOffset + (fullName.Length + 1) * sizeof(char)];
        rename[0] = replace ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(rename.AsSpan(lengthOffset), fullName.Length * sizeof(char));
        fullName.AsSpan().CopyTo(MemoryMarshal.Cast<byte, char>(rename.AsSpan(nameOffset)));
        if (!SetFileInformationByHandle(handle, 3, rename, rename.Length))
            throw NativeError("The staged file could not be published.");
        return true;
    }

    private static FileInformation ReadInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw NativeError("The file identity could not be verified.");
        return info;
    }

    private static FileObjectIdentity Identity(FileInformation info) =>
        new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);

    private static string InteropPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ?
        @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static IOException NativeError(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenAttributes(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref byte disposition, int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        [In] byte[] info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, Volume;
        public uint SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}
