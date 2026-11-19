using System.ComponentModel;
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
        string first = Path.Combine(folder, "first.json"), second = Path.Combine(folder, "second.json");
        const string text = "#tttym 1\n#constants First \"first.json\"\n#constants Second \"second.json\"\n" +
            "#constants Repeated \"first.json\"\nif (First.Delay != 3 || Second.Delay != 7 || Repeated.Delay != 3) { var fail = 1 / 0; }";
        const string a = "{\"Delay\":3}", b = "{\"Delay\":7}";
        // SHA-256 fixtures for the declaration sequences [a,b,a] and [b,a,b].
        const string original = "6815B761AE1303122312075D268F4C724582D17A8492D6ADEF28AEF8DF2CBB21";
        const string swapped = "4FB406930BB8041F695B0E56E8CC444FD17F402810C53112BA34FC7CBFEE355C";
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
        MacroTrust.UseTestStore(() => { if (failWrite) throw new Win32Exception(5); });
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
                    try { CheckMacroLaunch(FindControls<TextBox>(contents).Any(box => box.Text == b), "Inspection did not use captured JSON."); }
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
            foreach (var edit in new[] { a + " ", "{\"Delay\":3,\"Unused\":true}", a + "\r\n" })
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
            var resetOwner = app.CreateWindow(restorePlacement: false);
            resetOwner.Show();
            try
            {
                OpenSettings(resetOwner);
                var form = (FormView)((TabControl)resetOwner.FindName("tabs")).SelectedContent;
                var navigation = FindControls<ListBox>(form).Single();
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                    .Single(item => item.Content is TextBlock heading && heading.Text == App.Text("SettingsMaintenance"));
                void ResetFromOtherWindow(string choice)
                {
                    var answer = resetOwner.Dispatcher.InvokeAsync(() =>
                    {
                        var confirm = app.Windows.OfType<Window>().Single(w => w.Title == App.Text("MacroTrustReset") && w.IsVisible);
                        try
                        {
                            CheckMacroLaunch(FindControls<Button>(confirm).Single(button => button.IsDefault).Content.Equals(App.Text("No")),
                                "Reset did not default to cancellation.");
                            Click(confirm, choice);
                        }
                        finally { if (confirm.IsVisible) confirm.Close(); }
                    }, DispatcherPriority.ContextIdle);
                    Click(resetOwner, "MacroTrustReset");
                    answer.Task.GetAwaiter().GetResult();
                }
                ResetFromOtherWindow("No");
                CheckMacroLaunch(MacroTrust.Read(legacy) == MacroTrustDecision.Allow && MacroTrust.Read(allow) == MacroTrustDecision.Allow &&
                    MacroTrust.Read(otherArgs) == MacroTrustDecision.Block, "Cancelling reset changed remembered decisions.");
                request = window.OpenExternalMacroAsync(launch);
                await DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"), "MacroAlwaysAllow", () => ResetFromOtherWindow("Yes"));
                await DismissError("MacroTrustChanged");
                await request;
            }
            finally { resetOwner.Close(); }
            CheckMacroLaunch(MacroTrust.List().Count == 0 && window.TerminalTabs.Count() == before, "A stale review recreated a reset decision.");
        }
        finally
        {
            MacroTrust.Reset();
            MacroTrust.UseTestStore();
            await SetLanguage(previousLanguage);
        }

        Task SetLanguage(string language) => app.SaveSettingsAsync(language, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, app.VaultPath, app.SettingsRevision);

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
}
