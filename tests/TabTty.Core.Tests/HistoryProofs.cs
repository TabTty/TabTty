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
        if (deep.PrepareHistory() != bigLimit)
            throw new InvalidOperationException("The fixture did not fill the retention boundary.");
        var result = Search(deep, "absent");
        if (result.Matches.Length != 0 || result.Scanned is null || result.Next is null)
            throw new InvalidOperationException("An empty search page did not disclose validated progress.");
        Equal(Validate(deep, result.Dependencies, default), null);
        var wrapped = new TerminalOutput(columns: 4, retainedRowLimit: 100, screenRows: 4);
        Write(wrapped, "abCDabcd\r\nEF");
        var matches = Search(wrapped, "da");
        if (matches.Matches is not [{ Text: "Da" }])
            throw new InvalidOperationException("A literal match spanning a soft wrap was lost.");
        if (Search(wrapped, "de").Matches.Length != 0)
            throw new InvalidOperationException("Literal search crossed a hard line boundary.");
        VerifySearchContinuation();
        VerifyFixedSearchContext();

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
        Write(cluster, "b");
        var tail = Read(cluster, default, null, null, 1, 1, PageOutputBudget, default);
        if (tail.Fragments is not [{ text: "b" }] || tail.Earlier is not { } earlier)
            throw new InvalidOperationException("The backward checkpoint lost the preceding stored character.");
        try
        {
            _ = Read(cluster, default, earlier, null, 1, 1, PageOutputBudget, default);
            throw new InvalidOperationException("A backward read silently consumed a character above the byte limit.");
        }
        catch (HistoryException error) when (error.Message == "resource_limit") { }
        if (Read(cluster, default, earlier, null, 1, 3, PageOutputBudget, default).Fragments is not [{ text: "あ" }])
            throw new InvalidOperationException("Retrying the backward read did not return the remaining character.");
        VerifyMaximumSpan();
        Write(deep, "A\rB\r\u001b[K");
        // The requested range ends at the empty last row, so suffix-only appends
        // preserve its zero-width endpoint even when later overwritten.
        Equal(Validate(deep, result.Dependencies, default), null);
    }

    private static void VerifyMaximumSpan()
    {
        var model = new TerminalOutput(columns: 8, retainedRowLimit: 100, screenRows: 3);
        try
        {
            string retained = "👩" + new string('\u0301', 254) + "\u200D";
            Write(model, retained + "💻😀");
            Warm(model);
            int capacity = System.Text.Encoding.UTF8.GetByteCount(retained);
            var entire = Read(model, default, null, null, 1, capacity + 4, PageOutputBudget, default).Range ??
                throw new InvalidOperationException("The fixture did not capture both stored spans.");
            try
            {
                _ = Read(model, default, entire, entire.Start, 1, capacity - 1, PageOutputBudget, default);
                throw new InvalidOperationException("A maximum stored span was cut at the history byte limit.");
            }
            catch (HistoryException error) when (error.Message == "resource_limit") { }
            var first = Read(model, default, entire, entire.Start, 1, capacity, PageOutputBudget, default);
            if (first.Fragments is not [{ text: var text }] || text != retained || first.Range is null || first.Next is null)
                throw new InvalidOperationException("History did not return the exact first stored span and a continuation.");
            Write(model, "\u001b[?1049halternate");
            Warm(model);
            var next = Read(model, default, first.Range, first.Next, 1, capacity, PageOutputBudget, default);
            if (next.Fragments is not [{ text: "😀" }])
                throw new InvalidOperationException("Parked normal history lost the next span while alternate output was active.");
            Write(model, "\u001b[?1049l");
            model.Resize(4, 3);
            Warm(model);
            Equal(Validate(model, [first.Range], default), null);
            for (int row = 0; row < 120; row++) Write(model, "\r\nline");
            Warm(model);
            Equal(Validate(model, [first.Range], default), "range_evicted");
            if (first.Fragments[0].text != retained)
                throw new InvalidOperationException("Retiring history mutated an already owned page.");
        }
        finally { model.ReleaseNativeState(); }
    }

    private static void Write(TerminalOutput model, string text) =>
        model.Receive(TerminalReceiveSource.Data, System.Text.Encoding.UTF8.GetBytes(text));

    private static SearchPage Search(TerminalOutput model, string query, SearchCursor? cursor = null)
    {
        long started = Stopwatch.GetTimestamp();
        using var window = model.CaptureHistorySearch(cursor, query, 2, started, default);
        window.Scan(query, false, 4, started, PageOutputBudget, default);
        return model.CompleteHistorySearch(default, window, query, false, 4, 2, started, default);
    }

    private static void VerifySearchContinuation()
    {
        var model = new TerminalOutput(columns: 4, retainedRowLimit: 5000, screenRows: 3);
        try
        {
            Write(model, new string('a', 4095) + "ΣY" + new string('b', 12));
            Warm(model);
            SearchPage page = Search(model, "σy");
            var found = page.Matches.ToList();
            if (page.Next is null || page.Scanned is null)
                throw new InvalidOperationException("Search did not retain a checkpoint after the first bounded window.");
            Write(model, "ΣY");
            for (int attempts = 0; page.Next is { } cursor && attempts < 64; attempts++)
            {
                page = Search(model, "σy", cursor);
                found.AddRange(page.Matches);
            }
            if (page.Next is not null || found is not [{ Text: "ΣY", Position.Offset: 4095 }])
                throw new InvalidOperationException("Search lost a cross-window match, duplicated it, or extended its fixed end.");
        }
        finally { model.ReleaseNativeState(); }
    }

    private static void VerifyFixedSearchContext()
    {
        var model = new TerminalOutput(columns: 4, retainedRowLimit: 100, screenRows: 1);
        try
        {
            Write(model, "Q");
            var started = Stopwatch.GetTimestamp();
            using var window = model.CaptureHistorySearch(null, "Q", 0, started, default);
            window.Scan("Q", true, 1, started, PageOutputBudget, default);
            Write(model, "NEW");
            var page = model.CompleteHistorySearch(default, window, "Q", true, 1, 0, started, default);
            var context = page.Matches.Single().Context;
            if (Read(model, default, context, null, 1, 10, PageOutputBudget, default).Fragments is not [{ text: "Q" }])
                throw new InvalidOperationException("Search context disclosed output appended after capture.");
            Write(model, "\rX");
            Warm(model);
            Equal(Validate(model, page.Dependencies, default), "range_changed");
        }
        finally { model.ReleaseNativeState(); }

        foreach (bool breakBeforeCapture in new[] { false, true })
        {
            var terminal = new TerminalOutput(columns: 8, retainedRowLimit: 100, screenRows: 1);
            try
            {
                Write(terminal, breakBeforeCapture ? "Q\r\n" : "Q");
                long started = Stopwatch.GetTimestamp();
                using var search = terminal.CaptureHistorySearch(null, "Q", 0, started, default);
                search.Scan("Q", true, 4, started, PageOutputBudget, default);
                if (!breakBeforeCapture) Write(terminal, "\r\nNEW");
                Span scanned = terminal.CompleteHistorySearch(default, search, "Q", true, 4, 0, started, default).Scanned!;
                var row = Read(terminal, default, scanned, null, 4, 16, PageOutputBudget, default).Fragments[0];
                if (row.text != "Q" || row.line_end != breakBeforeCapture)
                    throw new InvalidOperationException("The scanned range did not preserve the original hard ending.");
            }
            finally { terminal.ReleaseNativeState(); }
        }
    }

    private static void Warm(TerminalOutput model)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { _ = model.PrepareHistory(); return; }
            catch (HistoryException error) when (error.Message == "busy" && attempt < 100) { }
        }
    }

    private static void Equal(string? actual, string? expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected ?? "valid"}, received {actual ?? "valid"}.");
    }
}
