using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal sealed class NativeTerminalExecutionException(Exception cause)
    : IOException("The admitted terminal operation failed during execution.", cause);

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
    // One source preview, its fixed parameter buffer and bounded parser stack.
    private const int PreviewWorkspaceBytes = 16 * 1024;
    internal bool BracketedPaste { get; private set; }
    internal bool ApplicationCursorKeys { get; private set; }
    internal bool ApplicationKeypad { get; private set; }
    internal TerminalMouseMode MouseMode { get; private set; }
    internal bool IsAlternateScreen { get; private set; }
    internal long LayoutEpoch { get; private set; }

    internal static int CheckUtf8Input(ReadOnlySpan<char> text)
    {
        fixed (char* input = text)
        {
            int result = Native.EncodeUtf8(input, checked((uint)text.Length), null, 0, out uint bytes);
            if (result == 1) throw new System.Text.EncoderFallbackException("The terminal input contains invalid Unicode.");
            if (result == 2) throw new ArgumentException("Terminal text exceeds its size limit.", nameof(text));
            Check(result, "UTF-8 input preflight");
            return checked((int)bytes);
        }
    }

    internal static byte[] PrepareUtf8Input(ReadOnlySpan<char> text, int size)
    {
        if (size == 0) return [];
        var result = new byte[size];
        fixed (char* input = text)
        fixed (byte* output = result)
        {
            Check(Native.EncodeUtf8(input, checked((uint)text.Length), output, checked((uint)size), out uint written), "UTF-8 input");
            if (written != size) throw new InvalidDataException("Terminal input did not match its prepared length.");
        }
        return result;
    }

    internal static int CheckLegacyInput(uint page, ReadOnlySpan<char> text, bool shiftKana)
    {
        fixed (char* input = text)
        {
            int result = Native.EncodeLegacy(page, input, checked((uint)text.Length), shiftKana ? 8U : 0U,
                null, 0, out uint size);
            if (result == 1) throw new EncoderFallbackException("The selected encoding cannot represent this text exactly.");
            if (result == 2) throw new ArgumentException("Encoded terminal input exceeds 1 MiB.", nameof(text));
            Check(result, "legacy input preflight");
            return checked((int)size);
        }
    }

    internal static byte[] PrepareLegacyInput(uint page, ReadOnlySpan<char> text, bool shiftKana, int length)
    {
        if (length == 0) return [];
        var result = new byte[length];
        fixed (char* input = text)
        fixed (byte* output = result)
        {
            Check(Native.EncodeLegacy(page, input, checked((uint)text.Length), shiftKana ? 8U : 0U,
                output, checked((uint)length), out uint size), "legacy input");
            if (size != length)
                throw new InvalidDataException("Terminal input did not match its prepared length.");
        }
        return result;
    }

    internal static byte[] EncodeKey(TerminalKey key, TerminalModifiers modifiers, bool applicationCursor, bool applicationKeypad)
    {
        byte* output = stackalloc byte[32];
        uint modes = (applicationCursor ? 1U : 0U) | (applicationKeypad ? 2U : 0U);
        Check(Native.EncodeKey((uint)key, (uint)modifiers, modes, output, out uint written), "key encoding");
        return new ReadOnlySpan<byte>(output, checked((int)written)).ToArray();
    }

    internal static TerminalMouseMode DecodeMouseMode(uint tracking, int generation) =>
        new((int)(tracking & 0x7FFF_FFFF), (tracking & 0x8000_0000) != 0, generation);

    internal static byte[] EncodeMouse(TerminalMouseInput input, TerminalMouseMode mode, int columns, int rows)
    {
        byte* output = stackalloc byte[32];
        var request = new MouseInput
        {
            Button = input.Button, Column = input.Column, Row = input.Row, Generation = input.Generation,
            Flags = (uint)input.Modifiers | (input.Release ? 8U : 0U) | (input.Motion ? 16U : 0U),
        };
        uint tracking = (uint)mode.Tracking | (mode.SgrCoordinates ? 0x8000_0000U : 0U);
        Check(Native.EncodeMouse(&request, tracking, mode.Generation, columns, rows, output, out uint written), "mouse encoding");
        return new ReadOnlySpan<byte>(output, checked((int)written)).ToArray();
    }

    internal sealed class LegacyDecoder : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal LegacyDecoder(uint page, bool shiftKana) : base(true)
        {
            Check(Native.LegacyOpen(page, shiftKana ? 1U : 0U, out nint receiver), "legacy converter creation");
            SetHandle(receiver);
        }

        internal bool Step(int value, bool inVt, int interval, Action<Rune, int> emit)
        {
            Check(Native.LegacyStep(this, value, inVt ? 1U : 0U, unchecked((uint)interval), out var decoded),
                "legacy reception");
            for (int index = 0; index < decoded.Length; index++)
                emit(new Rune(checked((int)decoded.Scalars[index * 2])), unchecked((int)decoded.Scalars[index * 2 + 1]));
            return decoded.Partial != 0;
        }

        protected override bool ReleaseHandle()
        {
            Native.LegacyDestroy(handle);
            return true;
        }
    }

    internal NativeTerminalCore(int columns, int rows, int retainedRows, int ambiguousWidth, ResourceQuota quota,
        uint receivePage = 0, bool receiveSevenBitKana = false)
    {
        if (sizeof(EffectsView) != 56 || sizeof(CellView) != 20 || sizeof(RowView) != 72 ||
            sizeof(SnapshotView) != 48 || sizeof(TabView) != 8 || sizeof(ExportRow) != 152 ||
            sizeof(SourceView) != 64 || sizeof(ExportState) != 168 || sizeof(EditingView) != 256 ||
            sizeof(MouseInput) != 20 || sizeof(ScreenStateView) != 48 || sizeof(ScreenRowView) != 56 ||
            Native.AbiVersion() != 8)
            throw new InvalidOperationException("The native terminal ABI does not match this application.");
        this.quota = quota;
        // Admission precedes native allocation; unused temporary allowance is released
        // after each operation while the native engine keeps its own hard byte limit.
        Check(Native.CreateRequired(columns, rows, retainedRows, ambiguousWidth, receivePage, out ulong required), "creation preflight");
        memory = quota.Reserve(checked((long)required));
        CoreHandle? owned = null;
        try
        {
            Check(Native.Create(columns, rows, retainedRows, ambiguousWidth, receivePage, receiveSevenBitKana ? 1U : 0U,
                required, out nint created), "create");
            if (created == 0) throw new InvalidOperationException("Native terminal returned an empty handle.");
            handle = owned = new CoreHandle(created);
            Interlocked.Increment(ref NativeTerminalCoreDiagnostics.CreatedModels);
            ReconcileMemoryCharge();
        }
        catch
        {
            owned?.Dispose();
            memory.Dispose();
            throw;
        }
    }

    internal bool TryFeed(uint source, ReadOnlySpan<byte> payload, uint flags,
        Action<byte[]>? textSink, Action<byte[]>? terminalSink, CancellationToken token = default,
        Action<byte[]>? replySink = null, int pendingReplies = 0, Action? bell = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("Terminal reception has ended.");
        if (payload.IsEmpty) return true;
        if (!TryRunEffects(0, source, payload, flags | (checked((uint)pendingReplies) << 8),
            textSink, terminalSink, token, replySink, bell)) return false;
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.FeedBatches);
        if (source == 2) Interlocked.Add(ref NativeTerminalCoreDiagnostics.EchoBytes, payload.Length);
        else Interlocked.Add(ref NativeTerminalCoreDiagnostics.FedBytes, payload.Length);
        return true;
    }

    internal void BeginRecording(uint flags, int epoch)
    {
        Check(Native.BeginRecording(handle, flags, checked((uint)epoch)), "begin recording");
        ReconcileMemoryCharge();
    }

    internal void EndRecording(uint flags, int epoch, bool abort, Action<byte[]>? textSink)
    {
        if (!abort && !TryPrepare(2, 0, [], flags)) throw new ResourceLimitException();
        try
        {
            Check(Native.CutoffRecording(handle, flags, checked((uint)epoch), abort ? 1U : 0U, out var effects),
                "end recording");
            PublishEffects(effects, textSink, null, null, null);
        }
        finally { ReconcileMemoryCharge(); }
    }

    internal bool TryComplete(uint flags, Action<byte[]>? textSink, Action<byte[]>? terminalSink)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return true;
        if (!TryRunEffects(1, 0, [], flags, textSink, terminalSink)) return false;
        completed = true;
        return true;
    }

    internal bool TryFlushText(uint flags, Action<byte[]>? textSink, Action<byte[]>? terminalSink)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return TryRunEffects(2, 0, [], flags, textSink, terminalSink);
    }

    private bool TryRunEffects(uint operation, uint source, ReadOnlySpan<byte> payload, uint flags,
        Action<byte[]>? textSink, Action<byte[]>? terminalSink, CancellationToken token = default,
        Action<byte[]>? replySink = null, Action? bell = null)
    {
        if (payload.Length > 1024 * 1024) return false;
        ResourceReservation workspace;
        try { workspace = quota.Reserve(PreviewWorkspaceBytes + payload.Length); }
        catch (ResourceLimitException) { return false; }
        using (workspace)
        {
            Span<byte> input = payload.Length <= 1024 ? stackalloc byte[payload.Length] : new byte[payload.Length];
            payload.CopyTo(input);
            if (!TryPrepare(operation, source, input, flags)) return false;
            try
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    EffectsView effects;
                    if (operation == 0)
                    {
                        long started = Stopwatch.GetTimestamp();
                        fixed (byte* bytes = input)
                            Check(Native.Feed(handle, source, bytes, checked((uint)input.Length), flags, out effects), "receive");
                        Interlocked.Add(ref NativeTerminalCoreDiagnostics.NativeFeedElapsedTicks, Stopwatch.GetTimestamp() - started);
                    }
                    else if (operation == 1)
                        Check(Native.Complete(handle, flags, out effects), "complete receive");
                    else Check(Native.FlushText(handle, flags, out effects), "flush Text recording");
                    PublishEffects(effects, textSink, terminalSink, replySink, bell);
                    return true;
                }
                finally { ReconcileMemoryCharge(); }
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or OverflowException)
            {
                throw new NativeTerminalExecutionException(exception);
            }
        }
    }

    internal PreparedResize PrepareResize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Check(Native.ResizeRequired(handle, columns, rows, out ulong required), "resize estimate");
        memory.Resize(Math.Max(memory.Size, checked((long)required)));
        try
        {
            Check(Native.SetLimit(handle, checked((ulong)memory.Size)), "resize admission");
            int status = Native.ResizePrepare(handle, columns, rows, out nint plan);
            if (status == 5) throw new ResourceLimitException();
            Check(status, "resize preparation");
            return new PreparedResize(this, plan);
        }
        catch
        {
            ReconcileMemoryCharge();
            throw;
        }
    }

    internal sealed class PreparedResize : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly NativeTerminalCore owner;

        internal PreparedResize(NativeTerminalCore owner, nint plan) : base(true)
        {
            this.owner = owner;
            SetHandle(plan);
        }

        internal void Apply(Action<byte[]>? textSink)
        {
            Check(Native.ResizeApply(owner.handle, this, out var effects), "resize");
            owner.PublishEffects(effects, textSink, null, null, null);
        }

        protected override bool ReleaseHandle()
        {
            Native.ResizeDrop(handle);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            bool ownsPlan = !IsClosed;
            base.Dispose(disposing);
            if (disposing && ownsPlan && !owner.disposed) owner.ReconcileMemoryCharge();
        }
    }

    internal AiTerminalState ReadScreenState()
    {
        Check(Native.ScreenState(handle, out var state), "screen state");
        return new AiTerminalState(state.LayoutEpoch, state.Revision, state.Columns, state.Rows,
            state.CursorRow, state.CursorColumn, (state.Flags & 1) != 0, (TerminalCursorShape)state.CursorShape,
            (state.Flags & 2) != 0, (state.Flags & 4) != 0, (state.Flags & 8) != 0,
            (state.Flags & 16) != 0, DecodeMouseMode(state.MouseTracking, state.MouseGeneration));
    }

    internal (long Id, long Revision) ReadScreenRow(int row, int first, int last, Action<TerminalCell> visit)
    {
        Check(Native.ScreenRow(handle, (uint)row, (uint)(first - 1), (uint)last, out var view), "screen row");
        if (view.Id == 0) return default;
        var cells = new ReadOnlySpan<CellView>(view.Cells, checked((int)view.CellCount));
        var formats = new ReadOnlySpan<FormatView>(view.Formats, checked((int)view.FormatCount));
        long workspaceSize = 64 + formats.Length * 4L + (view.End - view.First) * 56L;
        foreach (var cell in cells)
            if (cell.Length != ushort.MaxValue) workspaceSize += cell.Length * 4L;
        using var workspace = quota.Reserve(workspaceSize);
        for (uint column = view.First; column < view.End; column++)
        {
            int index = checked((int)(column - view.First));
            var cell = index < cells.Length ? cells[index] : new CellView { Length = ushort.MaxValue };
            int formatLength = 0;
            while (formatLength < formats.Length &&
                (formats[formatLength].Column == column || column + 1 == view.End && formats[formatLength].Column == view.End))
                formatLength++;
            if (cell.Width >= 0)
            {
                var text = cell.Length == ushort.MaxValue ? " ".AsSpan() : new ReadOnlySpan<char>(view.Arena + cell.Offset, cell.Length);
                string content;
                if (formatLength == 0) content = cell.Length == ushort.MaxValue ? " " : new string(text);
                else
                {
                    int prefixLength = 0;
                    while (prefixLength < formatLength && formats[prefixLength].Column == column)
                        prefixLength++;
                    Span<char> combined = new char[text.Length + formatLength];
                    for (int mark = 0; mark < prefixLength; mark++) combined[mark] = checked((char)formats[mark].Scalar);
                    text.CopyTo(combined[prefixLength..]);
                    for (int mark = prefixLength; mark < formatLength; mark++)
                        combined[mark + text.Length] = checked((char)formats[mark].Scalar);
                    content = new string(combined);
                }
                visit(new TerminalCell((int)column, Math.Max(1, (int)cell.Width), 0, content,
                    new TerminalStyle(Color(cell.Foreground), Color(cell.Background), (TerminalStyleFlags)cell.Flags),
                    cell.Width == 0));
            }
            formats = formats[formatLength..];
        }
        GC.KeepAlive(handle);
        return (view.Id, view.Revision);
    }

    internal bool TrySnapshot(out TerminalSnapshot? resultSnapshot)
    {
        resultSnapshot = null;
        ObjectDisposedException.ThrowIf(disposed, this);
        Check(Native.SnapshotCopyRequired(handle, out ulong copyRequired), "snapshot ownership estimate");
        ResourceReservation copies;
        try { copies = quota.Reserve(checked((long)copyRequired)); }
        catch (ResourceLimitException) { return false; }
        using var unownedCopies = copies;
        if (!TryPrepare(3, 0, [], 0)) return false;
        long nativeStarted = Stopwatch.GetTimestamp();
        SnapshotView snapshot;
        try { Check(Native.Snapshot(handle, out snapshot), "snapshot"); }
        finally { ReconcileMemoryCharge(); }
        Interlocked.Add(ref NativeTerminalCoreDiagnostics.NativeSnapshotElapsedTicks, Stopwatch.GetTimestamp() - nativeStarted);
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
                    cached = MaterializeRow(row, copies);
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
                (snapshot.Flags & 2) != 0, DecodeMouseMode(snapshot.MouseTracking, snapshot.MouseGeneration), admittedCopies: copies);
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

    private static TerminalSnapshot.Row MaterializeRow(RowView row, ResourceReservation admittedCopies)
    {
        int textLength = checked((int)row.TextLength);
        ResourceReservation textMemory = admittedCopies.Separate(textLength == 0 ? 0 : 24L + textLength * 2L);
        try
        {
            string displayText = new(row.Text, 0, textLength);
            CompactTerminalCells? cells = row.CellCount == 0 ? null : new(
                new ReadOnlySpan<CellView>(row.Cells, checked((int)row.CellCount)),
                new ReadOnlySpan<char>(row.CellText, checked((int)row.CellTextLength)), admittedCopies);
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
        if (status == 4) throw new ResourceLimitException("Terminal reply queue exceeds its size limit.");
        Check(status, "operation preflight");
        long prior = memory.Size;
        try { memory.Resize(Math.Max(prior, checked((long)required))); }
        catch (ResourceLimitException) { return false; }
        try { Check(Native.SetLimit(handle, checked((ulong)memory.Size)), "memory admission"); }
        catch { memory.Resize(prior); throw; }
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
    private void PublishEffects(EffectsView effects, Action<byte[]>? textSink, Action<byte[]>? terminalSink,
        Action<byte[]>? replySink, Action? bell)
    {
        BracketedPaste = (effects.ModeFlags & 16) != 0;
        ApplicationCursorKeys = (effects.ModeFlags & 32) != 0;
        ApplicationKeypad = (effects.ModeFlags & 64) != 0;
        MouseMode = DecodeMouseMode(effects.MouseTracking, effects.MouseGeneration);
        IsAlternateScreen = (effects.ModeFlags & 128) != 0;
        LayoutEpoch = effects.LayoutEpoch;
        List<byte[]>? text;
        List<byte[]>? terminal;
        List<byte[]>? replies = null;
        try
        {
            text = textSink is null ? null : CopyChunks(new ReadOnlySpan<byte>(effects.Text, checked((int)effects.TextLength)));
            terminal = terminalSink is null ? null : CopyChunks(new ReadOnlySpan<byte>(effects.Terminal, checked((int)effects.TerminalLength)));
            if (replySink is not null && effects.ReplyLength != 0)
            {
                replies = new List<byte[]>();
                var bytes = new ReadOnlySpan<byte>(effects.Replies, checked((int)effects.ReplyLength));
                while (!bytes.IsEmpty)
                {
                    int length = bytes[0];
                    if (length == 0 || length >= bytes.Length)
                        throw new InvalidDataException("The native terminal returned an invalid response.");
                    replies.Add(bytes.Slice(1, length).ToArray());
                    bytes = bytes[(length + 1)..];
                }
            }
        }
        finally { GC.KeepAlive(handle); }
        if (text is not null) foreach (byte[] chunk in text) textSink!(chunk);
        if (terminal is not null) foreach (byte[] chunk in terminal) terminalSink!(chunk);
        if (replies is not null) foreach (byte[] reply in replies) replySink!(reply);
        if (bell is not null)
            for (uint bells = effects.ModeFlags >> 8; bells != 0; bells--) bell();
    }

    private static List<byte[]>? CopyChunks(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        var owned = new List<byte[]>((bytes.Length + 4092) / 4093);
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(bytes.Length, 4096);
            if (count < bytes.Length)
                while ((bytes[count] & 0xC0) == 0x80) count--;
            // Never split a UTF-8 scalar, including malformed-input replacement text.
            owned.Add(bytes[..count].ToArray());
            bytes = bytes[count..];
        }
        return owned;
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
    internal struct DecodedLegacy
    {
        public uint Length, Partial;
        public fixed uint Scalars[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EffectsView
    {
        public byte* Text;
        public uint TextLength, ModeFlags;
        public byte* Terminal;
        public uint TerminalLength, ReplyLength;
        public byte* Replies;
        public uint MouseTracking;
        public int MouseGeneration;
        public long LayoutEpoch;
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
        public uint MouseTracking;
        public int MouseGeneration;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenStateView
    {
        public long LayoutEpoch;
        public int Revision, Columns, Rows, CursorRow, CursorColumn;
        public uint Flags, CursorShape, MouseTracking;
        public int MouseGeneration;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenRowView
    {
        public long Id, Revision;
        public CellView* Cells;
        public char* Arena;
        public FormatView* Formats;
        public uint CellCount, FormatCount, First, End;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Button, Column, Row, Generation;
        public uint Flags;
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
        public FormatView* Formats;
        public uint FormatCount;
        public TabView* Recorded;
        public uint RecordedCount, Excluded;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FormatView
    {
        public uint Column, Scalar;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceView
    {
        public byte* Pending;
        public byte* Parameters;
        public uint* LastGraphic;
        public uint PendingCount, ParameterCount, LastGraphicCount, Flags, Escape, Reserved;
        public uint PendingInterval, EscapeInterval, PreviousInterval;
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
        public EditingView* Editing;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EditingView
    {
        public uint MarginTop, MarginBottom;
        public int PositionRow;
        public uint PositionColumn, SavedRow, SavedColumn, SavedForeground, SavedBackground, SavedStyle, SavedFlags;
        public fixed ulong TabStops[16];
        public PageView Parked, Alternate;
        public CursorView NormalReturn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PageView
    {
        public uint First, Count, Row, Column, Columns, Rows, Wrap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorView
    {
        public uint Row, Column, Foreground, Background, Style, Flags, Active;
    }

    internal static class Native
    {
        private const string Library = "tabtty_terminal";
        [DllImport(Library, EntryPoint = "tc_encode_key", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeKey(uint key, uint modifiers, uint modes, byte* output, out uint written);
        [DllImport(Library, EntryPoint = "tc_encode_mouse", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeMouse(MouseInput* input, uint tracking, int generation, int columns, int rows,
            byte* output, out uint written);
        [DllImport(Library, EntryPoint = "tc_preflight", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Preflight(CoreHandle handle, uint operation, uint source, byte* data, uint length, uint flags, out ulong requiredPeak);
        [DllImport(Library, EntryPoint = "tc_create_required", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateRequired(int columns, int rows, int retained, int ambiguous, uint page, out ulong required);
        [DllImport(Library, EntryPoint = "tc_set_limit", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetLimit(CoreHandle handle, ulong allowance);
        [DllImport(Library, EntryPoint = "tc_export", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Export(CoreHandle handle, out ExportState state);
        [DllImport(Library, EntryPoint = "tc_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint AbiVersion();
        [DllImport(Library, EntryPoint = "tc_encode_utf8", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeUtf8(char* input, uint length, byte* output, uint capacity, out uint required);
        [DllImport(Library, EntryPoint = "tc_encode_legacy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeLegacy(uint page, char* input, uint length, uint flags,
            byte* output, uint capacity, out uint required);
        [DllImport(Library, EntryPoint = "tc_legacy_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LegacyOpen(uint page, uint shiftKana, out nint receiver);
        [DllImport(Library, EntryPoint = "tc_legacy_step", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LegacyStep(LegacyDecoder receiver, int value, uint inVt, uint interval, out DecodedLegacy decoded);
        [DllImport(Library, EntryPoint = "tc_legacy_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void LegacyDestroy(nint receiver);
        [DllImport(Library, EntryPoint = "tc_owned_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong OwnedBytes(CoreHandle handle);
        [DllImport(Library, EntryPoint = "tc_quota_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong QuotaBytes(CoreHandle handle);
        [DllImport(Library, EntryPoint = "tc_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(int columns, int rows, int retained, int ambiguous, uint page, uint shiftKana,
            ulong allowance, out nint handle);
        [DllImport(Library, EntryPoint = "tc_feed", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Feed(CoreHandle handle, uint source, byte* data, uint length, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_complete", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Complete(CoreHandle handle, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_flush_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FlushText(CoreHandle handle, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_begin_recording", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BeginRecording(CoreHandle handle, uint flags, uint interval);
        [DllImport(Library, EntryPoint = "tc_cutoff_recording", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CutoffRecording(CoreHandle handle, uint flags, uint interval, uint abort, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_resize_prepare", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResizePrepare(CoreHandle handle, int columns, int rows, out nint plan);
        [DllImport(Library, EntryPoint = "tc_resize_apply", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResizeApply(CoreHandle handle, PreparedResize plan, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_resize_drop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ResizeDrop(nint plan);
        [DllImport(Library, EntryPoint = "tc_resize_required", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResizeRequired(CoreHandle handle, int columns, int rows, out ulong required);
        [DllImport(Library, EntryPoint = "tc_snapshot", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Snapshot(CoreHandle handle, out SnapshotView snapshot);
        [DllImport(Library, EntryPoint = "tc_screen_state", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ScreenState(CoreHandle handle, out ScreenStateView state);
        [DllImport(Library, EntryPoint = "tc_screen_row", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ScreenRow(CoreHandle handle, uint row, uint first, uint end, out ScreenRowView view);
        [DllImport(Library, EntryPoint = "tc_snapshot_copy_required", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SnapshotCopyRequired(CoreHandle handle, out ulong required);
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
