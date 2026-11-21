using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TabTty.Core;

internal static partial class MacroContracts
{
    private static async Task NamedCallsAndReceiveBoundaryAsync()
    {
        await QueuedSubmissionBoundaryAsync();
        foreach (string kind in new[] { "Line", "Prompt" })
        foreach (bool multi in new[] { false, true })
        foreach (string timeout in new[] { "", ", 0", ", 2000" })
        {
            var host = new Host { Initial = "OK\r\nprefix" };
            var data = Encoding.UTF8.GetBytes(kind == "Line" ? "OK\r\n" : "OK");
            host.Wire.OnWrite = () => new(host.Session!.ProcessPayloadAsync(TerminalReceiveSource.Data, data, default));
            string regex = """Regex("(?<=prefix)OK|^OK")""";
            string pattern = multi ? $"[missing: Literal(\"other\"), ready: {regex}]" : regex;
            string wait = $"Wait{(multi ? "Any" : "")}{kind}({pattern}{timeout})";
            using var run = new MacroRun(Parse($$"""
                Connect("profile.ttty");
                WriteLine("status", markReceiveBoundary: true);
                var reply = {{wait}};
                Dialog(reply.AlternativeName, $"{reply.Text}:{reply.Index}", DialogButtons.OK);
                """), [], host);
            try
            {
                run.Start();
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(run.Status.State == MacroRunState.Completed, $"Send boundary failed for {wait}: {run.Status}");
                var dialog = host.Dialogs.Single();
                Check(dialog.Title == (multi ? "ready" : "") && dialog.Message == "OK:6", $"Wrong response for {wait}.");
            }
            finally { await host.CloseAsync(); }
        }

        var startup = new Host { Initial = "first\r\nsecond\r\nthird\r\n", TransmitEncoding = "iso-8859-1" };
        using var ordinary = new MacroRun(Parse("""
            Connect("profile.ttty");
            WaitLine(Literal("first"), 0);
            WriteLine("one");
            WaitLine(Literal("second"), 0);
            WriteLine("two", markReceiveBoundary: false);
            try { WriteLine("あ", markReceiveBoundary: true); }
            catch (MacroError e) { WaitLine(Literal("third"), 0); Dialog("", $"{e.Code}", DialogButtons.OK); }
            """), [], startup);
        ordinary.Start(); await ordinary.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ordinary.Status.State == MacroRunState.Completed && startup.Dialogs.Single().Message == "Encoding",
            "Default, false or rejected WriteLine changed the cursor.");
        await startup.CloseAsync();

        var failed = new Host { Initial = "OK\r\n" };
        failed.Wire.FailAfterFirstWrite = true;
        using var uncertain = new MacroRun(Parse("""
            Connect("profile.ttty");
            try { WriteLine("command", markReceiveBoundary: true); } catch (MacroError e) { Write("retry"); }
            WaitLine(Literal("OK"), 0); Dialog("", "continued", DialogButtons.OK);
            """), [], failed);
        uncertain.Start(); await uncertain.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(uncertain.Status.Result == "UncertainDelivery" && failed.Dialogs.Count == 0 && failed.Wire.Bytes.SequenceEqual("c"u8.ToArray()),
            "Uncertain send continued the macro.");
        await failed.CloseAsync();

        var zero = new Host { Initial = "OK\r\n" };
        zero.Wire.ReturnZeroOnSubmission = 1;
        using var refused = new MacroRun(Parse("""
            Connect("profile.ttty");
            try { WriteLine("command", markReceiveBoundary: true); }
            catch (MacroError e) { WaitLine(Literal("OK"), 0); Dialog("", $"{e.Code}", DialogButtons.OK); }
            """), [], zero);
        refused.Start(); await refused.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(refused.Status.State == MacroRunState.Completed && zero.Dialogs.Single().Message == "OperationUnavailable" &&
            zero.Wire.Bytes.Count == 0, "A proven zero-byte submission lost the old receive cursor.");
        await zero.CloseAsync();

        var secondChunk = new Host();
        secondChunk.Wire.ReturnZeroOnSubmission = 2;
        using var partial = new MacroRun(Parse($$"""
            Connect("profile.ttty");
            try { WriteLine("{{new string('A', 1025)}}", markReceiveBoundary: true); }
            catch (MacroError e) { Write("retry"); }
            """), [], secondChunk);
        partial.Start(); await partial.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(partial.Status.Result == "UncertainDelivery" && secondChunk.Wire.Bytes.Count == 1024,
            "A zero-byte suffix concealed the submission of an earlier chunk.");
        await secondChunk.CloseAsync();

        var alternatives = new Host { Initial = "earlier later\r\nlater\r\nsame\r\nsame\r\nunfinished" };
        using var named = new MacroRun(Parse("""
            WaitPattern Candidate(List<string> log, string text) { Add(log, text); return Literal(text); }
            Connect("profile.ttty");
            List<string> log = [];
            var a = WaitAnyLine([Late: Candidate(log, "later"), late: Candidate(log, "earlier")]);
            var b = WaitAnyLine([absent: Literal("absent"), Late: Literal("later"), late: Literal("earlier")]);
            WaitLine(Literal("later"));
            var c = WaitAnyLine([one: Literal("same"), two: Literal("same")]);
            var d = WaitAnyLine([two: Literal("same"), one: Literal("same"),]);
            try { WaitAnyLine([one: Literal("unfinished")], 0); } catch (MacroError e) {
                var prompt = WaitAnyPrompt([p: Literal("finished")], 0);
                Dialog(Join(",", log), $"{a.AlternativeName}:{b.AlternativeName}:{c.AlternativeName}:{d.AlternativeName}:{e.Code}:{prompt.Index}", DialogButtons.OK);
            }
            """), [], alternatives);
        named.Start(); await named.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(named.Status.State == MacroRunState.Completed &&
            alternatives.Dialogs.Single() is { Title: "later,earlier", Message: "late:Late:one:two:WaitTimeout:2" },
            "Named alternatives changed evaluation order, winning labels or line eligibility: " + named.Status);
        await alternatives.CloseAsync();

        foreach (var (source, code, at) in new (string, string, string)[]
        {
            ("Connect();", "CallArguments", "Connect"),
            ("WriteLine();", "CallArguments", "WriteLine"),
            ("WriteLine(\"x\", markReceiveBoundary: \"yes\");", "TypeMismatch", "\"yes\""),
            ("WriteLine(\"x\", markReceiveBoundary: (\"yes\" + \"no\"));", "TypeMismatch", "(\"yes\""),
            ("Connect(\"p\", logDirectry: \"x\");", "UnknownOption", "logDirectry"),
            ("Connect(\"p\", logDirectory: \"x\", logDirectory: \"y\");", "DuplicateOption", "logDirectory"),
            ("Connect(\"p\", allowLogOverwrite: true);", "UnknownOption", "allowLogOverwrite"),
            ("Connect(\"p\", logCollision: true);", "TypeMismatch", "true"),
            ("Connect(\"p\", logCollision: \"error\", logCollision: \"append\");", "DuplicateOption", "logCollision"),
            ("Connect(\"p\", \"x\");", "NamedOptionExpected", "\"x\""),
            ("WaitAnyLine([ok: Literal(\"x\"), ok: Literal(\"y\")]);", "DuplicateAlternative", "ok"),
            ("WaitAnyPrompt([Literal(\"x\")]);", "AlternativeNameExpected", "Literal"),
            ("WaitAnyPrompt([if: Literal(\"x\")]);", "AlternativeNameExpected", "if"),
            ("WaitAnyPrompt([]);", "AlternativeNameExpected", "]"),
        })
        {
            string text = "#tttym 1\n" + source;
            var position = new MacroPosition(2, source.LastIndexOf(at, StringComparison.Ordinal) + 1);
            try { using var invalid = Parse(source); throw new InvalidOperationException("Accepted " + source); }
            catch (MacroSourceException e) { Check(e.Code == code && e.Position == position, "Wrong launch diagnostic in " + source); }
            var editor = new MacroEditorSymbols();
            MacroProgram.AnalyzeForEditor("test.tttym", text, new Dictionary<string, string>(), editor, null);
            Check(editor.Error?.Code == code && editor.Error.Position == position, "Wrong editor diagnostic in " + source);
        }
    }

    private static async Task QueuedSubmissionBoundaryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wire = new Wire();
        var transport = new SessionTransport(wire, ConnectionProfile.CreateTelnet("Queued", "localhost"), (80, 24),
            (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.OnWrite = () => new(release.Task.WaitAsync(deadline.Token));
        try
        {
            var first = transport.WriteAsync("busy"u8.ToArray(), deadline.Token);
            int received = 0, sampled = -1;
            var second = transport.SubmitPreparedAsync("next"u8.ToArray(), deadline.Token,
                onFirstSubmission: () => sampled = received);
            Check(!second.IsCompleted && sampled == -1, "Boundary was sampled while the writer was busy.");
            received++;
            using var cancelled = new CancellationTokenSource();
            var skipped = transport.SubmitPreparedAsync("skip"u8.ToArray(), cancelled.Token,
                onFirstSubmission: () => throw new InvalidOperationException("A cancelled waiter was submitted."));
            cancelled.Cancel();
            Check((await skipped).Status == TransportSubmission.NotIssued, "Cancelled waiter lost its zero-byte result.");
            release.SetResult();
            await first;
            Check((await second).Status == TransportSubmission.Submitted && sampled == received,
                "Receive data admitted while the send was queued became eligible.");
        }
        finally { release.TrySetResult(); await transport.DisconnectAsync(); }
    }

    private static async Task NamedRecordingCollisionsAsync()
    {
        foreach (string policy in new[] { "error", "overwrite", "append" })
        {
            string option = $", logCollision: \"{policy}\"";
            await CheckMacroRecordingAsync("overwrite", option, policy, existing: false);
            await CheckMacroRecordingAsync("error", option, policy, existing: true);
            await CheckMacroRecordingAsync("append", option, policy, existing: true, serial: true);
            await CheckMacroRecordingAsync(policy, "", policy, existing: true);
        }
        await CheckMacroRecordingAsync("append", ", logDirectory: \"  \", logFilenameTemplate: \"\"", "append", existing: true);
        await CheckMacroRecordingAsync("append", ", logFilenameTemplate: \"fixed\"", "error", existing: true);
        await CheckMacroRecordingAsync("append", ", logDirectory: \"logs\"", "error", existing: true, directoryOverride: true);
        await CheckMacroRecordingAsync("overwrite", ", logDirectory: \"logs\", logCollision: mode", "append",
            existing: true, directoryOverride: true, resume: true);

        foreach (string invalid in new[] { "unknown", "", " ", "Append", " append", "append " })
        foreach (bool computed in new[] { false, true })
        {
            var host = new Host();
            string value = computed ? "choice" : $"\"{invalid}\"";
            using var run = new MacroRun(Parse($$"""
                string choice = "{{invalid}}";
                try { Connect("unopened.ttty", logCollision: {{value}}); }
                catch (MacroError e) { Dialog("", $"{e.Code}", DialogButtons.OK); }
                """), [], host);
            run.Start(); await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(run.Status.State == MacroRunState.Completed && host.Dialogs.Single().Message == "InvalidArgument" &&
                host.ConnectedPath is null, $"Invalid collision policy reached profile access ({invalid}).");
        }

        var order = new Host();
        using var ordered = new MacroRun(Parse("""
            string Name(string text) { Dialog("", text, DialogButtons.OK); return text; }
            Connect(Name("profile.ttty"), logFilenameTemplate: Name("template"), logCollision: Name("append"), logDirectory: Name("directory"));
            """), [], order);
        ordered.Start(); await ordered.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ordered.Status.State == MacroRunState.Completed &&
            order.Recording is { Template: "template", Directory: "directory", Collision: LogCollisionPolicy.Append } &&
            order.Dialogs.Select(d => d.Message).SequenceEqual(["profile.ttty", "template", "append", "directory"]),
            "Named option expressions were evaluated out of source order.");
        await order.CloseAsync();
    }

    private static async Task CheckMacroRecordingAsync(string profilePolicy, string options, string effectivePolicy,
        bool existing, bool serial = false, bool directoryOverride = false, bool resume = false)
    {
        using var temporary = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        listener.Start();
        string root = temporary.Path;
        string work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        string logs = Directory.CreateDirectory(Path.Combine(directoryOverride ? work : root, "logs")).FullName;
        string profilePath = Path.Combine(root, "profile.ttty");
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = ConnectionProfile.CreateTelnet("Profile", "127.0.0.1", port, recordRaw: true, recordText: true);
        var config = JsonNode.Parse(profile.ToEditorJson())!;
        config["logging"]!["directory"] = "logs";
        config["logging"]!["filenameTemplate"] = serial ? "run_<serial:7,4>" : "fixed";
        config["logging"]!["collision"] = profilePolicy;
        await profile.Edit(config.ToJsonString()).SaveNewAsync(profilePath);
        string prior = Path.Combine(logs, (serial ? "run_0007" : "fixed") + ".raw.tttylog");
        byte[] original = [0x80, 0, 0xff];
        if (existing) await File.WriteAllBytesAsync(prior, original);

        using var program = MacroProgram.Parse(Path.Combine(root, "test.tttym"), $$"""
            #tttym 1
            SetBaseDirectory("work");
            string mode = "append";
            Connect("../profile.ttty"{{options}});
            """);
        var host = new Host { OpenRealProfile = true };
        using var run = new MacroRun(program, [], host);
        try
        {
            run.Start(); await run.Completion.WaitAsync(deadline.Token);
            if (existing && !serial && effectivePolicy == "error")
            {
                Check(run.Status.Result == "ConnectionFailed" && !listener.Pending() &&
                    (await File.ReadAllBytesAsync(prior)).SequenceEqual(original),
                    "Recording collision changed an existing file or started the transport: " + options);
                return;
            }

            Check(run.Status.State == MacroRunState.Completed, $"Macro connection failed ({profilePolicy}, {options}): {run.Status}");
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var session = host.Session!;
            await peer.GetStream().WriteAsync("RX\r\n"u8.ToArray(), deadline.Token);
            await session.ReadOutputAsync(deadline.Token);
            if (resume)
            {
                await session.PauseRecordingAsync(deadline.Token);
                await peer.GetStream().WriteAsync("paused\r\n"u8.ToArray(), deadline.Token);
                await session.ReadOutputAsync(deadline.Token);
                await session.ResumeRecordingAsync(deadline.Token);
                await peer.GetStream().WriteAsync("again\r\n"u8.ToArray(), deadline.Token);
            }
            peer.Client.Shutdown(SocketShutdown.Send);
            while (await session.ReadOutputAsync(deadline.Token) is not null) { }
            string target = Path.Combine(logs, serial ? "run_0008" : "fixed");
            byte[] payload = Encoding.UTF8.GetBytes(resume ? "RX\r\nagain\r\n" : "RX\r\n");
            byte[] expected = !serial && existing && effectivePolicy == "append" ? [.. original, .. payload] : payload;
            Check((await File.ReadAllBytesAsync(target + ".raw.tttylog")).SequenceEqual(expected) &&
                (await File.ReadAllBytesAsync(target + ".log")).SequenceEqual(payload) &&
                !File.Exists(target + ".term.tttylog"), "Incorrect recording bytes or formats: " + options);
            if (serial) Check((await File.ReadAllBytesAsync(prior)).SequenceEqual(original), "A serial collision changed an earlier log.");
        }
        finally { await host.CloseAsync(); }
    }
}
