using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabTty.AiTransport;

internal sealed class AiPipe
{
    private const uint Overlapped = 0x40000000;
    private const uint FirstInstance = 0x00080000;
    private const uint QueryAndSynchronize = 0x00101000;
    private const uint TokenQuery = 0x0008;
    private readonly Identity local;
    private readonly string path;

    private readonly record struct Identity(string User, string Logon, bool Elevated, int Session);

    internal AiPipe()
    {
        using var process = Open((uint)Environment.ProcessId);
        local = ProcessIdentity(process);
        path = @"\\.\pipe\TabTty.Ai.v1." + local.Logon;
    }

    internal NamedPipeServerStream Listen(bool first)
    {
        string sddl = "D:P(A;;GA;;;" + local.Logon + ")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out nint descriptor, out _))
            throw LastError();
        try
        {
            var security = new SecurityAttributes
            {
                Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor,
            };
            SafePipeHandle handle = CreateNamedPipe(path, 3u | Overlapped | (first ? FirstInstance : 0),
                8, 9, 64 * 1024, 64 * 1024, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = LastError();
                handle.Dispose();
                throw error;
            }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    internal async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // READ_DATA | WRITE_DATA | READ_ATTRIBUTES | SYNCHRONIZE, without CREATE_PIPE_INSTANCE.
            SafePipeHandle handle = CreateFile(path, 0x00100083, 0, 0, 3,
                Overlapped | 0x00100000 | 0x00010000, 0);
            if (!handle.IsInvalid) return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (2 or 231)) throw new Win32Exception(error);
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }

    internal SafeProcessHandle VerifyPeer(PipeStream pipe, bool server, string sibling, out string image)
    {
        long observed = DateTime.UtcNow.ToFileTimeUtc();
        uint pid = PipePid(pipe, server);
        SafeProcessHandle process = Open(pid);
        try
        {
            if (Created(process) > observed || !IsAlive(process) || ProcessIdentity(process) != local || GetProcessId(process) != pid)
                throw new IOException("Invalid local counterpart.");
            image = CanonicalFile(Image(process));
            string expected = CanonicalFile(Path.Combine(AppContext.BaseDirectory, sibling));
            if (!image.Equals(expected, StringComparison.OrdinalIgnoreCase) || PipePid(pipe, server) != pid ||
                !IsAlive(process))
                throw new IOException("Invalid local counterpart.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    internal static SafeProcessHandle OpenLauncher()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw LastError();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) throw LastError();
        do
        {
            if (entry.ProcessId != Environment.ProcessId) continue;
            var parent = Open(entry.ParentId);
            using var self = Open((uint)Environment.ProcessId);
            try
            {
                if (!IsAlive(parent) || Created(parent) > Created(self) || GetProcessId(parent) != entry.ParentId)
                    throw new IOException("The launcher is unavailable.");
                return parent;
            }
            catch { parent.Dispose(); throw; }
        } while (Process32Next(snapshot, ref entry));
        throw new IOException("The launcher is unavailable.");
    }

    internal static bool IsAlive(SafeProcessHandle handle) => WaitForSingleObject(handle, 0) == 258;

    private static uint PipePid(PipeStream pipe, bool server)
    {
        bool success = server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)
            : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid);
        return success && pid != 0 ? pid : throw LastError();
    }

    private static SafeProcessHandle Open(uint pid)
    {
        SafeProcessHandle handle = OpenProcess(QueryAndSynchronize, false, pid);
        if (!handle.IsInvalid) return handle;
        var error = LastError();
        handle.Dispose();
        throw error;
    }

    private static Identity ProcessIdentity(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) throw LastError();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            nint logon = TokenInfo(token, 28);
            nint elevation = 0, session = 0;
            try
            {
                if (Marshal.ReadInt32(logon) != 1) throw new IOException("Missing logon SID.");
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(logon, IntPtr.Size));
                elevation = TokenInfo(token, 20);
                session = TokenInfo(token, 12);
                return new Identity(identity.User!.Value, sid.Value, Marshal.ReadInt32(elevation) != 0,
                    Marshal.ReadInt32(session));
            }
            finally
            {
                Marshal.FreeHGlobal(logon);
                Marshal.FreeHGlobal(elevation);
                Marshal.FreeHGlobal(session);
            }
        }
    }

    private static nint TokenInfo(SafeAccessTokenHandle token, int kind)
    {
        GetTokenInformation(token, kind, 0, 0, out int size);
        if (size is < 4 or > 65536) throw LastError();
        nint buffer = Marshal.AllocHGlobal(size);
        if (GetTokenInformation(token, kind, buffer, size, out _)) return buffer;
        var error = LastError();
        Marshal.FreeHGlobal(buffer);
        throw error;
    }

    private static long Created(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out long creation, out _, out _, out _) ? creation : throw LastError();

    private static string Image(SafeProcessHandle process)
    {
        var image = new StringBuilder(32768);
        int capacity = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref capacity)) throw LastError();
        return image.ToString();
    }

    private static string CanonicalFile(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var full = new StringBuilder(32768);
        int size = GetFinalPathNameByHandle(handle, full, full.Capacity, 0);
        if (size == 0 || size >= full.Capacity) throw LastError();
        return full.ToString();
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Size;
        internal nint Descriptor;
        internal int Inherit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal nuint Heap;
        internal uint Module, Threads, ParentId;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string FileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint access, uint mode, uint count,
        uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision,
        out nint descriptor, out int size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint info, int size, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder name, int length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
