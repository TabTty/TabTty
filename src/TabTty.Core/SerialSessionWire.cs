using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal sealed class SerialSessionWire : ISessionWire
{
    private const uint Binary = 1, CheckParity = 2, CtsFlow = 4, DsrFlow = 8;
    private const uint DtrOn = 1u << 4, DtrHandshake = 2u << 4, RtsOn = 1u << 12, RtsHandshake = 2u << 12;
    private const uint SoftwareFlow = (1u << 8) | (1u << 9);
    private const uint ConfigMask = 0x7fff;
    private const uint ReceiveOverrun = 0x0001 | 0x0002, ParityOrFrame = 0x0004 | 0x0008;
    private const int IoPending = 997;
    private readonly object gate = new();
    private readonly SafeFileHandle handle;
    private readonly SerialSettings settings;
    private bool closed;
    private bool breakActive;
    private IOException? receiveError;

    private SerialSessionWire(SafeFileHandle handle, SerialSettings settings)
    {
        this.handle = handle;
        this.settings = settings;
    }

    public bool CanSetDtr => settings.FlowControl != "dsrDtr";
    public bool CanSetRts => settings.FlowControl != "rtsCts";

    public static SerialSessionWire Open(SerialSettings settings)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("COM ports require Windows.");
        if (!ConnectionProfile.IsComPortName(settings.PortName))
            throw new InvalidDataException("Invalid COM port name.");

        var handle = CreateFile(@"\\.\" + settings.PortName.ToUpperInvariant(),
            0xC0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            IOException error = NativeFailure("Open COM port");
            handle.Dispose();
            throw error;
        }

        try
        {
            var requested = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
            Check(GetCommState(handle, ref requested), "Read COM settings");
            requested.BaudRate = settings.BaudRate;
            requested.ByteSize = (byte)settings.DataBits;
            requested.Parity = settings.Parity switch
            {
                "odd" => 1, "even" => 2, "mark" => 3, "space" => 4, _ => 0,
            };
            requested.StopBits = settings.StopBits == 2 ? (byte)2 : (byte)0;
            requested.Flags = Binary | (requested.Parity == 0 ? 0 : CheckParity) |
                (settings.FlowControl == "dsrDtr" ? DsrFlow | DtrHandshake : settings.Dtr ? DtrOn : 0) |
                (settings.FlowControl == "rtsCts" ? CtsFlow | RtsHandshake : settings.Rts ? RtsOn : 0) |
                (settings.FlowControl == "xonXoff" ? SoftwareFlow : 0);
            requested.XonChar = 0x11;
            requested.XoffChar = 0x13;
            Check(SetCommState(handle, ref requested), "Configure COM port");
            var actual = new Dcb { Length = requested.Length };
            Check(GetCommState(handle, ref actual), "Verify COM settings");
            if (actual.BaudRate != requested.BaudRate || actual.ByteSize != requested.ByteSize ||
                actual.Parity != requested.Parity || actual.StopBits != requested.StopBits ||
                (actual.Flags & ConfigMask) != requested.Flags)
                throw new IOException("The COM driver did not apply the requested line settings.");
            var timeouts = new CommTimeouts
            {
                ReadInterval = uint.MaxValue,
                ReadMultiplier = uint.MaxValue,
                ReadConstant = 250,
                WriteMultiplier = (uint)Math.Ceiling(12_000d / settings.BaudRate),
                WriteConstant = 5000,
            };
            Check(SetCommTimeouts(handle, ref timeouts), "Configure COM timeouts");
            return new SerialSessionWire(handle, settings);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return 0;
        if (receiveError is { } failure) throw failure;
        while (true)
        {
            int count = await Task.Run(() => Transfer(buffer, write: false, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (count != 0) return count;
        }
    }

    internal const int WriteChunkBytes = 1024;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int requested = Math.Min(buffer.Length, WriteChunkBytes);
            int count = await WriteSomeAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count != requested) throw new IOException("COM write timed out before sending the entire chunk.");
            buffer = buffer[count..];
        }
    }

    public async ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
    {
        var chunk = buffer[..Math.Min(buffer.Length, WriteChunkBytes)];
        return await Task.Run(() => Transfer(chunk, write: true, token), token).ConfigureAwait(false);
    }

    private unsafe int Transfer(ReadOnlyMemory<byte> buffer, bool write, CancellationToken cancellationToken)
    {
        using var completed = new ManualResetEvent(false);
        using var pin = buffer.Pin();
        var operation = new Overlapped { Event = completed.SafeWaitHandle.DangerousGetHandle() };
        IntPtr operationAddress = (IntPtr)(&operation);
        IntPtr device = IntPtr.Zero;
        bool ownsReference = false;
        try
        {
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(closed, this);
                if (write && breakActive) throw new InvalidOperationException("A COM break is in progress.");
                handle.DangerousAddRef(ref ownsReference);
                device = handle.DangerousGetHandle();
                bool success = write
                    ? WriteFile(device, pin.Pointer, (uint)buffer.Length, IntPtr.Zero, operationAddress)
                    : ReadFile(device, pin.Pointer, (uint)buffer.Length, IntPtr.Zero, operationAddress);
                if (!success && Marshal.GetLastPInvokeError() != IoPending)
                    throw NativeFailure(write ? "Write to COM port" : "Read from COM port");
            }

            using var registration = cancellationToken.Register(() => CancelIoEx(device, operationAddress));
            if (!GetOverlappedResult(device, operationAddress, out uint count, true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw NativeFailure(write ? "Complete COM write" : "Complete COM read");
            }

            if (!write)
            {
                Check(ClearCommError(device, out uint errors, IntPtr.Zero), "Read COM line status");
                if ((errors & (ReceiveOverrun | ParityOrFrame)) != 0)
                {
                    receiveError = new IOException((errors & ReceiveOverrun) != 0
                        ? "COM receive overrun. Some input was lost."
                        : "COM parity or framing error.");
                    if (count == 0) throw receiveError;
                }
            }

            return checked((int)count);
        }
        finally
        {
            if (ownsReference) handle.DangerousRelease();
        }
    }

    public void SetLine(bool dtr, bool enabled)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (dtr ? !CanSetDtr : !CanSetRts)
                throw new InvalidOperationException("This modem line is controlled by the selected handshake.");
            uint command = dtr ? (enabled ? 5u : 6u) : (enabled ? 3u : 4u);
            Check(EscapeCommFunction(handle, command), "Change COM modem line");
        }
    }

    public async Task SendBreakAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(closed, this);
            if (breakActive) throw new InvalidOperationException("A COM break is already in progress.");
            Check(SetCommBreak(handle), "Start COM break");
            breakActive = true;
        }

        try
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                breakActive = false;
                if (!closed) Check(ClearCommBreak(handle), "Clear COM break");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            if (breakActive) ClearCommBreak(handle);
            CancelIoEx(handle.DangerousGetHandle(), IntPtr.Zero);
            handle.Dispose();
        }
    }

    private static void Check(bool success, string operation)
    {
        if (!success) throw NativeFailure(operation);
    }

    private static IOException NativeFailure(string operation)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException($"{operation}: {error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length, BaudRate, Flags;
        public ushort Reserved, XonLimit, XoffLimit;
        public byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EventChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadInterval, ReadMultiplier, ReadConstant, WriteMultiplier, WriteConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public IntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr Event;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr attributes,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetCommState(SafeFileHandle file, ref Dcb state);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommState(SafeFileHandle file, ref Dcb state);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ClearCommError(IntPtr file, out uint errors, IntPtr status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool EscapeCommFunction(SafeFileHandle file, uint command);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommBreak(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ClearCommBreak(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint count, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(IntPtr file, void* buffer, uint length, IntPtr count, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(IntPtr file, void* buffer, uint length, IntPtr count, IntPtr overlapped);
}
