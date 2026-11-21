using TabTty.Core;

internal static partial class MacroContracts
{
    private static MacroProgram ParseForRunAndEditor(string body)
    {
        var editor = new MacroEditorSymbols();
        MacroProgram.AnalyzeForEditor("test.tttym", "#tttym 1\n" + body,
            new Dictionary<string, string>(), editor, null);
        Check(editor.Error is null, $"Editor reported {editor.Error?.Code} at {editor.Error?.Position}.");
        return Parse(body);
    }

    private static void CheckRejectedByBoth(string body, string code, MacroPosition? at = null, int length = 1)
    {
        try
        {
            using var parsed = Parse(body);
            throw new InvalidOperationException("Launch accepted: " + body);
        }
        catch (MacroSourceException error) { CheckError(error); }
        var editor = new MacroEditorSymbols();
        MacroProgram.AnalyzeForEditor("test.tttym", "#tttym 1\n" + body,
            new Dictionary<string, string>(), editor, null);
        CheckError(editor.Error);

        void CheckError(MacroSourceException? error)
        {
            Check(error?.Code == code && (at is null || error.Position == at && error.Length == length),
                $"Expected {code} at {at}; observed {error?.Code} at {error?.Position} for {body}.");
        }
    }

    private static async Task RethrowAsync()
    {
        CheckRejectedByBoth("if (false) { throw; }", "RethrowOutsideCatch", new(2, 14));
        CheckRejectedByBoth("void Bad() { throw; } try {} catch (MacroError e) { Bad(); }",
            "RethrowOutsideCatch", new(2, 14));
        CheckRejectedByBoth("try {} catch (MacroError e) {} throw;", "RethrowOutsideCatch");
        CheckRejectedByBoth("try {} catch (MacroError e) { throw e; }", "ExpectedToken");
        const string body = """
            bool IsReady() {
                try { WaitLine(Literal("ready"), 0); return true; }
                catch (MacroError e) {
                    if (e.Code == ErrorCode.WaitTimeout) { return false; }
                    try {
                        try { var n = 1 / 0; } catch (MacroError inner) { throw; }
                    } catch (MacroError observed) {
                        Dialog("", $"{observed.Code}", DialogButtons.OK);
                        e = observed;
                    }
                    throw;
                }
            }
            Connect("test.ttty");
            try {
                var ok = IsReady();
                Dialog("", $"{ok}", DialogButtons.OK);
            } catch (MacroError outer) {
                Dialog("", $"{outer.Code}:{outer.Message}:{outer.Line}:{outer.Column}", DialogButtons.OK);
                throw;
            }
            """;
        foreach (bool endOfInput in new[] { false, true })
        {
            var host = new Host { WaitAtConnect = true };
            using var run = new MacroRun(ParseForRunAndEditor(body), [], host);
            run.Start();
            await host.ConnectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (endOfInput) run.ReceiveEof();
            host.FinishConnect.SetResult();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (endOfInput)
            {
                Check(run.Status.State == MacroRunState.Failed && run.Status.Result == "EndOfInput" &&
                    run.Status.Position == new MacroPosition(3, 11) && run.Status.DisplayDetail == "EndOfInput",
                    "Rethrow lost the original wait failure: " + run.Status);
                Check(host.Dialogs.Select(d => d.Message).SequenceEqual(["DivideByZero", "EndOfInput:EndOfInput:3:11"]),
                    "Nested rethrow or restoration of the enclosing handler failed.");
            }
            else Check(run.Status.State == MacroRunState.Completed && host.Dialogs.Single().Message == "false",
                "Timeout should take the returning path of IsReady.");
            await host.CloseAsync();
        }
        var stoppedHost = new Host { WaitAtConnect = true };
        using var stopped = new MacroRun(Parse("""
            try { Connect("test.ttty"); }
            catch (MacroError e) { Dialog("", "caught Stop", DialogButtons.OK); throw; }
            """), [], stoppedHost);
        stopped.Start();
        await stoppedHost.ConnectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopped.Stop();
        await stopped.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stopped.Status.State == MacroRunState.Stopped && stoppedHost.Dialogs.Count == 0,
            "Stop entered a macro catch handler.");
        await stoppedHost.CloseAsync();
    }

    private static async Task IndexedAssignmentAsync()
    {
        foreach (var (bad, code) in new[] {
            ("""void Bad() { var a = ["text"]; a[0][0] = "x"; }""", "ReadOnly"),
            ("""void Bad() { var a = [1]; Clone(a)[0] = 0; }""", "AssignmentTarget"),
            ("""void Bad(WaitMatch m) { m.Groups[0].Captures[0] = "x"; }""", "AssignmentTarget"),
            ("""void Bad() { var a = [[0]]; a[0][0] = "x"; }""", "TypeMismatch")
        })
        {
            CheckRejectedByBoth(bad, code);
        }
        var host = new Host { Initial = "ready\r\n" };
        string[] args = ["original"];
        using var run = new MacroRun(ParseForRunAndEditor("""
            long Slot(List<string> trace, string label, long index) { Add(trace, label); return index; }
            long Change(List<long> row, List<string> trace) { Add(trace, "rhs"); row[1] = 50; return 3; }
            long Shrink(List<long> row) { RemoveAt(row, 1); return 99; }
            List<List<long>> rows = [[1, 2]];
            List<string> trace = [];
            var row = rows[0];
            (rows[0][1]) = 4;
            rows[Slot(trace, "outer", 0)][Slot(trace, "inner", 1)] += Change(row, trace);
            try { rows[Slot(trace, "bad", 1)][Slot(trace, "unexpected", 0)] = Change(row, trace); }
            catch (MacroError e) {}
            try { rows[0][Slot(trace, "bad-inner", 2)] = Change(row, trace); } catch (MacroError e) {}
            Dialog("", $"{row[1]}:{Join(",", trace)}", DialogButtons.OK);
            try { rows[0][1] = Shrink(row); } catch (MacroError e) {
                Dialog("", $"{e.Code}:{e.Line}:{e.Column}:{Count(row)}", DialogButtons.OK);
            }
            Macro.Args[0] = "changed";
            Connect("test.ttty");
            var matched = WaitLine(Regex("(ready)"), 0);
            var copy = matched.Groups[1].Captures;
            copy[0] = Macro.Args[0];
            Dialog("", $"{copy[0]}:{matched.Groups[1].Captures[0]}", DialogButtons.OK);
            row[0] = 9223372036854775807;
            try { rows[0][0]++; } catch (MacroError e) {
                Dialog("", $"{e.Code}:{row[0]}", DialogButtons.OK);
            }
            """), args, host);
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(run.Status.State == MacroRunState.Completed && args[0] == "original", "Nested list update failed: " + run.Status);
        Check(host.Dialogs.Select(d => d.Message).SequenceEqual([
            "7:outer,inner,rhs,bad,bad-inner", "IndexOutOfRange:14:14:1", "changed:ready",
            "NumericOverflow:9223372036854775807"
        ]), "Unexpected indexed write effects: " + string.Join(" / ", host.Dialogs.Select(d => d.Message)));
        await host.CloseAsync();
    }

    private static async Task AccessPositionsAsync()
    {
        CheckRejectedByBoth("var x = Macro\n    .Args\n    .Wrong;", "UnknownMember", new(4, 6), 5);
        const string unicode = "var x = Macro /*😀*/ . Wrong;";
        CheckRejectedByBoth(unicode, "UnknownMember", new(2, unicode.IndexOf("Wrong", StringComparison.Ordinal) + 1), 5);
        foreach (var (body, code, at) in new (string, string, MacroPosition)[] {
            ("var m = Dialog(\"\", \"\", DialogButtons.OK); var x = m\n .Text;", "NoTextValue", new(4, 3)),
            ("var rows = [1]; var x = rows\n [1 / 0];", "DivideByZero", new(4, 5)),
            ("var rows = [1]; rows\n [1 / 0] = 4;", "DivideByZero", new(4, 5)),
            ("var rows = [1]; var x = rows\n [1 / 1];", "IndexOutOfRange", new(4, 2)),
            ("var rows = [1]; rows\n [1 / 1] = 4;", "IndexOutOfRange", new(4, 2)),
            ("var rows = [[1]]; rows\n [1]\n [0] = 3;", "IndexOutOfRange", new(4, 2)),
            ("var rows = [[1]]; rows\n [0]\n [1] = 3;", "IndexOutOfRange", new(5, 2)),
            ("var m = Dialog(\"\", \"\", DialogButtons.OK); var x = m.Text[0];", "NoTextValue", new(3, 53))
        })
        {
            var host = new Host();
            using var run = new MacroRun(Parse("try {\n" + body +
                "\n} catch (MacroError e) { Dialog(\"\", $\"{e.Code}:{e.Line}:{e.Column}\", DialogButtons.OK); throw; }"), [], host);
            run.Start();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(run.Status.Result == code && run.Status.Position == at &&
                host.Dialogs.Last().Message == $"{code}:{at.Line}:{at.Column}",
                $"Expected {code} at {at} for {body}; got {run.Status}.");
        }
    }
}
