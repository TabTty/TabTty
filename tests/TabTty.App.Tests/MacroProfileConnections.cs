using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckMacroProfileConnectionsAsync(App app, string directory)
    {
        var owner = app.CreateWindow(restorePlacement: false);
        owner.Show();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var replacement = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        replacement.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string profiles = Directory.CreateDirectory(Path.Combine(directory, "profiles")).FullName;
        string path = Path.Combine(profiles, "reviewed.ttty");
        string logs = Path.Combine(profiles, "received");
        var original = ConnectionProfile.CreateTelnet("Reviewed", "127.0.0.1", port, recordRaw: true);
        var draft = JsonNode.Parse(original.ToEditorJson())!;
        draft["logging"]!["directory"] = logs;
        await original.Edit(draft.ToJsonString()).SaveNewAsync(path, token);
        byte[] reviewed = await File.ReadAllBytesAsync(path, token);
        var changed = ConnectionProfile.CreateTelnet("Changed", "127.0.0.1", ((IPEndPoint)replacement.LocalEndpoint).Port);
        string first = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "first")).FullName, "connect.tttym");
        string second = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "second")).FullName, "connect.tttym");
        const string source = """
            #tttym 1
            SetBaseDirectory("../profiles");
            Connect("reviewed.ttty", logFilenameTemplate: "daily", logCollision: "append");
            WaitLine(Literal("reviewed-payload"), 5000);
            """;
        await File.WriteAllTextAsync(first, source, token);
        await File.WriteAllTextAsync(second, source + "\n// independent macro", token);
        try
        {
            await SetLanguage("en");
            var tab = await Launch(first, approveLaunch: true);
            var review = await Review(first);
            Select(review, "Cancel");
            await tab.Macro!.Completion.WaitAsync(token);
            CheckMacroLaunch(tab.Macro.Status.Result == "UserCancelled" && !listener.Pending() && !Directory.Exists(logs),
                "Cancelling profile review started a connection or recording, or lost the cancellation result.");

            Directory.CreateDirectory(logs);
            string dailyLog = Path.Combine(logs, "daily.raw.tttylog");
            byte[] existing = [0xff, 0x00, 0x41];
            await File.WriteAllBytesAsync(dailyLog, existing, token);
            tab = await Launch(first);
            review = await Review(first);
            await File.WriteAllTextAsync(path, changed.ToEditorJson(), token);
            Select(review, "Connect once");
            await FinishConnection(tab);
            CheckMacroLaunch(!replacement.Pending() && Directory.EnumerateFiles(logs, "*.raw.tttylog").Any(),
                "The connection or recording used the profile written after review.");
            await File.WriteAllBytesAsync(path, reviewed, token);

            await SetLanguage("ja");
            tab = await Launch(first);
            review = await Review(first);
            CheckMacroLaunch(FindControls<Button>(review).Any(button => Equals(button.Content, "今回だけ接続")),
                "Japanese macro profile review was missing Connect once.");
            Select(review, "この版を信頼して接続");
            await FinishConnection(tab);
            tab = await Launch(second, approveLaunch: true);
            await FinishConnection(tab);
            byte[] payload = "reviewed-payload\r\n"u8.ToArray();
            byte[] expected = [.. existing, .. payload, .. payload, .. payload];
            CheckMacroLaunch((await File.ReadAllBytesAsync(dailyLog, token)).SequenceEqual(expected),
                "The append policy failed to retain all three macro connections in the reviewed Raw log.");
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 0,
                "A macro connection registered the reviewed profile.");

            await CheckEmbeddedHandoffAsync();

            var ssh = ConnectionProfile.CreateSsh("Saved password", "127.0.0.1", "operator",
                password: new("Macro default", "password"), methods: ["publicKey"], privateKey: new("Login key", "privateKey"));
            await File.WriteAllTextAsync(path, ssh.ToEditorJson(), token);
            tab = await Launch(second);
            var sshReview = await FindReview("ProfileTrustReview");
            string details = FindControls<TextBox>(sshReview).Single(box => box.IsReadOnly).Text;
            CheckMacroLaunch(details.Contains(second, StringComparison.Ordinal) &&
                details.Contains("Macro default", StringComparison.Ordinal) && details.Contains("Login key", StringComparison.Ordinal),
                "The SSH review omitted the configured macro password or authentication key reference.");
            Select(sshReview, App.Text("Cancel"));
            await tab.Macro!.Completion.WaitAsync(token);

            var remotePath = @"\\localhost\TabTty-profile-review-test\profile.ttty";
            var read = app.Profiles.AdmitAsync(owner, remotePath, first, token);
            var readReview = await FindReview("ProfileReadSource");
            details = FindControls<TextBox>(readReview).Single(box => box.IsReadOnly).Text;
            CheckMacroLaunch(details.Contains(first, StringComparison.Ordinal) && details.Contains(remotePath, StringComparison.Ordinal),
                "Read consent omitted the requesting macro or the source path.");
            Select(readReview, App.Text("Cancel"));
            try { await read; throw new InvalidOperationException("Declining remote read consent admitted a profile."); }
            catch (SshInteractionCancelledException) { }
            Console.WriteLine("PASS MacroConnectReviewsResolvedSnapshotAcrossLanguagesAndCallers");
        }
        finally { owner.Close(); }

        async Task<TerminalTab> Launch(string macroPath, bool approveLaunch = false)
        {
            var prior = owner.TerminalTabs.ToHashSet();
            var request = owner.OpenExternalMacroAsync(new InstanceLaunch(macroPath, []));
            if (approveLaunch)
                await DecideMacroRequest(app, owner, macroPath, App.Text("MacroNoArguments"), "MacroAlwaysAllow");
            await request.WaitAsync(token);
            return owner.TerminalTabs.Single(tab => !prior.Contains(tab));
        }

        async Task CheckEmbeddedHandoffAsync()
        {
            await SetLanguage("en");
            string caller = Path.Combine(Path.GetDirectoryName(first)!, "handoff.tttym");
            await File.WriteAllTextAsync(caller, """
                #tttym 1
                var message = "external";
                try { Connect("../profiles/reviewed.ttty", logFilenameTemplate: "handoff", logCollision: "append"); }
                catch (MacroError failure) { WriteLine("unexpected recovery"); return; }
                try { WaitPrompt(Literal("ready"), 0); return; }
                catch (MacroError failure) { }
                WaitPrompt(Regex(@"(?<=ready):suffix"), 5000);
                WaitPrompt(Literal("following"), 5000);
                WriteLine(message);
                """, token);
            const string embedded = """
                var message = "embedded";
                WaitPrompt(Literal("boot"), 5000);
                WriteLine(message, markReceiveBoundary: true);
                WaitPrompt(Literal("ready"), 5000);
                return;
                WriteLine("unexpected embedded continuation");
                """;
            await WriteBody(embedded);
            var tab = await Launch(caller, approveLaunch: true);
            var review = await Review(caller);
            CheckMacroLaunch(FindControls<TextBox>(review).Single().Text.Contains("unexpected embedded continuation", StringComparison.Ordinal),
                "The profile trust review omitted the embedded source.");
            Select(review, App.Text("ProfileConnectOnce"));
            using (var peer = await listener.AcceptTcpClientAsync(token))
            {
                var wire = peer.GetStream();
                await wire.WriteAsync("boot"u8.ToArray(), token);
                await ConsumeNegotiation(wire);
                using var lines = new StreamReader(wire, leaveOpen: true);
                CheckMacroLaunch(await lines.ReadLineAsync(token) == "embedded" && tab.InputSession is null,
                    "External Connect sent out of order or relinquished ownership during the embedded phase.");
                tab.Macro!.Pause();
                await wire.WriteAsync("\r\nready:suffix\r\nfollowing"u8.ToArray(), token);
                peer.Client.Shutdown(SocketShutdown.Send);
                while (tab.AiSession?.AiConnectionEnded != true) await Task.Delay(10, token);
                tab.Macro.Resume();
                CheckMacroLaunch(await lines.ReadLineAsync(token) == "external",
                    "External Connect lost the shared cursor, unread suffix, or caller locals.");
                await tab.Macro!.Completion.WaitAsync(token);
                CheckMacroLaunch(tab.Macro.Status is { State: MacroRunState.Completed, PostConnection: false },
                    "The external macro did not finish after the embedded phase.");
                tab.Disconnect();
            }
            await tab.Completion.WaitAsync(token);
            CheckMacroLaunch(Directory.EnumerateFiles(logs, "handoff*.raw.tttylog").Count() == 1,
                "The two phases did not share one recording.");

            foreach (string result in new[] { "WaitTimeout", "Stopped", "Disconnected" })
            {
                long wait = result == "WaitTimeout" ? 100 : 5000;
                await WriteBody($"WaitPrompt(Literal(\"absent\"), {wait});");
                tab = await Launch(caller);
                Select(await Review(caller), App.Text("ProfileConnectOnce"));
                using (var peer = await listener.AcceptTcpClientAsync(token))
                {
                    var wire = peer.GetStream();
                    await ConsumeNegotiation(wire);
                    if (result != "WaitTimeout")
                    {
                        while (!tab.Macro!.Status.PostConnection) await Task.Delay(10, token);
                        if (result == "Stopped") tab.Macro.Stop();
                        else tab.Disconnect();
                    }
                    await tab.Macro!.Completion.WaitAsync(token);
                    CheckMacroLaunch(tab.Macro.Status.PostConnection &&
                        tab.Macro.Status.Result == result &&
                        (tab.InputSession is not null) == (result != "Disconnected") && !wire.DataAvailable,
                        "The embedded failure leaked to the caller or failed to restore manual input.");
                    tab.Disconnect();
                }
                await tab.Completion.WaitAsync(token);
            }
            await WriteBody("\nif (false) { Connect(\"other.ttty\"); }");
            tab = await Launch(first);
            await tab.Macro!.Completion.WaitAsync(token);
            string sourceError = TerminalTab.PostConnectionSourceError(new MacroSourceException("PostConnectionSubset", new(2, 14)));
            CheckMacroLaunch(tab.Macro.Status.DisplayDetail == sourceError && !listener.Pending(),
                "External Connect hid the embedded source error or opened a transport.");
            await File.WriteAllBytesAsync(path, reviewed, token);
            Console.WriteLine("PASS ExternalMacroHandoffSharesCursorRecordingAndInput");

            Task WriteBody(string source)
            {
                draft["postConnectionMacro"] = new JsonObject { ["version"] = 1, ["source"] = source };
                return File.WriteAllTextAsync(path, draft.ToJsonString(), token);
            }

            Task ConsumeNegotiation(NetworkStream wire)
            {
                byte[] negotiation = new byte[new TelnetProtocol("xterm", 80, 24).Start().Length];
                return wire.ReadExactlyAsync(negotiation, token).AsTask();
            }
        }

        async Task<Window> FindReview(string title)
        {
            Window? review;
            while ((review = app.Windows.OfType<Window>().FirstOrDefault(window =>
                window.IsLoaded && window.Title == App.Text(title))) is null)
                await Task.Delay(10, token);
            return review;
        }

        async Task<Window> Review(string caller)
        {
            var review = await FindReview("ProfileTrustReview");
            string details = FindControls<TextBox>(review).Single(box => box.IsReadOnly).Text;
            CheckMacroLaunch(details.Contains(caller, StringComparison.Ordinal) && details.Contains(path, StringComparison.Ordinal) &&
                details.Contains($"127.0.0.1 [{port}]", StringComparison.Ordinal) && details.Contains("Raw", StringComparison.Ordinal),
                "The macro review omitted the calling source, resolved path, destination or recording format.");
            var buttons = FindControls<Button>(review).ToList();
            CheckMacroLaunch(Equals(buttons.Single(button => button.IsDefault).Content, App.Text("Cancel")) &&
                buttons.Any(button => Equals(button.Content, App.Text("ProfileTrustAndConnect"))) &&
                !buttons.Any(button => Equals(button.Content, App.Text("ProfileImportAndConnect"))),
                "The macro review offered the wrong trust choices.");
            return review;
        }

        async Task FinishConnection(TerminalTab tab)
        {
            using (var peer = await listener.AcceptTcpClientAsync(token))
            {
                await peer.GetStream().WriteAsync("reviewed-payload\r\n"u8.ToArray(), token);
                await tab.Macro!.Completion.WaitAsync(token);
                CheckMacroLaunch(tab.Macro.Status.State == MacroRunState.Completed, $"Profile connection failed: {tab.Macro.Status}");
            }
            await tab.Completion.WaitAsync(token);
        }

        Task SetLanguage(string choice) => app.SaveSettingsAsync(choice, app.ThemeChoice, app.BellChoice, app.TerminalFontFamily,
            app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, app.VaultPath, app.SettingsRevision);

        static void Select(Window review, string label) => FindControls<Button>(review)
            .Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
