using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal static class NativeTerminalCoreDiagnostics
{
    public static long FedBytes, EchoBytes, FeedBatches, SnapshotExports, UnsupportedErrors;
    public static long CreatedModels, ClosedModels, ManagedFallbackModels, ManagedHandoffs;
    public static long NativePreflightElapsedTicks;
    public static long CompactSnapshotRows, MaterializedCellRows, MaterializedCells;
    public static long NativeFeedElapsedTicks, NativeSnapshotElapsedTicks, SnapshotMaterializationElapsedTicks, SnapshotMaterializationCount;
    public static long LiveBytes => NativeTerminalCore.Enabled ? checked((long)NativeTerminalCore.Native.LiveBytes()) : 0;
    public static long PeakBytes => NativeTerminalCore.Enabled ? checked((long)NativeTerminalCore.Native.PeakBytes()) : 0;
    public static long LiveModels => NativeTerminalCore.Enabled ? checked((long)NativeTerminalCore.Native.LiveModels()) : 0;
}

internal sealed unsafe class NativeTerminalCore : IDisposable
{
    internal static readonly bool Enabled = !string.Equals(Environment.GetEnvironmentVariable("TABTTY_TERMINAL_CORE"),
        "off", StringComparison.OrdinalIgnoreCase);
    private readonly CoreHandle handle;
    private readonly ResourceQuota quota;
    private readonly ResourceReservation memory;
    private readonly Dictionary<long, TerminalSnapshot.Row> rowCache = new();
    private readonly HashSet<long> visibleIds = new();
    private readonly List<long> expiredIds = new();
    private bool disposed, completed;
    private int? interval;
    internal bool HasReceivedInput { get; private set; }
    internal bool BracketedPaste { get; private set; }
    internal int RecordedInterval => interval ?? 0;

    internal NativeTerminalCore(int columns, int rows, int retainedRows, int ambiguousWidth, ResourceQuota quota)
    {
        if (sizeof(EffectsView) != 32 || sizeof(CellView) != 20 || sizeof(RowView) != 72 ||
            sizeof(SnapshotView) != 40 || sizeof(TabView) != 8 || sizeof(ExportRow) != 120 ||
            sizeof(SourceView) != 48 || sizeof(ExportState) != 152 || Native.AbiVersion() != 2)
            throw new InvalidOperationException("The native terminal ABI does not match this application.");
        this.quota = quota;
        // Admission precedes native allocation; unused temporary allowance is released
        // after each operation while the native engine keeps its own hard byte limit.
        Check(Native.CreateRequired(columns, rows, retainedRows, ambiguousWidth, out ulong required), "creation preflight");
        memory = quota.Reserve(checked((long)required));
        try
        {
            Check(Native.Create(columns, rows, retainedRows, ambiguousWidth, required, out nint created), "create");
            if (created == 0) throw new InvalidOperationException("Native terminal returned an empty handle.");
            handle = new CoreHandle(created);
            Interlocked.Increment(ref NativeTerminalCoreDiagnostics.CreatedModels);
            ReconcileMemoryCharge();
        }
        catch { memory.Dispose(); throw; }
    }

    internal bool TryFeed(uint source, ReadOnlySpan<byte> payload, uint flags, int recordingInterval,
        Action<byte[]>? textSink, Action<byte[]>? terminalSink)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("Terminal reception has ended.");
        if (payload.IsEmpty) return true;
        if (interval is { } initial && initial != recordingInterval)
            return false;
        if (!TryPrepare(0, source, payload, flags)) return false;
        EffectsView effects;
        long started = Stopwatch.GetTimestamp();
        fixed (byte* bytes = payload)
            Check(Native.Feed(handle, source, bytes, checked((uint)payload.Length), flags, out effects), "receive");
        Interlocked.Add(ref NativeTerminalCoreDiagnostics.NativeFeedElapsedTicks, Stopwatch.GetTimestamp() - started);
        ReconcileMemoryCharge();
        interval = recordingInterval;
        HasReceivedInput = true;
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.FeedBatches);
        if (source == 2) Interlocked.Add(ref NativeTerminalCoreDiagnostics.EchoBytes, payload.Length);
        else Interlocked.Add(ref NativeTerminalCoreDiagnostics.FedBytes, payload.Length);
        PublishEffects(effects, textSink, terminalSink);
        return true;
    }

    internal bool TryComplete(uint flags, Action<byte[]>? textSink, Action<byte[]>? terminalSink)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return true;
        if (!TryPrepare(1, 0, [], flags)) return false;
        Check(Native.Complete(handle, flags, out EffectsView effects), "complete receive");
        ReconcileMemoryCharge();
        completed = true;
        PublishEffects(effects, textSink, terminalSink);
        return true;
    }

    internal bool TryFlushText(uint flags, Action<byte[]>? textSink, Action<byte[]>? terminalSink)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryPrepare(2, 0, [], flags)) return false;
        Check(Native.FlushText(handle, flags, out EffectsView effects), "flush Text recording");
        ReconcileMemoryCharge();
        PublishEffects(effects, textSink, terminalSink);
        return true;
    }

    internal void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (HasReceivedInput) throw Unsupported("resize after reception");
        Check(Native.Resize(handle, columns, rows), "resize");
    }

    internal bool TrySnapshot(out TerminalSnapshot? resultSnapshot)
    {
        resultSnapshot = null;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryPrepare(3, 0, [], 0)) return false;
        long nativeStarted = Stopwatch.GetTimestamp();
        Check(Native.Snapshot(handle, out SnapshotView snapshot), "snapshot");
        Interlocked.Add(ref NativeTerminalCoreDiagnostics.NativeSnapshotElapsedTicks, Stopwatch.GetTimestamp() - nativeStarted);
        ReconcileMemoryCharge();
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.SnapshotExports);
        long started = Stopwatch.GetTimestamp();
        try
        {
            int count = checked((int)snapshot.RowCount);
            var result = new TerminalSnapshot.Row[count];
            visibleIds.Clear();
            for (int index = 0; index < count; index++)
            {
                RowView row = snapshot.Rows[index];
                visibleIds.Add(row.Id);
                if (!rowCache.TryGetValue(row.Id, out TerminalSnapshot.Row cached) ||
                    cached.DisplayRevision != row.DisplayRevision || cached.LogicalId != row.LogicalId ||
                    cached.LogicalOffset != row.LogicalOffset || cached.SoftWrapToNext != (row.SoftWrap != 0))
                {
                    cached = MaterializeRow(row);
                    rowCache[row.Id] = cached;
                }
                result[index] = cached;
            }
            expiredIds.Clear();
            foreach (long id in rowCache.Keys)
                if (!visibleIds.Contains(id)) expiredIds.Add(id);
            foreach (long id in expiredIds) rowCache.Remove(id);
            var rows = new TerminalSnapshot(result, (snapshot.Flags & 4) != 0, snapshot.CursorRow,
                snapshot.CursorColumn, snapshot.Columns, snapshot.ScreenRows, snapshot.ScreenStart,
                (snapshot.Flags & 1) != 0, (TerminalCursorShape)snapshot.CursorShape,
                (snapshot.Flags & 2) != 0, default, quota);
            GC.KeepAlive(handle);
            resultSnapshot = rows;
            return true;
        }
        finally
        {
            GC.KeepAlive(handle);
            Interlocked.Add(ref NativeTerminalCoreDiagnostics.SnapshotMaterializationElapsedTicks,
                Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref NativeTerminalCoreDiagnostics.SnapshotMaterializationCount);
        }
    }

    private TerminalSnapshot.Row MaterializeRow(RowView row)
    {
        int textLength = checked((int)row.TextLength);
        ResourceReservation textMemory = quota.Reserve(textLength == 0 ? 0 : 24L + textLength * 2L);
        try
        {
            string displayText = new(row.Text, 0, textLength);
            CompactTerminalCells? cells = row.CellCount == 0 ? null : new(
                new ReadOnlySpan<CellView>(row.Cells, checked((int)row.CellCount)),
                new ReadOnlySpan<char>(row.CellText, checked((int)row.CellTextLength)), quota);
            return new TerminalSnapshot.Row(row.Id, row.LogicalId, row.LogicalOffset, displayText,
                row.SoftWrap != 0, default, textMemory, null, row.DisplayRevision, cells);
        }
        catch { textMemory.Dispose(); throw; }
    }
    internal static uint? Color(uint encoded) => (encoded & 0x01000000U) == 0 ? null : encoded & 0xFFFFFFU;
    private bool TryPrepare(uint operation, uint source, ReadOnlySpan<byte> payload, uint flags)
    {
        long started = Stopwatch.GetTimestamp();
        int status;
        ulong required;
        fixed (byte* bytes = payload)
            status = Native.Preflight(handle, operation, source, bytes, checked((uint)payload.Length), flags, out required);
        Interlocked.Add(ref NativeTerminalCoreDiagnostics.NativePreflightElapsedTicks, Stopwatch.GetTimestamp() - started);
        if (status == 2) return false;
        Check(status, "operation preflight");
        try { memory.Resize(Math.Max(memory.Size, checked((long)required))); }
        catch (ResourceLimitException) { return false; }
        Check(Native.SetLimit(handle, checked((ulong)memory.Size)), "memory admission");
        return true;
    }

    internal void Export(Action<ExportState> import)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryPrepare(4, 0, [], 0)) throw new ResourceLimitException();
        Check(Native.Export(handle, out ExportState state), "state export");
        ReconcileMemoryCharge();
        try { import(state); }
        finally { GC.KeepAlive(handle); }
    }

    private void ReconcileMemoryCharge()
    {
        long charged = checked((long)Native.QuotaBytes(handle));
        if (charged > memory.Size)
            throw new InvalidDataException("The native terminal exceeded its admitted memory allowance.");
        memory.Resize(charged);
    }
    private void PublishEffects(EffectsView effects, Action<byte[]>? textSink, Action<byte[]>? terminalSink)
    {
        BracketedPaste = (effects.ModeFlags & 16) != 0;
        try
        {
            PublishChunks(new ReadOnlySpan<byte>(effects.Text, checked((int)effects.TextLength)), textSink);
            PublishChunks(new ReadOnlySpan<byte>(effects.Terminal, checked((int)effects.TerminalLength)), terminalSink);
        }
        finally { GC.KeepAlive(handle); }
    }

    private static void PublishChunks(ReadOnlySpan<byte> bytes, Action<byte[]>? sink)
    {
        if (sink is null) return;
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(bytes.Length, 4096);
            if (count < bytes.Length)
                while ((bytes[count] & 0xC0) == 0x80) count--;
            // The sink admits this small owned array before allocating the next one.
            // Never split a UTF-8 scalar, including malformed-input replacement text.
            sink(bytes[..count].ToArray());
            bytes = bytes[count..];
        }
    }

    internal static InvalidDataException Unsupported(string operation)
    {
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.UnsupportedErrors);
        return new InvalidDataException("Native terminal does not support " + operation + ".");
    }

    private static void Check(int status, string operation)
    {
        if (status == 0) return;
        if (status == 2) throw Unsupported(operation);
        throw new InvalidDataException($"Native terminal {operation} failed with status {status}.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        handle.Dispose();
        memory.Dispose();
        rowCache.Clear();
        visibleIds.Clear();
        expiredIds.Clear();
    }

    internal sealed class CoreHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal CoreHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            Native.Destroy(handle);
            Interlocked.Increment(ref NativeTerminalCoreDiagnostics.ClosedModels);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EffectsView
    {
        public byte* Text;
        public uint TextLength, ModeFlags;
        public byte* Terminal;
        public uint TerminalLength, Padding1;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CellView
    {
        public uint Offset;
        public ushort Length;
        public short Width;
        public uint Foreground, Background, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RowView
    {
        public long Id, LogicalId, LogicalOffset, DisplayRevision;
        public CellView* Cells;
        public char* Text;
        public char* CellText;
        public uint CellCount, TextLength, CellTextLength, SoftWrap;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SnapshotView
    {
        public RowView* Rows;
        public uint RowCount;
        public int CursorRow, CursorColumn, Columns, ScreenRows, ScreenStart;
        public uint Flags, CursorShape;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TabView { public uint Start, End; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExportRow
    {
        public RowView View;
        public TabView* Tabs;
        public char* Submitted;
        public long ContentRevision, LogicalClusterOffset, SubmittedRevision;
        public uint TabCount, SubmittedLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceView
    {
        public byte* Pending;
        public byte* Parameters;
        public uint* LastGraphic;
        public uint PendingCount, ParameterCount, LastGraphicCount, Flags, Escape, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExportState
    {
        public SnapshotView Snapshot;
        public ExportRow* Rows;
        public SourceView* Sources;
        public uint* Cluster;
        public uint* RecordedCluster;
        public long ClusterRow;
        public uint ClusterCount, ClusterColumn, ClusterWidth, CursorRow;
        public uint StyleForeground, StyleBackground, StyleFlags, ClusterForeground, ClusterBackground, ClusterFlags;
        public uint ModeFlags, RecordedColumn, RecordedKnown, RecordedCount;
        public uint RowCount, SourceCount, Reserved0, Reserved1;
    }

    internal static class Native
    {
        private const string Library = "tabtty_terminal";
        [DllImport(Library, EntryPoint = "tc_preflight", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Preflight(CoreHandle handle, uint operation, uint source, byte* data, uint length, uint flags, out ulong requiredPeak);
        [DllImport(Library, EntryPoint = "tc_create_required", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateRequired(int columns, int rows, int retained, int ambiguous, out ulong required);
        [DllImport(Library, EntryPoint = "tc_set_limit", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetLimit(CoreHandle handle, ulong allowance);
        [DllImport(Library, EntryPoint = "tc_export", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Export(CoreHandle handle, out ExportState state);
        [DllImport(Library, EntryPoint = "tc_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint AbiVersion();
        [DllImport(Library, EntryPoint = "tc_owned_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong OwnedBytes(CoreHandle handle);
        [DllImport(Library, EntryPoint = "tc_quota_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong QuotaBytes(CoreHandle handle);
        [DllImport(Library, EntryPoint = "tc_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(int columns, int rows, int retained, int ambiguous, ulong allowance, out nint handle);
        [DllImport(Library, EntryPoint = "tc_feed", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Feed(CoreHandle handle, uint source, byte* data, uint length, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_complete", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Complete(CoreHandle handle, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_flush_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FlushText(CoreHandle handle, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_resize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Resize(CoreHandle handle, int columns, int rows);
        [DllImport(Library, EntryPoint = "tc_snapshot", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Snapshot(CoreHandle handle, out SnapshotView snapshot);
        [DllImport(Library, EntryPoint = "tc_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(nint handle);
        [DllImport(Library, EntryPoint = "tc_live_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong LiveBytes();
        [DllImport(Library, EntryPoint = "tc_peak_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong PeakBytes();
        [DllImport(Library, EntryPoint = "tc_live_models", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong LiveModels();
    }
}
