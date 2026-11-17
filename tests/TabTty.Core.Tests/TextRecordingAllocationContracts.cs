using System.Text;
using TabTty.Core;

internal static class TextRecordingAllocationContracts
{
    internal static void Run()
    {
        UnchangedRowsDoNotRebuildTheirRecordingText();
        SubmittedRowsRecordLaterEditsOnce();
    }

    private static void SubmittedRowsRecordLaterEditsOnce()
    {
        var recorded = new StringBuilder();
        var terminal = new TerminalOutput(120, 100, 4);
        terminal.BeginRecording(true, 1, value => recorded.Append(Encoding.UTF8.GetString(value)));
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
        var terminal = new TerminalOutput(120, 100, 4);
        terminal.BeginRecording(true, 1, value => recorded.Append(Encoding.UTF8.GetString(value)));
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

}
