using System.Text;
using TabTty.Core;

internal static class NativeTerminalContracts
{
    internal static void Run()
    {
        if (!NativeTerminalCore.Enabled) return;
        RejectedOperationPublishesNothingAndOwnedSnapshotSurvives();
        FailedPairedResizeLeavesBothModelsUntouched();
        HeightOnlyResizeKeepsRetainedRowIdentities();
        LegacyNativeAdmissionDoesNotConsumeTheLiveDecoder();
        Compare("partial UTF-8 from independent sources", t =>
        {
            t.Receive(TerminalReceiveSource.Data, [0xE3]);
            t.Receive(TerminalReceiveSource.StandardError, [0xF0, 0x9F]);
        }, t => _ = t.GetHistoryRows(), t =>
        {
            t.Receive(TerminalReceiveSource.Data, [0x81, 0x82]);
            t.Receive(TerminalReceiveSource.StandardError, [0x98, 0x80]);
            Feed(t, "tail\r\n");
        });
        Compare("partial CSI before resize", t => Feed(t, "first\r\n\u001b[31"),
            t => t.Resize(7, 5), t => Feed(t, ";1mred\u001b[0m\r\n"), handoff: false);
        Compare("per-source repeat graphic and pending cluster", t =>
        {
            Feed(t, "e\u0301");
            t.Receive(TerminalReceiveSource.StandardError, Encoding.UTF8.GetBytes("界"));
            Feed(t, "\u001b[3b");
        }, t => _ = t.GetHistoryRows(), t =>
        {
            t.Receive(TerminalReceiveSource.StandardError, "\u001b[2b"u8);
            Feed(t, "\r\n");
        });
        Compare("colored wide cells and tabs before reflow", t =>
            Feed(t, "\u001b[31m界\u001b[0m\tZ\r\nANCHOR e\u0301 tail"),
            t => t.Resize(7, 5), t => { Feed(t, "\r\nnext"); t.Resize(19, 4); }, handoff: false);
        Compare("pending CR across recording interval", t => Feed(t, "A\r"),
            t => { _ = t.GetHistoryRows(); t.RecordingInterval++; }, t => Feed(t, "\nB\r\n"));
        Compare("pending UTF-8 at EOF after handoff", t => Feed(t, "prefix"),
            t => _ = t.GetHistoryRows(), t => t.Receive(TerminalReceiveSource.Data, [0xF0, 0x9F]));
        Compare("parked normal screen at handoff",
            t => Feed(t, "start\r\n" + new string('a', 2500) + "\u001b[?1049hALT"),
            t => _ = t.GetHistoryRows(),
            t => Feed(t, "\u001b[?1049l\r\nend"));
        Compare("partial OSC across handoff", t => Feed(t, "before\u001b]0;title"),
            t => _ = t.GetHistoryRows(), t => Feed(t, "\u001b\\after"));
        CommonShellControlsKeepTheNativePath();
        NormalScreenEditingPreservesSourceAndCursorState();
        HeldSnapshotsAndSearchAnchorsSurviveHandoff();
        MacroCallbacksSeeOnlyNewInputAfterHandoff();
        LegacyEncodingStartsManaged();
    }

    private static void HeightOnlyResizeKeepsRetainedRowIdentities()
    {
        var terminal = new TerminalOutput(12, 100, 4);
        try
        {
            Feed(terminal, "one\r\ntwo\r\nthree\r\nfour\r\nfive");
            var before = terminal.GetRows();
            var identities = Enumerable.Range(0, before.Count).Select(before.DisplayIdentity).ToArray();
            terminal.Resize(12, 2);
            var shorter = terminal.GetRows();
            terminal.Resize(12, 5);
            var taller = terminal.GetRows();
            foreach (var snapshot in new[] { shorter, taller })
            {
                if (!terminal.UsesNativeCore || !snapshot.SequenceEqual(before) ||
                    !Enumerable.Range(0, snapshot.Count).Select(snapshot.DisplayIdentity).SequenceEqual(identities))
                    throw new InvalidOperationException("A height-only resize changed the retained text or row identity.");
            }
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void LegacyNativeAdmissionDoesNotConsumeTheLiveDecoder()
    {
        var quota = new ResourceQuota(128 * 1024 * 1024, collectOnPressure: false);
        using var native = new NativeTerminalCore(80, 24, 100, 1, quota, 50220, receiveSevenBitKana: true);
        var terminal = new List<byte>();
        var replies = new List<byte>();
        void Feed(uint source, ReadOnlySpan<byte> bytes)
        {
            if (!native.TryFeed(source, bytes, 1, null, terminal.AddRange, replySink: replies.AddRange))
                throw new InvalidOperationException("The native model rejected a supported JIS receive unit.");
        }
        Feed(0, "\u001b$B$"u8);
        Feed(1, "stderr "u8);
        byte[] invalid = Encoding.ASCII.GetBytes("\u001b[" + new string('1', 4097) + "m");
        if (native.TryFeed(0, invalid, 1, null, terminal.AddRange, replySink: replies.AddRange) ||
            !terminal.SequenceEqual("stderr "u8.ToArray()))
            throw new InvalidOperationException("JIS admission published effects for a rejected batch.");
        Feed(0, "\"\u001b(B\u001b[6n"u8);
        if (!native.TrySnapshot(out var snapshot) || snapshot![0] != "stderr あ" ||
            !terminal.SequenceEqual("stderr あ"u8.ToArray()) ||
            !replies.SequenceEqual("\u001b[1;10R"u8.ToArray()))
            throw new InvalidOperationException("A rejected batch changed JIS state, output, or cursor reports.");
    }

    private static void FailedPairedResizeLeavesBothModelsUntouched()
    {
        var quota = new ResourceQuota(4 * 1024 * 1024, collectOnPressure: false);
        using var display = new NativeTerminalCore(80, 24, 100, 1, quota);
        using var text = new NativeTerminalCore(80, 24, 100, 1, quota);
        if (!display.TryFeed(0, "display"u8, 0, null, null) || !text.TryFeed(0, "receive"u8, 0, null, null))
            throw new InvalidOperationException("Could not prepare both resize projections.");
        long before = quota.Used;
        using (display.PrepareResize(40, 12))
        {
            try
            {
                using var second = text.PrepareResize(40, 12);
                throw new InvalidOperationException("The second projection exceeded the shared resize allowance.");
            }
            catch (ResourceLimitException) { }
        }
        if (quota.Used != before || display.ReadScreenState().Columns != 80 || text.ReadScreenState().Columns != 80)
            throw new InvalidOperationException("Rejected paired resize changed geometry or retained temporary memory.");
        using var retry = text.PrepareResize(40, 12);
        retry.Apply(null);
        if (text.ReadScreenState().Columns != 40)
            throw new InvalidOperationException("Releasing the prepared resize did not enable a retry.");
    }

    private static void NormalScreenEditingPreservesSourceAndCursorState()
    {
        var terminal = new TerminalOutput(16, 100, 5);
        try
        {
            Feed(terminal, "\u001b[2;4r\u001b[?6h\u001b[2;3H\u001b[31m\u001b7");
            Feed(terminal, "\u001b[1;1H\u001b[32m\u001b8\u001b)");
            Feed(terminal, "0\u000e");
            terminal.Receive(TerminalReceiveSource.StandardError, "q"u8);
            Feed(terminal, "q\u000f\uFEFFx\u001b[?6n");
            var snapshot = terminal.GetRows();
            if (!terminal.UsesNativeCore || snapshot[2] != "  q─\uFEFFx" ||
                snapshot.GetCells(2).Span[2].Style.Foreground != 0xCD0000 ||
                !terminal.TakeReplies().Single().AsSpan().SequenceEqual("\u001b[?2;6R"u8))
                throw new InvalidOperationException("Native save/restore, source charset, zero-width text, or origin reply diverged.");

            Feed(terminal, "\u001b[!p\u001b[3;1H\u001b[3g\u001b[5G\u001bH\u001b[1G\tY");
            var edited = terminal.GetRows();
            if (!terminal.UsesNativeCore || edited.CursorColumn != 5 || edited[2] != "  q─\uFEFFY")
                throw new InvalidOperationException("Native reset or custom tab stops changed cursor positioning.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void RejectedOperationPublishesNothingAndOwnedSnapshotSurvives()
    {
        var budget = new ResourceQuota(128L * 1024 * 1024, collectOnPressure: false);
        using var native = new NativeTerminalCore(80, 24, 100, 1, budget);
        var text = new List<byte>();
        var display = new List<byte>();
        var replies = new List<byte>();
        if (!native.TryFeed(0, "saved 界\r\n\u001b[?6n"u8, 3, text.AddRange, display.AddRange,
            replySink: replies.AddRange) ||
            !native.TrySnapshot(out TerminalSnapshot? before))
            throw new InvalidOperationException("The native test operation did not reach its admitted effect boundary.");
        byte[] expected = "saved 界\r\n"u8.ToArray();
        if (!text.SequenceEqual(expected) || !display.SequenceEqual(expected) || !replies.SequenceEqual("\u001b[?2;1R"u8.ToArray()))
            throw new InvalidOperationException("The admitted operation did not deliver the exact Text and Terminal bytes.");
        long beforeRejection = budget.Used;
        try
        {
            native.TryFeed(0, "cancelled\r\n"u8, 3, text.AddRange, display.AddRange, new CancellationToken(true));
            throw new InvalidOperationException("Cancelled native execution reached the terminal.");
        }
        catch (OperationCanceledException) { }
        try
        {
            native.TryFeed(0, "rejected\r\n\u001b[5n"u8, 3, text.AddRange, display.AddRange,
                replySink: replies.AddRange, pendingReplies: 128);
            throw new InvalidOperationException("A full reply queue admitted a responding operation.");
        }
        catch (ResourceLimitException) { }
        byte[] rejected = Encoding.ASCII.GetBytes(new string('x', 2048) + "\u001b[" + new string('1', 4097) + "m");
        if (native.TryFeed(0, rejected, 3, text.AddRange, display.AddRange) ||
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
        Action<TerminalOutput> transfer, Action<TerminalOutput> suffix, bool handoff = true)
    {
        var native = new TerminalOutput(12, 100, 4);
        var managed = new TerminalOutput(12, 100, 4);
        _ = managed.GetHistoryRows(); // Select the existing reducer before any input.
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
            if (native.UsesNativeCore == handoff) throw new InvalidOperationException(name + ": unexpected engine after the operation.");
            Equal(name + " operation", managed.GetRows(), native.GetRows());
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
            _ = terminal.GetHistoryRows();
            if (terminal.UsesNativeCore) throw new InvalidOperationException("AI history did not transfer ownership.");
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
            _ = terminal.GetHistoryRows();
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
