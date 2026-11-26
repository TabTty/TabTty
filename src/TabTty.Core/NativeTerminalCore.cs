using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal sealed class NativeTerminalExecutionException(Exception cause)
    : IOException("The admitted terminal operation failed during execution.", cause);

internal static class NativeTerminalCoreDiagnostics
{
    public static long FedBytes, EchoBytes, FeedBatches, SnapshotExports, UnsupportedErrors, MacroObservations;
    public static long CreatedModels, ClosedModels;
    public static long NativePreflightElapsedTicks;
    public static long CompactSnapshotRows, MaterializedCellRows, MaterializedCells;
    public static long NativeFeedElapsedTicks, NativeSnapshotElapsedTicks, SnapshotMaterializationElapsedTicks, SnapshotMaterializationCount;
    public static long LiveBytes => checked((long)NativeTerminalCore.Native.LiveBytes());
    public static long PeakBytes => checked((long)NativeTerminalCore.Native.PeakBytes());
    public static long LiveModels => checked((long)NativeTerminalCore.Native.LiveModels());
}

internal sealed unsafe partial class NativeTerminalCore : IDisposable
{
    internal Action<TerminalReceiveEvent>? ReceiveObserver { get; set; }
    internal Action? RecordingRefused { get; set; }
    private readonly CoreHandle handle;
    private readonly ResourceQuota quota;
    private readonly ResourceReservation memory;
    private readonly Dictionary<long, TerminalSnapshot.Row> rowCache = new();
    private readonly HashSet<long> visibleIds = new();
    private readonly List<long> expiredIds = new();
    private bool disposed, completed;
    // One source preview, its fixed parameter buffer and bounded parser stack.
    private const int PreviewWorkspaceBytes = 16 * 1024;
    private volatile bool bracketedPaste;
    internal bool BracketedPaste => bracketedPaste;
    internal bool ApplicationCursorKeys { get; private set; }
    internal bool ApplicationKeypad { get; private set; }
    internal TerminalMouseMode MouseMode { get; private set; }
    internal long LayoutEpoch { get; private set; }

    internal static (int Length, int Graphemes, int LineBreaks) AnalyzePastePage(ReadOnlySpan<char> text, ref uint context)
    {
        uint* output = stackalloc uint[4];
        fixed (char* input = text)
        {
            int result = Native.AnalyzePastePage(input, (uint)text.Length, context, output);
            if (result == 1)
                throw new ArgumentException("Paste contains invalid Unicode or a disallowed control character.", nameof(text));
            Check(result, "paste analysis");
        }
        if (output[0] == 0 || output[0] > text.Length)
            throw new InvalidDataException("The native paste analysis returned invalid page bounds.");
        context = output[3];
        return (checked((int)output[0]), checked((int)output[1]), checked((int)output[2]));
    }

    internal static (int Length, int Completed, int Delay) NextPasteChunk(ReadOnlySpan<char> text,
        int characterDelay, int lineDelay, ref uint context)
    {
        uint* output = stackalloc uint[4];
        fixed (char* input = text)
        {
            Check(Native.PasteChunk(input, (uint)text.Length, context, checked((uint)characterDelay),
                checked((uint)lineDelay), output), "paste chunk");
        }
        if (output[0] == 0 || output[0] > text.Length)
            throw new InvalidDataException("The native input plan returned invalid chunk bounds.");
        context = output[3];
        return (checked((int)output[0]), checked((int)output[1]), checked((int)output[2]));
    }

    internal static bool HasDrawingMark(Rune scalar) => Native.DrawingMark((uint)scalar.Value) != 0;

    internal static int NextGraphemeLength(ReadOnlySpan<char> text)
    {
        fixed (char* input = text)
        {
            int result = Native.NextGrapheme(input, (uint)text.Length, out uint length);
            if (result == 1) throw new ArgumentException("Invalid Unicode text.", nameof(text));
            Check(result, "grapheme boundary");
            return checked((int)length);
        }
    }

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

    internal static int TransmitBoundaries(uint page, ReadOnlySpan<char> text, ReadOnlySpan<byte> encoded,
        bool shiftKana, bool paste, int confirmed, Span<int> bytes, Span<int> source, out int sourcePrefix)
    {
        if (text.IsEmpty) { sourcePrefix = 0; return 0; }
        fixed (char* input = text)
        fixed (byte* prepared = encoded)
        fixed (int* byteEnds = bytes, sourceEnds = source)
        {
            uint flags = (shiftKana ? 8U : 0U) | (paste ? 16U : 0U);
            Check(Native.TransmitBoundaries(page, input, (uint)text.Length, flags, prepared, (uint)encoded.Length,
                (uint)confirmed, byteEnds, sourceEnds, (uint)bytes.Length, out uint count, out uint prefix), "transmit boundaries");
            sourcePrefix = checked((int)prefix);
            return checked((int)count);
        }
    }

    internal static int TransmitCutoff(uint page, ReadOnlySpan<char> text, ReadOnlySpan<byte> encoded, int confirmed, bool shiftKana)
    {
        if (confirmed == 0 || confirmed == encoded.Length) return confirmed;
        fixed (char* input = text)
        fixed (byte* bytes = encoded)
        {
            Check(Native.TransmitCutoff(page, input, (uint)text.Length, shiftKana ? 24U : 16U,
                bytes, (uint)encoded.Length, (uint)confirmed, out uint cutoff), "transmit cutoff");
            return checked((int)cutoff);
        }
    }

    internal const int MaxTransmitClosureBytes = 16;

    internal static byte[] TransmitClosure(uint page, ReadOnlySpan<char> text, ReadOnlySpan<byte> submitted, bool shiftKana)
    {
        Span<byte> suffix = stackalloc byte[MaxTransmitClosureBytes];
        fixed (char* input = text)
        fixed (byte* prefix = submitted, output = suffix)
        {
            Check(Native.TransmitClosure(page, input, (uint)text.Length, shiftKana ? 8U : 0U,
                prefix, (uint)submitted.Length, output, (uint)suffix.Length, out uint count), "transmit closure");
            return suffix[..checked((int)count)].ToArray();
        }
    }

    internal static TerminalMouseMode DecodeMouseMode(uint tracking, int generation) =>
        new((int)(tracking & 0x7FFF_FFFF), (tracking & 0x8000_0000) != 0, generation);

    internal static TerminalMouseReport EncodeMouse(TerminalMouseInput input, TerminalMouseMode mode, int columns, int rows,
        TerminalMouseInput? previous)
    {
        byte* output = stackalloc byte[32];
        int* result = stackalloc int[3];
        var request = new MouseInput
        {
            Button = input.Button, Column = input.Column, Row = input.Row, Generation = input.Generation,
            Flags = (uint)input.Modifiers | (input.Release ? 8U : 0U) | (input.Motion ? 16U : 0U),
            FallbackColumn = previous?.Column ?? -1, FallbackRow = previous?.Row ?? -1,
        };
        uint tracking = (uint)mode.Tracking | (mode.SgrCoordinates ? 0x8000_0000U : 0U);
        Check(Native.EncodeMouse(&request, tracking, mode.Generation, columns, rows, output, result), "mouse encoding");
        return new(new ReadOnlySpan<byte>(output, result[0]).ToArray(), input with { Column = result[1], Row = result[2] });
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
        if (sizeof(EffectsView) != 72 || sizeof(CellView) != 32 || sizeof(RowView) != 88 ||
            sizeof(SnapshotView) != 48 || sizeof(TerminalTextPosition) != 12 || sizeof(RangeView) != 88 ||
            sizeof(MouseInput) != 28 || sizeof(ScreenStateView) != 48 || sizeof(ScreenRowView) != 56 ||
            Native.AbiVersion() != 15)
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

    internal readonly record struct ReceiveProjection(NativeTerminalCore? Core, uint Flags, Action<byte[]>? TextSink)
    {
        internal nint Handle => Core?.handle.DangerousGetHandle() ?? 0;
    }

    internal void ConfigureReceiveOnly() => Check(Native.ReceiveOnly(handle), "configure receive-only recording");

    internal bool TryFeed(uint source, ReadOnlySpan<byte> payload, uint flags,
        Action<byte[]>? textSink, Action<byte[]>? terminalSink, CancellationToken token = default,
        Action<byte[]>? replySink = null, int pendingReplies = 0, Action? bell = null, ResourceReservation? prepaid = null,
        ReceiveProjection projection = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed && source != 2) throw new InvalidOperationException("Terminal reception has ended.");
        if (payload.IsEmpty) return true;
        if (!TryRunEffects(0, source, payload, flags | (checked((uint)pendingReplies) << 8),
            textSink, terminalSink, token, replySink, bell, prepaid, projection)) return false;
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.FeedBatches);
        if (source == 2) Interlocked.Add(ref NativeTerminalCoreDiagnostics.EchoBytes, payload.Length);
        else Interlocked.Add(ref NativeTerminalCoreDiagnostics.FedBytes, payload.Length);
        return true;
    }

    internal ResourceReservation ReserveEcho(ReadOnlySpan<byte> payload, uint flags, int pendingReplies)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var reservation = quota.Reserve(PreviewWorkspaceBytes + payload.Length);
        try
        {
            if (ReceiveObserver is not null) flags |= 4;
            if (!TryPrepare(0, 2, payload, flags | (checked((uint)pendingReplies) << 8)))
                throw new ResourceLimitException();
            long used = checked((long)Native.QuotaBytes(handle));
            using var growth = memory.Separate(checked(memory.Size - used));
            Check(Native.SetLimit(handle, checked((ulong)used)), "echo preflight release");
            reservation.Absorb(growth);
            return reservation;
        }
        catch { reservation.Dispose(); throw; }
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

    internal bool TryComplete(uint flags, Action<byte[]>? textSink, Action<byte[]>? terminalSink,
        ReceiveProjection projection = default, bool flushText = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return true;
        if (!TryRunEffects(1, 0, [], flags, textSink, terminalSink, projection: projection, flushText: flushText)) return false;
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
        Action<byte[]>? replySink = null, Action? bell = null, ResourceReservation? prepaid = null,
        ReceiveProjection projection = default, bool flushText = false)
    {
        if (operation <= 1 && ReceiveObserver is not null) flags |= 4;
        if (payload.Length > 1024 * 1024) return false;
        ResourceReservation workspace;
        int workspaceBytes = PreviewWorkspaceBytes + payload.Length;
        try
        {
            workspace = prepaid is not null && prepaid.Size >= workspaceBytes
                ? prepaid.Separate(workspaceBytes) : quota.Reserve(workspaceBytes);
        }
        catch (ResourceLimitException) { return false; }
        using (workspace)
        {
            Span<byte> input = payload.Length <= 1024 ? stackalloc byte[payload.Length] : new byte[payload.Length];
            payload.CopyTo(input);
            if (prepaid is { Size: > 0 }) memory.Absorb(prepaid);
            try
            {
                try
                {
                    if (!TryPrepare(operation, source, input, flags) ||
                        projection.Core is { } receive && !receive.TryPrepare(operation, source, input, projection.Flags)) return false;
                    token.ThrowIfCancellationRequested();
                    EffectsView effects, textEffects = default;
                    if (operation == 0)
                    {
                        long started = Stopwatch.GetTimestamp();
                        fixed (byte* bytes = input)
                            Check(Native.Feed(handle, projection.Handle, projection.Flags, source, bytes,
                                checked((uint)input.Length), flags, out effects, out textEffects), "receive");
                        Interlocked.Add(ref NativeTerminalCoreDiagnostics.NativeFeedElapsedTicks, Stopwatch.GetTimestamp() - started);
                    }
                    else if (operation == 1)
                        Check(Native.Complete(handle, projection.Handle, projection.Flags, flags,
                            flushText ? 1U : 0, out effects, out textEffects), "complete receive");
                    else Check(Native.FlushText(handle, flags, out effects), "flush Text recording");
                    PublishEffects(effects, textSink, terminalSink, replySink, bell);
                    projection.Core?.PublishEffects(textEffects, projection.TextSink, null, null, null);
                    return true;
                }
                finally
                {
                    ReconcileMemoryCharge();
                    projection.Core?.ReconcileMemoryCharge();
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or OverflowException)
            {
                throw new NativeTerminalExecutionException(exception);
            }
        }
    }

    private void ReserveResize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Check(Native.ResizeRequired(handle, columns, rows, out ulong required), "resize estimate");
        memory.Resize(Math.Max(memory.Size, checked((long)required)));
        Check(Native.SetLimit(handle, checked((ulong)memory.Size)), "resize admission");
    }

    internal PreparedResize PrepareResize(int columns, int rows, ReceiveProjection projection = default)
    {
        try
        {
            ReserveResize(columns, rows);
            projection.Core?.ReserveResize(columns, rows);
            int status = Native.ResizePrepare(handle, projection.Handle, columns, rows, out nint plan);
            if (status == 5) throw new ResourceLimitException();
            Check(status, "resize preparation");
            return new PreparedResize(this, projection, plan);
        }
        catch
        {
            ReconcileMemoryCharge();
            projection.Core?.ReconcileMemoryCharge();
            throw;
        }
    }

    internal sealed class PreparedResize : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly NativeTerminalCore owner;
        private readonly ReceiveProjection projection;

        internal PreparedResize(NativeTerminalCore owner, ReceiveProjection projection, nint plan) : base(true)
        {
            this.owner = owner;
            this.projection = projection;
            SetHandle(plan);
        }

        internal void Apply(Action<byte[]>? textSink)
        {
            Check(Native.ResizeApply(owner.handle, projection.Handle, this, out var effects, out var textEffects), "resize");
            owner.PublishEffects(effects, textSink, null, null, null);
            projection.Core?.PublishEffects(textEffects, projection.TextSink, null, null, null);
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
            if (disposing && ownsPlan && projection.Core is { disposed: false } receive) receive.ReconcileMemoryCharge();
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
        var nativeCells = new ReadOnlySpan<CellView>(row.Cells, checked((int)row.CellCount));
        var nativePositions = new ReadOnlySpan<TerminalTextPosition>(row.Positions, checked((int)row.PositionCount));
        long textBytes = textLength == 0 ? 0 : 24L + textLength * 2L;
        long positionBytes = nativePositions.IsEmpty ? 0 : 24L + nativePositions.Length * sizeof(TerminalTextPosition);
        ResourceReservation textMemory = admittedCopies.Separate(textBytes + positionBytes);
        try
        {
            string displayText = new(row.Text, 0, textLength);
            TerminalTextPosition[]? positions = nativePositions.IsEmpty ? null : nativePositions.ToArray();
            CompactTerminalCells? cells = null;
            if (!nativeCells.IsEmpty)
            {
                var cellText = new ReadOnlySpan<char>(row.CellText, checked((int)row.CellTextLength));
                if (positions is null)
                    cells = CompactTerminalCells.TryCreatePlainAscii(nativeCells, cellText, displayText, admittedCopies);
                cells ??= new CompactTerminalCells(nativeCells, cellText, textLength, positions, admittedCopies);
            }
            return new TerminalSnapshot.Row(row.Id, row.LogicalId, row.LogicalOffset, displayText,
                row.SoftWrap != 0, default, textMemory, null, row.DisplayRevision, cells, positions);
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
        bracketedPaste = (effects.ModeFlags & 16) != 0;
        ApplicationCursorKeys = (effects.ModeFlags & 32) != 0;
        ApplicationKeypad = (effects.ModeFlags & 64) != 0;
        MouseMode = DecodeMouseMode(effects.MouseTracking, effects.MouseGeneration);
        LayoutEpoch = effects.LayoutEpoch;
        List<byte[]>? text;
        List<byte[]>? terminal;
        List<byte[]>? replies = null;
        ReceiveObservation[] observations;
        try
        {
            text = textSink is null || effects.RecordingRefused != 0 ? null :
                CopyChunks(new ReadOnlySpan<byte>(effects.Text, checked((int)effects.TextLength)));
            terminal = terminalSink is null || effects.RecordingRefused != 0 ? null :
                CopyChunks(new ReadOnlySpan<byte>(effects.Terminal, checked((int)effects.TerminalLength)));
            observations = effects.ObservationCount == 0 ? [] :
                new ReadOnlySpan<ReceiveObservation>(effects.Observations, checked((int)effects.ObservationCount)).ToArray();
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
        if (effects.RecordingRefused != 0) RecordingRefused?.Invoke();
        if (text is not null) foreach (byte[] chunk in text) textSink!(chunk);
        if (terminal is not null) foreach (byte[] chunk in terminal) terminalSink!(chunk);
        if (replies is not null) foreach (byte[] reply in replies) replySink!(reply);
        if (bell is not null)
            for (uint bells = effects.ModeFlags >> 8; bells != 0; bells--) bell();
        if (observations.Length != 0) Interlocked.Add(ref NativeTerminalCoreDiagnostics.MacroObservations, observations.Length);
        foreach (var observation in observations)
        {
            ReceiveObserver?.Invoke((observation.Flags & 4) != 0 ? new(TerminalReceiveEventKind.AlternateScreen) : new(
                TerminalReceiveEventKind.Character,
                (observation.Flags & 1) == 0 ? TerminalReceiveSource.Data : TerminalReceiveSource.StandardError,
                new Rune(checked((int)observation.Scalar)), (observation.Flags & 2) != 0));
        }
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
        public ReceiveObservation* Observations;
        public uint ObservationCount, RecordingRefused;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReceiveObservation
    {
        public uint Scalar, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CellView
    {
        public long Revision;
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
        public TerminalTextPosition* Positions;
        public uint PositionCount, CellCount, TextLength, CellTextLength, SoftWrap;
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
        public int FallbackColumn, FallbackRow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FormatView
    {
        public long Revision;
        public uint Column, Scalar;
    }

    private const string Library = "tabtty_terminal";

    internal static class Native
    {
        [DllImport(Library, EntryPoint = "tc_analyze_paste_page", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AnalyzePastePage(char* input, uint count, uint context, uint* output);
        [DllImport(Library, EntryPoint = "tc_paste_chunk", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PasteChunk(char* text, uint length, uint context,
            uint characterDelay, uint lineDelay, uint* output);
        [DllImport(Library, EntryPoint = "tc_drawing_mark", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint DrawingMark(uint scalar);
        [DllImport(Library, EntryPoint = "tc_next_grapheme", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NextGrapheme(char* input, uint count, out uint consumed);
        [DllImport(Library, EntryPoint = "tc_encode_key", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeKey(uint key, uint modifiers, uint modes, byte* output, out uint written);
        [DllImport(Library, EntryPoint = "tc_encode_mouse", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeMouse(MouseInput* input, uint tracking, int generation, int columns, int rows,
            byte* output, int* result);
        [DllImport(Library, EntryPoint = "tc_preflight", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Preflight(CoreHandle handle, uint operation, uint source, byte* data, uint length, uint flags, out ulong requiredPeak);
        [DllImport(Library, EntryPoint = "tc_create_required", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateRequired(int columns, int rows, int retained, int ambiguous, uint page, out ulong required);
        [DllImport(Library, EntryPoint = "tc_set_limit", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetLimit(CoreHandle handle, ulong allowance);
        [DllImport(Library, EntryPoint = "tc_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint AbiVersion();
        [DllImport(Library, EntryPoint = "tc_encode_utf8", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeUtf8(char* input, uint length, byte* output, uint capacity, out uint required);
        [DllImport(Library, EntryPoint = "tc_encode_legacy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeLegacy(uint page, char* input, uint length, uint flags,
            byte* output, uint capacity, out uint required);
        [DllImport(Library, EntryPoint = "tc_transmit_boundaries", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TransmitBoundaries(uint page, char* input, uint length, uint flags,
            byte* encoded, uint encodedLength, uint confirmed, int* byteEnds, int* sourceEnds, uint capacity,
            out uint written, out uint sourcePrefix);
        [DllImport(Library, EntryPoint = "tc_transmit_cutoff", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TransmitCutoff(uint page, char* input, uint length, uint flags, byte* encoded,
            uint encodedLength, uint confirmed, out uint cutoff);

        [DllImport(Library, EntryPoint = "tc_transmit_closure", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TransmitClosure(uint page, char* input, uint length, uint flags, byte* prefix,
            uint prefixLength, byte* output, uint capacity, out uint written);
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
        internal static extern int Feed(CoreHandle handle, nint receiveOnly, uint receiveFlags, uint source, byte* data,
            uint length, uint flags, out EffectsView effects, out EffectsView textEffects);
        [DllImport(Library, EntryPoint = "tc_complete", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Complete(CoreHandle handle, nint receiveOnly, uint receiveFlags, uint flags,
            uint flushText, out EffectsView effects, out EffectsView textEffects);
        [DllImport(Library, EntryPoint = "tc_receive_only", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ReceiveOnly(CoreHandle handle);
        [DllImport(Library, EntryPoint = "tc_flush_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FlushText(CoreHandle handle, uint flags, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_begin_recording", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BeginRecording(CoreHandle handle, uint flags, uint interval);
        [DllImport(Library, EntryPoint = "tc_cutoff_recording", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CutoffRecording(CoreHandle handle, uint flags, uint interval, uint abort, out EffectsView effects);
        [DllImport(Library, EntryPoint = "tc_resize_prepare", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResizePrepare(CoreHandle handle, nint receiveOnly, int columns, int rows, out nint plan);
        [DllImport(Library, EntryPoint = "tc_resize_apply", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResizeApply(CoreHandle handle, nint receiveOnly, PreparedResize plan,
            out EffectsView effects, out EffectsView textEffects);
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
