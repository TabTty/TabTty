using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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

    public static void CreateDirectory(string path)
    {
        if (!CreateDirectoryNative(path, 0))
            throw new IOException("Unable to create the transfer directory.", new Win32Exception(Marshal.GetLastWin32Error()));
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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryNative(string path, nint security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out uint info, uint size);
}

