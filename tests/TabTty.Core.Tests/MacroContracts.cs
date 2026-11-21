using System.Net;
using System.Net.Sockets;
using System.Text;
using TabTty.Core;

internal static partial class MacroContracts
{
    internal static async Task RunAsync()
    {
        RejectsBeforeEffects();
        Console.WriteLine("PASS MacroRejectsInvalidUnusedCodeBeforeEffects");
        await FilePathPartsAsync();
        Console.WriteLine("PASS MacroFilePathParts");
        await SourceEncodingsAsync();
        Console.WriteLine("PASS MacroSourceEncodingsAndSnapshot");
        await SharedConstantsLanguageAsync();
        Console.WriteLine("PASS SharedConstantsLanguage");
        await MacroConstantsLoadTests.RunAsync();
        Console.WriteLine("PASS SharedConstantsLoadAndBudgets");
        await LanguageAsync();
        Console.WriteLine("PASS MacroFixedTypesControlFlowListsAndInvariantValues");
        await ReceiveAsync();
        Console.WriteLine("PASS MacroReceivesOriginalLinesAndPromptSubstrings");
        await CancellationAsync();
        Console.WriteLine("PASS MacroPauseResumeAndStopComputation");
        await ReceiveHoldsAsync();
        Console.WriteLine("PASS MacroHistoryLossAndIndependentHolds");
        await DeadlineAndEofAsync();
        Console.WriteLine("PASS MacroDeadlineCutoffAndRetainedOutputAtEof");
        await PasswordAndTransmissionAsync();
        Console.WriteLine("PASS MacroPasswordReloadAndPartialTransmission");
        await RecordingRetryAsync();
        Console.WriteLine("PASS MacroRecordingRetryBeforeTransport");
        await PausedConnectionAndAlternateScreenAsync();
        Console.WriteLine("PASS MacroPausedConnectionAndAlternateScreen");
        await PathAndSendPreflightAsync();
        Console.WriteLine("PASS MacroBaseDirectoryAndStrictSendPreflight");
    }

    internal static async Task SshEarlyDataAsync(int port)
    {
        using var directory = new TemporaryDirectory();
        string profilePath = Path.Combine(directory.Path, "ssh.ttty");
        var configured = new CredentialReference("profile password", "password");
        await ConnectionProfile.CreateSsh("SSH", "127.0.0.1", "tabtty-interop", port, configured, recordRaw: true)
            .SaveNewAsync(profilePath);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        var host = new Host { OpenRealProfile = true, Ssh = interaction, Password = "go" };
        var program = MacroProgram.Parse(Path.Combine(directory.Path, "ssh-test.tttym"), """
            #tttym 1
            Connect("ssh.ttty");
            WaitPrompt(Literal("early "), 0);
            WritePassword();
            """);
        using var run = new MacroRun(program, [], host);
        run.Start();
        try
        {
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Check(run.Status.State == MacroRunState.Completed, "SSH startup text did not reach the macro: " + run.Status);
            Check(host.Session is { CanTransfer: true }, "Macro completion closed the SSH session.");
            Check(host.LastCredential == configured, "The password operation did not resolve the configured profile reference.");
            string raw = Directory.GetFiles(Path.Combine(directory.Path, "Logs"), "*.raw.tttylog").Single();
            await host.CloseAsync();
            Check((await File.ReadAllBytesAsync(raw)).AsSpan().StartsWith("early "u8),
                "The macro observer prevented recording of early SSH data.");
        }
        finally { await host.CloseAsync(); }
    }

    private static void RejectsBeforeEffects()
    {
        foreach (string source in new[]
        {
            "var x = 1; x = \"done\";", "var x = 1; x = 1.0;", "if (false) { BadApi(); }",
            "void Recurse() { Recurse(); }", "var x = [];", "var x = [1, \"x\"];",
            "long Missing() { while (true) { return 1; } }", "var x = 1; void Bad() { Write(x); }",
            "var x = (long)\"1\";", "var x = 1; { var x = 2; }", "System.Diagnostics.Process.Start(\"x\");",
            "if (false) { var extension = Macro.Extension; }",
        })
        {
            try { Parse(source); throw new Exception("Invalid source was accepted."); }
            catch (MacroSourceException) { }
        }
        try { MacroProgram.Parse("missing.tttym", "Write(\"x\");"); throw new Exception("Missing header accepted."); }
        catch (MacroSourceException) { }
        try { Parse("var x = " + string.Join('+', Enumerable.Repeat("1", 200)) + ";"); throw new Exception("Deep expression accepted."); }
        catch (MacroSourceException) { }
    }

    private static async Task SourceEncodingsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TabTty-macro-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.tttym");
        const string source = "#tttym 1\r\nDialog(\"\", \"日本語\", DialogButtons.OK, false, \"\");";
        try
        {
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
                new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
                new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
            {
                await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(source)]);
                var program = await MacroProgram.LoadAsync(path);
                await File.WriteAllTextAsync(path, "invalid subsequent edit");
                var host = new Host();
                using var run = new MacroRun(program, [], host); run.Start();
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(run.Status.State == MacroRunState.Completed && host.Dialogs.Single().Message == "日本語", "Source snapshot changed.");
            }
            await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0x00]);
            try { await MacroProgram.LoadAsync(path); throw new Exception("Malformed UTF-16 accepted."); }
            catch (MacroSourceException) { }
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static async Task LanguageAsync()
    {
        var host = new Host();
        using var run = new MacroRun(Parse("""
            long Twice(long n) { return n * 2; }
            var values = [1, 2, 3];
            var sum = 0;
            foreach (var value in values) { sum += Twice(value); }
            for (var i = 0; i < 4; i++) { if (i == 1) { continue; } sum += i; }
            var count = 0; while (count < 2) { count++; }
            var alias = values; Add(alias, 4);
            var copy = Clone(values); Clear(copy);
            var errors = "";
            try { var bad = 1 / 0; } catch (MacroError e) { errors += $"{e.Code}"; }
            try { foreach (var v in values) { values[0] = 7; } } catch (MacroError e) { errors += $":{e.Code}"; }
            try { var bad = Regex("["); } catch (MacroError e) { errors += $":{e.Code}"; }
            try { var bad = (long)9223372036854775808.0; } catch (MacroError e) { errors += $":{e.Code}"; }
            var exact = 9007199254740993 == 9007199254740992;
            var rounded = 9007199254740993 == 9007199254740992.0;
            var minimum = -9223372036854775808;
            var remainder = minimum % -1;
            var escaped = $@"{{{sum}}}:C:\ops";
            Dialog("", $"{sum},{count},{Count(values)},{Count(copy)},{exact},{rounded},{remainder},{escaped},{errors}", DialogButtons.OK, false, "");
            """), [], host);
        run.Start(); await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(run.Status.State == MacroRunState.Completed, "Language run failed: " + run.Status);
        Check(host.Dialogs.Single().Message == @"17,2,4,0,false,true,0,{17}:C:\ops,DivideByZero:CollectionModified:InvalidPattern:NumericOverflow", "Language result differs: " + host.Dialogs.Single().Message);
    }

    private static async Task ReceiveAsync()
    {
        var host = new Host { Initial = "\u001b[31mabc\bdef\tOK\r\nnext> " };
        using var run = new MacroRun(Parse("""
            Connect("profile.ttty");
            var a = WaitLine(Literal("abc"));
            try { var no = WaitLine(Regex("^def"), 0); } catch (MacroError e) { Dialog("", $"{e.Code}", DialogButtons.OK, false, ""); }
            var b = WaitLine(Regex("(?<=abc)def"));
            var c = WaitPrompt(Literal("next>"));
            WriteLine($"{a.Text},{b.Text},{c.Text}");
            """), [], host);
        run.Start(); await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(run.Status.State == MacroRunState.Completed, "Receive run failed: " + run.Status);
        Check(host.Dialogs.Single().Message == "WaitTimeout", "Anchors were redefined.");
        Check(Encoding.UTF8.GetString(host.Wire.Bytes.ToArray()) == "abc,def,next>\r\n", "Unexpected wire payload.");
        await host.CloseAsync();
    }

    private static async Task CancellationAsync()
    {
        var host = new Host();
        using var run = new MacroRun(Parse("var n = 0; while (true) { n++; }"), [], host);
        run.Start(); run.Pause();
        await Task.Delay(30);
        Check(run.Status.State == MacroRunState.Paused, "Computation did not pause.");
        run.Resume(); await Task.Delay(10); run.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(run.Status.State == MacroRunState.Stopped, "Stop did not terminate computation.");
    }

    private static async Task ReceiveHoldsAsync()
    {
        var host = new Host();
        using var run = new MacroRun(Parse("""
            Connect("profile.ttty");
            var text = WaitPrompt(Literal("ready"));
            Write(text.Text);
            """), [], host);
        run.Start();
        await UntilAsync(() => run.Status.Operation == "Wait");
        run.Pause();
        run.RecordingFailed();
        // Small completed lines fill the retained record count without growing the terminal input queue.
        for (int i = 0; i < 7; i++)
        {
            byte[] chunk = new byte[8192];
            Array.Fill(chunk, (byte)'\n');
            await host.Session!.ProcessPayloadAsync(TerminalReceiveSource.Data, chunk, default);
        }
        Check(run.Status.Holds.HasFlag(MacroHold.HistoryLoss), "Receive history did not announce the loss.");
        run.ContinueAfterHistoryLoss();
        Check(run.Status.Holds == (MacroHold.UserPause | MacroHold.RecordingFailure), "Continue cleared another hold.");
        run.Resume();
        Check(run.Status.Holds == MacroHold.RecordingFailure, "Resume bypassed a recording failure.");
        run.RecordingRecovered();
        Check(run.Status.Holds == MacroHold.RecordingReady, "Recording Retry resumed the macro.");
        await host.Session!.ProcessPayloadAsync(TerminalReceiveSource.Data, "ready"u8.ToArray(), default);
        run.ContinueAfterHistoryLoss();
        run.Resume();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.Wire.Bytes.SequenceEqual("ready"u8.ToArray()), "Retained prompt did not resume the pending wait.");
        await host.CloseAsync();
    }

    private static async Task DeadlineAndEofAsync()
    {
        var clock = new TestClock();
        var host = new Host();
        using var run = new MacroRun(Parse("""
            Connect("profile.ttty");
            try { WaitPrompt(Literal("late"), 50); }
            catch (MacroError e) { Write($"{e.Code},"); }
            var final = WaitPrompt(Literal("late"));
            Write(final.Text);
            try { WaitLine(Literal("unknown")); }
            catch (MacroError e) { Write($",{e.Code}"); }
            """), [], host, clock);
        run.Start();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(60);
        await host.Session!.ProcessPayloadAsync(TerminalReceiveSource.Data, "late"u8.ToArray(), default);
        await host.Session.ProcessOutputAsync();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        string transcript = Encoding.UTF8.GetString(host.Wire.Bytes.ToArray());
        Check(transcript == "WaitTimeout,late,EndOfInput",
            $"Late output rescued an expired wait or EOF lost a final prompt. Observed {transcript}, {run.Status}.");
        await host.CloseAsync();
    }

    private static async Task PasswordAndTransmissionAsync()
    {
        var host = new Host();
        using var run = new MacroRun(Parse("""
            Connect("profile.ttty");
            var ref1 = "Team \u65E5\uD83D\uDD11 \"operations\"";
            WritePasswordLine(ref1);
            WritePassword(ref1);
            """), [], host);
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.LastCredential == new CredentialReference("Team 日🔑 \"operations\"", "password"), "The exact macro name was lost.");
        Check(run.Status.PlaintextNotice, "Explicit Telnet password produced no nonblocking notice.");
        Check(Encoding.UTF8.GetString(host.Wire.Bytes.ToArray()) == "secret1\r\nsecret2", "The password was cached or the Enter bytes differed.");
        Check(!(await host.Session!.GetOutputAsync()).Any(line => line.Contains("secret")), "Password appeared as local echo.");
        await host.CloseAsync();

        var partial = new Host();
        partial.Wire.FailAfterFirstWrite = true;
        using var failed = new MacroRun(Parse("""
            Connect("profile.ttty");
            try { Write("once"); } catch (MacroError e) { Write("again"); }
            """), [], partial);
        failed.Start();
        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(failed.Status.Result == "UncertainDelivery" && Encoding.UTF8.GetString(partial.Wire.Bytes.ToArray()) == "o",
            "Possible partial transmission was caught or replayed.");
        Check(partial.Session!.AcceptsInput, "The still healthy terminal did not release input.");
        await partial.CloseAsync();
    }

    internal static async Task RecordingRetryAsync()
    {
        using var directory = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        listener.Start();
        string root = directory.Path;
        string profilePath = Path.Combine(root, "profile.ttty");
        string logs = Path.Combine(root, "logs");
        await File.WriteAllTextAsync(logs, "", deadline.Token);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = ConnectionProfile.CreateTelnet("接続:テスト", "127.0.0.1", port, recordRaw: true);
        var profileJson = System.Text.Json.Nodes.JsonNode.Parse(profile.ToEditorJson())!;
        profileJson["logging"]!["directory"] = "logs";
        await profile.Edit(profileJson.ToJsonString()).SaveNewAsync(profilePath, deadline.Token);
        var host = new Host { OpenRealProfile = true };
        var program = MacroProgram.Parse(Path.Combine(root, "example.tttym"), "#tttym 1\nConnect(\"profile.ttty\"); Write(\"ready\");");
        using var run = new MacroRun(program, [], host);
        try
        {
            run.Start();
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
            await UntilAsync(() => run.Status.Holds.HasFlag(MacroHold.RecordingFailure));
            Check(!accepted.IsCompleted, "The transport started before recording preparation.");
            File.Delete(logs);
            Directory.CreateDirectory(logs);
            run.RequestRecordingRetry();
            await UntilAsync(() => run.Status.Holds.HasFlag(MacroHold.RecordingReady));
            Check(!accepted.IsCompleted, "Retry implicitly resumed the connection.");
            string part = Directory.GetFiles(logs).Single();
            Check(Path.GetFileName(part).StartsWith("example_", StringComparison.Ordinal) &&
                part.EndsWith("_0000.raw.tttylog", StringComparison.Ordinal),
                "The first successful open did not use the first daily sequence.");
            run.Resume();
            using var peer = await accepted;
            byte[] message = new byte[20]; // Initial Telnet negotiation and the five macro bytes.
            await peer.GetStream().ReadExactlyAsync(message, deadline.Token);
            Check(message.AsSpan(15).SequenceEqual("ready"u8), "Resume did not complete the pending connection.");
            await run.Completion.WaitAsync(deadline.Token);
            Check(run.Status.State == MacroRunState.Completed, $"The resumed run did not complete: {run.Status}");
        }
        finally
        {
            if (!run.Completion.IsCompleted)
            {
                run.Stop();
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await host.CloseAsync();
        }
    }

    private static async Task PausedConnectionAndAlternateScreenAsync()
    {
        const string source = """
            Connect("profile.ttty");
            Write("after");
            """;
        var host = new Host { WaitAtConnect = true };
        using var run = new MacroRun(Parse(source), [], host);
        run.Start();
        await host.ConnectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Pause();
        host.FinishConnect.TrySetResult();
        await UntilAsync(() => run.Status.State == MacroRunState.Paused);
        Check(host.Wire.Bytes.Count == 0 && host.Session?.AcceptsInput == false,
            "A paused connection continued running or released terminal input.");
        run.Resume();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.Wire.Bytes.SequenceEqual("after"u8.ToArray()), "Resume did not continue the completed connection.");
        await host.CloseAsync();

        var alternate = new Host { WaitAtConnect = true };
        using var stopped = new MacroRun(Parse(source), [], alternate);
        stopped.Start();
        await alternate.ConnectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await alternate.Session!.ProcessPayloadAsync(TerminalReceiveSource.Data, "\u001b[?1049h\u001b[?1049l"u8.ToArray(), default);
        alternate.FinishConnect.TrySetResult();
        await stopped.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stopped.Status.Result == "AlternateScreen" && alternate.Wire.Bytes.Count == 0 &&
            alternate.ConnectionFinished && alternate.Session.AcceptsInput, "Startup alternate screen did not stop only the macro.");
        await alternate.CloseAsync();
    }

    private static async Task FilePathPartsAsync()
    {
        var cases = new List<(string Path, string Parent, string BaseName, string Extension)>
        {
            ("", "", "", ""),
            ("/", "", "", ""),
            ("/file.txt", "/", "file", ".txt"),
            ("config/.gitignore", "config", ".gitignore", ""),
            (".env.local", "", ".env", ".local"),
            ("report.", "", "report.", ""),
            ("dir//", "dir", "", ""),
            ("dir/../sub//trace.raw.log", "dir/../sub", "trace.raw", ".log"),
        };
        if (OperatingSystem.IsWindows())
            cases.AddRange([
                (@"dir\", "dir", "", ""),
                (@"C:\", "", "", ""),
                ("C:report", "C:", "report", ""),
                (@"C:\report", @"C:\", "report", ""),
                (@"C:/ops\archive\", "C:/ops\\archive", "", ""),
                (@"\\server\share", "", "", ""),
                (@"\\server\share\", "", "", ""),
                (@"\\server\share\report.txt", @"\\server\share", "report", ".txt"),
                (@"\\?\UNC\server\share\report.txt", @"\\?\UNC\server\share", "report", ".txt"),
            ]);
        else
            cases.Add((@"dir\", "", @"dir\", ""));

        using var program = MacroProgram.Parse(".macro", """
            #tttym 1
            if (Macro.BaseName != ".macro") { var fail = 1 / 0; }
            foreach (var path in Macro.Args)
            {
                Dialog(GetParentDirectory(path), GetFileBaseName(path), DialogButtons.OK, true, GetFileExtension(path));
            }
            """);
        var host = new Host();
        using var run = new MacroRun(program, cases.Select(test => test.Path).ToArray(), host);
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(run.Status.State == MacroRunState.Completed && host.Dialogs.Count == cases.Count,
            "Path operations did not complete: " + run.Status);
        foreach (var (test, dialog) in cases.Zip(host.Dialogs))
            Check((dialog.Title, dialog.Message, dialog.InitialText) == (test.Parent, test.BaseName, test.Extension),
                $"Unexpected parts for '{test.Path}': ({dialog.Title}, {dialog.Message}, {dialog.InitialText}).");
    }

    private static async Task PathAndSendPreflightAsync()
    {
        var host = new Host { TransmitEncoding = "iso-8859-1" };
        var program = Parse("""
            SetBaseDirectory("nested");
            var saved = BaseDirectory();
            ResetBaseDirectory();
            if (saved == BaseDirectory()) { var fail = 1 / 0; }
            Connect("profile.ttty");
            try { Write("must not send: あ"); }
            catch (MacroError e) { Write($"{e.Code}"); }
            """);
        using var run = new MacroRun(program, [], host);
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.ConnectedPath == Path.Combine(Path.GetDirectoryName(program.FullPath)!, "profile.ttty"),
            "The profile did not resolve from the restored base directory.");
        Check(host.Wire.Bytes.SequenceEqual("Encoding"u8.ToArray()), "Encoding failure sent prefix bytes or was uncatchable.");
        await host.CloseAsync();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestClock : TimeProvider
    {
        private long milliseconds;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public void Advance(long value) => Interlocked.Add(ref milliseconds, value);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreated.TrySetResult();
            return TimeProvider.System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private static MacroProgram Parse(string source) => MacroProgram.Parse("test.tttym", "#tttym 1\n" + source);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Host : IMacroHost
    {
        public List<MacroDialogRequest> Dialogs { get; } = [];
        public Wire Wire { get; } = new();
        public string Initial { get; init; } = "";
        public bool OpenRealProfile { get; init; }
        public SshInteraction? Ssh { get; init; }
        public bool WaitAtConnect { get; init; }
        public string? TransmitEncoding { get; init; }
        public string? ConnectedPath { get; private set; }
        public bool ConnectionFinished { get; private set; }
        public string? Password { get; init; }
        public CredentialReference? LastCredential { get; private set; }
        public TaskCompletionSource ConnectionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishConnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int passwordReads;
        public TerminalSession? Session;
        public async Task<TerminalSession> ConnectAsync(MacroRun run, string profilePath, CancellationToken token)
        {
            if (OpenRealProfile)
                return Session = await TerminalSession.ConnectMacroAsync(profilePath, run, token, Ssh,
                    viewport: _ => Task.FromResult((120, 40)));
            ConnectedPath = profilePath;
            var profile = ConnectionProfile.CreateTelnet("Macro", "localhost", transmitEncoding: TransmitEncoding);
            run.ProfileLoaded(profile);
            Session = new TerminalSession(Wire, profile, null, 100, macro: run);
            if (Initial.Length != 0) await Session.ProcessPayloadAsync(TerminalReceiveSource.Data, Encoding.UTF8.GetBytes(Initial), token);
            ConnectionEntered.TrySetResult();
            if (WaitAtConnect) await FinishConnect.Task.WaitAsync(token);
            ConnectionFinished = true;
            return Session;
        }
        public Task<VaultSecret?> ReadPasswordAsync(CredentialReference reference, CancellationToken token)
        {
            LastCredential = reference;
            return Task.FromResult<VaultSecret?>(new VaultSecret(Encoding.UTF8.GetBytes(Password ?? $"secret{++passwordReads}")));
        }
        public Task<MacroDialogResult> DialogAsync(MacroDialogRequest request, CancellationToken token)
        { Dialogs.Add(request); return Task.FromResult(new MacroDialogResult(MacroDialogButton.OK, request.InputEnabled, request.InitialText)); }
        public Task SetMinimizedAsync(bool minimized, CancellationToken token) => Task.CompletedTask;
        public async Task CloseAsync() { if (Session is not null) await Session.DisposeAsync(); }
    }

    private sealed class Wire : ISessionWire
    {
        public List<byte> Bytes { get; } = [];
        public bool FailAfterFirstWrite { get; set; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailAfterFirstWrite)
            {
                Bytes.Add(buffer.Span[0]);
                throw new IOException("Simulated uncertain write.");
            }
            Bytes.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        public void Dispose() { }
    }
}
