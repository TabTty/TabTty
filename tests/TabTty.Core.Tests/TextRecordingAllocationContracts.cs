using System.Text;
using TabTty.Core;

internal static class TextRecordingAllocationContracts
{
    internal static void Run()
    {
        UnchangedRowsDoNotRebuildTheirRecordingText();
        SubmittedRowsRecordLaterEditsOnce();
        RecordingTextFollowsEditsTabsAndFormatting();
    }

    private static void SubmittedRowsRecordLaterEditsOnce()
    {
        var recorded = new StringBuilder();
        var terminal = new TerminalOutput(120, 100, 4) { TextRecord = value => recorded.Append(value) };
        terminal.Receive(TerminalReceiveSource.Data, "A\r\n"u8);
        terminal.Receive(TerminalReceiveSource.Data, "\u001b[1;1HB\r\n"u8);
        terminal.Receive(TerminalReceiveSource.Data, "\u001b[1;1HB\r\n"u8);
        terminal.Receive(TerminalReceiveSource.Data, "\u001b[1;1H\u001b[31mB\u001b[0m\r\n"u8);
        terminal.CompleteReceive();
        if (recorded.ToString() != "A\r\nB\r\n")
            throw new InvalidOperationException("Recorded text edits or unchanged revisions were lost or repeated.");
    }

    private static void UnchangedRowsDoNotRebuildTheirRecordingText()
    {
        var recorded = new StringBuilder();
        var terminal = new TerminalOutput(120, 100, 4) { TextRecord = value => recorded.Append(value) };
        string line = new('X', 96);
        byte[] row = Encoding.UTF8.GetBytes(line + "\r\n");
        for (int i = 0; i < 90; i++) terminal.Receive(TerminalReceiveSource.Data, row);
        terminal.FlushText();
        string expected = string.Concat(Enumerable.Repeat(line + "\r\n", 90));
        if (recorded.ToString() != expected) throw new InvalidOperationException("Initial Text recording changed.");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) terminal.FlushText();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 100_000)
            throw new InvalidOperationException($"Unchanged Text rows allocated {allocated} bytes while being revisited.");
        if (recorded.ToString() != expected) throw new InvalidOperationException("Unchanged Text rows were recorded again.");

        // Retirement must also reuse the completed revisions without replaying them.
        for (int i = 0; i < 30; i++) terminal.Receive(TerminalReceiveSource.Data, row);
        terminal.CompleteReceive();
        if (recorded.ToString() != string.Concat(Enumerable.Repeat(line + "\r\n", 120)))
            throw new InvalidOperationException("Text recording changed when completed rows retired.");
    }

    private static void RecordingTextFollowsEditsTabsAndFormatting()
    {
        var row = new TerminalRow();
        row.Write(0, "A", 1, default);
        Expect(row, "A", "A");
        row.Write(0, "界", 2, default);
        Expect(row, "界", "界");
        row.Write(2, "e\u0301", 1, default);
        Expect(row, "界e\u0301", "界e\u0301");

        // ANSI rendition changes do not alter a Text revision.
        row.Write(2, "e\u0301", 1, new TerminalStyle(0xFF0000, null, TerminalStyleFlags.Bold));
        Expect(row, "界e\u0301", "界e\u0301");
        row.AddTab(3, 8);
        Expect(row, "界e\u0301", "界e\u0301     ", serialized: "界e\u0301\t");
        row.AddFormat(4, new Rune(0xFEFF));
        Expect(row, "界e\u0301 \uFEFF", "界e\u0301 \uFEFF    ");
        row.Write(7, "Z", 1, default);
        Expect(row, "界e\u0301 \uFEFF   Z", "界e\u0301 \uFEFF   Z");
        row.RecordedColumns = [(0, 3)];
        if (row.GetText(preserveTabs: true, pendingOnly: true, serializedColumn: null) != " \uFEFF   Z")
            throw new InvalidOperationException("Pending Text columns were replaced by a full cached row.");

        row.Recycle();
        Expect(row, "", "");
        row.Write(3, "x", 1, default);
        Expect(row, "   x", "   x");
        row.Erase(1, 2, new TerminalStyle(null, 0x0000FF, default));
        Expect(row, "   x", "   x");
        row.Insert(1, 1, 8);
        Expect(row, "    x", "    x");
        row.Delete(0, 2, 8);
        Expect(row, "  x", "  x");

        static void Expect(TerminalRow row, string displayed, string revision, string? serialized = null)
        {
            if (row.GetText() != displayed ||
                row.GetText(preserveTabs: true, serializedColumn: null) != revision ||
                row.GetText(preserveTabs: true) != (serialized ?? revision))
                throw new InvalidOperationException("Text serialization changed after a row edit.");
        }
    }
}
