using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckFileEditors(App app, string directory, FilePublicationFixture settingsFixture,
        FilePublicationFixture profileFixture)
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
        var secondEditor = app.Windows.OfType<ProfileEditorWindow>().Single(window => window != firstEditor);
        FindControls<Button>(secondEditor).Single(button => Equals(button.Content, App.Text("CloseDialog")))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (secondEditor.IsVisible) throw new InvalidOperationException("Close did not dismiss an unchanged editor.");
        Console.WriteLine("PASS ProfileEditorActivation");
        await CheckProfilePublicationCloseAsync();
        Console.WriteLine("PASS ProfileEditorPreservesUnverifiedPublicationOnClose");

        const string master = "editor-fixture-master";
        using (await CredentialVault.CreateAsync(selectedPath, master.AsMemory())) { }
        File.Copy(selectedPath, copyPath);
        await SelectVault(selectedPath);
        Console.WriteLine("PASS VaultEditorFixture");
        VaultEditorWindow a = await OpenVaultAsync(selectedPath);
        Console.WriteLine("PASS SelectedVaultEditorUnlock");
        VaultEditorWindow b = await OpenVaultAsync(copyPath);
        Console.WriteLine("PASS IndependentVaultEditorUnlock");
        if (ReferenceEquals(a, b)) throw new InvalidOperationException("A separate copy reused the selected vault's editor.");
        await ManageWithoutPromptAsync();
        string savedSettings = Path.Combine(directory, "settings.json");
        byte[] previousSettings = await File.ReadAllBytesAsync(savedSettings);
        long previousEpoch = app.Vault.SelectionEpoch;
        int previousRevision = app.SettingsRevision;
        byte[] externalSettings = [.. previousSettings, (byte)'\n'];
        File.WriteAllBytes(savedSettings, externalSettings);
        try
        {
            await SelectVault(copyPath);
            throw new InvalidOperationException("The settings editor overwrote an external revision.");
        }
        catch (FilePublicationException error) when (error.Result.Status == FilePublicationStatus.Conflict) { }
        if (!File.ReadAllBytes(savedSettings).AsSpan().SequenceEqual(externalSettings) ||
            app.VaultPath != selectedPath || app.Vault.SelectionEpoch != previousEpoch || app.SettingsRevision != previousRevision)
            throw new InvalidOperationException("A stale settings draft changed the external file or effective role.");
        File.WriteAllBytes(savedSettings, previousSettings);
        using (VerifiedFilePublication.ReserveNamespace([Path.Combine(directory, "settings-capacity")], 256L * 1024 * 1024))
        {
            try
            {
                await SelectVault(copyPath);
                throw new InvalidOperationException("A settings save bypassed artifact capacity.");
            }
            catch (ResourceLimitException) { }
            if (app.VaultPath != selectedPath || app.Vault.SelectionEpoch != previousEpoch ||
                app.SettingsRevision != previousRevision || !File.ReadAllBytes(savedSettings).AsSpan().SequenceEqual(previousSettings))
                throw new InvalidOperationException("Settings capacity refusal changed the prior selection.");
        }
        using (var held = new FileStream(savedSettings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await SelectVault(copyPath);
                throw new InvalidDataException("The settings file was replaced while the fixture denied deletion.");
            }
            catch (FilePublicationException exception) when (exception.Result.Status == FilePublicationStatus.Unchanged) { }
        }
        if (app.VaultPath != selectedPath || app.Vault.SelectionEpoch != previousEpoch ||
            !previousSettings.AsSpan().SequenceEqual(File.ReadAllBytes(savedSettings)))
            throw new InvalidOperationException("A failed settings publication changed the file or selected owner.");
        using (var paused = settingsFixture.PauseNextWrite())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Task adopting = SelectVault(copyPath);
            await paused.Issued.Task.WaitAsync(deadline.Token);
            using var physical = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(savedSettings, deadline.Token));
            if (physical.RootElement.GetProperty("credentialVaultPath").GetString() != copyPath ||
                app.VaultPath != selectedPath || app.Vault.SelectionEpoch != previousEpoch || adopting.IsCompleted)
                throw new InvalidOperationException("An issued settings write exposed its role before verification.");
            b.Close();
            if (b.IsVisible) throw new InvalidOperationException("The reserved destination editor could not close.");
            paused.Dispose();
            await adopting.WaitAsync(deadline.Token);
        }
        await ManageWithoutPromptAsync();
        await SelectVault(selectedPath);
        await ManageWithoutPromptAsync();
        string selectedPathLabel = string.Format(App.Text("VaultPathDisplay"), ProfileLocation.Normalize(selectedPath));
        if (!a.IsLoaded || !FindControls<TextBlock>(a).Any(label => label.Text == selectedPathLabel) ||
            app.VaultPath != selectedPath)
            throw new InvalidOperationException("Switching the selected vault moved or closed another editor.");

        await CheckPartialSelectedVaultRecoveryAsync();
        Console.WriteLine("PASS PartialSettingsReinspectionAdoptsTheMatchingVaultOwner");
        await CheckUnlockHostTransferAsync();
        Console.WriteLine("PASS VaultUnlockTransfersTheLivePrompt");
        Console.WriteLine("PASS CheckFileEditors");

        async Task CheckPartialSelectedVaultRecoveryAsync()
        {
            var destinationEditor = await OpenVaultAsync(copyPath);
            byte[] original = File.ReadAllBytes(savedSettings);
            int priorRevision = app.SettingsRevision;
            long priorEpoch = app.Vault.SelectionEpoch;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var paused = settingsFixture.PauseNextWrite();
            Task changing = SelectVault(copyPath);
            await paused.Issued.Task.WaitAsync(deadline.Token);
            byte[] candidate = File.ReadAllBytes(savedSettings);
            File.Delete(savedSettings + ".bak");
            paused.Dispose();
            try { await changing; throw new InvalidOperationException("Unverified settings were adopted."); }
            catch (FilePublicationException error) when (error.Result.Status == FilePublicationStatus.UnverifiedPreservation) { }
            if (app.VaultPath != selectedPath || app.SettingsRevision != priorRevision ||
                app.Vault.SelectionEpoch != priorEpoch || !VerifiedFilePublication.IsUnresolved(savedSettings))
                throw new InvalidOperationException("Partial settings publication changed the effective owner or revision.");

            File.WriteAllBytes(savedSettings + ".bak", original);
            app.ShowStoreRecovery(terminal);
            await UntilAsync(() => FindControls<Button>(terminal).Any(button =>
                button.IsVisible && Equals(button.Content, App.Text("RecheckFilePublication"))));
            FindControls<Button>(terminal).Single(button => button.IsVisible &&
                Equals(button.Content, App.Text("RecheckFilePublication"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => app.VaultPath == copyPath);
            if (app.SettingsRevision != priorRevision + 1 || app.Vault.SelectionEpoch != priorEpoch + 1 ||
                !candidate.AsSpan().SequenceEqual(File.ReadAllBytes(savedSettings)) ||
                VerifiedFilePublication.IsUnresolved(savedSettings))
                throw new InvalidOperationException("Reinspection rewrote the candidate or split the effective settings from vault ownership.");
            await ManageWithoutPromptAsync();
            await SelectVault(selectedPath);
            destinationEditor.Close();
        }

        async Task CheckProfilePublicationCloseAsync()
        {
            var form = (FormView)firstEditor.Content;
            var name = FindControls<TextBox>(form).Single(box => box.Text == "First profile");
            name.Text = "New name under verification";
            byte[] original = File.ReadAllBytes(first);
            var save = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("Save")));
            string? replacementStage = null;
            string movedStage = Path.Combine(directory, "moved-profile-stage");
            profileFixture.ValidateNext(stage =>
            {
                replacementStage = stage;
                File.Move(stage, movedStage);
                File.Copy(movedStage, stage);
            });
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RespondAsync("Error", "OK", "The prepared file changed before publication.");
            await UntilAsync(() => !form.IsBusy);
            if (replacementStage is null ||
                !File.ReadAllBytes(replacementStage).AsSpan().SequenceEqual(File.ReadAllBytes(movedStage)) ||
                !File.ReadAllBytes(first).AsSpan().SequenceEqual(original) || !form.HasUnsavedChanges)
                throw new InvalidOperationException("The profile editor changed its destination or deleted a foreign stage.");

            byte[] external = [.. original, (byte)'\n'];
            File.WriteAllBytes(first, external);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RespondAsync("Error", "OK", App.Text("StoreChanged"));
            await UntilAsync(() => !form.IsBusy);
            if (!form.HasUnsavedChanges || !File.ReadAllBytes(first).AsSpan().SequenceEqual(external) || File.Exists(first + ".bak"))
                throw new InvalidOperationException("The stale profile editor lost its draft or changed the external file.");
            File.WriteAllBytes(first, original);
            using (VerifiedFilePublication.ReserveNamespace([Path.Combine(directory, "full-artifacts")], 256L * 1024 * 1024))
            {
                var dismiss = RespondAsync("Error", "OK", "The file publication artifact budget reached its limit.");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await dismiss;
                await UntilAsync(() => !form.IsBusy);
                if (!form.HasUnsavedChanges || !File.ReadAllBytes(first).AsSpan().SequenceEqual(original) || File.Exists(first + ".bak"))
                    throw new InvalidOperationException("A profile capacity refusal lost the draft or changed the file.");
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var paused = profileFixture.PauseNextWrite();
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await paused.Issued.Task.WaitAsync(deadline.Token);
            firstEditor.Close();
            if (!firstEditor.IsVisible || !FindControls<TextBlock>(form).Any(text =>
                text.IsVisible && text.Text == App.Text("FileOperationPending")))
                throw new InvalidOperationException("Issued publication lost its editor lifetime or pending status.");
            byte[] candidate = File.ReadAllBytes(first);
            if (candidate.AsSpan().SequenceEqual(original) ||
                !original.AsSpan().SequenceEqual(File.ReadAllBytes(first + ".bak")))
                throw new InvalidOperationException("Profile replacement did not preserve the original.");
            File.Delete(first + ".bak");
            paused.Dispose();
            await RespondAsync("Error", "OK");
            await UntilAsync(() => !form.IsBusy);

            var close = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("CloseDialog")));
            _ = app.Dispatcher.BeginInvoke(() => close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await RespondAsync(firstEditor.Title, "Cancel");
            if (!firstEditor.IsVisible) throw new InvalidOperationException("Declining closure dismissed the editor.");
            _ = app.Dispatcher.BeginInvoke(() => close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await RespondAsync(firstEditor.Title, "OK");
            await UntilAsync(() => !firstEditor.IsVisible);
            if (!VerifiedFilePublication.IsUnresolved(first) || !candidate.AsSpan().SequenceEqual(File.ReadAllBytes(first)))
                throw new InvalidOperationException("Closing the profile editor changed its physical outcome or write restriction.");

            File.WriteAllBytes(first + ".bak", original);
            if (!app.Activate(InstanceLaunch.Parse(["--edit", first])))
                throw new InvalidOperationException("Reopening the affected profile was not admitted.");
            ProfileEditorWindow? reopened = null;
            await UntilAsync(() => (reopened = app.Windows.OfType<ProfileEditorWindow>()
                .SingleOrDefault(window => window.IsVisible && window.Identity == firstLocation)) is not null);
            var recheck = FindControls<Button>(reopened!).Single(button =>
                Equals(button.Content, App.Text("RecheckFilePublication")));
            recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !((FormView)reopened!.Content).IsBusy);
            if (VerifiedFilePublication.IsUnresolved(first) ||
                !candidate.AsSpan().SequenceEqual(File.ReadAllBytes(first)) ||
                !original.AsSpan().SequenceEqual(File.ReadAllBytes(first + ".bak")))
                throw new InvalidOperationException("Reinspection from a reopened editor republished or lost its evidence.");
            reopened!.Close();

            async Task RespondAsync(string title, string action, string? message = null)
            {
                Window? dialog = null;
                await UntilAsync(() => (dialog = app.Windows.Cast<Window>()
                    .SingleOrDefault(window => window.IsVisible && window.Owner == firstEditor &&
                        window.Title == (title == "Error" ? App.Text(title) : title))) is not null);
                if (!FindControls<TextBlock>(dialog!).Any(text => message is not null
                    ? text.Text.Contains(message, StringComparison.Ordinal)
                    : text.Text.Contains(first + ".bak", StringComparison.Ordinal) &&
                      text.Text.Contains(App.Text("StoreSaveUnverified"), StringComparison.Ordinal)))
                    throw new InvalidOperationException("The editor dialog omitted the unverified recovery artifact.");
                FindControls<Button>(dialog!).Single(button => Equals(button.Content, App.Text(action)))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        async Task CheckUnlockHostTransferAsync()
        {
            string transferPath = Path.Combine(directory, "Transfer host vault.tttykey");
            var expected = "host-transfer-test-data"u8.ToArray();
            CredentialReference reference;
            using (var created = await CredentialVault.CreateAsync(transferPath, master.AsMemory()))
                reference = new(await created.AddPasswordAsync("fixture", expected), "password");
            await SelectVault(transferPath);

            var firstHost = new Window { Width = 320, Height = 240 };
            var nextHost = new Window { Width = 320, Height = 240 };
            MainWindow.FormWindow? prompt = null;
            try
            {
                firstHost.Show();
                nextHost.Show();
                Task<VaultSecret?> firstRead = app.Vault.ReadSavedPasswordAsync(firstHost, reference, CancellationToken.None);
                await UntilAsync(() => app.Windows.OfType<MainWindow.FormWindow>().Any(window => window.IsVisible));
                prompt = app.Windows.OfType<MainWindow.FormWindow>().Single(window => window.IsVisible);
                int reservedRevision = app.SettingsRevision;
                byte[] settingsBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "settings.json"));
                try
                {
                    await SelectVault(selectedPath);
                    throw new InvalidDataException("A busy vault allowed settings publication.");
                }
                catch (InvalidOperationException exception) when (exception.Message == App.Text("VaultBusy")) { }
                if (app.VaultPath != transferPath || app.SettingsRevision != reservedRevision ||
                    !settingsBytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "settings.json"))))
                    throw new InvalidOperationException("Busy rejection changed the saved or effective selection.");
                Task<VaultSecret?> nextRead = app.Vault.ReadSavedPasswordAsync(nextHost, reference, CancellationToken.None);
                firstHost.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using VaultSecret? closed = await firstRead.WaitAsync(deadline.Token);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (closed is not null || nextRead.IsCompleted || !prompt.IsVisible ||
                    app.Windows.OfType<MainWindow.FormWindow>().Count(window => window.IsVisible) != 1)
                    throw new InvalidOperationException("Closing the first host ended the prompt or retained its consumer.");

                FindControls<PasswordBox>(prompt).Single().Password = master;
                FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("UnlockVault")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using VaultSecret? surviving = await nextRead.WaitAsync(deadline.Token);
                if (surviving is null || !surviving.Bytes.Span.SequenceEqual(expected))
                    throw new InvalidOperationException("The live consumer did not receive the exact credential.");
            }
            finally
            {
                if (prompt?.IsVisible == true) prompt.Close();
                if (firstHost.IsVisible) firstHost.Close();
                if (nextHost.IsVisible) nextHost.Close();
                await SelectVault(selectedPath);
            }
        }

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

        Task SelectVault(string path) => app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
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

    private sealed class FilePublicationFixture
    {
        private HeldWrite? next;
        private Action<string>? nextValidation;

        internal void ValidateNext(Action<string> validate) => nextValidation = validate;
        internal void Validate(string stage) => Interlocked.Exchange(ref nextValidation, null)?.Invoke(stage);

        internal HeldWrite PauseNextWrite()
        {
            var operation = new HeldWrite();
            if (Interlocked.CompareExchange(ref next, operation, null) is not null)
                throw new InvalidOperationException("A file publication fixture is already armed.");
            return operation;
        }

        internal void Issue(string stage, string target, string? preservation)
        {
            if (preservation is null) File.Move(stage, target, overwrite: false);
            else File.Replace(stage, target, preservation);
            var operation = Interlocked.Exchange(ref next, null);
            if (operation is null) return;
            operation.Preservation = preservation;
            operation.Issued.SetResult();
            operation.Release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        internal sealed class HeldWrite : IDisposable
        {
            internal string? Preservation { get; set; }
            internal TaskCompletionSource Issued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Dispose() => Release.TrySetResult();
        }
    }
}
