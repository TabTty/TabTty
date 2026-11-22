using System.Text;
using TabTty.Core;

internal static class NativeTerminalContracts
{
    internal static void Run()
    {
        RejectedOperationPublishesNothingAndOwnedSnapshotSurvives();
        RefusedCreationDoesNotStartAManagedTerminal();
        RefusedSnapshotKeepsTheNativeTerminal();
        FailedPairedResizeLeavesBothModelsUntouched();
        HeightOnlyResizeKeepsRetainedRowIdentities();
        LegacyNativeAdmissionDoesNotConsumeTheLiveDecoder();
        FragmentedReceiveSurvivesResizeAndEof();
        CommonShellControlsKeepTheNativePath();
        NormalScreenEditingPreservesSourceAndCursorState();
        HeldSnapshotsAndSearchAnchorsSurviveNativeMutation();
        PlainAsciiSnapshotsPreserveOwnedCellFacts();
        ObserversSeeOnlyNewInput();
        LegacyEncodingsUseNativeModels();
    }

    private static void PlainAsciiSnapshotsPreserveOwnedCellFacts()
    {
        var terminal = new TerminalOutput(8, screenRows: 4);
        Feed(terminal, "A B\r\n\u001b[7mA B\u001b[0m\r\n\u001b[2GQ");
        TerminalSnapshot snapshot = terminal.GetRows();
        terminal.ReleaseNativeState();
        var plain = snapshot.GetCells(0).Span;
        var inverse = snapshot.GetCells(1).Span;
        var padded = snapshot.GetCells(2).Span;
        if (snapshot[0] != "A B" || plain.Length != 3 ||
            plain[0] != new TerminalCell(0, 1, 0, "A", default) ||
            plain[1] != new TerminalCell(1, 1, 1, " ", default) ||
            plain[2] != new TerminalCell(2, 1, 2, "B", default) ||
            inverse[1].Style.Flags != TerminalStyleFlags.Inverse || inverse[1].IsErased ||
            !padded[0].IsErased || padded[1].Column != 1 || padded[1].Text != "Q")
            throw new InvalidOperationException("The owned ASCII snapshot lost its cells, rendition, or erased positions.");
    }

    private static void RefusedCreationDoesNotStartAManagedTerminal()
    {
        var quota = new ResourceQuota(64 * 1024, collectOnPressure: false);
        try
        {
            var terminal = new TerminalOutput(20, 100, 4, "utf-8", false, 1, quota);
            terminal.ReleaseNativeState();
            throw new InvalidOperationException("The application started a UTF-8 terminal without native admission.");
        }
        catch (ResourceLimitException) { }
        if (quota.Used != 0)
            throw new InvalidOperationException("Native creation failure started a managed terminal or retained memory.");
    }

    private static void RefusedSnapshotKeepsTheNativeTerminal()
    {
        const int limit = 4 * 1024 * 1024;
        var quota = new ResourceQuota(limit, collectOnPressure: false);
        var terminal = new TerminalOutput(20, 100, 4, "utf-8", false, 1, quota);
        try
        {
            Feed(terminal, "retained");
            using (quota.Reserve(limit - quota.Used))
            {
                try
                {
                    _ = terminal.GetRows();
                    throw new InvalidOperationException("A native snapshot exceeded its ownership allowance.");
                }
                catch (ResourceLimitException) { }
            }
            Feed(terminal, " text");
            if (!terminal.UsesNativeCore || terminal.GetRows()[0] != "retained text")
                throw new InvalidOperationException("A refused snapshot changed or handed off the native terminal.");
        }
        finally { terminal.ReleaseNativeState(); }
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

    private static void HeldSnapshotsAndSearchAnchorsSurviveNativeMutation()
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
            var observed = terminal.ReadAiScreenState();
            using (terminal.GetHistoryRows()) { }
            if (!terminal.UsesNativeCore) throw new InvalidOperationException("AI history transferred ownership.");
            if (terminal.ReadAiScreenState() != observed)
                throw new InvalidOperationException("An unchanged screen published a new revision during history observation.");
            var after = terminal.GetRows();
            if (after.MapMatchFrom(before, match) is null)
                throw new InvalidOperationException("A retained search anchor was lost after observation.");
            if (!before.SequenceEqual(frozenText) ||
                !Enumerable.Range(0, before.Count).Select(before.DisplayIdentity).SequenceEqual(identities))
                throw new InvalidOperationException("An immutable snapshot changed after native destruction.");
            foreach (int row in Enumerable.Range(0, before.Count))
                foreach (var cell in before.GetCells(row).Span)
                    if (cell.Text is null) throw new InvalidOperationException("Lazy cells lost their owned storage.");
            Feed(terminal, "\u001b[2Jchanged");
            if (terminal.ReadAiScreenState() == observed)
                throw new InvalidOperationException("A real screen change kept the transferred observation.");
            if (!before.SequenceEqual(frozenText))
                throw new InvalidOperationException("A held snapshot changed after native mutation.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void ObserversSeeOnlyNewInput()
    {
        var terminal = new TerminalOutput(12, 100, 4);
        try
        {
            Feed(terminal, "prior");
            var observed = new StringBuilder();
            terminal.MacroCharacter = (_, rune, _) => observed.Append(rune.ToString());
            Feed(terminal, "new");
            if (!terminal.UsesNativeCore || observed.ToString() != "new")
                throw new InvalidOperationException("Receive observation replayed old characters or omitted new input.");
            if (terminal.GetRows()[0] != "priornew")
                throw new InvalidOperationException("Receive observation lost terminal text.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void LegacyEncodingsUseNativeModels()
    {
        foreach (string id in TerminalEncodings.Identifiers)
        {
            var terminal = new TerminalOutput(20, 100, 4, receiveEncoding: id);
            try
            {
                terminal.Receive(TerminalReceiveSource.Data, "prompt\u001b[6n"u8);
                terminal.CompleteReceive();
                if (!terminal.UsesNativeCore || terminal.GetRows()[0] != "prompt" ||
                    !terminal.TakeReplies().Single().AsSpan().SequenceEqual("\u001b[1;7R"u8))
                    throw new InvalidOperationException("Native receive or VT reply failed for " + id + ".");
            }
            finally { terminal.ReleaseNativeState(); }
        }
    }

    private static void FragmentedReceiveSurvivesResizeAndEof()
    {
        var terminal = new TerminalOutput(20, 100, 4);
        try
        {
            terminal.Receive(TerminalReceiveSource.Data, [0xE3]);
            terminal.Receive(TerminalReceiveSource.StandardError, [0xF0, 0x9F]);
            terminal.Resize(16, 5);
            terminal.Receive(TerminalReceiveSource.Data, [0x81, 0x82]);
            terminal.Receive(TerminalReceiveSource.StandardError, [0x98, 0x80]);
            terminal.Receive(TerminalReceiveSource.Data, [0xF0, 0x9F]);
            terminal.CompleteReceive();
            if (terminal.GetRows()[0] != "あ😀�")
                throw new InvalidOperationException("Resize or EOF changed independent source decoding.");
        }
        finally { terminal.ReleaseNativeState(); }
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
            if (!terminal.BracketedPaste)
                throw new InvalidOperationException("Bracketed-paste mode was not restored.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static void Feed(TerminalOutput terminal, string text) =>
        terminal.Receive(TerminalReceiveSource.Data, Encoding.UTF8.GetBytes(text));

}
