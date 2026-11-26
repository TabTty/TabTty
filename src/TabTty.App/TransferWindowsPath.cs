using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TransferWindowsPath : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint Overlapped = 0x40000000;
    private const uint DirectoryAttribute = 0x10;
    private const uint ReparseAttribute = 0x400;
    private const uint PersistentAcls = 0x8;
    private readonly List<SafeFileHandle> parents = [];

    public TransferWindowsPath(string directory)
    {
        var ancestors = new Stack<string>();
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            ancestors.Push(current.FullName);
        try
        {
            foreach (string ancestor in ancestors)
            {
                SafeFileHandle handle = Open(ancestor, ReadAttributes, BackupSemantics | OpenReparsePoint);
                parents.Add(handle);
                uint attributes = GetInfo(handle).Attributes;
                if ((attributes & (DirectoryAttribute | ReparseAttribute)) != DirectoryAttribute)
                    throw new SftpException(SftpFailure.InvalidName);
            }
        }
        catch { Dispose(); throw; }
    }

    public bool IsCaseSensitive => GetFileInformationByHandleEx(parents[^1], 23, out uint flags, 4) && (flags & 1) != 0;

    public static TransferAttributes? Stat(string path)
    {
        using var handle = CreateFile(path, ReadAttributes, 7, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            throw new IOException("Unable to inspect the transfer path.", new Win32Exception(error));
        }
        return ReadAttributesFrom(handle);
    }

    internal static TransferAttributes ReadAttributesFrom(SafeFileHandle handle)
    {
        FileInformation info = GetInfo(handle);
        if ((info.Attributes & ReparseAttribute) != 0) return new(TransferKind.Link, null, null);
        DateTime modified = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
        bool directory = (info.Attributes & DirectoryAttribute) != 0;
        long? size = directory ? null : ((long)info.SizeHigh << 32) | info.SizeLow;
        return new(directory ? TransferKind.Directory : TransferKind.File, size, modified)
        {
            LocalIdentity = GetFileId(handle, 18 /* FileIdInfo */, out var id, 24) && id.File != Guid.Empty
                ? (id.Volume, id.File) : null,
        };
    }

    internal static void DeleteMovedSource(string path, TransferAttributes expected)
    {
        const uint deleteAccess = 0x10000;
        // Hold the checked object open without allowing concurrent writes or replacement.
        using var handle = CreateFile(path, ReadAttributes | deleteAccess, (uint)FileShare.Read, 0,
            OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
            throw new IOException("Unable to open the moved source for removal.", new Win32Exception(Marshal.GetLastWin32Error()));
        var actual = ReadAttributesFrom(handle);
        bool matches = expected.Kind == TransferKind.Directory
            ? actual.Kind == TransferKind.Directory && expected.LocalIdentity == actual.LocalIdentity
            : TransferAttributes.SameOverwriteTarget(expected, actual);
        if (expected.LocalIdentity is null || !matches) throw new SftpException(SftpFailure.SourceChanged);
        FileObjectIdentity.MarkForDeletion(handle);
    }

    public TransferFileIdentity Identity
    {
        get
        {
            FileInformation info = GetInfo(parents[^1]);
            return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
        }
    }

    public static FileStream OpenRead(string path)
    {
        SafeFileHandle handle = Open(path, GenericRead, OpenReparsePoint | Overlapped);
        try
        {
            if ((GetInfo(handle).Attributes & (DirectoryAttribute | ReparseAttribute)) != 0)
                throw new SftpException(SftpFailure.InvalidName);
            return new FileStream(handle, FileAccess.Read, SftpClient.BlockSize, isAsync: true);
        }
        catch { handle.Dispose(); throw; }
    }

    private static ReadOnlySpan<byte> InternetZone => "[ZoneTransfer]\r\nZoneId=3\r\n"u8;

    public static FileStream CreateDownload(string path) => PrivateWindowsFile.Create(path,
        FileSystemRights.Write | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions,
        FileShare.Read, SftpClient.BlockSize, FileOptions.Asynchronous);

    // The caller pins the ancestor directories and keeps the newly created data file open.
    public static void MarkDownload(string path, FileStream dataFile)
    {
        try
        {
            const uint namedStreams = 0x40000;
            if (!GetVolumeInformationByHandleW(dataFile.SafeFileHandle, 0, 0, out _, out _, out uint features, 0, 0) ||
                (features & namedStreams) == 0)
                throw new SftpException(SftpFailure.DownloadOrigin);
            if ((features & PersistentAcls) == 0 || !PrivateWindowsFile.HasPrivateAccess(dataFile))
                throw new SftpException(SftpFailure.DownloadPermissions);
            using var file = OpenRead(path);
            var identity = FileObjectIdentity.Read(dataFile.SafeFileHandle);
            if (FileObjectIdentity.Read(file.SafeFileHandle) != identity)
                throw new SftpException(SftpFailure.SourceChanged);
            using var marker = new FileStream(path + ":Zone.Identifier:$DATA", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (FileObjectIdentity.Read(marker.SafeFileHandle) != identity)
                throw new SftpException(SftpFailure.DownloadOrigin);
            marker.Write(InternetZone);
            marker.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is not SftpException && error is (IOException or UnauthorizedAccessException))
        { throw new SftpException(SftpFailure.DownloadOrigin, error); }
    }

    public static FileStream OpenVerifiedDownloadMark(string path, FileObjectIdentity identity)
    {
        FileStream? marker = null;
        try
        {
            marker = new FileStream(path + ":Zone.Identifier:$DATA", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Span<byte> data = stackalloc byte[InternetZone.Length];
            if (FileObjectIdentity.Read(marker.SafeFileHandle) != identity || marker.Length != data.Length)
                throw new SftpException(SftpFailure.DownloadOrigin);
            marker.ReadExactly(data);
            if (!data.SequenceEqual(InternetZone)) throw new SftpException(SftpFailure.DownloadOrigin);
            return marker;
        }
        catch (Exception error)
        {
            marker?.Dispose();
            if (error is not SftpException && error is (IOException or UnauthorizedAccessException))
                throw new SftpException(SftpFailure.DownloadOrigin, error);
            throw;
        }
    }

    public unsafe void CreateDirectory(string path)
    {
        if (!GetVolumeInformationByHandleW(parents[^1], 0, 0, out _, out _, out uint features, 0, 0) ||
            (features & PersistentAcls) == 0)
            throw new SftpException(SftpFailure.DownloadPermissions);
        fixed (byte* descriptor = PrivateWindowsFile.DirectoryDescriptor())
        {
            var security = new SecurityAttributes { Length = sizeof(SecurityAttributes), Descriptor = (nint)descriptor };
            if (!CreateDirectoryNative(path, ref security))
                throw new IOException("Unable to create the transfer directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    public void Dispose()
    {
        foreach (SafeFileHandle handle in parents) handle.Dispose();
        parents.Clear();
    }

    private static SafeFileHandle Open(string path, uint access, uint flags)
    {
        // Sharing read and write, but not delete, pins the path while the operation is in flight.
        SafeFileHandle handle = CreateFile(path, access, 3, 0, OpenExisting, flags, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("Unable to open the transfer path.", new Win32Exception(error));
    }

    private static FileInformation GetInfo(SafeFileHandle handle)
    {
        if (GetFileInformationByHandle(handle, out FileInformation info)) return info;
        throw new IOException("Unable to inspect the transfer path.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong Volume;
        public Guid File;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetFileId(SafeFileHandle handle, int kind, out FileIdInformation info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryNative(string path, ref SecurityAttributes security);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, nint volumeName, uint volumeCapacity,
        out uint serialNumber, out uint maximumComponentLength, out uint flags, nint fileSystemName, uint fileSystemCapacity);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out uint info, uint size);
}

