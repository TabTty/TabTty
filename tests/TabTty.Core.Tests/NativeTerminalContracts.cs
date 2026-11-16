using System.Text;
using TabTty.Core;

internal static class NativeTerminalContracts
{
    internal static void Run()
    {
        if (!NativeTerminalCore.Enabled) return;
        RejectedOperationPublishesNothingAndOwnedSnapshotSurvives();
        Compare("partial UTF-8 from independent sources", t =>
        {
            t.Receive(TerminalReceiveSource.Data, [0xE3]);
            t.Receive(TerminalReceiveSource.StandardError, [0xF0, 0x9F]);
        }, t => _ = t.AiColumns, t =>
        {
            t.Receive(TerminalReceiveSource.Data, [0x81, 0x82]);
            t.Receive(TerminalReceiveSource.StandardError, [0x98, 0x80]);
            Feed(t, "tail\r\n");
        });
        Compare("partial CSI before resize", t => Feed(t, "first\r\n\u001b[31"),
            t => t.Resize(7, 5), t => Feed(t, ";1mred\u001b[0m\r\n"));
        Compare("per-source repeat graphic and pending cluster", t =>
        {
            Feed(t, "e\u0301");
            t.Receive(TerminalReceiveSource.StandardError, Encoding.UTF8.GetBytes("界"));
            Feed(t, "\u001b[3b");
        }, t => _ = t.AiColumns, t =>
        {
            t.Receive(TerminalReceiveSource.StandardError, "\u001b[2b"u8);
            Feed(t, "\r\n");
        });
        Compare("colored wide cells and tabs before reflow", t =>
            Feed(t, "\u001b[31m界\u001b[0m\tZ\r\nANCHOR e\u0301 tail"),
            t => t.Resize(7, 5), t => { Feed(t, "\r\nnext"); t.Resize(19, 4); });
        Compare("pending CR across recording interval", t => Feed(t, "A\r"),
            t => t.RecordingInterval++, t => Feed(t, "\nB\r\n"));
        Compare("pending UTF-8 at EOF after handoff", t => Feed(t, "prefix"),
            t => _ = t.AiRows, t => t.Receive(TerminalReceiveSource.Data, [0xF0, 0x9F]));
        Compare("handoff inside a large receive batch", t => Feed(t, "start\r\n"),
            t => Feed(t, new string('a', 2500) + "\u001b[?1049hALT"),
            t => Feed(t, "\u001b[?1049l\r\nend"));
        Compare("partial OSC across handoff", t => Feed(t, "before\u001b]0;title"),
            t => _ = t.AiColumns, t => Feed(t, "\u001b\\after"));
        CommonShellControlsKeepTheNativePath();
        HeldSnapshotsAndSearchAnchorsSurviveHandoff();
        MacroCallbacksSeeOnlyNewInputAfterHandoff();
        LegacyEncodingStartsManaged();
    }

    private static void RejectedOperationPublishesNothingAndOwnedSnapshotSurvives()
    {
        var budget = new ResourceQuota(128L * 1024 * 1024, collectOnPressure: false);
        using var native = new NativeTerminalCore(80, 24, 100, 1, budget);
        var text = new List<byte>();
        var display = new List<byte>();
        var replies = new List<byte>();
        if (!native.TryFeed(0, "saved 界\r\n\u001b[?6n"u8, 3, 1, text.AddRange, display.AddRange,
            replySink: replies.AddRange) ||
            !native.TrySnapshot(out TerminalSnapshot? before))
            throw new InvalidOperationException("The native test operation did not reach its admitted effect boundary.");
        byte[] expected = "saved 界\r\n"u8.ToArray();
        if (!text.SequenceEqual(expected) || !display.SequenceEqual(expected) || !replies.SequenceEqual("\u001b[?2;1R"u8.ToArray()))
            throw new InvalidOperationException("The admitted operation did not deliver the exact Text and Terminal bytes.");
        long beforeRejection = budget.Used;
        try
        {
            native.TryFeed(0, "cancelled\r\n"u8, 3, 1, text.AddRange, display.AddRange, new CancellationToken(true));
            throw new InvalidOperationException("Cancelled native execution reached the terminal.");
        }
        catch (OperationCanceledException) { }
        try
        {
            native.TryFeed(0, "rejected\r\n\u001b[5n"u8, 3, 1, text.AddRange, display.AddRange,
                replySink: replies.AddRange, pendingReplies: 128);
            throw new InvalidOperationException("A full reply queue admitted a responding operation.");
        }
        catch (ResourceLimitException) { }
        byte[] rejected = Encoding.ASCII.GetBytes(new string('x', 2048) + "\u001b[" + new string('1', 4097) + "m");
        if (native.TryFeed(0, rejected, 3, 1, text.AddRange, display.AddRange) ||
            !text.SequenceEqual(expected) || !display.SequenceEqual(expected) || budget.Used != beforeRejection)
            throw new InvalidOperationException("Rejected operation changed an effect or retained its preparation charge.");
        if (!native.TrySnapshot(out TerminalSnapshot? after) || !before!.SequenceEqual(after!))
            throw new InvalidOperationException("Rejected operation changed the original screen.");
        native.Dispose();
        if (budget.Used == 0 || before is null || !before.Contains("saved 界") || before.GetCells(0).IsEmpty)
            throw new InvalidOperationException("The owned snapshot lost its data or charge when native state was released.");
        GC.KeepAlive(after);
    }

    private static void Compare(string name, Action<TerminalOutput> prefix,
        Action<TerminalOutput> transfer, Action<TerminalOutput> suffix)
    {
        var native = new TerminalOutput(12, 100, 4);
        var managed = new TerminalOutput(12, 100, 4);
        _ = managed.AiColumns; // Select the existing reducer before any input.
        if (!native.UsesNativeCore || managed.UsesNativeCore)
            throw new InvalidOperationException("Native and managed comparison did not select different reducers.");
        try
        {
            prefix(native);
            prefix(managed);
            if (!native.UsesNativeCore) throw new InvalidOperationException(name + ": prefix left the native path.");
            Equal(name + " prefix", managed.GetRows(), native.GetRows());
            transfer(native);
            transfer(managed);
            if (native.UsesNativeCore) throw new InvalidOperationException(name + ": expected handoff did not occur.");
            Equal(name + " handoff", managed.GetRows(), native.GetRows());
            suffix(native);
            suffix(managed);
            native.CompleteReceive();
            managed.CompleteReceive();
            Equal(name + " completion", managed.GetRows(), native.GetRows());
            var expectedReplies = managed.TakeReplies().Select(Convert.ToHexString);
            if (!expectedReplies.SequenceEqual(native.TakeReplies().Select(Convert.ToHexString)))
                throw new InvalidOperationException(name + ": terminal replies changed.");
        }
        finally { native.ReleaseNativeState(); managed.ReleaseNativeState(); }
    }

    private static void HeldSnapshotsAndSearchAnchorsSurviveHandoff()
    {
        var terminal = new TerminalOutput(12, 100, 4);
        try
        {
            // Retire a prefix of a soft-wrapped line before transferring its logical identity.
            Feed(terminal, new string('x', 1300) + "\r\nANCHOR tail");
            var before = terminal.GetRows();
            var match = before.Find("ANCHOR", true) ?? throw new InvalidOperationException("Missing search fixture.");
            string[] frozenText = before.ToArray();
            var identities = Enumerable.Range(0, before.Count).Select(before.DisplayIdentity).ToArray();
            // Leave cells lazy until after both native mutation and destruction.
            Feed(terminal, "\r\nnext");
            _ = terminal.AiCursorRow;
            if (terminal.UsesNativeCore) throw new InvalidOperationException("AI observation did not transfer ownership.");
            var after = terminal.GetRows();
            if (after.MapMatchFrom(before, match) is null)
                throw new InvalidOperationException("A retained search anchor was lost at handoff.");
            if (!before.SequenceEqual(frozenText) ||
                !Enumerable.Range(0, before.Count).Select(before.DisplayIdentity).SequenceEqual(identities))
                throw new InvalidOperationException("An immutable snapshot changed after native destruction.");
            foreach (int row in Enumerable.Range(0, before.Count))
                foreach (var cell in before.GetCells(row).Span)
                    if (cell.Text is null) throw new InvalidOperationException("Lazy cells lost their owned storage.");
            Feed(terminal, "\u001b[2Jchanged");
            if (!before.SequenceEqual(frozenText))
                throw new InvalidOperationException("A held snapshot changed after managed mutation.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void MacroCallbacksSeeOnlyNewInputAfterHandoff()
    {
        var terminal = new TerminalOutput(12, 100, 4);
        try
        {
            Feed(terminal, "prior");
            var observed = new StringBuilder();
            terminal.MacroCharacter = (_, rune, _) => observed.Append(rune.ToString());
            Feed(terminal, "new");
            if (terminal.UsesNativeCore || observed.ToString() != "new")
                throw new InvalidOperationException("Macro handoff replayed old characters or omitted new input.");
            if (terminal.GetRows()[0] != "priornew")
                throw new InvalidOperationException("Macro handoff lost terminal text.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void LegacyEncodingStartsManaged()
    {
        var terminal = new TerminalOutput(12, 100, 4, receiveEncoding: "cp932");
        if (terminal.UsesNativeCore) throw new InvalidOperationException("Legacy decoding entered the UTF-8 native core.");
        terminal.Receive(TerminalReceiveSource.Data, [0x82, 0xA0]);
        terminal.CompleteReceive();
        if (terminal.GetRows()[0] != "あ") throw new InvalidOperationException("Legacy decoding regressed.");
    }

    private static void CommonShellControlsKeepTheNativePath()
    {
        var terminal = new TerminalOutput(40, 100, 4);
        try
        {
            Feed(terminal, "\u001b]0;shell title\a\u001b[?2004hprompt> ");
            if (!terminal.UsesNativeCore || !terminal.BracketedPaste || terminal.GetRows()[0] != "prompt> ")
                throw new InvalidOperationException("Common shell controls left the native path or lost paste mode.");
            Feed(terminal, "\u001b[?2004l");
            if (!terminal.UsesNativeCore || terminal.BracketedPaste)
                throw new InvalidOperationException("Native bracketed-paste reset was not published.");
            Feed(terminal, "\u001b[?2004h");
            _ = terminal.AiColumns;
            if (!terminal.BracketedPaste)
                throw new InvalidOperationException("Bracketed-paste mode was lost during handoff.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void Feed(TerminalOutput terminal, string text) =>
        terminal.Receive(TerminalReceiveSource.Data, Encoding.UTF8.GetBytes(text));

    private static void Equal(string name, TerminalSnapshot expected, TerminalSnapshot actual)
    {
        if (expected.Count != actual.Count || expected.Columns != actual.Columns ||
            expected.ScreenRows != actual.ScreenRows || expected.ScreenStartRow != actual.ScreenStartRow ||
            expected.CursorRow != actual.CursorRow || expected.CursorColumn != actual.CursorColumn ||
            expected.CursorVisible != actual.CursorVisible || expected.CursorShape != actual.CursorShape ||
            expected.CursorBlink != actual.CursorBlink || expected.IsAlternateScreen != actual.IsAlternateScreen ||
            expected.Mouse != actual.Mouse)
            throw new InvalidOperationException(name + ": snapshot geometry or modes differ.");
        for (int row = 0; row < expected.Count; row++)
            if (expected[row] != actual[row] || expected.IsSoftWrapped(row) != actual.IsSoftWrapped(row) ||
                !expected.GetCells(row).Span.SequenceEqual(actual.GetCells(row).Span))
                throw new InvalidOperationException(name + ": text, wrap or cells differ at row " + row + ".");
    }
}
