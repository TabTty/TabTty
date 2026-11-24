using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckPostConnectionMacroAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var owner = app.CreateWindow(restorePlacement: false);
        owner.Show();
        string path = Path.Combine(directory, "switch.ttty");
        string folder = Path.Combine(directory, "imports");
        var original = ConnectionProfile.CreateTelnet("Switch", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        const string body = """
            var prompt = Literal("sw>");
            WaitPrompt(prompt, 5000);
            WriteLine("show clock", markReceiveBoundary: true);
            WaitPrompt(prompt, 5000);
            WriteLine("terminal length 0", markReceiveBoundary: true);
            WaitPrompt(prompt, 5000);
            WriteLine("enable", markReceiveBoundary: true);
            var reply = WaitAnyPrompt([password: Literal("Password:"), ready: Literal("sw#")], 5000);
            if (reply.AlternativeName == "password")
            {
                WritePasswordLine("Enable secret");
                WaitPrompt(Literal("sw#"), 5000);
            }
            """;
        var draft = JsonNode.Parse(original.ToEditorJson())!;
        draft["postConnectionMacro"] = new JsonObject { ["version"] = 1, ["source"] = body };
        await original.Edit(draft.ToJsonString()).SaveNewAsync(path, token);

        try
        {
            await app.SaveSettingsAsync(app.SettingsRevision, new(AutoClose: false, ProfileDirectory: folder));
            var import = app.Profiles.ImportFileAsync(owner, path, token);
            var review = await FindWindow("PostConnectionMacroRegistration", import);
            CheckMacroLaunch(FindControls<TextBox>(review).Single().Text.Contains("WaitAnyPrompt", StringComparison.Ordinal),
                "Registration review did not show the body.");
            Click(review, "PostConnectionMacroTrustProfile");
            var imported = await import.WaitAsync(token);
            CheckMacroLaunch(imported.PostConnectionSource == body && !listener.Pending(), "Import changed or ran the body.");
            Console.WriteLine("PASS PostConnectionMacroImportReview");

            await CheckEditorAsync(imported, "en", "light", "Post-connection macro",
                "This macro runs automatically in this tab after connection.");
            await CheckEditorAsync(imported, "ja", "dark", "接続後マクロ",
                "接続後、このタブで自動的に実行するマクロです。");
            Console.WriteLine("PASS PostConnectionMacroEditorLayout");

            await ComposeAndSaveAsync();

            long compilations = MacroProgram.PostConnectionCompilations;
            CheckMacroLaunch(app.Activate([new InstanceLaunch(path, [])]), "The profile file activation was rejected.");
            review = await FindWindow("ProfileTrustReview");
            var tab = owner.TerminalTabs.Single();
            Click(review, "ProfileConnectOnce");
            await VerifyConnectionAsync(tab, null);
            CheckMacroLaunch(MacroProgram.PostConnectionCompilations == compilations + 1,
                "A reviewed manual connection compiled its embedded body more than once.");
            Console.WriteLine("PASS PostConnectionMacroTelnetWithoutPassword");

            foreach (string secret in new[] { "first-local-value", "other-local-value" })
            {
                string vaultPath = Path.Combine(directory, secret + ".tttykey");
                const string master = "post connection testing master password";
                using (var vault = await CredentialVault.CreateAsync(vaultPath, master.AsMemory(), token))
                    await vault.AddPasswordAsync("Enable secret", Encoding.UTF8.GetBytes(secret), token);
                await app.SaveSettingsAsync(app.SettingsRevision, new(VaultPath: vaultPath));
                CheckMacroLaunch(tab.CanReconnect, "The completed macro prevented reconnect.");
                compilations = MacroProgram.PostConnectionCompilations;
                tab.Start();
                await VerifyConnectionAsync(tab, secret, master);
                CheckMacroLaunch(MacroProgram.PostConnectionCompilations == compilations + 1,
                    "A reconnect did not compile exactly one body for its attempt.");
            }
            Console.WriteLine("PASS PostConnectionMacroTelnetWithNamedVaultEntries");

            CheckMacroLaunch(tab.CanReconnect, "The completed macro prevented a subsequent reconnect.");
            tab.Start();
            using (var peer = await listener.AcceptTcpClientAsync(token))
            {
                var run = tab.Macro!;
                run.Stop();
                await run.Completion.WaitAsync(token);
                while (tab.InputSession is null) await Task.Delay(15, token);
                CheckMacroLaunch(run.Status.State == MacroRunState.Stopped, "Stop did not release input.");
            }
            await tab.Completion.WaitAsync(token);

            draft["postConnectionMacro"]!["source"] = "WaitPrompt(Literal(\"absent\"), 20);\nWriteLine(\"unexpected\");";
            await File.WriteAllTextAsync(path, draft.ToJsonString(), token);
            tab.Start();
            review = await FindWindow("ProfileTrustReview");
            Click(review, "ProfileConnectOnce");
            using (var peer = await listener.AcceptTcpClientAsync(token))
            {
                var run = tab.Macro!;
                while (run.IsActive) await Task.Delay(15, token);
                byte[] negotiation = new byte[new TelnetProtocol("xterm", 80, 24).Start().Length];
                await peer.GetStream().ReadExactlyAsync(negotiation, token);
                CheckMacroLaunch(run.Status.Result == "WaitTimeout" && run.Status.Position.Line == 1 &&
                    tab.InputSession is not null && !peer.GetStream().DataAvailable,
                    "A timeout did not report its source and return the terminal without sending a command.");
            }
            await tab.Completion.WaitAsync(token);

            draft["postConnectionMacro"]!["source"] = "WritePasswordLine(\"Missing entry\");";
            await File.WriteAllTextAsync(path, draft.ToJsonString(), token);
            tab.Start();
            review = await FindWindow("ProfileTrustReview");
            Click(review, "ProfileConnectOnce");
            using (var peer = await listener.AcceptTcpClientAsync(token))
            {
                var run = tab.Macro!;
                while (run.IsActive) await Task.Delay(15, token);
                byte[] negotiation = new byte[new TelnetProtocol("xterm", 80, 24).Start().Length];
                await peer.GetStream().ReadExactlyAsync(negotiation, token);
                CheckMacroLaunch(run.Status.Result == "CredentialUnavailable" &&
                    tab.InputSession is not null && !peer.GetStream().DataAvailable,
                    "A missing entry did not fail visibly before password submission.");
            }
            await tab.Completion.WaitAsync(token);

            draft["postConnectionMacro"]!["source"] = "if (false) { Connect(\"other.ttty\"); }";
            await File.WriteAllTextAsync(path, draft.ToJsonString(), token);
            tab.Start();
            await tab.Completion.WaitAsync(token);
            CheckMacroLaunch(!listener.Pending() && tab.ConnectionStatus.Contains(App.Text("PostConnectionMacro"), StringComparison.Ordinal),
                "Invalid external edits were not rejected before connecting.");
            Console.WriteLine("PASS PostConnectionMacroReviewEditorRuntimeReconnectAndVault");
            await CheckMovedGroupConnectionAsync();
        }
        finally
        {
            var terminals = owner.TerminalTabs.ToArray();
            foreach (var terminal in terminals) terminal.Disconnect();
            try { await Task.WhenAll(terminals.Select(terminal => terminal.Completion)).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            if (terminals.All(terminal => !terminal.IsRunning)) owner.Close();
        }

        async Task CheckEditorAsync(ConnectionProfile profile, string language, string theme,
            string heading, string description)
        {
            await app.SaveSettingsAsync(app.SettingsRevision, new(Language: language, Theme: theme, AutoClose: false));
            var editor = new ProfileEditorWindow(app, "EditProfile", profile.OpenedPath, profile, creating: false);
            editor.Show();
            try
            {
                await WaitIdleAsync();
                var navigation = FindControls<ListBox>(editor)
                    .Single(list => list.Items.OfType<ListBoxItem>().Any(item => Equals(item.Tag, "PostConnectionMacro")));
                var page = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "PostConnectionMacro"));
                navigation.SelectedItem = page;
                await WaitIdleAsync();
                CheckMacroLaunch(page.Content is TextBlock label && label.Text == heading &&
                    FindControls<TextBlock>(editor).Any(text => text.Text == description),
                    "The editor did not localize the navigation and description.");
                string insert = language == "ja" ? "定型処理を挿入" : "Insert routine";
                CheckMacroLaunch(FindControls<Button>(editor).Any(button => Equals(button.Content, insert + " ▾")),
                    "The routine insert control was missing or untranslated.");
                var input = FindControls<TextBox>(editor).Single(box => box.AcceptsReturn && !box.IsReadOnly);
                double before = input.ActualHeight;
                editor.Height = editor.ActualHeight + 110;
                await WaitIdleAsync();
                CheckMacroLaunch(input.Text.ReplaceLineEndings("\n") == body.ReplaceLineEndings("\n") && input.ActualHeight > before,
                    "The editor failed to load or resize the body field.");
            }
            finally { editor.Close(); }
        }

        async Task ComposeAndSaveAsync()
        {
            var profile = await ConnectionProfile.LoadAsync(path, token);
            var editor = new ProfileEditorWindow(app, "EditProfile", profile.OpenedPath, profile, creating: false);
            editor.Show();
            try
            {
                await WaitIdleAsync();
                var navigation = FindControls<ListBox>(editor)
                    .Single(list => list.Items.OfType<ListBoxItem>().Any(item => Equals(item.Tag, "PostConnectionMacro")));
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "PostConnectionMacro"));
                await WaitIdleAsync();
                var input = FindControls<TextBox>(editor).Single(box => box.AcceptsReturn && !box.IsReadOnly);
                var button = FindControls<Button>(editor).Single(item => Equals(item.Content, App.Text("PostConnectionInsertRoutine") + " ▾"));
                input.Text = "";
                await InsertAsync("PostConnectionRoutineCommands", dialog =>
                {
                    SetField(dialog, "PostConnectionRoutineNormalPrompt", "sw>");
                    SetField(dialog, "PostConnectionRoutineCommandLines", "show \"C:\\ops\"");
                });
                await InsertAsync("PostConnectionRoutineSudo", dialog =>
                {
                    SetField(dialog, "PostConnectionRoutineNormalPrompt", "user$");
                    SetField(dialog, "PostConnectionRoutinePrivilegedPrompt", "root#");
                    SetField(dialog, "PostConnectionRoutineVaultEntry", "sudo entry");
                });
                using (var escaped = MacroProgram.ParsePostConnection(path, input.Text))
                {
                    CheckMacroLaunch(input.Text.Contains("C:\\\\ops", StringComparison.Ordinal),
                        "The routine did not escape the backslash in the command.");
                }

                input.Text = "// before\r\n\r\n// after";
                input.CaretIndex = input.Text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 2;
                await InsertAsync("PostConnectionRoutineCommands", dialog =>
                {
                    SetField(dialog, "PostConnectionRoutineNormalPrompt", "sw>");
                    SetField(dialog, "PostConnectionRoutineCommandLines", "show clock\r\nterminal length 0");
                });
                await InsertAsync("PostConnectionRoutineEnable", dialog =>
                {
                    SetField(dialog, "PostConnectionRoutineNormalPrompt", "sw>");
                    SetField(dialog, "PostConnectionRoutinePrivilegedPrompt", "sw#");
                    SetField(dialog, "PostConnectionRoutineVaultEntry", "Enable secret");
                });
                await InsertAsync("PostConnectionRoutineCommands", dialog =>
                {
                    FindControls<CheckBox>(dialog).Single(check =>
                        check.Content is TextBlock label && label.Text == App.Text("PostConnectionRoutineWaitFirst")).IsChecked = false;
                    SetField(dialog, "PostConnectionRoutineNormalPrompt", "sw#");
                    SetField(dialog, "PostConnectionRoutineCommandLines", "show privilege");
                });
                string generated = input.Text;
                CheckMacroLaunch(generated.StartsWith("// before", StringComparison.Ordinal) &&
                    generated.EndsWith("// after", StringComparison.Ordinal),
                    "Insertion replaced text around the caret.");
                input.AppendText("\r\n// manually edited");
                Click(editor, "Save");
                while (editor.HasPendingEdits) await Task.Delay(10, token);
                var reopened = await ConnectionProfile.LoadAsync(path, token);
                CheckMacroLaunch(reopened.PostConnectionSource.StartsWith(generated, StringComparison.Ordinal) &&
                    reopened.PostConnectionSource.EndsWith("// manually edited", StringComparison.Ordinal),
                    "The generated and manually edited text did not survive saving.");
                Console.WriteLine("PASS ComposePostConnectionRoutinesAndSave");

                async Task InsertAsync(string key, Action<Window> configure)
                {
                    var menu = button.ContextMenu!;
                    var item = menu.Items.OfType<MenuItem>().Single(choice => Equals(choice.Header, App.Text(key)));
                    var opened = editor.Dispatcher.InvokeAsync(() => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                    var dialog = await FindWindow(key, opened.Task);
                    try
                    {
                        configure(dialog);
                        Click(dialog, "PostConnectionRoutineInsert");
                    }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                    await opened.Task.WaitAsync(token);
                }
            }
            finally { if (!editor.HasPendingEdits) editor.Close(); }

            static void SetField(Window dialog, string key, string value) =>
                FindControls<TextBox>(dialog).Single(box => AutomationProperties.GetLabeledBy(box)
                    is Label { Content: TextBlock label } && label.Text == App.Text(key)).Text = value;
        }

        async Task CheckMovedGroupConnectionAsync()
        {
            var paths = new List<string>();
            foreach (string name in new[] { "router-a", "router-b" })
            {
                string groupPath = Path.Combine(folder, name + ".ttty");
                var settings = JsonNode.Parse(original.ToEditorJson())!;
                settings["logging"]!["raw"] = true;
                settings["postConnectionMacro"] = new JsonObject { ["version"] = 1,
                    ["source"] = $"WaitPrompt(Literal(\"proceed\"), 10000); WriteLine(\"{name}\");" };
                await original.Edit(settings.ToJsonString()).SaveNewAsync(groupPath, token);
                var registration = app.Profiles.ImportFileAsync(owner, groupPath, token);
                Click(await FindWindow("PostConnectionMacroRegistration", registration), "PostConnectionMacroTrustProfile");
                paths.Add((await registration.WaitAsync(token)).OpenedPath);
            }

            var destination = app.CreateWindow(restorePlacement: false);
            destination.Show();
            try
            {
                FormView? list = null;
                owner.OpenForm("Profiles", form => list = form);
                owner.OpenProfiles(paths, list!);
                using var first = await listener.AcceptTcpClientAsync(token);
                using var second = await listener.AcceptTcpClientAsync(token);
                var terminals = owner.TerminalTabs.Where(tab => paths.Contains(tab.ProfilePath, StringComparer.OrdinalIgnoreCase)).ToArray();
                CheckMacroLaunch(terminals.Length == 2, "Opening the two selected profiles did not create both tabs.");
                while (terminals.Any(terminal => terminal.Macro?.Status.Operation != "Wait")) await Task.Delay(10, token);
                var runs = terminals.Select(terminal => terminal.Macro!).ToArray();
                var menu = (Menu)owner.FindName("mainMenu");
                var windowMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("WindowMenu")));
                var move = windowMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("MoveTab")));
                move.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, move));
                var target = move.Items.OfType<MenuItem>().Last();
                target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, target));
                CheckMacroLaunch(destination.TerminalTabs.Single() == terminals[0] && terminals[0].Macro == runs[0],
                    "Moving the waiting tab replaced its post-connection run.");

                var replies = new HashSet<string?>();
                foreach (TcpClient peer in new[] { first, second })
                {
                    var wire = peer.GetStream();
                    await wire.ReadExactlyAsync(new byte[new TelnetProtocol("xterm", 80, 24).Start().Length], token);
                    await wire.WriteAsync("proceed"u8.ToArray(), token);
                    using var reader = new StreamReader(wire, leaveOpen: true);
                    replies.Add(await reader.ReadLineAsync(token));
                }
                await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(token);
                CheckMacroLaunch(replies.SetEquals(["router-a", "router-b"]) && terminals.All(terminal => terminal.InputSession is not null),
                    "The profiles did not complete independently and release both terminals.");
                first.Close();
                second.Close();
                await Task.WhenAll(terminals.Select(terminal => terminal.Completion)).WaitAsync(token);
                foreach (string opened in paths)
                {
                    string log = Directory.GetFiles(Path.Combine(folder, "Logs"), Path.GetFileNameWithoutExtension(opened) + "_*.raw.tttylog").Single();
                    CheckMacroLaunch((await File.ReadAllBytesAsync(log, token)).AsSpan().SequenceEqual("proceed"u8),
                        "Opening or moving the tab changed its receive recording.");
                }
                Console.WriteLine("PASS PostConnectionMacroMultiProfileOpenAndTabMove");
            }
            finally
            {
                var tabs = destination.TerminalTabs.ToArray();
                foreach (var tab in tabs) tab.Disconnect();
                await Task.WhenAll(tabs.Select(tab => tab.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
                destination.Close();
            }
        }

        async Task VerifyConnectionAsync(TerminalTab terminal, string? password, string? master = null)
        {
            using (var peer = await listener.AcceptTcpClientAsync(token))
            {
                using var wire = peer.GetStream();
                await wire.WriteAsync("sw>\r\n"u8.ToArray(), token);
                byte[] negotiation = new byte[new TelnetProtocol("xterm", 80, 24).Start().Length];
                await wire.ReadExactlyAsync(negotiation, token);
                using var reader = new StreamReader(wire, Encoding.UTF8, leaveOpen: true);
                await CheckCommand("show clock");
                CheckMacroLaunch(terminal.InputSession is null, "Manual input was available during the macro.");
                await wire.WriteAsync("12:00\r\nsw>\r\n"u8.ToArray(), token);
                await CheckCommand("terminal length 0");
                await wire.WriteAsync("sw>\r\n"u8.ToArray(), token);
                await CheckCommand("enable");
                if (password is not null)
                {
                    await wire.WriteAsync("Password:"u8.ToArray(), token);
                    var unlock = await FindWindow("UnlockVault");
                    FindControls<PasswordBox>(unlock).Single().Password = master!;
                    Click(unlock, "UnlockVault");
                    await CheckCommand(password);
                }
                await wire.WriteAsync("\r\nsw#"u8.ToArray(), token);
                await CheckCommand("show privilege");
                var run = terminal.Macro!;
                await run.Completion.WaitAsync(token);
                CheckMacroLaunch(run.Status.State == MacroRunState.Completed &&
                    run.Status.PlaintextNotice == (password is not null) && terminal.InputSession is not null,
                    "The macro did not hand a healthy connection to manual input: " + run.Status);

                async Task CheckCommand(string expected)
                {
                    string? command = await reader.ReadLineAsync(token);
                    CheckMacroLaunch(command == expected, "Unexpected terminal command or password in the scripted sequence.");
                }
            }
            await terminal.Completion.WaitAsync(token);
        }

        async Task<Window> FindWindow(string key, Task? request = null)
        {
            for (int tries = 0; tries < 500; tries++)
            {
                var window = app.Windows.OfType<Window>().FirstOrDefault(candidate =>
                    candidate.IsLoaded && candidate.Title == App.Text(key));
                if (window is not null) return window;
                if (request?.IsCompleted == true)
                {
                    await request;
                    throw new InvalidOperationException("The action completed before showing " + key);
                }
                await Task.Delay(15, token);
            }
            throw new InvalidOperationException($"Missing {key}. Windows: {string.Join(", ", app.Windows.OfType<Window>().Select(window => window.Title))}");
        }

        Task WaitIdleAsync() => owner.Dispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, token).Task;

        static void Click(Window window, string key) => FindControls<Button>(window)
            .Single(button => Equals(button.Content, App.Text(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
