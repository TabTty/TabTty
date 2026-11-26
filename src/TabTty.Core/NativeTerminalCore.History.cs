using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using static TabTty.Core.TerminalHistory;

namespace TabTty.Core;

internal sealed unsafe partial class NativeTerminalCore
{
    private static readonly ulong firstHistoryBase = NewHistoryBase();
    private static readonly ulong secondHistoryBase = NewHistoryBase();

    private static ulong NewHistoryBase() =>
        BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(sizeof(ulong))) | 0x100000001UL;

    internal int PrepareHistory(bool allowBuild = true)
    {
        Check(HistoryNative.Required(handle, out ulong required), "history allowance");
        try { memory.Resize(Math.Max(memory.Size, checked((long)required))); }
        catch (ResourceLimitException) { throw new HistoryException("resource_limit"); }
        try
        {
            Check(Native.SetLimit(handle, checked((ulong)memory.Size)), "history admission");
            CheckHistory(HistoryNative.Prepare(handle, firstHistoryBase, secondHistoryBase, allowBuild ? 1U : 0U, out int length));
            return length;
        }
        finally { ReconcileMemoryCharge(); }
    }

    internal void VerifyHistoryRanges(Span[] ranges, CancellationToken token)
    {
        if (ranges.Length == 0) return;
        long started = Stopwatch.GetTimestamp();
        PrepareHistory(allowBuild: false);
        foreach (var range in ranges)
        {
            token.ThrowIfCancellationRequested();
            if (RemainingSearchMilliseconds(started) == 0) throw new HistoryException("busy");
            var requested = new RangeView(range);
            CheckHistory(HistoryNative.Verify(handle, &requested));
        }
        token.ThrowIfCancellationRequested();
    }

    internal HistoryPage ReadHistoryPage(AiTerminalState state, Span? requested, Point? position,
        int rows, int bytes, int outputBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 1000);
        PrepareHistory();
        RangeView range = requested is null ? default : new(requested);
        Point start = position.GetValueOrDefault();
        HistoryFragmentView* fragments = stackalloc HistoryFragmentView[rows];
        CheckHistory(HistoryNative.Read(handle, requested is null ? null : &range, position.HasValue ? &start : null,
            (uint)rows, checked((uint)bytes), checked((uint)outputBudget), fragments, out var page));
        var owned = new Fragment[page.Count];
        for (int index = 0; index < owned.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = fragments[index];
            owned[index] = new(item.Position.Line, item.ClusterOffset, item.Position.Offset, new string(item.Text, 0, item.Length),
                item.Flags.HasFlag(FragmentFlags.LineEnd), item.Flags.HasFlag(FragmentFlags.ContinuesBefore),
                item.Flags.HasFlag(FragmentFlags.ContinuesAfter), item.Flags.HasFlag(FragmentFlags.PrefixMissing),
                item.Flags.HasFlag(FragmentFlags.SuffixMissing));
        }
        GC.KeepAlive(handle);
        return new(state, owned, page.Flags.HasFlag(PageFlags.HasRange) ? requested ?? page.Range.AsSpan(rows, bytes) : null,
            page.Flags.HasFlag(PageFlags.HasEarlier) ? page.Earlier.AsSpan(rows, bytes) : null,
            page.Flags.HasFlag(PageFlags.HasNext) ? page.Next : null);
    }

    internal HistorySearch CaptureHistorySearch(SearchCursor? cursor, string query, int context, long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PrepareHistory();
        SearchCheckpoint checkpoint = cursor is null ? default : new(cursor.Position, cursor.End, cursor.RemainingProof);
        CheckHistory(HistoryNative.Capture(handle, cursor is null ? null : &checkpoint, (uint)query.Length, checked((uint)context),
            RemainingSearchMilliseconds(started), out nint captured));
        return new HistorySearch(captured);
    }

    internal SearchPage CompleteHistorySearch(AiTerminalState state, HistorySearch window, string query,
        bool caseSensitive, int limit, int context, long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        PrepareHistory(allowBuild: false);
        HistoryMatchView* matches = stackalloc HistoryMatchView[limit];
        CheckHistory(HistoryNative.Complete(handle, window, checked((uint)context), (uint)limit,
            RemainingSearchMilliseconds(started), matches, out var page));
        var owned = new SearchMatch[page.Count];
        const int rangeRows = 200, rangeBytes = 64 * 1024;
        for (int index = 0; index < owned.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var match = matches[index];
            owned[index] = new(new string(match.Text, 0, match.Length), match.Position, match.Context.AsSpan(rangeRows, rangeBytes));
        }
        GC.KeepAlive(window);
        SearchCursor Cursor(SearchCheckpoint value) => new(query, caseSensitive, limit, context, value.Position, value.End, value.Remaining);
        return new(state, owned, page.Flags.HasFlag(SearchFlags.HasScanned) ? page.Scanned.AsSpan(rangeRows, rangeBytes) : null,
            page.Flags.HasFlag(SearchFlags.HasCoverage) ? page.Covered.AsSpan(rangeRows, rangeBytes) : null, Cursor(page.Start),
            page.Flags.HasFlag(SearchFlags.HasNext) ? Cursor(page.Next) : null);
    }

    // Keep these bit assignments in sync with history.rs.
    [Flags]
    private enum FragmentFlags : uint
    {
        LineEnd = 1 << 0, ContinuesBefore = 1 << 1, ContinuesAfter = 1 << 2, PrefixMissing = 1 << 3, SuffixMissing = 1 << 4,
    }
    [Flags]
    private enum PageFlags : uint { HasRange = 1 << 0, HasEarlier = 1 << 1, HasNext = 1 << 2 }
    [Flags]
    private enum SearchFlags : uint { HasCoverage = 1 << 0, HasScanned = 1 << 1, HasNext = 1 << 2 }

    private static void CheckHistory(int status)
    {
        if (status is >= 5 and <= 8)
            throw new HistoryException(status switch { 5 => "resource_limit", 6 => "busy", 7 => "range_evicted", _ => "range_changed" });
        Check(status, "retained history");
    }

    internal sealed class HistorySearch : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal HistorySearch(nint captured) : base(true) => SetHandle(captured);

        internal void Scan(string query, bool caseSensitive, int limit, long started, int outputBudget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            fixed (char* text = query)
                CheckHistory(HistoryNative.Scan(this, text, (uint)query.Length, caseSensitive ? 1U : 0U, checked((uint)limit),
                    checked((uint)outputBudget), RemainingSearchMilliseconds(started), &OrdinalIgnoreCaseEquals));
            token.ThrowIfCancellationRequested();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OrdinalIgnoreCaseEquals(char* text, char* query, uint length) =>
            new ReadOnlySpan<char>(text, (int)length).Equals(new ReadOnlySpan<char>(query, (int)length), StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        protected override bool ReleaseHandle() { HistoryNative.Drop(handle); return true; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RangeView(Point Start, Point End, HistoryProof Proof, uint EndBoundary)
    {
        internal RangeView(Span span) : this(span.Start, span.End, span.Proof, span.EndBoundary ? 1U : 0) { }
        internal Span AsSpan(int rows, int bytes) => new(Start, End, Proof, EndBoundary != 0, rows, bytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HistoryFragmentView
    {
        internal Point Position;
        internal long ClusterOffset;
        internal char* Text;
        internal int Length;
        internal FragmentFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HistoryPageView
    {
        internal RangeView Range, Earlier;
        internal Point Next;
        internal int Count;
        internal PageFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SearchCheckpoint(Point Position, Point End, HistoryProof Remaining);

    [StructLayout(LayoutKind.Sequential)]
    private struct HistoryMatchView
    {
        internal Point Position;
        internal RangeView Context;
        internal char* Text;
        internal int Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HistorySearchPageView
    {
        internal SearchCheckpoint Start, Next;
        internal RangeView Covered, Scanned;
        internal int Count;
        internal SearchFlags Flags;
    }

    private static class HistoryNative
    {
        [DllImport(Library, EntryPoint = "tc_history_required", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Required(CoreHandle handle, out ulong required);
        [DllImport(Library, EntryPoint = "tc_history_prepare", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Prepare(CoreHandle handle, ulong first, ulong second, uint allowBuild, out int length);
        [DllImport(Library, EntryPoint = "tc_history_verify", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Verify(CoreHandle handle, RangeView* range);
        [DllImport(Library, EntryPoint = "tc_history_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Read(CoreHandle handle, RangeView* requested, Point* position,
            uint rows, uint bytes, uint outputBudget, HistoryFragmentView* fragments, out HistoryPageView output);
        [DllImport(Library, EntryPoint = "tc_history_search_capture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Capture(CoreHandle handle, SearchCheckpoint* checkpoint, uint queryLength,
            uint context, uint milliseconds, out nint output);
        [DllImport(Library, EntryPoint = "tc_history_search_scan", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Scan(HistorySearch window, char* query, uint queryLength, uint caseSensitive, uint limit,
            uint outputBudget, uint milliseconds, delegate* unmanaged[Cdecl]<char*, char*, uint, int> unicodeEquals);
        [DllImport(Library, EntryPoint = "tc_history_search_complete", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Complete(CoreHandle handle, HistorySearch window, uint context, uint limit,
            uint milliseconds, HistoryMatchView* matches, out HistorySearchPageView output);
        [DllImport(Library, EntryPoint = "tc_history_search_drop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Drop(nint window);
    }
}
