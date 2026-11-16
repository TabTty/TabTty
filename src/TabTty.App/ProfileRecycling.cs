using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabTty.App;

internal static class ProfileRecycling
{
    public static void Recycle(Window owner, string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".ttty", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
            throw new IOException(App.Text("RecycleProfileFailed"));
        RecycleItem(new WindowInteropHelper(owner).Handle, fullPath);
    }

    internal static void RecycleItem(nint owner, string fullPath)
    {
        nint item = 0;
        IFileOperation? operation = null;
        bool issued = false;
        try
        {
            Guid shellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(fullPath, 0, ref shellItemId, out item));
            operation = (IFileOperation)(object)new FileOperation();
            // FOFX_RECYCLEONDELETE fails if recycling is unavailable.
            const uint recycleOnDelete = 0x00080000, noErrorUi = 0x0400, earlyFailure = 0x00100000;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(recycleOnDelete | noErrorUi | earlyFailure));
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(owner));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, 0));
            issued = true;
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            if (aborted) throw new RecycleOutcomeException();
            if (Path.Exists(fullPath)) throw new RecycleOutcomeException();
        }
        catch (COMException exception) when (issued)
        {
            const int recycleForceNuke = unchecked((int)0x80270036);
            const int recycleBinNotFound = unchecked((int)0x8027003A);
            if (exception.ErrorCode is recycleForceNuke or recycleBinNotFound)
                throw new IOException(App.Text("RecycleProfileFailed"), exception);
            throw new RecycleOutcomeException(exception);
        }
        catch (COMException exception) { throw new IOException(App.Text("RecycleProfileFailed"), exception); }
        finally
        {
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
            if (item != 0) Marshal.Release(item);
        }
    }

    internal sealed class RecycleOutcomeException(Exception? error = null) :
        IOException(App.Text("RecycleProfileFailed"), error);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid id,
        out nint item);

    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private sealed class FileOperation { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint window);
        [PreserveSig] int ApplyPropertiesToItem(nint item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(nint item, nint name, nint sink);
        [PreserveSig] int RenameItems(nint items, nint name);
        [PreserveSig] int MoveItem(nint item, nint destination, nint name, nint sink);
        [PreserveSig] int MoveItems(nint items, nint destination);
        [PreserveSig] int CopyItem(nint item, nint destination, nint name, nint sink);
        [PreserveSig] int CopyItems(nint items, nint destination);
        [PreserveSig] int DeleteItem(nint item, nint sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
