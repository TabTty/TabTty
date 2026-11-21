using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

// A disposable Windows Cloud Files provider. The launch test observes real
// hydration requests from the file system through this process's stdout.
internal sealed class CloudConstantsProvider
{
    private readonly byte[] contents = "string Marker = \"hydrated\";"u8.ToArray();
    private int fetchCount;
    private nint payload;

    internal static int Run(string root)
    {
        try
        {
            if (!CreateDirectoryW(root, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var registration = new SyncRegistration
            {
                Size = Marshal.SizeOf<SyncRegistration>(),
                ProviderName = "TabTty constants acceptance fixture", ProviderVersion = "1",
                ProviderId = new Guid("798C3A22-6C9F-4732-884F-B5D21C8C1160"),
            };
            var policies = new SyncPolicies
            {
                Size = Marshal.SizeOf<SyncPolicies>(),
                Hydration = 2, // CF_HYDRATION_POLICY_FULL
                Population = 3, // CF_POPULATION_POLICY_ALWAYS_FULL
            };
            Check("CfRegisterSyncRoot", CfRegisterSyncRoot(root, in registration, in policies, 0));
            try { new CloudConstantsProvider().Serve(root); }
            finally { Check("CfUnregisterSyncRoot", CfUnregisterSyncRoot(root)); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Cloud Files fixture: " + error.Message);
            return 1;
        }
    }

    private void Serve(string root)
    {
        FetchCallback fetch = Fetch;
        CallbackRegistration[] callbacks =
        [
            new() { Type = 0, Callback = Marshal.GetFunctionPointerForDelegate(fetch) },
            new() { Type = -1 },
        ];
        var pin = GCHandle.Alloc(contents, GCHandleType.Pinned);
        try
        {
            payload = pin.AddrOfPinnedObject();
            Check("CfConnectSyncRoot", CfConnectSyncRoot(root, callbacks, 0, 0, out long key));
            try
            {
                for (int i = 0; i < 4; i++) CreatePlaceholder(root, $"online-{i}.tttyc");
                Console.WriteLine("ready");
                Console.Out.Flush();
                while (Console.ReadLine() is string command)
                {
                    if (command != "count") throw new InvalidDataException("Unknown fixture command.");
                    Console.WriteLine(Volatile.Read(ref fetchCount));
                    Console.Out.Flush();
                }
            }
            finally { Check("CfDisconnectSyncRoot", CfDisconnectSyncRoot(key)); }
        }
        finally
        {
            GC.KeepAlive(fetch);
            pin.Free();
        }
    }

    private void CreatePlaceholder(string root, string name)
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var file = new Placeholder
        {
            RelativeFileName = Marshal.StringToCoTaskMemUni(name),
            Creation = now, LastAccess = now, LastWrite = now, Change = now,
            Attributes = (uint)FileAttributes.Normal, FileSize = contents.Length,
            Identity = payload, IdentitySize = contents.Length, Flags = 2, // MARK_IN_SYNC
        };
        try
        {
            Check("CfCreatePlaceholders", CfCreatePlaceholders(root, ref file, 1, 1, out int processed));
            Check("placeholder " + name, file.Result);
            if (processed != 1) throw new IOException("The placeholder was not created: " + name);
        }
        finally { Marshal.FreeCoTaskMem(file.RelativeFileName); }
    }

    private void Fetch(in CallbackInfo info, in FetchParameters request)
    {
        Interlocked.Increment(ref fetchCount);
        var operation = new OperationInfo
        {
            Size = Marshal.SizeOf<OperationInfo>(), Connection = info.Connection,
            Transfer = info.Transfer, Request = info.Request,
        };
        var data = new TransferParameters
        {
            Size = Marshal.SizeOf<TransferParameters>(), Offset = request.Offset, Length = request.Length,
        };
        if (request.Offset >= 0 && request.Offset < contents.Length && request.Length > 0)
        {
            data.Buffer = payload + (int)request.Offset;
            data.Length = Math.Min(request.Length, contents.Length - request.Offset);
        }
        else data.Status = unchecked((int)0xC000000D); // STATUS_INVALID_PARAMETER
        int result = CfExecute(in operation, in data);
        if (result < 0) Console.Error.WriteLine($"CfExecute failed (0x{result:X8}).");
    }

    private static void Check(string operation, int result)
    {
        if (result < 0) throw new IOException($"{operation} failed (0x{result:X8}).", result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SyncRegistration
    {
        internal int Size;
        internal string ProviderName, ProviderVersion;
        internal nint RootIdentity;
        internal int RootIdentitySize;
        internal nint FileIdentity;
        internal int FileIdentitySize;
        internal Guid ProviderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SyncPolicies
    {
        internal int Size;
        internal ushort Hydration, HydrationModifier, Population, PopulationModifier;
        internal int InSync, HardLinks, PlaceholderManagement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placeholder
    {
        internal nint RelativeFileName;
        internal long Creation, LastAccess, LastWrite, Change;
        internal uint Attributes;
        internal long FileSize;
        internal nint Identity;
        internal int IdentitySize, Flags, Result;
        internal long CreateUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackRegistration { internal int Type; internal nint Callback; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackInfo
    {
        internal uint Size;
        internal long Connection;
        internal nint Context, VolumeGuid, VolumeDos;
        internal uint SerialNumber;
        internal long RootId;
        internal nint RootIdentity;
        internal uint RootIdentitySize;
        internal long FileId, FileSize;
        internal nint Identity;
        internal uint IdentitySize;
        internal nint Path;
        internal long Transfer;
        internal byte Priority;
        internal nint Correlation, ProcessInfo;
        internal long Request;
    }

    // The native parameters begin with a DWORD and an eight-byte-aligned union.
    [StructLayout(LayoutKind.Sequential)]
    private struct FetchParameters { internal uint Size, Padding, Flags; internal long Offset, Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OperationInfo
    {
        internal int Size, Type;
        internal long Connection, Transfer;
        internal nint Correlation, SyncStatus;
        internal long Request;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TransferParameters
    {
        internal int Size, Padding, Flags, Status;
        internal nint Buffer;
        internal long Offset, Length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void FetchCallback(in CallbackInfo info, in FetchParameters request);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, nint attributes);

    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CfRegisterSyncRoot(string root, in SyncRegistration registration, in SyncPolicies policies, int flags);
    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CfUnregisterSyncRoot(string root);
    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CfConnectSyncRoot(string root, [In] CallbackRegistration[] callbacks, nint context, int flags, out long key);
    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfDisconnectSyncRoot(long key);
    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CfCreatePlaceholders(string root, ref Placeholder file, int count, int flags, out int processed);
    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfExecute(in OperationInfo info, in TransferParameters data);
}
