using System.Diagnostics;
using TabTty.Core;
using static TabTty.Core.TerminalHistory;

internal static class HistoryProofs
{
    internal static void Run()
    {
        var model = new TerminalOutput(columns: 8, retainedRowLimit: 100, screenRows: 3);
        Write(model, "abbcdefg");
        var prefix = Read(model, default, null, null, 3, 256, PageOutputBudget, default).Range!;
        Write(model, "hijkl");
        Equal(Validate(model, [prefix], default), null);
        model.Resize(6, 3);
        Warm(model);
        Equal(Validate(model, [prefix], default), null);
        Write(model, "\u001b[1;1HA\u001b[1;1Ha");
        Equal(Validate(model, [prefix], default), "range_changed");
        for (int row = 0; row < 100; row++) Write(model, "\r\nline");
        Equal(Validate(model, [prefix], default), "range_evicted");

        const int bigLimit = 100_000;
        var deep = new TerminalOutput(columns: 12, retainedRowLimit: bigLimit, screenRows: 3);
        Write(deep, "needle\r\n");
        for (int row = 1; row < bigLimit; row++) Write(deep, "filler\r\n");
        Warm(deep);
        if (deep.GetHistoryRows().Length != bigLimit)
            throw new InvalidOperationException("The fixture did not fill the retention boundary.");
        var window = CaptureSearch(deep, null, "absent", default, Stopwatch.GetTimestamp());
        var scan = Scan(window, "absent", false, 4, default);
        var result = CompleteSearch(deep, default, window, scan, "absent", false, 4, 2, default);
        if (result.Matches.Length != 0 || result.Scanned is null || result.Next is null)
            throw new InvalidOperationException("An empty search page did not disclose validated progress.");
        Equal(Validate(deep, result.Dependencies, default), null);
        var wrapped = new TerminalOutput(columns: 4, retainedRowLimit: 100, screenRows: 4);
        Write(wrapped, "abCDabcd\r\nEF");
        var contiguous = CaptureSearch(wrapped, null, "da", default, Stopwatch.GetTimestamp());
        var matches = Scan(contiguous, "da", false, 4, default);
        if (matches.Matches is not [{ Text: "Da" }])
            throw new InvalidOperationException("A literal match spanning a soft wrap was lost.");
        var hard = CaptureSearch(wrapped, null, "de", default, Stopwatch.GetTimestamp());
        if (Scan(hard, "de", false, 4, default).Matches.Length != 0)
            throw new InvalidOperationException("Literal search crossed a hard line boundary.");

        var sized = new TerminalOutput(columns: 1024, retainedRowLimit: 1000, screenRows: 3);
        for (int row = 0; row < 200; row++) Write(sized, new string('あ', 500) + "\r\n");
        Warm(sized);
        HistoryPage first = Read(sized, default, null, null, 200, 256 * 1024, PageOutputBudget, default);
        if (first.Range is null || first.Next is null)
            throw new InvalidOperationException("Escaped output did not require a continuation.");
        var fragments = first.Fragments.ToList();
        Point? next = first.Next;
        for (int pages = 0; next is not null && pages < 20; pages++)
        {
            var continued = Read(sized, default, first.Range, next, 200, 256 * 1024, PageOutputBudget, default);
            if (continued.Next == next || continued.Fragments.Length == 0)
                throw new InvalidOperationException("A size-limited page did not advance.");
            fragments.AddRange(continued.Fragments);
            next = continued.Next;
        }
        if (next is not null || fragments.Any(fragment => fragment.text.Any(character => character != 'あ')) ||
            fragments.Where(fragment => fragment.text.Length != 0).GroupBy(fragment => fragment.logical_line_id)
                .Any(line => line.Count() > 1 && line.Zip(line.Skip(1)).Any(pair =>
                    pair.First.content_offset + pair.First.text.Length != pair.Second.content_offset)))
            throw new InvalidOperationException("Size-limited continuation corrupted or skipped retained text.");

        var cluster = new TerminalOutput(columns: 8);
        Write(cluster, "あ");
        try
        {
            _ = Read(cluster, default, null, null, 1, 2, PageOutputBudget, default);
            throw new InvalidOperationException("A complete cluster exceeded the explicit text limit.");
        }
        catch (HistoryException error) when (error.Message == "resource_limit") { }
        Write(deep, "A\rB\r\u001b[K");
        // The requested range ends at the empty last row, so suffix-only appends
        // preserve its zero-width endpoint even when later overwritten.
        Equal(Validate(deep, result.Dependencies, default), null);
    }

    private static void Write(TerminalOutput model, string text) =>
        model.Receive(TerminalReceiveSource.Data, System.Text.Encoding.UTF8.GetBytes(text));

    private static void Warm(TerminalOutput model)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { _ = model.GetHistoryRows(); return; }
            catch (HistoryException error) when (error.Message == "busy" && attempt < 100) { }
        }
    }

    private static void Equal(string? actual, string? expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected ?? "valid"}, received {actual ?? "valid"}.");
    }
}
