using System.Text;
using System.Text.Json.Nodes;
using TabTty.Core;

internal static partial class MacroContracts
{
    private static void CheckPostConnectionEditor()
    {
        static PostConnectionEditorAnalysis At(string marked)
        {
            int caret = marked.IndexOf('|');
            return PostConnectionEditorAnalysis.Analyze(marked.Remove(caret, 1), caret);
        }

        static string[] Labels(string marked) => At(marked).Completions.Select(item => item.Label).ToArray();

        Check(Labels("Wr|ite").SequenceEqual(["WriteLine", "WritePasswordLine"]) &&
            At("Wr|ite").ReplacementLength == 5 && Labels("var |").Length == 0 &&
            Labels("Sl|").Length == 0, "The statement completion range or embedded subset is incorrect.");
        const string scope = "var pattern = Literal(\">\"); var delay = 100; { var hidden = 2; } ";
        var prompt = At(scope + "WaitPrompt(|");
        Check(prompt.Signature is { Name: "WaitPrompt", ActiveParameter: 0 } &&
            prompt.Signature.Parameters.SequenceEqual(["WaitPattern pattern", "long timeoutMs"]) &&
            prompt.Completions.Select(item => item.Label).SequenceEqual(["Literal", "Regex", "pattern"]),
            "An unfinished wait did not offer its required signature and typed in-scope patterns.");
        var timeout = At(scope + "WaitPrompt(pattern, |");
        Check(timeout.Signature is { Name: "WaitPrompt", ActiveParameter: 1 } &&
            timeout.Completions.Select(item => item.Label).SequenceEqual(["delay"]),
            "Timeout completion exposed an out-of-scope or incompatible local.");
        Check(At("WaitAnyPrompt([prompt: Reg|], 100);").Completions.Single().Label == "Regex" &&
            At("WaitAnyPrompt([prompt: Regex(\"(a,b)\", |").Signature is { Name: "Regex", ActiveParameter: 1 },
            "Nested calls or alternatives confused the open call.");
        Check(At("WriteLine(\"x\", ma|").Completions.Single().InsertText == "markReceiveBoundary: " &&
            At("WriteLine(\"x\", ma|rkReceiveBoundary: false);").Completions.Single().InsertText == "markReceiveBoundary" &&
            Labels("WriteLine(\"x\", markReceiveBoundary: |").SequenceEqual(["WaitAnyPrompt", "WaitPrompt", "false", "true"]) &&
            Labels("WriteLine(\"x\", markReceiveBoundary: false, |").Length == 0,
            "The named option was missing, duplicated or offered in its value.");
        Check(Labels("var r = WaitPrompt(Literal(\">\"), 1); var a = r.Al|").SequenceEqual(["AlternativeName"]) &&
            Labels("var a = WaitPrompt(Literal(\">\"), 1).|").SequenceEqual(["AlternativeName"]) &&
            Labels("string x = \"\"; var a = x.|").Length == 0, "Unexpected embedded member completion.");
        Check(Labels("if (true) {} el|").SequenceEqual(["else"]) &&
            Labels("if (true) {} else |").SequenceEqual(["if"]), "The conditional continuation completion is incorrect.");
        foreach (string code in new[] { "/* Wr|", "// Wr|", "WriteLine(@\"Wr|\");", "WriteLine(\"Wr|" })
            Check(Labels(code).Length == 0, "Completions appeared inside a comment or string.");
        const string invalid = "// header\r\nif (false) { Connect(\"other.ttty\"); }";
        var rejected = PostConnectionEditorAnalysis.Analyze(invalid, 0);
        Check(rejected.Diagnostic is { Code: "PostConnectionSubset", Position: { Line: 2, Column: 14 } } &&
            invalid.Substring(rejected.DiagnosticStart, rejected.DiagnosticLength) == "Connect",
            "The embedded diagnostic did not underline the forbidden call in a dead branch.");
        var argument = At("WaitPrompt(Literal(\">\"), \"wrong\");|");
        Check(argument.Diagnostic is { Code: "TypeMismatch" } && argument.DiagnosticLength == 7 &&
            At("WaitPrompt(Literal(\">\"), 100);|").Diagnostic is null,
            "Argument diagnostic did not mark the offending expression or clear after correction.");
        Check(At("long timeout = \"oops\";|").DiagnosticStart == 15 &&
            At("if (123) {}|").DiagnosticStart == 4 &&
            At("var same = 1; var same = 2;|").DiagnosticStart == 18,
            "A diagnostic pointed at the enclosing statement instead of its invalid name or value.");
        var unfinished = At("WriteLine(|");
        Check(unfinished.DiagnosticStart == 10 && unfinished.DiagnosticLength == 0,
            "An EOF diagnostic was placed on the preceding token.");
        const string reply = "var reply = WaitPrompt(Literal(\">\"), 1); ";
        Check(At("WriteLine($\"{ }\");|").Diagnostic is not null && Labels("return |").Length == 0 &&
            Labels(reply + "if (rep|").Contains("reply") && Labels(reply + "if (true && rep|").Contains("reply") &&
            Labels(reply + "if (!(rep|").Contains("reply") && Labels(reply + "if (!rep|").Length == 0 &&
            Labels("if (WaitAny|").Contains("WaitAnyPrompt") && Labels("if (Lit|").Length == 0,
            "Invalid drafts or partially written conditions broke editor recovery.");
    }

    internal static async Task PostConnectionSshEarlyDataAsync(int port)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string path = Path.Combine(directory.Path, "ssh.ttty");
        var reference = new CredentialReference("Profile password", "password");
        var profile = ConnectionProfile.CreateSsh("Early output", "127.0.0.1", "tabtty-interop", port, reference);
        var draft = JsonNode.Parse(profile.ToEditorJson())!;
        draft["postConnectionMacro"] = new JsonObject
        {
            ["version"] = 1,
            ["source"] = """
                WaitPrompt(Literal("early "), 0);
                WritePasswordLine();
                """,
        };
        profile = profile.Edit(draft.ToJsonString());
        await profile.SaveNewAsync(path, deadline.Token);
        using var program = profile.CompilePostConnectionMacro()!;
        var host = new Host { Password = "go" };
        using var run = new MacroRun(program, [], host);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        var reviewed = await ConnectionProfile.LoadAsync(path, deadline.Token);
        await using var session = await TerminalSession.ConnectReviewedAsync(path, reviewed, run, deadline.Token, interaction,
            viewport: _ => Task.FromResult((120, 40)));
        Check(!session.AcceptsInput && host.LastCredential is null, "The post-connection macro ran during SSH startup.");
        run.Start(session);
        await run.Completion.WaitAsync(deadline.Token);
        Check(run.Status.State == MacroRunState.Completed && host.LastCredential == reference && !run.Status.PlaintextNotice,
            "SSH startup data or the captured password reference was lost: " + run.Status);
    }

    private static async Task PostConnectionSerialAsync()
    {
        foreach (var (source, line) in new (string, int)[]
        {
            ("// first line\nif (false) { Connect(\"elsewhere.ttty\"); }", 2),
            ("WaitPrompt(Literal(\">\"));", 1),
            ("#tttym 1\nWriteLine(\"test\");", 1),
            ("void Helper() {}", 1),
            ("while (false) {}", 1),
            ("var value = Macro;", 1),
            ("if (false) { WriteLine(123); }", 1),
        })
        {
            try
            {
                using var rejected = MacroProgram.ParsePostConnection("serial.ttty", source);
                throw new InvalidOperationException("The embedded subset accepted " + source);
            }
            catch (MacroSourceException error)
            { Check(error.Position.Line == line, "Embedded source coordinates included an implicit header."); }
        }

        const string body = """
            WriteLine("", markReceiveBoundary: true);
            WaitPrompt(Regex(@"serial> ?$"), 2000);
            WriteLine("enable", markReceiveBoundary: true);
            var reply = WaitAnyPrompt([password: Literal("Password:"), ready: Literal("serial#")], 2000);
            if (reply.AlternativeName == "password")
            {
                WritePasswordLine("Console access");
                WaitPrompt(Literal("serial#"), 2000);
            }
            """;
        var profile = ConnectionProfile.CreateSerial("Quiet console", new("COM254"));
        var host = new Host { Password = "fixture-secret" };
        using var program = MacroProgram.ParsePostConnection("serial.ttty", body);
        using var run = new MacroRun(program, [], host);
        await using var session = new TerminalSession(host.Wire, profile, null, 100, macro: run);
        int sends = 0;
        host.Wire.OnWrite = async () =>
        {
            string response = ++sends switch { 1 => "serial>", 2 => "\r\nPassword:", _ => "\r\nserial#" };
            await session.ProcessPayloadAsync(TerminalReceiveSource.Data, Encoding.UTF8.GetBytes(response), default);
        };
        Check(!session.AcceptsInput && sends == 0, "The COM macro sent input before readiness.");
        run.Start(session);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(run.Status.State == MacroRunState.Completed && run.Status.PlaintextNotice && session.AcceptsInput,
            "The COM macro did not complete with a password notice and return manual input: " + run.Status);
        Check(host.LastCredential == new CredentialReference("Console access", "password") &&
            host.Wire.Bytes.SequenceEqual("\renable\rfixture-secret\r"u8.ToArray()),
            "Unexpected command order or missing explicit COM password.");
    }

    private static async Task PostConnectionSudoAsync()
    {
        const string source = """
            WaitPrompt(Literal("operator$"), 2000);
            WriteLine("sudo -i", markReceiveBoundary: true);
            var reply = WaitAnyPrompt([password: Literal("[sudo] password"), ready: Literal("root#")], 2000);
            if (reply.AlternativeName == "password")
            {
                WritePasswordLine();
                WaitPrompt(Literal("root#"), 2000);
            }
            """;
        var reference = new CredentialReference("Configured password", "password");
        var profile = ConnectionProfile.CreateSsh("SSH", "localhost", "operator", password: reference);
        foreach (bool passwordRequired in new[] { true, false })
        {
            var host = new Host { Password = "fixture-secret" };
            using var program = MacroProgram.ParsePostConnection("ssh.ttty", source);
            using var run = new MacroRun(program, [], host);
            await using var session = new TerminalSession(host.Wire, profile, null, 100, macro: run);
            int sends = 0;
            host.Wire.OnWrite = () => new(session.ProcessPayloadAsync(TerminalReceiveSource.Data,
                ++sends == 1 && passwordRequired ? "\r\n[sudo] password:"u8.ToArray() : "\r\nroot#"u8.ToArray(), default));
            await session.ProcessPayloadAsync(TerminalReceiveSource.Data, "operator$"u8.ToArray(), default);
            run.Start(session);
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(run.Status.State == MacroRunState.Completed && host.LastCredential == (passwordRequired ? reference : null) &&
                host.Wire.Bytes.SequenceEqual(passwordRequired ? "sudo -i\rfixture-secret\r"u8.ToArray() : "sudo -i\r"u8.ToArray()),
                "The sudo branch sent unexpected input or resolved the wrong reference.");
        }
    }
}
