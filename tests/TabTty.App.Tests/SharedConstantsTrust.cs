using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task SharedConstantsTrust(App app, MainWindow window, string directory)
    {
        string folder = Directory.CreateDirectory(Path.Combine(directory, "constants-trust")).FullName;
        string source = Path.Combine(folder, "review.tttym");
        string first = Path.Combine(folder, "first.tttyc"), second = Path.Combine(folder, "second.tttyc");
        const string text = "#tttym 1\n#constants First \"first.tttyc\"\n#constants Second \"second.tttyc\"\n" +
            "#constants Repeated \"first.tttyc\"\nif (First.Delay != 3 || Second.Delay != 7 || Repeated.Delay != 3) { var fail = 1 / 0; }";
        const string a = "long Delay = 3;", b = "long Delay = 7;";
        // SHA-256 fixtures for the declaration sequences [a,b,a] and [b,a,b].
        const string original = "9067AC8DB57FBD2C3930AC511B0E7F25BD47035F1EC4931FFCBD34AA0584B8B2";
        const string swapped = "B1F0FE6ACA90C4F39C9EE84A306C9BF664DAB03A4047C0EEB89FDA0E3907986F";
        void WriteInputs(string firstJson, string secondJson)
        {
            File.WriteAllText(source, text);
            File.WriteAllText(first, firstJson);
            File.WriteAllText(second, secondJson);
        }
        WriteInputs(a, b);
        string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var legacy = MacroTrustKey.Create(digest, []);
        var allow = MacroTrustKey.Create(digest, [], original);
        var block = MacroTrustKey.Create(digest, [], swapped);
        var otherArgs = MacroTrustKey.Create(digest, ["other"], original);
        var launch = new InstanceLaunch(source, []);
        string previousLanguage = app.LanguageChoice;
        bool failWrite = false;
        var storeId = Guid.NewGuid();
        int deletesBeforeFailure = 0;
        MacroTrust.UseTestStore(
            beforeWrite: () => { if (failWrite) throw new Win32Exception(5); },
            beforeDelete: () => { if (deletesBeforeFailure > 0 && --deletesBeforeFailure == 0) throw new IOException("Test deletion failure."); },
            storeId: storeId);
        try
        {
            await SetLanguage("en");
            MacroTrust.Save(legacy, MacroTrustDecision.Allow);
            MacroTrust.Save(otherArgs, MacroTrustDecision.Block);
            int before = window.TerminalTabs.Count();
            var request = window.OpenExternalMacroAsync(launch);
            await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"), "MacroAlwaysAllow", () =>
            {
                Window dialog = app.Windows.OfType<Window>().Single(w => w.Owner == window && w.IsVisible);
                CheckMacroLaunch(FindControls<TextBox>(dialog).Any(box => box.Text == original), "Combined version digest mismatch.");
                var aliases = FindControls<ComboBox>(dialog).Single();
                CheckMacroLaunch(aliases.Items.Cast<string>().SequenceEqual(new[] { "First", "Second", "Repeated" }),
                    "Review lost a declared alias or reordered the inputs.");
                aliases.SelectedIndex = 1;
                string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second)));
                var fields = FindControls<TextBox>(dialog).Select(box => box.Text).ToArray();
                CheckMacroLaunch(fields.Contains(second) && fields.Contains(expectedHash), "Review did not show the selected file and full digest.");
                File.Delete(second);
                File.WriteAllText(first, b);
                var inspect = dialog.Dispatcher.InvokeAsync(() =>
                {
                    var contents = app.Windows.OfType<Window>().Single(w => w.Owner == dialog && w.IsVisible);
                    try { CheckMacroLaunch(FindControls<TextBox>(contents).Any(box => box.Text == b), "Inspection did not use the captured definitions."); }
                    finally { contents.Close(); }
                }, DispatcherPriority.ContextIdle);
                Click(dialog, "MacroInspectConstants");
                inspect.Task.GetAwaiter().GetResult();
                MacroTrust.Forget(otherArgs.Fingerprint, otherArgs.Version);
            });
            await request.WaitAsync(TimeSpan.FromSeconds(6));
            var admitted = window.TerminalTabs.Last().Macro!;
            await admitted.Completion.WaitAsync(TimeSpan.FromSeconds(6));
            CheckMacroLaunch(admitted.Status.State == MacroRunState.Completed && window.TerminalTabs.Count() == ++before &&
                MacroTrust.Read(allow) == MacroTrustDecision.Allow, "Approval did not execute the reviewed constants.");

            WriteInputs(b, a);
            request = window.OpenExternalMacroAsync(launch);
            await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"), "MacroAlwaysBlock");
            await request;
            CheckMacroLaunch(MacroTrust.Read(block) == MacroTrustDecision.Block &&
                window.TerminalTabs.Count() == before, "Swapping the file contents did not select a distinct decision.");
            await window.OpenExternalMacroAsync(launch).WaitAsync(TimeSpan.FromSeconds(6));
            CheckMacroLaunch(window.TerminalTabs.Count() == before, "A remembered constants block started a run.");

            WriteInputs(a, b);
            string copy = Directory.CreateDirectory(Path.Combine(folder, "copy")).FullName;
            foreach (string input in new[] { source, first, second }) File.Copy(input, Path.Combine(copy, Path.GetFileName(input)), true);
            await window.OpenExternalMacroAsync(launch with { Path = Path.Combine(copy, Path.GetFileName(source)) }).WaitAsync(TimeSpan.FromSeconds(6));
            CheckMacroLaunch(window.TerminalTabs.Count() == ++before, "An identical copy of the macro and dependencies lost its decision.");
            await SetLanguage("ja");
            foreach (var edit in new[] { a + " ", a + " bool Unused = true;", a + "\r\n" })
            {
                File.WriteAllText(first, edit);
                request = window.OpenExternalMacroAsync(launch);
                await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"));
                await request;
            }
            File.WriteAllText(first, a, System.Text.Encoding.UTF8);
            request = window.OpenExternalMacroAsync(launch);
            await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"));
            await request;
            CheckMacroLaunch(window.TerminalTabs.Count() == before, "A formatting, BOM or unused-value change bypassed review.");

            File.Delete(source); File.Delete(first); File.Delete(second);
            MacroTrust.Forget(legacy.Fingerprint);
            MacroTrust.Save(otherArgs, MacroTrustDecision.Block);
            await ForgetFromSettings(app, window, block, (dialog, choices) =>
            {
                var find = dialog.Dispatcher.InvokeAsync(() =>
                {
                    var input = app.Windows.OfType<Window>().Single(w => w.Owner == dialog && w.IsVisible);
                    Click(input, "MacroFindArguments");
                }, DispatcherPriority.ContextIdle);
                Click(dialog, "MacroFindArguments");
                find.Task.GetAwaiter().GetResult();
                var matches = choices.Items.Cast<string>().ToArray();
                CheckMacroLaunch(matches.Length == 2 && matches.Any(s => s.Contains(original)) && matches.Any(s => s.Contains(swapped)),
                    "Settings did not find both versions for the same arguments after file deletion.");
            });
            CheckMacroLaunch(MacroTrust.Read(allow) == MacroTrustDecision.Allow && MacroTrust.Read(block) is null &&
                MacroTrust.Read(otherArgs) == MacroTrustDecision.Block, "Forgetting one version affected a different decision.");

            WriteInputs(b, a);
            failWrite = true;
            request = window.OpenExternalMacroAsync(launch);
            await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"), "MacroAlwaysAllow");
            await DismissError("MacroTrustUnavailable");
            await request;
            failWrite = false;
            CheckMacroLaunch(MacroTrust.Read(block) is null && window.TerminalTabs.Count() == before, "Failed save admitted a run.");

            MacroTrust.Save(legacy, MacroTrustDecision.Allow);
            MacroTrust.Save(MacroTrustKey.Create(digest, ["left", "right"], swapped), MacroTrustDecision.Allow);
            MacroTrust.Save(MacroTrustKey.Create(digest, ["right", "left"], swapped), MacroTrustDecision.Block);
            foreach (string language in new[] { "en", "ja" })
            {
                await SetLanguage(language);
                var resetOwner = app.CreateWindow(restorePlacement: false);
                resetOwner.Show();
                try
                {
                    OpenSettings(resetOwner);
                    var form = (FormView)((TabControl)resetOwner.FindName("tabs")).SelectedContent;
                    var navigation = FindControls<ListBox>(form).Single();
                    navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                        .Single(item => item.Content is TextBlock heading && heading.Text == App.Text("SettingsMaintenance"));
                    void ResetFromOtherWindow(params string[] choices)
                    {
                        var answers = choices.Select((choice, index) => resetOwner.Dispatcher.InvokeAsync(() =>
                        {
                            var confirm = app.Windows.OfType<Window>().Single(w => w.Owner == resetOwner && w.Title == App.Text("MacroTrustReset") && w.IsVisible);
                            try
                            {
                                CheckMacroLaunch(FindControls<Button>(confirm).Single(button => button.IsDefault).Content.Equals(App.Text("Cancel")),
                                    "Reset did not default to Cancel.");
                                string expected = index == 0 ? "MacroTrustResetConfirm" : "MacroTrustResetFailed";
                                CheckMacroLaunch(FindControls<TextBlock>(confirm).Any(text => text.Text == App.Text(expected)),
                                    "Reset omitted its confirmation or failure notice.");
                                Click(confirm, choice);
                            }
                            finally { if (confirm.IsVisible) confirm.Close(); }
                        }, DispatcherPriority.ContextIdle)).ToArray();
                        var reset = SettingsActionButton(form, "MacroTrustReset");
                        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, reset));
                        foreach (var answer in answers) answer.Task.GetAwaiter().GetResult();
                    }
                    ResetFromOtherWindow("Cancel");
                    CheckMacroLaunch(MacroTrust.Read(legacy) == MacroTrustDecision.Allow && MacroTrust.Read(allow) == MacroTrustDecision.Allow &&
                        MacroTrust.Read(otherArgs) == MacroTrustDecision.Block, "Cancelling reset changed remembered decisions.");
                    if (language == "ja")
                    {
                        deletesBeforeFailure = 2;
                        request = window.OpenExternalMacroAsync(launch);
                        await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"), "MacroAlwaysAllow",
                            () => ResetFromOtherWindow("MacroTrustReset", "MacroTrustResetRetry"));
                        await DismissError("MacroTrustChanged");
                        await request;
                    }
                }
                finally { resetOwner.Close(); }
            }
            CheckMacroLaunch(MacroTrust.List().Count == 0 && window.TerminalTabs.Count() == before, "A stale review recreated a reset decision.");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--macro-reset-relaunch");
            start.ArgumentList.Add(storeId.ToString());
            start.ArgumentList.Add(source);
            using var child = Process.Start(start) ?? throw new IOException("Could not start macro reset relaunch test.");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            CheckMacroLaunch(child.ExitCode == 0, "The restarted app failed to review the cleared decisions.");
        }
        finally
        {
            MacroTrust.Reset();
            MacroTrust.UseTestStore();
            await SetLanguage(previousLanguage);
        }

        Task SetLanguage(string language) => app.SaveSettingsAsync(app.SettingsRevision, new(Language: language));

        static void Click(Window owner, string resource) =>
            FindControls<Button>(owner).Single(button => Equals(button.Content, App.Text(resource))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        async Task DismissError(string resource)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            for (;;)
            {
                var dialog = app.Windows.OfType<Window>().SingleOrDefault(w => w.Owner == window && w.IsVisible && w.Title == App.Text("RunMacro"));
                if (dialog is not null)
                {
                    try { CheckMacroLaunch(FindControls<TextBlock>(dialog).Any(e => e.Text == App.Text(resource)), "Unexpected macro admission error."); }
                    finally { dialog.Close(); }
                    return;
                }
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    private static async Task CheckMacroResetRelaunch(App app, string storeId, string source)
    {
        MacroTrust.UseTestStore(storeId: Guid.Parse(storeId));
        var owner = app.CreateWindow(restorePlacement: false);
        owner.Show();
        try
        {
            CheckMacroLaunch(MacroTrust.List().Count == 0, "Recovered obsolete macro decisions after restart.");
            foreach (var arguments in new[] { new[] { "left", "right" }, new[] { "right", "left" } })
            {
                var launch = owner.OpenExternalMacroAsync(new InstanceLaunch(source, arguments));
                await DecideMacroRequest(app, owner, source, $"[0] \"{arguments[0]}\"\n[1] \"{arguments[1]}\"");
                await launch;
            }
            CheckMacroLaunch(!owner.TerminalTabs.Any(), "Cancelling a fresh review admitted the macro.");
        }
        finally { owner.Close(); }
    }
}
