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
