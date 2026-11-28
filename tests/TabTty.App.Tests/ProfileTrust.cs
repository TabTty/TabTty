using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void AcceptFixtureProfileReviews() =>
        EventManager.RegisterClassHandler(typeof(MainWindow.FormWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is not MainWindow.FormWindow review || review.Title != App.Text("ProfileTrustReview")) return;
                review.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    FindControls<Button>(review).Single(button => Equals(button.Content, App.Text("ProfileConnectOnce")))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            }));

    private static async Task CheckProfileTrustAsync(App app, string directory)
    {
        var owner = app.CreateWindow(restorePlacement: false);
        owner.Show();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        string sourcePath = Path.Combine(directory, "profile.ttty");
        string catalogFolder = Path.Combine(directory, "profiles");
        string path = sourcePath;
        try
        {
            await SetProfileFolderAsync(catalogFolder);
            var remote = app.Profiles.AdmitAsync(owner, @"\\localhost\TabTty-profile-review-test\profile.ttty", null, token);
            Window? remoteReview;
            while ((remoteReview = app.Windows.OfType<Window>().FirstOrDefault(window =>
                window.IsLoaded && window.Title == App.Text("ProfileReadSource"))) is null) await Task.Delay(10, token);
            Click(remoteReview, "Cancel");
            try { await remote; throw new InvalidOperationException("Cancelling the source read admitted a connection."); }
            catch (SshInteractionCancelledException) { }

            var original = ConnectionProfile.CreateTelnet("First version", "localhost", 23001);
            await original.SaveNewAsync(path, token);
            string originalVersion = original.TrustVersion;
            var initial = app.Profiles.AdmitAsync(owner, path, null, token, reconnect: false, previousOnce: null);
            await AnswerAsync("ProfileImportAndConnect", "ProfileConnectOnce");
            var first = await initial;
            CheckMacroLaunch(first.Once is not null && first.Profile.TrustVersion == originalVersion &&
                app.Profiles.ReadRegistrations().Count == 0, "Connect once registered a file.");
            var reconnect = await app.Profiles.AdmitAsync(owner, path, null, token, reconnect: true, first.Once)
                .WaitAsync(TimeSpan.FromSeconds(2), token);
            CheckMacroLaunch(reconnect.Once == first.Once, "An unchanged reconnect did not reuse the tab's decision.");
            var stateFiles = new[] { Path.Combine(LocalState.DirectoryPath, "profile-catalog.json"), Path.Combine(LocalState.DirectoryPath, "profile-catalog.json.bak") };
            var lastWrites = stateFiles.Select(ReadUsageFile).ToArray();
            await app.Profiles.RememberUsageAsync(path, first.Profile);
            await app.Profiles.RememberUsageAsync(path, reconnect.Profile);
            CheckMacroLaunch(lastWrites.SequenceEqual(stateFiles.Select(ReadUsageFile)),
                "Using an unregistered profile wrote local state or its backup.");

            static (DateTime Time, string? Content) ReadUsageFile(string file) => File.Exists(file)
                ? (File.GetLastWriteTimeUtc(file), File.ReadAllText(file)) : default;

            var admission = app.Profiles.AdmitAsync(owner, path, "Fixture macro", token);
            await ReviewAsync();
            MacroTrust.Reset();
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            await admission;
            await app.Profiles.AdmitAsync(owner, path, "Second macro", token);
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 0, "Macro trust registered the profile.");

            Directory.CreateDirectory(catalogFolder);
            string occupied = Path.Combine(catalogFolder, "profile.ttty");
            string occupiedSecond = Path.Combine(catalogFolder, "profile (2).ttty");
            File.WriteAllText(occupied, "existing");
            File.WriteAllText(occupiedSecond, "existing");
            File.AppendAllText(sourcePath, "\n");
            var changedReconnect = app.Profiles.AdmitAsync(owner, sourcePath, null, token, reconnect: true, first.Once);
            Window review = await ReviewAsync();
            byte[] reviewed = File.ReadAllBytes(sourcePath);
            File.AppendAllText(sourcePath, " ");
            Click(review, "ProfileImportAndConnect");
            var imported = (await changedReconnect).Profile;
            path = Path.Combine(catalogFolder, "profile (3).ttty");
            CheckMacroLaunch(imported.OpenedPath == path && imported.DisplayName == "profile (3)" &&
                File.ReadAllBytes(path).SequenceEqual(reviewed) && File.ReadAllText(sourcePath).EndsWith(' ') &&
                File.ReadAllText(occupied) == "existing" && File.ReadAllText(occupiedSecond) == "existing" &&
                app.Profiles.ReadRegistrations().Single().Name == "profile (3)",
                "The import did not preserve the reviewed bytes or allocate the next free name.");
            await app.Profiles.AdmitAsync(owner, path, null, token);
            await SetProfileFolderAsync(Path.Combine(directory, "another profile folder"));
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 0, "A different folder showed the prior catalog.");
            await SetProfileFolderAsync(catalogFolder);
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Single().Path == path, "The original catalog did not return.");
            File.AppendAllText(path, "\n");
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            var modified = await admission;
            CheckMacroLaunch(modified.TrustVersion != originalVersion, "A file-only byte change reused trust.");
            MacroTrust.Reset();
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 1, "A macro reset removed a profile registration.");
            await app.Profiles.AdmitAsync(owner, path, null, token);
            await app.Profiles.AdmitAsync(owner, path, "Another approved macro", token)
                .WaitAsync(TimeSpan.FromSeconds(2), token);

            app.Profiles.Show(owner);
            ListView? list;
            while ((list = FindControls<ListView>(owner).FirstOrDefault(control => control.Items.Count == 1)) is null)
                await Task.Delay(10, token);
            var groupTabs = FindControls<TabControl>(owner).Single(tabs =>
                System.Windows.Automation.AutomationProperties.GetName(tabs) == App.Text("ProfileGroups"));
            groupTabs.SelectedItem = groupTabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, ""));
            list.SelectedIndex = 0;
            const string oldDisplayPath = @"Z:\legacy-display-name.ttty";
            ProfileState.Change(state =>
            {
                var registration = state["registrations"]!.AsArray().Single()!;
                CheckMacroLaunch(registration["path"]!.GetValue<string>() == path && registration["displayPath"] is null,
                    "A new registration did not store exactly one opening path.");
                registration["displayPath"] = oldDisplayPath;
            });
            await ClickProfileActionAsync(list, "ProfileFavoriteAdd");
            var previousRegistration = app.Profiles.ReadRegistrations().Single();
            CheckMacroLaunch(previousRegistration.Favorite && previousRegistration.LastUsed is not null &&
                previousRegistration.Path == path && previousRegistration.Name == "profile (3)",
                "The registration lost usage or used the legacy display path.");
            await ClickProfileActionAsync(list, "EditProfile");
            const string groupName = "Lab_東京";
            await EditProfileNameAsync("profile (3)", "Main editor version", $" {groupName} ");
            var renamedRegistration = new ProfileCatalog(app).ReadRegistrations().Single();
            var storedRegistration = ProfileState.ReadForProfileTrust()["registrations"]!.AsArray().Single()!;
            CheckMacroLaunch(renamedRegistration.Id == previousRegistration.Id && renamedRegistration.Favorite &&
                renamedRegistration.LastUsed == previousRegistration.LastUsed && renamedRegistration.Path == path &&
                renamedRegistration.Name == "Main editor version" && storedRegistration["displayPath"]!.GetValue<string>() == oldDisplayPath,
                "Renaming the opening path lost registration data or rewrote the legacy field.");
            if (groupTabs.SelectedItem is not TabItem { Tag: string selectedGroup } || selectedGroup != groupName ||
                list.SelectedItem is not ProfileCatalog.Registration { GroupName: groupName } ||
                renamedRegistration.GroupName != groupName)
                throw new InvalidOperationException("The saved group did not update the management tab and the registration.");
            await ClickProfileActionAsync(list, "ProfileFavoriteRemove");
            var tabs = (TabControl)owner.FindName("tabs");
            var newTab = (Button)tabs.Template.FindName("newTabButton", tabs);
            newTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, newTab));
            var profileMenu = newTab.ContextMenu;
            try
            {
                var submenu = profileMenu.Items.OfType<MenuItem>().Single(item =>
                    System.Windows.Automation.AutomationProperties.GetName(item) ==
                    string.Format(App.Text("ProfileGroupCount"), groupName, 1));
                if (!profileMenu.IsOpen || submenu.Items.OfType<MenuItem>().Single().ToolTip is not string)
                    throw new InvalidOperationException("The new-tab menu did not put the profile in its group.");
                CheckMenuDidNotAnimate(profileMenu);
                await OpenMenuWithoutAnimationAsync(submenu);
            }
            finally { profileMenu.IsOpen = false; }
            await RefreshSelectionsAcrossWindowsAsync(list, groupTabs, groupName);
            await CompleteVerifiedSaveAfterReopeningAsync(list);
            await app.Profiles.AdmitAsync(owner, path, null, token);
            await app.Profiles.EditFromExplorerAsync(path);
            await EditProfileNameAsync("Main editor version", "Standalone editor version");
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            await admission;

            string other = Path.Combine(directory, "copy.ttty");
            File.Copy(path, other);
            var forCopy = app.Profiles.AdmitAsync(owner, other, "Fixture macro", token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            await forCopy;

            await CheckRecordingDestinationAsync(app, owner, directory, token);

            File.AppendAllText(path, "\n");
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            review = await ReviewAsync();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) =>
            {
                var confirm = app.Windows.OfType<Window>().FirstOrDefault(window => window.Title == App.Text("ProfileTrustReset"));
                if (confirm is null) return;
                timer.Stop();
                Click(confirm, "Reset");
            };
            timer.Start();
            var sourceDigest = new string('A', 64);
            var macroAllow = MacroTrustKey.Create(sourceDigest, ["left", "right"]);
            var macroBlock = MacroTrustKey.Create(sourceDigest, ["right", "left"]);
            try
            {
                MacroTrust.Save(macroAllow, MacroTrustDecision.Allow);
                MacroTrust.Save(macroBlock, MacroTrustDecision.Block);
                using var pendingMacro = MacroTrust.Evaluate(MacroTrustKey.Create(sourceDigest, []), elevated: false).Review!;
                app.Profiles.ResetTrust(owner, error => throw error);
                CheckMacroLaunch(!pendingMacro.Expired && MacroTrust.Read(macroAllow) == MacroTrustDecision.Allow &&
                    MacroTrust.Read(macroBlock) == MacroTrustDecision.Block, "A profile reset affected macro decisions.");
            }
            finally
            {
                timer.Stop();
                MacroTrust.Reset();
            }
            Click(review, "ProfileTrustAndConnect");
            await AnswerAsync("ProfileTrustAndConnect", "Cancel");
            try { await admission; throw new InvalidOperationException("A stale reply admitted a connection."); }
            catch (SshInteractionCancelledException) { }
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 1, "Reset removed a registration.");
            forCopy = app.Profiles.AdmitAsync(owner, other, "Fixture macro", token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileConnectOnce");
            await forCopy;

            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            await admission;
            var changedProfile = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
            changedProfile["postConnectionMacro"] = new System.Text.Json.Nodes.JsonObject
                { ["version"] = 1, ["source"] = "return;" };
            await File.WriteAllTextAsync(path, changedProfile.ToJsonString(), token);
            var existing = await app.Profiles.ImportFileAsync(owner, path, token).WaitAsync(TimeSpan.FromSeconds(2), token);
            CheckMacroLaunch(existing.PostConnectionSource == "return;", "Reimport did not select the existing profile.");
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            await AnswerAsync("ProfileTrustAndConnect", "Cancel");
            try { await admission; throw new InvalidOperationException("Selecting an existing profile renewed its trust."); }
            catch (SshInteractionCancelledException) { }
            list.SelectedIndex = 0;
            await ClickProfileActionAsync(list, "RemoveRegistration");
            var pendingImport = app.Profiles.ImportFileAsync(owner, path, token);
            var importReview = await ImportReviewAsync();
            var secondImport = app.Profiles.ImportFileAsync(owner, path, token);
            Click(await ImportReviewAsync(importReview), "PostConnectionMacroTrustProfile");
            await secondImport;
            list.SelectedIndex = 0;
            await ClickProfileActionAsync(list, "RemoveRegistration");
            Click(importReview, "PostConnectionMacroTrustProfile");
            try { await pendingImport; throw new InvalidOperationException("A stale import recreated the removed registration."); }
            catch (IOException error) when (error.Message == App.Text("ProfileTrustWriteFailed")) { }
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 0, "The registration was not removed.");
            admission = app.Profiles.AdmitAsync(owner, path, "Fixture macro", token);
            await AnswerAsync("ProfileTrustAndConnect", "Cancel");
            try { await admission; throw new InvalidOperationException("Unregister preserved remembered trust."); }
            catch (SshInteractionCancelledException) { }

            string numbered = Path.Combine(directory, "numbered (2).ttty");
            await ConnectionProfile.CreateTelnet("Unused draft name", "localhost").SaveNewAsync(numbered, token);
            var firstCopy = await app.Profiles.ImportFileAsync(owner, numbered, token);
            var secondCopy = await app.Profiles.ImportFileAsync(owner, numbered, token);
            CheckMacroLaunch(firstCopy.DisplayName == "numbered (2)" &&
                secondCopy.DisplayName == "numbered (2) (2)" && app.Profiles.ReadRegistrations().Count == 2 &&
                File.Exists(numbered), "Repeated import did not retain the entire original stem and source.");
            var inPlace = await app.Profiles.ImportFileAsync(owner, secondCopy.OpenedPath, token);
            CheckMacroLaunch(inPlace.OpenedPath == secondCopy.OpenedPath && app.Profiles.ReadRegistrations().Count == 2,
                "Importing a registered file in the destination directory created a copy.");

            await app.SaveSettingsAsync(app.SettingsRevision, new(Language: "ja"));
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            review = await ReviewAsync();
            CheckMacroLaunch(FindControls<Button>(review).Any(button => Equals(button.Content, "取り込んで接続")) &&
                FindControls<Button>(review).Any(button => Equals(button.Content, "今回だけ接続")),
                "Japanese review actions were missing.");
            Click(review, "Cancel");
            try { await admission; throw new InvalidOperationException("Cancel admitted a connection."); }
            catch (SshInteractionCancelledException) { }
        }
        finally { owner.Close(); }

        async Task<Window> ImportReviewAsync(Window? other = null)
        {
            while (true)
            {
                var found = app.Windows.OfType<Window>().FirstOrDefault(window => window != other &&
                    window.IsLoaded && window.Title == App.Text("PostConnectionMacroRegistration"));
                if (found is not null) return found;
                await Task.Delay(10, token);
            }
        }

        Task SetProfileFolderAsync(string folder) => app.SaveSettingsAsync(app.SettingsRevision, new(ProfileDirectory: folder));

        async Task RefreshSelectionsAcrossWindowsAsync(ListView list, TabControl groupTabs, string groupName)
        {
            const string name = "Second catalog profile";
            string extraPath = Path.Combine(catalogFolder, name + ".ttty");
            var extra = ConnectionProfile.CreateTelnet(name, "localhost", 23001);
            var draft = System.Text.Json.Nodes.JsonNode.Parse(extra.ToEditorJson())!;
            draft["groupName"] = groupName;
            extra = extra.Edit(draft.ToJsonString());
            await extra.SaveNewAsync(extraPath, token);
            var registering = app.Profiles.AdmitAsync(owner, extraPath, null, token);
            await AnswerAsync("ProfileImportAndConnect", "ProfileImportAndConnect");
            await registering;
            var other = app.CreateWindow(restorePlacement: false);
            other.Show();
            try
            {
                app.Profiles.Show(other);
                await app.Profiles.RememberUsageAsync(path, await ConnectionProfile.LoadAsync(path, token));
                while (list.Items.Count != 2) await Task.Delay(10, token);
                if (!((GridView)list.View).Columns.Select(column => column.Header).OfType<GridViewColumnHeader>().Any(header =>
                    System.Windows.Automation.AutomationProperties.GetName(header) == App.Text("ProfileListLastUsed")))
                    throw new InvalidOperationException("The catalog did not offer a last-used sort.");
                list.SelectAll();
                Guid[] selected = list.SelectedItems.Cast<ProfileCatalog.Registration>().Select(item => item.Id).Order().ToArray();
                await app.Profiles.RememberUsageAsync(extraPath, extra);
                var reordered = list.Items.Cast<ProfileCatalog.Registration>().ToArray();
                if (reordered[0].Name != name || !selected.SequenceEqual(list.SelectedItems.Cast<ProfileCatalog.Registration>()
                    .Select(item => item.Id).Order()))
                    throw new InvalidOperationException("Connection usage lost the catalog selection during sorting.");
                draft["groupName"] = "Moved out";
                extra = extra.Edit(draft.ToJsonString());
                await app.Profiles.RememberUsageAsync(extraPath, extra);
                if ((groupTabs.SelectedItem as TabItem)?.Tag as string != groupName ||
                    list.SelectedItems.Count != 1 || list.SelectedItem is not ProfileCatalog.Registration selectedItem ||
                    selectedItem.Id == reordered[0].Id)
                    throw new InvalidOperationException("Connection usage moved the group filter or kept a hidden selection.");
            }
            finally
            {
                other.Close();
                ProfileState.Change(state =>
                {
                    var entries = state["registrations"]!.AsArray();
                    entries.Remove(entries.Single(entry => entry!["path"]!.GetValue<string>() == extraPath));
                });
                await app.Profiles.RememberUsageAsync(path, await ConnectionProfile.LoadAsync(path, token));
            }
        }

        async Task CompleteVerifiedSaveAfterReopeningAsync(ListView profileList)
        {
            await ClickProfileActionAsync(profileList, "EditProfile");
            while (!app.Windows.OfType<ProfileEditorWindow>().Any(window => window.IsVisible))
                await Task.Delay(10, token);
            var editor = app.Windows.OfType<ProfileEditorWindow>().Single(window => window.IsVisible);
            var form = (FormView)editor.Content;
            const string savedGroup = "Retained completion";
            ((TextBox)form.FindName("ProfileGroupName")).Text = savedGroup;
            using (File.Open(Path.Combine(LocalState.DirectoryPath, "profile-catalog.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(editor, "Save");
                await AcknowledgeReportAsync(editor, App.Text("ProfileRegistrationIncomplete"));
                while (form.IsBusy) await Task.Delay(10, token);
                if ((await ConnectionProfile.LoadAsync(path, token)).GroupName != savedGroup ||
                    form.UnresolvedPublication is null)
                    throw new InvalidOperationException("The verified save did not retain its unfinished registration update.");
            }
            _ = editor.Dispatcher.BeginInvoke(() => editor.Close());
            await AnswerEditorNotice(editor);
            while (editor.IsVisible) await Task.Delay(10, token);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);

            await app.Profiles.EditFromExplorerAsync(path);
            editor = app.Windows.OfType<ProfileEditorWindow>().Single(window => window.IsVisible);
            form = (FormView)editor.Content;
            var field = (TextBox)form.FindName("ProfileGroupName");
            field.Text = "Unsaved draft after reopening";
            if (new ProfileCatalog(app).ReadRegistrations().Single().GroupName == savedGroup ||
                form.UnresolvedPublication is null)
                throw new InvalidOperationException("Reopening completed the profile save without consent.");
            Click(editor, "RetryProfileRegistration");
            while (form.IsBusy) await Task.Delay(10, token);
            byte[] after = await File.ReadAllBytesAsync(path, token);
            if (form.UnresolvedPublication is not null || !form.HasUnsavedChanges ||
                field.Text != "Unsaved draft after reopening" ||
                new ProfileCatalog(app).ReadRegistrations().Single().GroupName != savedGroup ||
                !bytes.AsSpan().SequenceEqual(after))
                throw new InvalidOperationException("Explicit completion overwrote the draft or failed to update the registration.");
            _ = editor.Dispatcher.BeginInvoke(() => editor.Close());
            await AnswerEditorNotice(editor);
            while (editor.IsVisible) await Task.Delay(10, token);

            async Task AnswerEditorNotice(Window source)
            {
                Window? dialog;
                while ((dialog = app.Windows.OfType<Window>().SingleOrDefault(window =>
                    window.IsVisible && window.Owner == source && window.Title == App.Text("DiscardAndClose"))) is null)
                    await Task.Delay(10, token);
                Click(dialog, "DiscardAndClose");
            }
        }

        async Task<Window> ReviewAsync()
        {
            Window? window;
            while ((window = app.Windows.OfType<Window>().FirstOrDefault(item =>
                item.IsLoaded && item.Title == App.Text("ProfileTrustReview"))) is null)
                await Task.Delay(10, token);
            CheckMacroLaunch(owner.IsEnabled, "Connection review disabled the other terminal controls.");
            CheckMacroLaunch(FindControls<Button>(window).Single(button => Equals(button.Content, App.Text("Cancel"))).IsDefault,
                "Cancel is not the default.");
            return window;
        }

        async Task AnswerAsync(string expected, string action)
        {
            Window review = await ReviewAsync();
            CheckMacroLaunch(FindControls<Button>(review).Any(button => Equals(button.Content, App.Text(expected))),
                "The expected action was missing.");
            Click(review, action);
        }

        async Task EditProfileNameAsync(string before, string after, string? group = null)
        {
            ProfileEditorWindow? editor;
            while ((editor = app.Windows.OfType<ProfileEditorWindow>().SingleOrDefault(window => window.IsLoaded)) is null)
                await Task.Delay(10, token);
            FindControls<TextBox>(editor).Single(box => box.Text == before).Text = after;
            if (group is not null)
                ((TextBox)((FormView)editor.Content).FindName("ProfileGroupName")).Text = group;
            Click(editor, "Save");
            var form = (FormView)editor.Content;
            while (form.IsBusy) await Task.Delay(10, token);
            string renamed = Path.Combine(catalogFolder, after + ".ttty");
            CheckMacroLaunch(!File.Exists(path) && (await ConnectionProfile.LoadAsync(renamed, token)).DisplayName == after,
                "The profile editor did not save its changes.");
            path = renamed;
            editor.Close();
        }

        async Task ClickProfileActionAsync(ListView list, string action)
        {
            ContextMenu menu = list.ContextMenu;
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler onOpened = (_, _) => opened.TrySetResult();
            menu.Opened += onOpened;
            try
            {
                list.UpdateLayout();
                var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
                row.Focus();
                InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(row), Environment.TickCount, Key.Apps)
                { RoutedEvent = Keyboard.KeyUpEvent, Source = row });
                await opened.Task.WaitAsync(token);
                var item = menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, App.Text(action)));
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            }
            finally
            {
                menu.Opened -= onOpened;
                menu.IsOpen = false;
            }
        }

        static void Click(Window window, string action) =>
            FindControls<Button>(window).Single(button => Equals(button.Content, App.Text(action)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task CheckRecordingDestinationAsync(App app, Window owner, string directory, CancellationToken token)
    {
        var inUse = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        char letter = Enumerable.Range('P', 11).Select(value => (char)value).LastOrDefault(value => !inUse.Contains(value));
        if (letter == default) throw new IOException("No free drive letter for the recording mapping test.");
        string drive = $"{letter}:";
        string original = Directory.CreateDirectory(Path.Combine(directory, "recording-original")).FullName;
        string replacement = Directory.CreateDirectory(Path.Combine(directory, "recording-replacement")).FullName;
        bool mapped = false;
        try
        {
            Map(original);
            mapped = true;
            string profilePath = Path.Combine(directory, "recording.ttty");
            var profile = ConnectionProfile.CreateTelnet("Recording destination", "localhost", recordRaw: true);
            var draft = System.Text.Json.Nodes.JsonNode.Parse(profile.ToEditorJson())!;
            draft["logging"]!["directory"] = drive + "\\Logs";
            await profile.Edit(draft.ToJsonString()).SaveNewAsync(profilePath, token);
            var admission = app.Profiles.AdmitAsync(owner, profilePath, "Mapping fixture", token);
            await AnswerAsync("ProfileTrustReview", "ProfileTrustAndConnect");
            profile = await admission;
            var destination = RecordingDestination.Capture(profilePath, profile);
            await app.Profiles.ApproveRecordingDestinationAsync(owner, destination, token);
            var recording = SessionRecording.Open(profilePath, profile, destination: destination);
            try
            {
                Map("/D");
                mapped = false;
                Map(replacement);
                mapped = true;
                await Task.Delay(1100, token);
                recording.Enqueue(RecordingFormat.Raw, [1]);
                await recording.Incomplete.WaitAsync(token);
                try { _ = recording.OpenPart(2); throw new InvalidOperationException("Resume opened a changed mapping."); }
                catch (RecordingDestinationChangedException) { }
                try
                {
                    _ = SessionRecording.Open(profilePath, profile, destination: destination);
                    throw new InvalidOperationException("Connection admission opened a changed mapping.");
                }
                catch (RecordingDestinationChangedException) { }
                CheckMacroLaunch(!Directory.EnumerateFileSystemEntries(replacement).Any(),
                    "Recording created a file or directory under a different mapping.");
                var trusted = await app.Profiles.AdmitAsync(owner, profilePath, "Mapping fixture", token)
                    .WaitAsync(TimeSpan.FromSeconds(2), token);
                CheckMacroLaunch(trusted.TrustVersion == profile.TrustVersion, "The new mapping invalidated profile trust.");
                var changed = RecordingDestination.Capture(profilePath, profile);
                var rejected = app.Profiles.ApproveRecordingDestinationAsync(owner, changed, token);
                await AnswerAsync("LogDestinationReview", "Cancel");
                try { await rejected; throw new InvalidOperationException("A declined mapping was accepted."); }
                catch (RecordingStartException) { }
                CheckMacroLaunch(!Directory.EnumerateFileSystemEntries(replacement).Any(), "Declining the mapping touched it.");
                var accepted = app.Profiles.ApproveRecordingDestinationAsync(owner, changed, token);
                await AnswerAsync("LogDestinationReview", "ApproveLogDestination");
                await accepted;
                await SessionRecording.Open(profilePath, profile, destination: changed).FinishAsync();
                CheckMacroLaunch(Directory.GetFiles(Path.Combine(replacement, "Logs"), "*.raw.tttylog").Length == 1,
                    "An approved mapping did not receive the log.");
            }
            finally { await recording.FinishAsync(); }
            Map("/D");
            mapped = false;
            Map(original);
            mapped = true;
            var retry = recording.OpenPart(3);
            await retry.FinishAsync();
            Console.WriteLine("PASS ReviewedRecordingDestinationRejectsChangedDriveMapping");
        }
        finally { if (mapped) Map("/D"); }

        async Task AnswerAsync(string title, string action)
        {
            Window? dialog;
            while ((dialog = app.Windows.OfType<Window>().FirstOrDefault(window => window.Title == App.Text(title))) is null)
                await Task.Delay(10, token);
            FindControls<Button>(dialog).Single(button => Equals(button.Content, App.Text(action)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        void Map(string target)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "subst.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(drive);
            start.ArgumentList.Add(target);
            using Process process = Process.Start(start) ?? throw new IOException("Could not start the drive mapping fixture.");
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
                throw new IOException("Could not update the recording fixture's drive mapping.");
        }
    }
}
