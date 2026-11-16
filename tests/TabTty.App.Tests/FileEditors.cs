using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckFileEditors(App app, string directory)
    {
        string first = Path.Combine(directory, "First profile.ttty");
        string second = Path.Combine(directory, "Second profile.ttty");
        string selectedPath = Path.Combine(directory, "Selected vault.tttykey");
        string copyPath = Path.Combine(directory, "Vault copy.tttykey");

        var opens = InstanceLaunch.Parse([first, second]);
        var edits = InstanceLaunch.Parse(["--edit", first, second]);
        if (opens.Length != 2 || opens.Any(launch => launch.Edit) ||
            edits.Length != 2 || edits.Any(launch => !launch.Edit) ||
            !edits.Select(launch => launch.Path).SequenceEqual([first, second]))
            throw new InvalidOperationException("The profile file verbs lost their intent or input order.");
        if (!InstanceLaunch.Parse([selectedPath]).Single().IsVault ||
            !InstanceLaunch.Parse(["--edit", selectedPath]).Single().IsVault)
            throw new InvalidOperationException("A vault verb was routed as a profile.");
        Reject([selectedPath, copyPath]);
        Reject(["--edit", selectedPath, copyPath]);
        Reject(["--misspelled-edit", first]);
        Reject(["--edit", Path.ChangeExtension(first, "tttym")]);

        await ConnectionProfile.CreateTelnet("First profile", "127.0.0.1").SaveNewAsync(first);
        await ConnectionProfile.CreateTelnet("Second profile", "127.0.0.1").SaveNewAsync(second);
        if (!app.Activate(edits)) throw new InvalidOperationException("Profile edits were not admitted.");
        await UntilAsync(() => app.Windows.OfType<ProfileEditorWindow>().Count() == 2);
        string firstLocation = ProfileLocation.Normalize(first);
        var firstEditor = app.Windows.OfType<ProfileEditorWindow>().Single(window => window.Identity == firstLocation);
        if (!app.Activate(InstanceLaunch.Parse(["--edit", first])) || !app.Activate([]))
            throw new InvalidOperationException("A repeated edit or normal launch was not admitted.");
        await UntilAsync(() => app.Windows.OfType<MainWindow>().Any(window => window.CanAcceptTabs));
        var terminal = app.Windows.OfType<MainWindow>().Single();
        if (terminal.TerminalTabs.Any() || app.Windows.OfType<ProfileEditorWindow>().Count() != 2 ||
            !app.Windows.OfType<ProfileEditorWindow>().Contains(firstEditor))
            throw new InvalidOperationException("Edit opened a connection or duplicated the existing editor.");
        Console.WriteLine("PASS ProfileEditorActivation");

        const string master = "editor-fixture-master";
        using (await CredentialVault.CreateAsync(selectedPath, master.AsMemory())) { }
        File.Copy(selectedPath, copyPath);
        SelectVault(selectedPath);
        Console.WriteLine("PASS VaultEditorFixture");
        VaultEditorWindow a = await OpenVaultAsync(selectedPath);
        Console.WriteLine("PASS SelectedVaultEditorUnlock");
        VaultEditorWindow b = await OpenVaultAsync(copyPath);
        Console.WriteLine("PASS IndependentVaultEditorUnlock");
        if (ReferenceEquals(a, b)) throw new InvalidOperationException("A separate copy reused the selected vault's editor.");
        await ManageWithoutPromptAsync();
        SelectVault(copyPath);
        await ManageWithoutPromptAsync();
        b.Close();
        await ManageWithoutPromptAsync();
        SelectVault(selectedPath);
        await ManageWithoutPromptAsync();
        if (!a.IsLoaded || !FindControls<TextBox>(a).Any(box => box.Text == ProfileLocation.Normalize(selectedPath)) ||
            app.VaultPath != selectedPath)
            throw new InvalidOperationException("Switching the selected vault moved or closed another editor.");

        Console.WriteLine("PASS CheckFileEditors");

        async Task<VaultEditorWindow> OpenVaultAsync(string path)
        {
            if (!app.Activate(InstanceLaunch.Parse([path])))
                throw new InvalidOperationException("The vault editor request was not admitted.");
            await UntilAsync(() => app.Windows.OfType<MainWindow.FormWindow>().Any(window => window.IsLoaded));
            var prompt = app.Windows.OfType<MainWindow.FormWindow>().Single(window => window.IsLoaded);
            try
            {
                FindControls<PasswordBox>(prompt).Single().Password = master;
                FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("UnlockVault")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editor = app.Windows.OfType<VaultEditorWindow>()
                    .Single(window => window.Title.EndsWith(Path.GetFileName(path), StringComparison.Ordinal));
                await UntilAsync(() => editor.Content is FormView { IsEnabled: true });
                return editor;
            }
            finally
            {
                if (prompt.IsVisible) prompt.Close();
            }
        }

        void SelectVault(string path) => app.SaveSettings(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, path,
            app.SettingsRevision);

        async Task ManageWithoutPromptAsync()
        {
            Task manage = app.Dispatcher.InvokeAsync(() => app.Vault.ManageAsync(terminal)).Task.Unwrap();
            await UntilAsync(() => manage.IsCompleted || app.Windows.OfType<MainWindow.FormWindow>().Any());
            var prompt = app.Windows.OfType<MainWindow.FormWindow>().SingleOrDefault();
            if (prompt is not null)
            {
                prompt.Close();
                await manage;
                throw new InvalidOperationException("Managing an already unlocked vault prompted again.");
            }
            await manage;
        }

        async Task UntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!predicate() && !timeout.IsCancellationRequested)
                await Task.Delay(15);
            if (predicate()) return;
            foreach (Window window in app.Windows.Cast<Window>().Where(window => window.Owner is not null).Reverse().ToArray())
                window.Close();
            throw new TimeoutException("The expected editor or password prompt did not finish.");
        }

        static void Reject(string[] arguments)
        {
            try { _ = InstanceLaunch.Parse(arguments); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("An incompatible file-verb command was accepted.");
        }
    }
}
