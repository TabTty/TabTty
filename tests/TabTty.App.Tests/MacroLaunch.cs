using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void CheckMacroLaunch(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task MacroLaunchAndDialog(App app, string directory)
    {
        string source = Path.Combine(directory, "dialog.tttym");
        const string program = """
            #tttym 1
            var answer = Dialog(Macro.BaseName, Macro.Args[0], DialogButtons.OKCancel, true, "");
            if (answer.Button != DialogButton.OK || !answer.HasText || answer.Text != "" || Macro.Args[1] != "" ||
                Macro.Args[2] != "一\n二" || Macro.Args[3] != "<U+000A>") {
                var fail = 1 / 0;
            }
            """;
        var launch = InstanceLaunch.Parse(["--macro", source, "--", "A B", "", "一\n二", "<U+000A>", "\"名\u202E"]).Single();
        File.WriteAllText(source, program);
        using var forwarded = JsonDocument.Parse(InstanceActivation.EncodeRequests([launch]));
        CheckMacroLaunch(forwarded.RootElement.ValueKind == JsonValueKind.Object &&
            forwarded.RootElement.GetProperty(InstanceActivation.MacroRequestsProperty)[0].GetProperty("Arguments").GetArrayLength() == 5 &&
            !forwarded.RootElement.TryGetProperty("MacroReviewRequests", out _) &&
            !forwarded.RootElement.TryGetProperty("EditorRequests", out _), "An older receiver could accept the new macro envelope.");
        var window = app.CreateWindow(restorePlacement: false);
        window.Show();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var associated = InstanceLaunch.Parse([source]);
            CheckMacroLaunch(app.Activate(associated), "File association was rejected.");
            await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"));
            CheckMacroLaunch(!window.TerminalTabs.Any(), "Cancelling an external macro request created a terminal.");
            const string shown = "[0] \"A B\"\n[1] \"\"\n[2] \"一<U+000A>二\"\n[3] \"<U+003C>U+000A>\"\n[4] \"<U+0022>名<U+202E>\"";
            CheckMacroLaunch(app.Activate([launch]), "CLI activation was rejected.");
            await DecideMacroRequest(app, window, source, shown);
            CheckMacroLaunch(!window.TerminalTabs.Any(), "Cancelling the CLI request created a terminal.");
            CheckMacroLaunch(app.Activate([launch]), "A repeated CLI activation was rejected.");
            // Replace the disk file during review. The first run uses the reviewed snapshot.
            await DecideMacroRequest(app, window, source, shown, "MacroRunOnce", () => File.WriteAllText(source, "#tttym 1\nvar n = 1;"));
            MacroDialogWindow? prompt = null;
            while ((prompt = app.Windows.OfType<MacroDialogWindow>().SingleOrDefault()) is null)
                await Task.Delay(10, timeout.Token);
            TerminalTab tab = window.TerminalTabs.Single();
            MacroRun run = tab.Macro!;
            if (!ReferenceEquals(prompt.Owner, window) || !window.IsEnabled)
                throw new InvalidOperationException("The macro dialog disabled or lost its host.");
            if (!FindControls<TextBlock>(prompt).Any(block => block.Text == "A B"))
                throw new InvalidOperationException("The macro did not receive the launch argument.");
            var ok = FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("OK")));
            var cancel = FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("Cancel")));
            if (!cancel.IsDefault) throw new InvalidOperationException("Cancel was not the default.");
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await run.Completion.WaitAsync(timeout.Token);
            if (run.Status.State != MacroRunState.Completed || !window.TerminalTabs.Contains(tab))
                throw new InvalidOperationException("Accepted empty input failed or the preconnection result tab disappeared.");
            await RememberMacroDecisions(app, window, directory);
        }
        finally
        {
            foreach (TerminalTab tab in window.TerminalTabs) tab.Macro?.Stop();
            foreach (TerminalTab tab in window.TerminalTabs) await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
        }
    }

    private static async Task RememberMacroDecisions(App app, MainWindow window, string directory)
    {
        string original = Path.Combine(directory, "trust.tttym");
        string copy = Path.Combine(directory, "renamed.tttym");
        File.WriteAllText(original, $"#tttym 1\nvar runTag = \"{Guid.NewGuid():N}\";");
        File.Copy(original, copy);
        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        var allow = MacroTrustKey.Create(digest, ["ok"]);
        var block = MacroTrustKey.Create(digest, [""]);
        var alternatives = new string[][] { [], [""], ["a", "b"], ["b", "a"], ["a b"], ["ab", ""], ["é"], ["e\u0301"] };
        CheckMacroLaunch(alternatives.Select(args => MacroTrustKey.Create(digest, args)).Distinct().Count() == alternatives.Length,
            "Argument order, boundaries, empty values or Unicode spelling were lost.");
        try
        {
            int tabs = window.TerminalTabs.Count();
            var launch = new InstanceLaunch(original, ["ok"]);
            var request = window.OpenExternalMacroAsync(launch);
            await DecideMacroRequest(app, window, original, "[0] \"ok\"", "MacroAlwaysAllow");
            await request;
            CheckMacroLaunch(MacroTrust.Read(allow) == MacroTrustDecision.Allow && window.TerminalTabs.Count() == ++tabs,
                "A remembered allow did not start the reviewed run.");
            await window.OpenExternalMacroAsync(launch with { Path = copy }).WaitAsync(TimeSpan.FromSeconds(5));
            CheckMacroLaunch(window.TerminalTabs.Count() == ++tabs, "An identical copy with identical arguments did not reuse the allow.");

            File.WriteAllText(copy, File.ReadAllText(original), System.Text.Encoding.UTF8);
            request = window.OpenExternalMacroAsync(launch with { Path = copy });
            await DecideMacroRequest(app, window, copy, "[0] \"ok\"");
            await request;
            CheckMacroLaunch(window.TerminalTabs.Count() == tabs, "Changing only the BOM reused a remembered allow.");
            File.Copy(original, copy, overwrite: true);

            var empty = new InstanceLaunch(original, [""]);
            request = window.OpenExternalMacroAsync(empty);
            await DecideMacroRequest(app, window, original, "[0] \"\"", "MacroAlwaysBlock");
            await request;
            CheckMacroLaunch(MacroTrust.Read(block) == MacroTrustDecision.Block && window.TerminalTabs.Count() == tabs,
                "A remembered block started a run or was not persisted.");
            await window.OpenExternalMacroAsync(empty with { Path = copy }).WaitAsync(TimeSpan.FromSeconds(5));
            CheckMacroLaunch(window.TerminalTabs.Count() == tabs, "Opening a blocked pair created a tab.");
            CheckMacroLaunch(MacroTrust.List().Any(entry => entry == new MacroTrust.Entry(block.Fingerprint, digest, MacroTrustDecision.Block)),
                "A stored block was missing from the settings view.");

            await ForgetFromSettings(app, window, block);
            request = window.OpenExternalMacroAsync(empty);
            await DecideMacroRequest(app, window, original, "[0] \"\"");
            await request;
            CheckMacroLaunch(MacroTrust.Read(block) is null && MacroTrust.Read(allow) == MacroTrustDecision.Allow,
                "Forgetting a block affected another pair.");
            string remote = @"\\localhost\TabTty-approval-test\macro.tttym";
            request = window.OpenExternalMacroAsync(new(remote, []));
            await DecideMacroRequest(app, window, remote, null);
            await request;
            CheckMacroLaunch(window.TerminalTabs.Count() == tabs, "Cancelling the network read created a tab.");
            MacroTrust.Save(block, MacroTrustDecision.Block);
            MacroTrust.Reset();
            CheckMacroLaunch(MacroTrust.Read(allow) is null && MacroTrust.Read(block) is null &&
                MacroTrust.List().Count == 0, "Reset did not erase both kinds of macro decision.");
            request = window.OpenExternalMacroAsync(launch);
            await DecideMacroRequest(app, window, original, "[0] \"ok\"");
            await request;
            request = window.OpenExternalMacroAsync(empty);
            await DecideMacroRequest(app, window, original, "[0] \"\"");
            await request;
        }
        finally
        {
            MacroTrust.Forget(allow.Fingerprint);
            MacroTrust.Forget(block.Fingerprint);
        }
    }

    private static async Task ForgetFromSettings(App app, MainWindow window, MacroTrustKey key)
    {
        OpenSettings(window);
        var form = (FormView)((TabControl)window.FindName("tabs")).SelectedContent;
        var navigation = FindControls<ListBox>(form).Single();
        navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
            .Single(item => item.Content is TextBlock heading && heading.Text == App.Text("SettingsMaintenance"));
        var manage = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("MacroManageTrust")));
        var forget = window.Dispatcher.InvokeAsync(() =>
        {
            Window dialog = app.Windows.OfType<Window>().Single(candidate => candidate.Owner == window && candidate.IsVisible);
            try
            {
                var lists = FindControls<ComboBox>(dialog).ToArray();
                var sources = lists.Single(list => list.Items.Contains(key.SourceDigest));
                sources.SelectedItem = key.SourceDigest;
                var pairs = lists.Single(list => list != sources);
                pairs.SelectedItem = pairs.Items.OfType<string>().Single(item => item.StartsWith(key.Fingerprint, StringComparison.Ordinal));
                FindControls<Button>(dialog).Single(button => Equals(button.Content, App.Text("MacroForgetDecision")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ContextIdle);
        manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await forget.Task;
    }

    private static async Task DecideMacroRequest(App app, MainWindow window, string path, string? displayedArguments,
        string? action = null, Action? beforeDecision = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        Window? dialog;
        while ((dialog = app.Windows.OfType<Window>().SingleOrDefault(candidate => candidate.Owner == window && candidate.IsVisible)) is null)
            await Task.Delay(10, timeout.Token);
        try
        {
            string title = displayedArguments is null ? "MacroReadRemote" : "MacroReview";
            CheckMacroLaunch(dialog.Title == App.Text(title), "Unexpected macro consent dialog.");
            var buttons = FindControls<Button>(dialog).ToArray();
            CheckMacroLaunch(buttons.Single(button => button.IsDefault).IsCancel, "External macro consent must default to Cancel.");
            var fields = FindControls<TextBox>(dialog).Where(box => box.IsReadOnly).Select(box => box.Text.ReplaceLineEndings("\n")).ToArray();
            CheckMacroLaunch(fields.Contains(path) && (displayedArguments is null || fields.Contains(displayedArguments)),
                "Macro review must show the path and all argument boundaries.");
            beforeDecision?.Invoke();
            if (action is not null)
                buttons.Single(button => Equals(button.Content, App.Text(action))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }
}
