using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TabTty.Core;

internal sealed class MacroPattern
{
    public string Text { get; }
    public bool IgnoreCase { get; }
    public Regex? Engine { get; }
    public MacroPattern(string text, bool regex, bool ignoreCase)
    {
        if (text.Length > 4096) throw new MacroError(MacroErrorCode.PayloadLimit);
        if (!regex && text.Length == 0) throw new MacroError(MacroErrorCode.InvalidArgument);
        Text = text; IgnoreCase = ignoreCase;
        if (!regex) return;
        try
        {
            Engine = new Regex(text, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException) { throw new MacroError(MacroErrorCode.InvalidPattern); }
    }

    public MacroMatch? Match(string text, int start, string alternative)
    {
        if (Engine is null)
        {
            int index = text.IndexOf(Text, start, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return index < 0 ? null : new(alternative, text.Substring(index, Text.Length), index, Text.Length,
                [new(0, "0", true, text.Substring(index, Text.Length), index, Text.Length, [text.Substring(index, Text.Length)])]);
        }
        Match match;
        try { match = Engine.Match(text, start); }
        catch (RegexMatchTimeoutException) { throw new MacroError(MacroErrorCode.RegexTimeout); }
        if (!match.Success) return null;
        if (match.Length == 0) throw new MacroError(MacroErrorCode.ZeroLengthMatch);
        int[] numbers = Engine.GetGroupNumbers();
        if (numbers.Length > 128) throw new MacroError(MacroErrorCode.CaptureLimit);
        long units = match.Length;
        int captures = 0;
        foreach (int number in numbers)
        {
            Group group = match.Groups[number];
            units += group.Length + Engine.GroupNameFromNumber(number).Length;
            captures += group.Captures.Count;
            if (captures > 4096 || units > 262144) throw new MacroError(MacroErrorCode.CaptureLimit);
            foreach (Capture capture in group.Captures)
            {
                units += capture.Length;
                if (units > 262144) throw new MacroError(MacroErrorCode.CaptureLimit);
            }
        }
        return new(alternative, match.Value, match.Index, match.Length, numbers.Select(number =>
        {
            Group group = match.Groups[number];
            return new MacroCapture(number, Engine.GroupNameFromNumber(number), group.Success, group.Value,
                group.Success ? group.Index : -1, group.Length, group.Captures.Select(capture => capture.Value).ToArray());
        }).ToArray());
    }
}

internal sealed record MacroReceiveLine(long Id, string Text, bool Complete, long LostPrefix, int Floor)
{
    public bool Truncated => LostPrefix != 0;
}
internal sealed record MacroReceiveSnapshot(MacroReceiveLine[] Lines, bool Eof);
internal readonly record struct MacroReceiveCutoff(long Line, int Length, bool Eof);

// All methods are called under the run coordinator lock. This observer never owns terminal data.
internal sealed class MacroReceiveHistory
{
    private readonly LinkedList<MacroReceiveLine> lines = [];
    private readonly StringBuilder current = new();
    private long currentId;
    private int currentFloor;
    private long lostPrefix;
    private long firstRetainedLine;
    private int units;
    private readonly bool[] previousCr = new bool[2];
    public bool Eof { get; set; }
    public long LossRevision { get; private set; }

    public bool Append(TerminalReceiveSource source, Rune character, bool admittedText)
    {
        int channel = source == TerminalReceiveSource.StandardError ? 1 : 0;
        bool adjacentCr = previousCr[channel];
        previousCr[channel] = admittedText && character.Value == '\r';
        if (!admittedText) return false;
        if (character.Value == '\n' && adjacentCr) return false;
        if (character.Value is '\r' or '\n')
        {
            lines.AddLast(new MacroReceiveLine(currentId++, current.ToString(), true, lostPrefix, currentFloor));
            current.Clear(); currentFloor = 0; lostPrefix = 0;
        }
        else if (character.Value == '\t' || !Rune.IsControl(character))
        {
            Span<char> scalar = stackalloc char[2];
            int count = character.EncodeToUtf16(scalar);
            current.Append(scalar[..count]);
            units += count;
        }
        else return false;

        bool lost = false;
        if (current.Length > MacroLimits.LineUnits)
        {
            // Evict a block so a long unterminated stream cannot repeatedly shift the entire retained line.
            int remove = 4096;
            if (char.IsHighSurrogate(current[remove - 1])) remove++;
            current.Remove(0, remove);
            units -= remove;
            lostPrefix += remove;
            currentFloor = 0;
            lost = true;
        }
        // Charge line/string/linked-list metadata as well as text, including consumed prefix context.
        while (lines.Count != 0 && (units > MacroLimits.HistoryUnits || lines.Count + 1 > MacroLimits.HistoryLines ||
            units * 2L + (lines.Count + 1) * 160L > 7L * 1024 * 1024))
        {
            units -= lines.First!.Value.Text.Length;
            firstRetainedLine = lines.First.Value.Id + 1;
            lines.RemoveFirst(); lost = true;
        }
        if (lost) LossRevision++;
        return lost;
    }

    public MacroReceiveCutoff Cutoff() => new(currentId, current.Length, Eof);

    public MacroReceiveSnapshot Snapshot(long after, MacroReceiveCutoff? cutoff)
    {
        var snapshot = new List<MacroReceiveLine>();
        if (currentId > after && (cutoff is null || currentId <= cutoff.Value.Line) && lostPrefix == 0)
        {
            int length = cutoff is { } boundary ? Math.Min(current.Length, boundary.Length) : current.Length;
            if (length >= currentFloor)
                snapshot.Add(new(currentId, current.ToString(0, length), false, 0, currentFloor));
        }

        for (var node = lines.Last; node is not null && node.Value.Id > after; node = node.Previous)
        {
            MacroReceiveLine line = node.Value;
            if (line.Truncated || cutoff is { } end && line.Id > end.Line) continue;
            if (cutoff is { } boundary && line.Id == boundary.Line)
            {
                int length = Math.Min(line.Text.Length, boundary.Length);
                if (length < line.Floor) continue;
                line = line with { Text = line.Text[..length], Complete = false };
            }
            snapshot.Add(line);
        }
        snapshot.Reverse();
        return new(snapshot.ToArray(), cutoff?.Eof ?? Eof);
    }

    public void Clear()
    {
        lines.Clear();
        current.Clear();
        current.Capacity = 16;
        units = 0;
    }

    public void MarkBoundary(MacroReceiveCutoff boundary)
    {
        while (lines.First is { } first && first.Value.Id < boundary.Line)
        {
            units -= first.Value.Text.Length;
            lines.RemoveFirst();
        }
        // The sampled line may have completed while the command was in flight.
        if (boundary.Line == currentId && lostPrefix == 0)
            currentFloor = Math.Max(currentFloor, boundary.Length);
        else if (lines.First is { Value: var line } node && line.Id == boundary.Line && !line.Truncated)
            node.Value = line with { Floor = Math.Max(line.Floor, boundary.Length) };
        firstRetainedLine = lines.First?.Value.Id ?? currentId;
    }

    public bool Consume(long id, int end)
    {
        if (id == currentId)
        {
            if (lostPrefix != 0 || end < currentFloor || end > current.Length) return false;
            foreach (var line in lines) units -= line.Text.Length;
            lines.Clear(); currentFloor = end; firstRetainedLine = currentId; return true;
        }
        if (id < firstRetainedLine) return false;
        var node = lines.First;
        while (node is not null && node.Value.Id != id) node = node.Next;
        if (node is null || node.Value.Truncated || end < node.Value.Floor || end > node.Value.Text.Length) return false;
        while (lines.First != node) { units -= lines.First!.Value.Text.Length; lines.RemoveFirst(); }
        if (end == node.Value.Text.Length)
        { units -= node.Value.Text.Length; lines.RemoveFirst(); }
        else node.Value = node.Value with { Floor = end };
        firstRetainedLine = lines.First?.Value.Id ?? currentId;
        return true;
    }

    public static async Task<((long Line, MacroMatch Match)? Match, long ScannedThrough)> SearchAsync(
        MacroReceiveSnapshot snapshot, MacroCandidate[] patterns, bool prompt, long scannedThrough,
        Func<bool> isCurrent, CancellationToken token)
    {
        var work = new Stopwatch();
        TimeSpan sliceStart = TimeSpan.Zero;
        int searches = 0;
        await MacroLimits.Workers.WaitAsync(token).ConfigureAwait(false);
        bool ownsWorker = true;
        work.Start();
        try
        {
            foreach (var line in snapshot.Lines)
            {
                if (line.Id <= scannedThrough || line.Truncated || !line.Complete && !prompt) continue;
                MacroMatch? best = null;
                for (int i = 0; i < patterns.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!isCurrent()) return (null, scannedThrough);
                    if (++searches > 65536 || work.ElapsedMilliseconds >= 1000)
                        throw new MacroError(MacroErrorCode.MatchWorkLimit);
                    if (work.Elapsed - sliceStart >= TimeSpan.FromMilliseconds(5))
                    {
                        work.Stop();
                        MacroLimits.Workers.Release();
                        ownsWorker = false;
                        await Task.Yield();
                        if (!isCurrent()) return (null, scannedThrough);
                        await MacroLimits.Workers.WaitAsync(token).ConfigureAwait(false);
                        ownsWorker = true;
                        sliceStart = work.Elapsed;
                        work.Start();
                    }
                    var match = patterns[i].Pattern.Match(line.Text, line.Floor, patterns[i].Name);
                    if (match is not null && (best is null || match.Index < best.Index)) best = match;
                }
                if (best is not null) return ((line.Id, best), scannedThrough);
                if (line.Complete) scannedThrough = line.Id;
            }
            return (null, scannedThrough);
        }
        finally { if (ownsWorker) MacroLimits.Workers.Release(); }
    }
}
