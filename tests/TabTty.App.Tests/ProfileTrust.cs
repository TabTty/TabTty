using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        string path = Path.Combine(directory, "profile.ttty");
        try
        {
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
            var admission = app.Profiles.AdmitAsync(owner, path, null, token);
            await AnswerAsync("ProfileRegisterAndConnect", "ProfileConnectOnce");
            CheckMacroLaunch((await admission).Profile.TrustVersion == originalVersion &&
                app.Profiles.ReadRegistrations().Count == 0, "Connect once registered a file.");

            admission = app.Profiles.AdmitAsync(owner, path, "Fixture macro", token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            await admission;
            await app.Profiles.AdmitAsync(owner, path, "Second macro", token);
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 0, "Macro trust registered the profile.");

            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            Window review = await ReviewAsync();
            File.AppendAllText(path, "\n");
            Click(review, "ProfileRegisterAndConnect");
            CheckMacroLaunch((await admission).Profile.TrustVersion == originalVersion &&
                app.Profiles.ReadRegistrations().Count == 1, "Registration did not use the reviewed file revision.");
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            await AnswerAsync("ProfileTrustAndConnect", "ProfileTrustAndConnect");
            var modified = (await admission).Profile;
            CheckMacroLaunch(modified.TrustVersion != originalVersion, "A file-only byte change reused trust.");
            await app.Profiles.AdmitAsync(owner, path, null, token);

            app.Profiles.Show(owner);
            ListView? list;
            while ((list = FindControls<ListView>(owner).FirstOrDefault(control => control.Items.Count == 1)) is null)
                await Task.Delay(10, token);
            var groupTabs = FindControls<TabControl>(owner).Single(tabs =>
                System.Windows.Automation.AutomationProperties.GetName(tabs) == App.Text("ProfileGroups"));
            groupTabs.SelectedItem = groupTabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, ""));
            list.SelectedIndex = 0;
            await ClickProfileActionAsync(list, "EditProfile");
            const string groupName = "Lab_東京";
            await EditProfileNameAsync("First version", "Main editor version", $" {groupName} ");
            if (groupTabs.SelectedItem is not TabItem { Tag: string selectedGroup } || selectedGroup != groupName ||
                list.SelectedItem is not ProfileCatalog.Registration { GroupName: groupName } ||
                new ProfileCatalog(app).ReadRegistrations().Single().GroupName != groupName)
                throw new InvalidOperationException("The saved group did not update the management tab and the registration.");
            var tabs = (TabControl)owner.FindName("tabs");
            var newTab = (Button)tabs.Template.FindName("newTabButton", tabs);
            newTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, newTab));
            var profileMenu = newTab.ContextMenu;
            try
            {
                var submenu = profileMenu.Items.OfType<MenuItem>().Single(item =>
                    System.Windows.Automation.AutomationProperties.GetName(item) == groupName);
                if (!profileMenu.IsOpen || submenu.Items.OfType<MenuItem>().Single().ToolTip is not string)
                    throw new InvalidOperationException("The new-tab menu did not put the profile in its group.");
            }
            finally { profileMenu.IsOpen = false; }
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

            await CheckRecordingDestinationAsync(directory, token);

            File.AppendAllText(path, "\n");
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            review = await ReviewAsync();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) =>
            {
                var confirm = app.Windows.OfType<Window>().FirstOrDefault(window => window.Title == App.Text("ProfileTrustReset"));
                if (confirm is null) return;
                timer.Stop();
                Click(confirm, "Yes");
            };
            timer.Start();
            try { app.Profiles.ResetTrust(owner); }
            finally { timer.Stop(); }
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
            list.SelectedIndex = 0;
            await ClickProfileActionAsync(list, "RemoveRegistration");
            CheckMacroLaunch(app.Profiles.ReadRegistrations().Count == 0, "The registration was not removed.");
            admission = app.Profiles.AdmitAsync(owner, path, "Fixture macro", token);
            await AnswerAsync("ProfileTrustAndConnect", "Cancel");
            try { await admission; throw new InvalidOperationException("Unregister preserved remembered trust."); }
            catch (SshInteractionCancelledException) { }

            await app.SaveSettingsAsync("ja", app.ThemeChoice, app.BellChoice, app.TerminalFontFamily,
                app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, app.VaultPath, app.SettingsRevision);
            admission = app.Profiles.AdmitAsync(owner, path, null, token);
            review = await ReviewAsync();
            CheckMacroLaunch(FindControls<Button>(review).Any(button => Equals(button.Content, "登録して接続")) &&
                FindControls<Button>(review).Any(button => Equals(button.Content, "今回だけ接続")),
                "Japanese review actions were missing.");
            Click(review, "Cancel");
            try { await admission; throw new InvalidOperationException("Cancel admitted a connection."); }
            catch (SshInteractionCancelledException) { }
        }
        finally { owner.Close(); }

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
            CheckMacroLaunch((await ConnectionProfile.LoadAsync(path, token)).DisplayName == after,
                "The profile editor did not save its changes.");
            editor.Close();
        }

        async Task ClickProfileActionAsync(ListView list, string action)
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

    private static async Task CheckRecordingDestinationAsync(string directory, CancellationToken token)
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
            string profilePath = drive + "\\recording.ttty";
            var profile = ConnectionProfile.CreateTelnet("Recording destination", "localhost", recordRaw: true);
            var destination = RecordingDestination.Capture(profilePath, profile);
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
