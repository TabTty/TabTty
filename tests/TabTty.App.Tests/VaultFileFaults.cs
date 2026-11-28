using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckCredentialProfileWriteAsync(App app, string directory, FilePublicationFixture publication)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        var first = await RegisterAsync("First");
        var second = await RegisterAsync("Second");
        byte[] preserved = await File.ReadAllBytesAsync(first.Path, token);
        byte[] secondOriginal = await File.ReadAllBytesAsync(second.Path, token);
        var editor = new ProfileEditorWindow(app, "EditProfile", first.Path, await ConnectionProfile.LoadAsync(first.Path, token), false);
        var form = (FormView)editor.Content;
        var group = (TextBox)form.FindName("ProfileGroupName");
        editor.Show();
        try
        {
            using var pause = publication.PauseNextWrite();
            var update = app.Profiles.UpdateCredentialReferencesAsync(new([first, second], 0), "password", "After");
            await pause.Issued.Task.WaitAsync(token);
            group.Text = "Unsaved group";
            File.Delete(pause.Preservation!);
            pause.Dispose();
            var result = await update.WaitAsync(token);
            if (result.Updated != 1 || result.Problems.Count != 1 ||
                !result.Problems[0].Contains(App.Text("ProfileSaveResultUnknown"), StringComparison.Ordinal) ||
                !result.Problems[0].Contains(first.Path, StringComparison.Ordinal) ||
                (await ConnectionProfile.LoadAsync(second.Path, token)).SshPassword?.Name != "After")
                throw new InvalidOperationException("The unresolved write did not remain independent of the other profile update.");

            byte[] secondSaved = await File.ReadAllBytesAsync(second.Path, token);
            if (secondOriginal.SequenceEqual(secondSaved)) throw new InvalidOperationException("The second write did not change the file.");
            await File.WriteAllBytesAsync(pause.Preservation!, preserved, token);
            var recheck = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("CheckProfileSaveResult")));
            if (!recheck.IsVisible) throw new InvalidOperationException("The affected profile had no pending verification action.");
            recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, recheck));
            while (form.IsBusy) await Task.Delay(10, token);
            byte[] secondAfterCheck = await File.ReadAllBytesAsync(second.Path, token);
            if (recheck.IsVisible || !form.HasUnsavedChanges || group.Text != "Unsaved group" ||
                VerifiedFilePublication.IsUnresolved(first.Path) ||
                (await ConnectionProfile.LoadAsync(first.Path, token)).SshPassword?.Name != "After" ||
                !secondSaved.AsSpan().SequenceEqual(secondAfterCheck))
                throw new InvalidOperationException("Checking the original profile write changed the draft or replayed another write.");
        }
        finally
        {
            group.Text = "";
            editor.Close();
        }

        async Task<ProfileCatalog.CredentialProfileMatch> RegisterAsync(string name)
        {
            string file = Path.Combine(directory, name + ".ttty");
            var profile = ConnectionProfile.CreateSsh(name, "localhost", "fixture", password: new("Before", "password"));
            await profile.SaveNewAsync(file, token);
            string location = await ProfileCatalog.NormalizeLocationAsync(file, token);
            Guid registration = Guid.NewGuid();
            ProfileState.Change(state => state["registrations"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["registrationId"] = registration, ["path"] = location, ["displayPath"] = location,
                ["favorite"] = false, ["lastUsed"] = null,
            }));
            return new(registration, location, profile.TrustVersion);
        }
    }
    private static async Task CheckVaultFirstUseAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            CheckVaultStatus(host, unlocked: false);
            await CheckSharedStatusRowAsync(host);
            await CheckAsync(empty: false);
            await CheckAsync(empty: true);
        }
        finally { host.Close(); }

        async Task CheckAsync(bool empty)
        {
            string path = Path.Combine(directory, empty ? "first-use-empty.tttykey" : "first-use-missing.tttykey");
            if (empty) File.WriteAllBytes(path, []);
            var original = CredentialVault.InspectFile(path);
            await app.SaveSettingsAsync(app.SettingsRevision, new(VaultPath: path));
            CheckVaultStatus(host, unlocked: false);

            Task cancelled = host.Dispatcher.InvokeAsync(UseVault).Task.Unwrap();
            var prompt = await FindDialogAsync("InitializeVault");
            prompt.Close();
            await cancelled.WaitAsync(deadline.Token);
            if (CredentialVault.InspectFile(path) != original)
                throw new InvalidOperationException("Cancelling first use changed the missing or empty file.");

            Task accepted = host.Dispatcher.InvokeAsync(UseVault).Task.Unwrap();
            prompt = await FindDialogAsync("InitializeVault");
            string caption = string.Format(App.Text("MasterPasswordWithLength"),
                VaultContainer.MinimumMasterLength, VaultContainer.MaximumMasterLength);
            var fields = FindControls<PasswordBox>(prompt).ToArray();
            var labels = FindControls<TextBlock>(prompt).ToArray();
            bool showsPathLabel = FindControls<DialogText>(prompt).Any(value => value.Text == ProfileLocation.Normalize(path) &&
                AutomationProperties.GetLabeledBy(value) is TextBlock label && label.Text == App.Text("VaultPath") + ":");
            bool showsExpectedPrompt = labels.Any(label => label.Text == caption) &&
                labels.Any(label => label.Text == App.Text("InitializeEmptyVaultDescription")) == empty;
            bool hasTextBox = FindControls<TextBox>(prompt).Any(field => !field.IsReadOnly);
            if (fields.Length != 2 || showsPathLabel != empty || !showsExpectedPrompt || hasTextBox)
            {
                prompt.Close();
                throw new InvalidOperationException(
                    $"Vault setup fields: {fields.Length} passwords, path label: {showsPathLabel}, " +
                    $"expected prompt: {showsExpectedPrompt}, text box: {hasTextBox}.");
            }
            foreach (var field in fields) field.Password = "fixture password for vault first use";
            var save = FindControls<Button>(prompt).Single(button => Equals(button.Content, App.Text("Save")));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (empty)
            {
                var picker = await FindDialogAsync("SelectSavedPassword");
                if (!FindControls<Button>(picker).Any(button => Equals(button.Content, App.Text("ManageVault"))) ||
                    !FindControls<TextBlock>(picker).Any(label => label.Text == App.Text("NoSavedPasswords")))
                    throw new InvalidOperationException("An empty profile credential picker must offer vault management.");
                picker.Close();
            }
            await accepted.WaitAsync(deadline.Token);
            if (CredentialVault.InspectFile(path) is not { Length: > 0 } ||
                (!empty && !FindControls<Button>(host).Any(button => Equals(button.Content, App.Text("AddVaultPassword")))))
                throw new InvalidOperationException("First use did not publish the new vault.");

            if (!app.Vault.IsSelectedUnlocked)
                throw new InvalidOperationException("Vault initialization did not retain the shared unlock.");
            await app.Vault.UnlockFromMenuAsync(host).WaitAsync(deadline.Token);
            CheckVaultStatus(host, unlocked: true);

            Task choose = host.Dispatcher.InvokeAsync(ChooseSavedPassword).Task.Unwrap();
            (await FindDialogAsync("SelectSavedPassword")).Close();
            await choose.WaitAsync(deadline.Token);

            Task UseVault() => empty ? ChooseSavedPassword() : app.Vault.UnlockFromMenuAsync(host);
        }

        Task ChooseSavedPassword() => app.Vault.ChoosePasswordAsync(host, _ =>
            throw new InvalidOperationException("A new vault had a saved password."));

        async Task<Window> FindDialogAsync(string title)
        {
            Window? dialog;
            while ((dialog = app.Windows.Cast<Window>().SingleOrDefault(window => window != host && window.IsVisible)) is null)
                await Task.Delay(15, deadline.Token);
            if (dialog.Title == App.Text(title)) return dialog;
            string actual = dialog.Title;
            dialog.Close();
            throw new InvalidOperationException($"Expected vault dialog {title}, got {actual}.");
        }
    }

    private static async Task CheckVaultRenameAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        const string master = "vault rename master password";
        const string original = "Team password";
        const string renamed = "Renamed team password";
        string vaultPath = Path.Combine(directory, "rename.tttykey");
        using (var vault = await CredentialVault.CreateAsync(vaultPath, master.AsMemory(), token))
            await vault.AddPasswordAsync(original, "fixture secret"u8.ToArray(), token);
        await app.SaveSettingsAsync(app.SettingsRevision, new(VaultPath: vaultPath));
        string saved = await MakeProfileAsync("Saved", register: true);
        string changed = await MakeProfileAsync("Changed", register: true);
        string pending = await MakeProfileAsync("Pending", register: true);
        string unregistered = await MakeProfileAsync("Unregistered", register: false);

        var host = app.CreateWindow(restorePlacement: false);
        host.Show();
        var before = await ConnectionProfile.LoadAsync(saved, token);
        var editor = new ProfileEditorWindow(app, "EditProfile", pending, await ConnectionProfile.LoadAsync(pending, token),
            creating: false);
        editor.Show();
        var draftName = FindControls<TextBox>(editor).Single(field => field.Text == "Pending");
        draftName.Text = "Unsaved draft";
        try
        {
            var target = new TerminalTab(saved);
            target.ResolveTabHost(() => host);
            var read = app.Vault.ReadSavedPasswordAsync(host, new CredentialReference(original, "password"), token, tab: target);
            var targetedUnlock = await FindDialogAsync("UnlockVault", form: true);
            try
            {
                targetedUnlock.UpdateLayout();
                var body = (FrameworkElement)targetedUnlock.Content;
                var labels = FindControls<TextBlock>(targetedUnlock).ToArray();
                var values = FindControls<DialogText>(targetedUnlock).ToArray();
                var tabValue = values.Single(value => value.Text == PermissionDisplay.Literal(target.TabCaption));
                var pathValue = values.Single(value => value.Text == ProfileLocation.Normalize(vaultPath));
                if (AutomationProperties.GetLabeledBy(tabValue) is not TextBlock tabLabel ||
                    tabLabel.Text != App.Text("DecisionTargetTabLabel") + ":" ||
                    AutomationProperties.GetLabeledBy(pathValue) is not TextBlock pathLabel ||
                    pathLabel.Text != App.Text("VaultPath") + ":")
                    throw new InvalidOperationException("The vault unlock fields did not identify the target and path.");
                var masterLabel = labels.Single(label => label.Text == App.Text("MasterPassword"));
                CheckDialogFieldLayout(targetedUnlock, false, tabValue, pathValue);
                double offset = pathLabel.TranslatePoint(new Point(), body).X - tabLabel.TranslatePoint(new Point(), body).X;
                double outerGap = masterLabel.TranslatePoint(new Point(), body).Y -
                    pathValue.TranslatePoint(new Point(0, pathValue.ActualHeight), body).Y;
                if (Math.Abs(offset) > 1 || outerGap < 16)
                    throw new InvalidOperationException($"Vault unlock layout offsets (DIP): left {offset:F1}, between fields {outerGap:F1}.");
            }
            finally
            {
                targetedUnlock.Close();
                using var unused = await read.WaitAsync(token);
            }

            Task management = host.Dispatcher.InvokeAsync(() => app.Vault.ManageAsync(host)).Task.Unwrap();
            var unlock = await FindDialogAsync("UnlockVault", form: true);
            if (!FindControls<DialogText>(unlock).Any(value => value.Text == ProfileLocation.Normalize(vaultPath) &&
                value.TextWrapping == TextWrapping.Wrap && AutomationProperties.GetLabeledBy(value) is TextBlock label &&
                label.Text == App.Text("VaultPath") + ":") ||
                FindControls<TextBox>(unlock).Any(field => !field.IsReadOnly))
            {
                unlock.Close();
                throw new InvalidOperationException("Vault unlock must display the labeled, copyable path.");
            }
            FindControls<PasswordBox>(unlock).Single().Password = master;
            Click(unlock, "UnlockVault");
            await management.WaitAsync(token);
            ListView? list;
            while ((list = FindControls<ListView>(host).FirstOrDefault(control => control.Items.OfType<VaultEntryInfo>().Any())) is null)
                await Task.Delay(15, token);

            byte[] beforeLock = File.ReadAllBytes(vaultPath);
            var peer = app.CreateWindow(restorePlacement: false);
            peer.ShowActivated = false;
            peer.Show();
            try
            {
                var hostCommand = VaultLockCommand(host);
                var peerCommand = VaultLockCommand(peer);
                CheckVaultStatus(host, unlocked: true);
                CheckVaultStatus(peer, unlocked: true);
                if (!Equals(hostCommand.Header, App.Text("LockVault")) || !Equals(peerCommand.Header, App.Text("LockVault")))
                    throw new InvalidOperationException("The unlocked vault did not show the lock command across windows.");

                peerCommand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                CheckVaultStatus(host, unlocked: false);
                CheckVaultStatus(peer, unlocked: false);
                if (app.Vault.IsSelectedUnlocked || list.Items.Count != 0 ||
                    !Equals(hostCommand.Header, App.Text("UnlockVault")) || !Equals(peerCommand.Header, App.Text("UnlockVault")) ||
                    !beforeLock.AsSpan().SequenceEqual(File.ReadAllBytes(vaultPath)))
                    throw new InvalidOperationException("Manual lock did not clear the shared unlock while preserving the vault file.");

                hostCommand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                unlock = await FindDialogAsync("UnlockVault", form: true);
                FindControls<PasswordBox>(unlock).Single().Password = master;
                Click(unlock, "UnlockVault");
                await WaitIdleAsync();
                CheckVaultStatus(host, unlocked: true);
                CheckVaultStatus(peer, unlocked: true);
                if (!Equals(peerCommand.Header, App.Text("LockVault")) ||
                    list.Items.OfType<VaultEntryInfo>().Single().Name != original)
                    throw new InvalidOperationException("Unlocking again did not restore the shared entries and lock command.");
            }
            finally { peer.Close(); }

            var (edit, confirm) = await OpenRenameAsync(renamed);
            var renameFields = FindControls<DialogText>(confirm).ToDictionary(
                field => ((TextBlock)AutomationProperties.GetLabeledBy(field)).Text, field => field.Text);
            string reviewed = renameFields[App.Text("KnownCredentialReferences") + ":"];
            if (renameFields[App.Text("CurrentCredentialName") + ":"] != PermissionDisplay.Literal(original) ||
                renameFields[App.Text("NewCredentialName") + ":"] != PermissionDisplay.Literal(renamed) ||
                !reviewed.Contains(saved, StringComparison.OrdinalIgnoreCase) || reviewed.Contains(unregistered, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Rename confirmation did not show the requested names and registered scope.");
            Click(confirm, "Cancel");
            while (!FindControls<Button>(edit).Any(button => button.IsEnabled && Equals(button.Content, App.Text("OK"))))
                await Task.Delay(15, token);
            if (!edit.IsVisible) throw new InvalidOperationException("Cancelling the rename closed the edit dialog.");
            edit.Close();
            await WaitIdleAsync();
            if (list.Items.OfType<VaultEntryInfo>().Single().Name != original)
                throw new InvalidOperationException("Cancelling renamed the vault credential.");

            byte[] originalVault = File.ReadAllBytes(vaultPath);
            (edit, confirm) = await OpenRenameAsync(renamed);
            using (VerifiedFilePublication.ReserveNamespace([Path.Combine(directory, "reserve-artifacts")], 256L * 1024 * 1024))
            {
                Click(confirm, "RenameCredentialOnly");
                await AcknowledgeReportAsync(edit, "The file publication artifact budget reached its limit.");
            }
            var editForm = (FormView)edit.Content;
            if (!edit.IsVisible || editForm.IsBusy || editForm.Reports.HasReports ||
                FindControls<TextBox>(edit).Single().Text != renamed ||
                !originalVault.AsSpan().SequenceEqual(File.ReadAllBytes(vaultPath)))
                throw new InvalidOperationException("Acknowledging a failed credential save lost its draft or changed the vault.");
            edit.Close();
            await WaitIdleAsync();

            (edit, confirm) = await OpenRenameAsync(renamed);
            File.AppendAllText(changed, "\n");
            Click(confirm, "RenameAndUpdateProfiles");
            await AcknowledgeReportAsync(host, string.Format(App.Text("CredentialRenameUpdatedProfiles"), 1),
                App.Text("CredentialRenameProfileChanged"), App.Text("CredentialRenamePendingProfileEdits"));
            await WaitIdleAsync();
            if (edit.IsVisible) throw new InvalidOperationException("A successful rename did not close the edit dialog.");

            var after = await ConnectionProfile.LoadAsync(saved, token);
            if (after.ToEditorJson() != before.ToEditorJson().Replace(original, renamed, StringComparison.Ordinal) ||
                list.Items.OfType<VaultEntryInfo>().Single().Name != renamed)
                throw new InvalidOperationException("The rename changed more than the credential name or failed to save it.");
            foreach (string path in new[] { changed, pending, unregistered })
                if ((await ConnectionProfile.LoadAsync(path, token)).SshPassword?.Name != original)
                    throw new InvalidOperationException("The rename overwrote a profile outside the reviewed save basis.");
            if (draftName.Text != "Unsaved draft")
                throw new InvalidOperationException("The rename replaced an unsaved profile draft.");

            (_, confirm) = await OpenRenameAsync("Final password");
            Click(confirm, "RenameCredentialOnly");
            await WaitIdleAsync();
            if (list.Items.OfType<VaultEntryInfo>().Single().Name != "Final password" ||
                (await ConnectionProfile.LoadAsync(saved, token)).SshPassword?.Name != renamed)
                throw new InvalidOperationException("Rename only also changed the registered profile.");
            Console.WriteLine("PASS VaultRenameUpdatesOnlyReviewedRegisteredProfiles");

            const string another = "Another password";
            _ = host.Dispatcher.BeginInvoke(() => Click(host, "AddVaultPassword"));
            var add = await FindDialogAsync("AddVaultPassword", form: true);
            FindControls<TextBox>(add).Single().Text = another;
            FindControls<PasswordBox>(add).Single().Password = "fixture secret";
            Click(add, "Save");
            await WaitIdleAsync();
            if (list.SelectionMode != SelectionMode.Extended || list.Items.Count != 2)
                throw new InvalidOperationException("The vault list did not allow multiple entries to be selected.");
            list.SelectAll();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ContextMenu menu = await OpenMenuAsync();
                MenuItem editAction = EntryAction(menu, "VaultEditEntry");
                MenuItem deleteAction = EntryAction(menu, "VaultDeleteEntry");
                if (editAction.IsEnabled || !deleteAction.IsEnabled)
                    throw new InvalidOperationException("The vault menu offered the wrong actions for multiple entries.");
                _ = host.Dispatcher.BeginInvoke(() => deleteAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                var delete = await FindDialogAsync("DeleteCredential", form: false);
                if (!FindControls<DialogText>(delete).Any(block => block.Text.Contains("Final password") &&
                    block.Text.Contains(another)))
                    throw new InvalidOperationException("The confirmation did not identify both selected vault entries.");
                Click(delete, attempt == 0 ? "Cancel" : "DeleteCredential");
                await WaitIdleAsync();
                if (list.Items.Count != (attempt == 0 ? 2 : 0))
                    throw new InvalidOperationException("The vault bulk deletion did not follow the confirmation.");
            }
            Console.WriteLine("PASS VaultManagementDeletesMultipleSelectedEntries");

            async Task<(Window Edit, Window Confirm)> OpenRenameAsync(string newName)
            {
                list.SelectedIndex = 0;
                var editAction = EntryAction(await OpenMenuAsync(), "VaultEditEntry");
                _ = host.Dispatcher.BeginInvoke(() => editAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, editAction)));
                var edit = await FindDialogAsync("EditCredential", form: true);
                AssertSaveRequiresExplicitActivation((FormView)edit.Content, "OK");
                if (FindControls<CheckBox>(edit).Single().Content is not TextBlock caption ||
                    caption.Text != App.Text("ReplacePassword"))
                    throw new InvalidOperationException("The password replacement checkbox did not display its caption inline.");
                var name = FindControls<TextBox>(edit).Single();
                if (name.Text != ((VaultEntryInfo)list.SelectedItem).Name)
                    throw new InvalidOperationException("The vault editor did not show the selected credential name.");
                name.Text = newName;
                _ = edit.Dispatcher.BeginInvoke(() => Click(edit, "OK"));
                return (edit, await FindDialogAsync("EditCredential", form: false));
            }

            static MenuItem EntryAction(ContextMenu menu, string key) =>
                menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text(key)));

            async Task<ContextMenu> OpenMenuAsync()
            {
                ContextMenu menu = list.ContextMenu;
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler onOpened = (_, _) => opened.TrySetResult();
                menu.Opened += onOpened;
                menu.PlacementTarget = list;
                try
                {
                    menu.IsOpen = true;
                    await opened.Task.WaitAsync(token);
                }
                finally
                {
                    menu.Opened -= onOpened;
                    menu.IsOpen = false;
                }
                return menu;
            }
        }
        finally
        {
            foreach (Window dialog in app.Windows.OfType<Window>().Where(window => window != host && window != editor).ToArray())
                dialog.Close();
            draftName.Text = "Pending";
            editor.Close();
            host.Close();
        }

        async Task<string> MakeProfileAsync(string name, bool register)
        {
            var profile = ConnectionProfile.CreateSsh(name, "localhost", "operator", password: new(original, "password"),
                methods: ["publicKey"]);
            string path = Path.Combine(directory, name + ".ttty");
            await profile.SaveNewAsync(path, token);
            path = await ProfileCatalog.NormalizeLocationAsync(path, token);
            if (register)
                ProfileState.Change(state => state["registrations"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["registrationId"] = Guid.NewGuid(), ["path"] = path, ["displayPath"] = path,
                    ["favorite"] = false, ["lastUsed"] = null,
                }));
            return path;
        }

        async Task<Window> FindDialogAsync(string title, bool form)
        {
            while (true)
            {
                if (app.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible &&
                    window.Title == App.Text(title) && (window is MainWindow.FormWindow) == form) is { } found)
                    return found;
                await Task.Delay(15, token);
            }
        }

        static void Click(Window window, string key) => FindControls<Button>(window)
            .Single(button => Equals(button.Content, App.Text(key)))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        static MenuItem VaultLockCommand(MainWindow window)
        {
            var tools = ((Menu)window.FindName("mainMenu")).Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, App.Text("ToolsMenu")));
            var vault = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("Vault")));
            return (MenuItem)vault.Items[0];
        }

        async Task WaitIdleAsync()
        {
            while (app.Vault.HasActiveOperations) await Task.Delay(15, token);
        }
    }

    private sealed class VaultFileFaultFixture
    {
        internal readonly TaskCompletionSource<string> Installing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseInstallation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseHandoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Archived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseArchive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly FilePublicationFixture Restore = new();
        internal string? RestoreSource;
        internal bool HoldHandoff;

        internal VaultFileHooks Hooks => new((step, source, target) =>
        {
            if (step == VaultInitializationStep.InstallCandidate && Installing.TrySetResult(source))
                ReleaseInstallation.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            File.Move(source, target, overwrite: false);
        }, () =>
        {
            if (!HoldHandoff) return Task.CompletedTask;
            HoldHandoff = false;
            Handoff.SetResult();
            return ReleaseHandoff.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }, _ => RestoreSource, Restore.Issue, (source, destination) =>
        {
            File.Move(source, destination, overwrite: false);
            if (!Archived.TrySetResult(destination)) return;
            ReleaseArchive.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new IOException("Controlled error after the archive move.");
        });
    }

    private static async Task CheckVaultFilesAsync(App app, string directory, VaultFileFaultFixture fixture)
    {
        const string master = "fixture password for vault failures";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(38));
        var token = deadline.Token;
        string empty = Path.Combine(directory, "empty.tttykey");
        string first = Path.Combine(directory, "first.tttykey");
        string backup = Path.Combine(directory, "backup.tttykey");
        File.WriteAllBytes(empty, []);
        var originalEmpty = CredentialVault.InspectFile(empty)!;
        CredentialReference reference;
        using (var owned = await CredentialVault.CreateAsync(first, master.AsMemory(), token))
        {
            string entry = await owned.AddPasswordAsync("prior metadata", "prior fixture secret"u8.ToArray(), token);
            reference = new(entry, "password");
            await owned.BackupAsync(backup, token);
            await owned.UpdateEntryAsync(entry, "later metadata", "later fixture secret"u8.ToArray(), token);
        }
        fixture.RestoreSource = backup;
        var host = app.CreateWindow(restorePlacement: false);
        host.Show();
        try
        {
            await SelectAsync(empty);
            using (VaultController.ManagementDeliveries.Reserve(VaultController.ManagementDeliveryLimit))
            {
                StartManagement();
                await AcknowledgeWindowReportAsync(host, App.Text("VaultBusy"));
                await UntilIdleAsync();
                if (fixture.Installing.Task.IsCompleted || CredentialVault.InspectFile(empty) != originalEmpty)
                    throw new InvalidOperationException("Notification pressure admitted vault initialization.");
            }
            Console.WriteLine("PASS VaultManagementDeliveryAdmission");

            StartManagement();
            (await FindDialogAsync("InitializeVault", form: true)).Close();
            await UntilIdleAsync();
            if (fixture.Installing.Task.IsCompleted || CredentialVault.InspectFile(empty) != originalEmpty)
                throw new InvalidOperationException("Dismissed initialization input changed the empty file.");

            StartManagement();
            await AcceptPasswordAsync("InitializeVault", "Save", "InitializeEmptyVaultDescription");
            string stage = await fixture.Installing.Task.WaitAsync(token);
            string parked = Directory.GetFiles(directory, ".tabtty-empty-*.tmp").Single();
            if (!app.Vault.HasActiveOperations || File.Exists(empty) || CredentialVault.InspectFile(parked) != originalEmpty)
                throw new InvalidOperationException("Initialization lost its operation or parked file before the window close.");
            host.Close();
            if (!host.IsVisible)
                throw new InvalidOperationException("The issuing window closed while vault initialization was publishing.");
            try
            {
                await VerifiedFilePublication.Begin(new(empty, "competing writer"u8.ToArray(), null, null, 1024)).ObserveAsync(token);
                throw new InvalidOperationException("Initialization did not retain its shared namespace.");
            }
            catch (IOException) when (!File.Exists(empty)) { }
            byte[] collision = "separately created target"u8.ToArray();
            File.WriteAllBytes(empty, collision);
            fixture.ReleaseInstallation.SetResult();
            await AcknowledgeWindowReportAsync(host, parked, stage, App.Text("PublishedImageOther"));
            await UntilIdleAsync();
            if (!File.ReadAllBytes(empty).AsSpan().SequenceEqual(collision) || !VerifiedFilePublication.IsUnresolved(empty))
                throw new InvalidOperationException("Initialization overwrote the competing file or lost its restriction.");
            File.Move(empty, Path.Combine(directory, "external-file.tttykey"));
            File.Move(stage, empty);
            await app.Vault.ManageAsync(host);
            await UntilAsync(() => CurrentButton("RecheckFilePublication") is not null);
            Start("RecheckFilePublication");
            await AcceptPasswordAsync("RecheckFilePublication", "UnlockVault");
            await UntilIdleAsync();
            if (VerifiedFilePublication.IsUnresolved(empty) || CredentialVault.InspectFile(parked) != originalEmpty)
                throw new InvalidOperationException("Initialization reinspection changed the original or failed to adopt.");
            Console.WriteLine("PASS VaultInitializationCollisionAndReinspection");

            await SelectAsync(first);
            Task manage = app.Vault.ManageAsync(host);
            await AcceptPasswordAsync("UnlockVault", "UnlockVault");
            await manage.WaitAsync(token);
            await UntilAsync(() => CurrentButton("RestoreVault") is not null);
            byte[] prior = File.ReadAllBytes(first);
            Start("RestoreVault");
            Click(await FindDialogAsync("RestoreVault", form: false), "RestoreVault");
            (await FindDialogAsync("RestoreVault", form: true)).Close();
            await UntilIdleAsync();
            if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(prior) ||
                Directory.GetFiles(directory, "*.recovery-*.tttykey").Length != 0)
                throw new InvalidOperationException("Dismissed restore input changed the file or created recovery artifacts.");

            using (var hold = fixture.Restore.PauseNextWrite())
            {
                Start("RestoreVault");
                Click(await FindDialogAsync("RestoreVault", form: false), "RestoreVault");
                await AcceptPasswordAsync("RestoreVault", "UnlockVault");
                await hold.Issued.Task.WaitAsync(token);
                string preservation = hold.Preservation!;
                if (!File.ReadAllBytes(preservation).AsSpan().SequenceEqual(prior) ||
                    !FindControls<ListBox>(host).Any(list => list.Items.OfType<VaultEntryInfo>()
                        .Any(entry => entry.Name == "later metadata")))
                    throw new InvalidOperationException("Restore exposed metadata before verifying preservation.");
                File.Delete(preservation);
                hold.Dispose();
                await AcknowledgeReportAsync(host, preservation, App.Text("PublishedImageMissing"));
                await UntilIdleAsync();
                Start("RecheckFilePublication");
                await AcknowledgeReportAsync(host, preservation, App.Text("PublishedImageMissing"));
                await UntilIdleAsync();
                File.WriteAllBytes(preservation, prior);
                Start("RecheckFilePublication");
                await UntilIdleAsync();
                await UntilAsync(() => FindControls<ListBox>(host).Any(list => list.Items.OfType<VaultEntryInfo>()
                    .Any(entry => entry.Name == "prior metadata")));
            }
            Console.WriteLine("PASS VaultRestorePublishesMetadataAfterReinspection");

            fixture.HoldHandoff = true;
            Task<VaultSecret?> old = app.Vault.ReadSavedPasswordAsync(host, reference, token);
            await fixture.Handoff.Task.WaitAsync(token);
            long epoch = app.Vault.SelectionEpoch;
            await SelectAsync(empty);
            await SelectAsync(first);
            if (old.IsCompleted || app.Vault.SelectionEpoch != epoch + 2 || !app.Vault.HasActiveOperations)
                throw new InvalidOperationException("Changing the selected role retired the earlier credential read prematurely.");
            using (var current = await app.Vault.ReadSavedPasswordAsync(host, reference, token))
                if (current is null || !current.Bytes.Span.SequenceEqual("prior fixture secret"u8))
                    throw new InvalidOperationException("The replacement role could not read its current credential.");
            fixture.ReleaseHandoff.SetResult();
            using var discarded = await old.WaitAsync(token);
            if (discarded is not null || app.Vault.HasActiveOperations)
                throw new InvalidOperationException("An old credential survived a selection switch-back.");
            Console.WriteLine("PASS VaultLateCredentialAfterSelectionSwitchBack");

            byte[] beforeArchive = File.ReadAllBytes(first);
            byte[] priorBackup = File.ReadAllBytes(backup);
            if (!app.Activate(InstanceLaunch.Parse(["--edit", first])))
                throw new InvalidOperationException("The archive editor was not admitted.");
            VaultEditorWindow? archiveEditor = null;
            await UntilAsync(() => (archiveEditor = app.Windows.OfType<VaultEditorWindow>()
                .SingleOrDefault(window => window.IsVisible)) is not null);
            await UntilAsync(() => FindControls<Button>(archiveEditor!).Any(button =>
                button.IsEnabled && Equals(button.Content, App.Text("ArchiveVault"))));
            _ = app.Dispatcher.BeginInvoke(() => Click(archiveEditor!, "ArchiveVault"));
            Click(await FindDialogAsync("ArchiveVault", form: false), "ArchiveVault");
            string archive = await fixture.Archived.Task.WaitAsync(token);
            archiveEditor!.Close();
            if (!archiveEditor.IsVisible || !app.Vault.HasActiveOperations || File.Exists(first) ||
                !File.ReadAllBytes(archive).AsSpan().SequenceEqual(beforeArchive))
                throw new InvalidOperationException("Archive closure released its owner before the issued move returned.");
            try
            {
                await VerifiedFilePublication.Begin(new(first, "competing initialization"u8.ToArray(), null, null, 1024))
                    .ObserveAsync(token);
                throw new InvalidOperationException("Initialization bypassed the archive namespace.");
            }
            catch (IOException) when (!File.Exists(first)) { }
            fixture.ReleaseArchive.SetResult();
            var acknowledged = await FindDialogAsync("ArchiveVault", form: false);
            EnsureMessage(acknowledged, archive, StringComparison.OrdinalIgnoreCase);
            EnsureMessage(acknowledged, App.Text("VaultArchived"));
            if (!archiveEditor.IsVisible) throw new InvalidOperationException("The archive editor closed before acknowledgement.");
            Click(acknowledged, "OK");
            await UntilAsync(() => !archiveEditor.IsVisible && !app.Vault.HasActiveOperations);
            string archiveLocation = string.Format(App.Text("VaultArchiveDestination"), archive);
            if (!FindControls<TextBlock>(host).Any(text => text.IsVisible && text.Text.Contains(archiveLocation)) ||
                File.Exists(first) || app.VaultPath != first || !File.ReadAllBytes(backup).AsSpan().SequenceEqual(priorBackup))
                throw new InvalidOperationException("Archive changed the selected path, created a replacement or moved its backup.");
            Console.WriteLine("PASS VaultArchiveOwnsIssuedMoveUntilTheEditorAcknowledges");

            var peer = app.CreateWindow(restorePlacement: false);
            peer.Show();
            try
            {
                using (var heldRestore = fixture.Restore.PauseNextWrite())
                {
                    var restoring = app.Dispatcher.InvokeAsync(() => app.Vault.RestoreFromMenuAsync(host)).Task.Unwrap();
                    Click(await FindDialogAsync("RestoreVault", form: false), "RestoreVault");
                    await AcceptPasswordAsync("RestoreVault", "UnlockVault");
                    await heldRestore.Issued.Task.WaitAsync(token);
                    host.OpenForm("Settings", _ => { });
                    heldRestore.Dispose();
                    await restoring.WaitAsync(token);
                }
                if (app.StatusReports.Pending(host).Count != 1 ||
                    FindControls<FormView>(host).Any(view => view.Reports.HasReports))
                    throw new InvalidOperationException("A menu restore did not keep its result at application scope after a tab switch.");
                var restoreFields = app.StatusReports.Pending(peer).Single().Content.Fields;
                if (!restoreFields.Any(field => field.Caption == App.Text("BackupFilePath") &&
                        ProfileLocation.SameVaultOpeningPath(field.Value, backup)) ||
                    !restoreFields.Any(field => field.Caption == App.Text("VaultPath") &&
                        ProfileLocation.SameVaultOpeningPath(field.Value, first)))
                    throw new InvalidOperationException($"The restore report did not label the source and destination separately: {ReportField.Describe(restoreFields)}");
                await AcknowledgeWindowReportAsync(peer, App.Text("VaultRestoreCompleted"), backup, first);

                var archiving = app.Dispatcher.InvokeAsync(() => app.Vault.ArchiveAsync(host)).Task.Unwrap();
                Click(await FindDialogAsync("ArchiveVault", form: false), "ArchiveVault");
                await archiving.WaitAsync(token);
                var archiveReport = app.StatusReports.Pending(peer).Single();
                var archivedPath = archiveReport.Content.Fields.Single(field => field.Caption == App.Text("VaultArchivePath")).Value;
                if (!File.Exists(archivedPath)) throw new InvalidOperationException("The menu archive omitted its verified destination.");
                await AcknowledgeWindowReportAsync(peer, App.Text("VaultArchived"), archivedPath);
                if (app.StatusReports.Pending(host).Count != 0)
                    throw new InvalidOperationException("A reviewed menu result stayed pending in the issuing window.");

                string invalidStore = Path.Combine(directory, "invalid-highlights.json");
                File.WriteAllText(invalidStore, "{");
                new HighlightCatalog(app, invalidStore, null).Show(host);
                if (app.StatusReports.Pending(peer).Count != 0 || FindControls<FormView>(host).Any(view => view.Reports.HasReports))
                    throw new InvalidOperationException("A failure opening a manager was attributed to another view or window.");
                await AcknowledgeWindowReportAsync(host, App.Text("HighlightStoreInvalid"));
                Console.WriteLine("PASS MenuResultsFollowTheirOperationScope");
            }
            finally { peer.Close(); }
        }
        finally
        {
            fixture.ReleaseInstallation.TrySetResult();
            fixture.ReleaseHandoff.TrySetResult();
            fixture.ReleaseArchive.TrySetResult();
            foreach (Window dialog in app.Windows.Cast<Window>().Where(window =>
                window.Owner == host || window is MainWindow.FormWindow).ToArray())
                if (dialog.IsVisible) dialog.Close();
            if (host.IsVisible) host.Close();
        }

        Task SelectAsync(string path) => app.SaveSettingsAsync(app.SettingsRevision, new(VaultPath: path));

        Button? CurrentButton(string key) => FindControls<Button>(host).SingleOrDefault(button =>
            button.IsVisible && Equals(button.Content, App.Text(key)));

        void StartManagement() => _ = app.Dispatcher.BeginInvoke(async () => await app.Vault.ManageAsync(host));

        void Start(string key) => _ = app.Dispatcher.BeginInvoke(() =>
            CurrentButton(key)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        async Task UntilAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(15, token);
        }

        Task UntilIdleAsync() => UntilAsync(() => !app.Vault.HasActiveOperations);

        async Task<Window> FindDialogAsync(string key, bool form)
        {
            Window? match = null;
            await UntilAsync(() => (match = app.Windows.Cast<Window>().SingleOrDefault(window =>
                window != host && window.IsVisible && window.Title == App.Text(key) &&
                (window is MainWindow.FormWindow) == form)) is not null);
            return match!;
        }

        async Task AcceptPasswordAsync(string title, string button, string? message = null)
        {
            Window dialog = await FindDialogAsync(title, form: true);
            if (message is not null) EnsureMessage(dialog, App.Text(message));
            foreach (var box in FindControls<PasswordBox>(dialog)) box.Password = master;
            Click(dialog, button);
        }

        static void EnsureMessage(Window dialog, string expected, StringComparison comparison = StringComparison.Ordinal)
        {
            if (!FindControls<TextBlock>(dialog).Any(text => text.Text.Contains(expected, comparison)) &&
                !FindControls<DialogText>(dialog).Any(value => value.Text.Contains(expected, comparison)))
                throw new InvalidOperationException($"The vault dialog omitted the expected publication fact '{expected}'.");
        }

        static void Click(Window dialog, string key) => FindControls<Button>(dialog)
            .Single(button => Equals(button.Content, App.Text(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
