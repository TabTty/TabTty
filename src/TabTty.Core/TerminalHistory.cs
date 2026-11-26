using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TabTty.Core;

internal static class TerminalHistory
{
    private const int KiB = 1024;
    internal const int PageOutputBudget = 384 * KiB;
    internal sealed class HistoryException(string code) : Exception(code);
    private const int ControlWorkBytes = 4 * 1024 * 1024;
    private static readonly ResourceQuota TransientWork = new(64 * 1024 * 1024, collectOnPressure: false);
    internal static readonly ResourceQuota WorkQuota = new(64 * 1024 * 1024 - ControlWorkBytes,
        TransientWork, collectOnPressure: false);
    internal static readonly ResourceQuota ControlWorkQuota = new(ControlWorkBytes,
        TransientWork, collectOnPressure: false);
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct HistoryProof(ulong First, ulong Second, ulong FirstPower, ulong SecondPower,
        long Length, long Revision);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Point(long Line, long Offset);

    internal sealed record Span(Point Start, Point End, HistoryProof Proof, bool EndBoundary, int Rows, int Bytes);
    internal sealed record HistoryCursor(Span Range, Point Position);
    internal sealed record Fragment(long logical_line_id, long cluster_offset, long content_offset, string text,
        bool line_end, bool continues_before, bool continues_after, bool retained_prefix_missing, bool retained_suffix_missing);
    internal sealed record HistoryPage(AiTerminalState State, Fragment[] Fragments, Span? Range, Span? Earlier,
        Point? Next)
    {
        internal Span[] Dependencies => new[] { Range, Earlier }.OfType<Span>().ToArray();
    }
    internal sealed record SearchCursor(string Query, bool CaseSensitive, int Limit, int Context, Point Position,
        Point End, HistoryProof RemainingProof);
    internal sealed record SearchMatch(string Text, Point Position, Span Context);
    internal sealed record SearchPage(AiTerminalState State, SearchMatch[] Matches, Span? Scanned,
        Span? Covered, SearchCursor Start, SearchCursor? Next)
    {
        internal Span[] Dependencies => Matches.Select(match => match.Context)
            .Concat(new[] { Covered, Scanned, Next is { } cursor ? new Span(cursor.Position, cursor.End, cursor.RemainingProof, false, 200, 64 * KiB) : null }.OfType<Span>())
            .Distinct().ToArray();
    }
    internal static HistoryPage Read(TerminalOutput model, AiTerminalState state, Span? requested,
        Point? position, int rowLimit, int byteLimit, int outputBudget, CancellationToken token) =>
        model.ReadHistoryPage(state, requested, position, rowLimit, byteLimit, outputBudget, token);

    internal static string? Validate(TerminalOutput model, Span[] ranges, CancellationToken token)
    {
        try
        {
            model.VerifyHistoryRanges(ranges, token);
            return null;
        }
        catch (HistoryException error) { return error.Message; }
    }

    internal static uint RemainingSearchMilliseconds(long started)
    {
        double remaining = 100 - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return remaining <= 0 ? 0 : (uint)Math.Ceiling(remaining);
    }
}
