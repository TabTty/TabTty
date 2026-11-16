using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class ProfileCatalog(App app)
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private event Action? ProfileSaved;
    private bool stateErrorShown;
    internal static string Filter => $"{Text("Profiles")} (*.ttty)|*.ttty";

    public void ReportStateFailure(Window owner)
    {
        if (stateErrorShown) return;
        stateErrorShown = true;
        MainWindow.ShowError(owner, new IOException(Text("LocalStateInvalid")));
    }

    public async Task<(IReadOnlyList<string> Paths, int Unavailable)> FindCredentialReferencesAsync(
        Guid vaultId, Guid entryId, string kind)
    {
        string[] locations = LocalState.Read()["registrations"]!.AsArray().OfType<JsonObject>()
            .Select(item => item["path"]!.GetValue<string>()).ToArray();
        var matches = new List<string>();
        int unavailable = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (string location in locations)
        {
            if (timeout.IsCancellationRequested) { unavailable++; continue; }
            try
            {
                using var itemTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                itemTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                ConnectionProfile profile = await ConnectionProfile.LoadAsync(location, itemTimeout.Token);
                var expected = new CredentialReference(vaultId, entryId, kind);
                if (profile.Type == "ssh" && (profile.SshPassword == expected || profile.SshPrivateKey == expected))
                    matches.Add(location);
            }
            catch (Exception exception) when (IsFileProblem(exception) || exception is OperationCanceledException)
            { unavailable++; }
        }
        return (matches, unavailable);
    }

    public async Task<string> RememberAsync(string path, ConnectionProfile profile, bool used)
    {
        string location = path;
        try
        {
            location = Path.GetFullPath(path);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                location = await NormalizeLocationAsync(path, timeout.Token);
            }
            catch (OperationCanceledException) { }
            if (app.Dispatcher.HasShutdownStarted) return location;
            names[location] = profile.DisplayName;
            LocalState.Change(state =>
            {
                JsonArray registrations = state["registrations"]!.AsArray();
                var registration = registrations.OfType<JsonObject>()
                    .FirstOrDefault(item => item["path"]?.GetValue<string>() == location);
                if (registration is null)
                {
                    if (registrations.Count >= 10_000) throw new IOException(Text("LocalStateInvalid"));
                    registration = new JsonObject
                    {
                        ["registrationId"] = Guid.NewGuid(), ["path"] = location, ["displayPath"] = path,
                        ["favorite"] = false, ["lastUsed"] = null,
                    };
                    registrations.Add(registration);
                }
                if (used) registration["lastUsed"] = DateTimeOffset.UtcNow;
            });
            stateErrorShown = false;
        }
        catch (Exception exception) when (IsFileProblem(exception))
        {
            if (!stateErrorShown && app.Windows.OfType<MainWindow>().LastOrDefault() is { } owner)
            {
                stateErrorShown = true;
                MainWindow.ShowError(owner, new IOException(Text("LocalStateInvalid")));
            }
        }
        return location;
    }

    public IReadOnlyList<Registration> ReadRegistrations() => LocalState.Read()["registrations"]!.AsArray()
        .OfType<JsonObject>().Select(node =>
        {
            string path = node["path"]!.GetValue<string>();
            return new Registration(node["registrationId"]!.GetValue<Guid>(), path,
                names.GetValueOrDefault(path, Path.GetFileNameWithoutExtension(path)),
                node["favorite"]!.GetValue<bool>(), node["lastUsed"]?.GetValue<DateTimeOffset>());
        }).OrderByDescending(item => item.Favorite).ThenByDescending(item => item.LastUsed)
        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public async Task<string?> ReadDisplayNameAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            ConnectionProfile profile = await ConnectionProfile.LoadAsync(path, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            names[path] = profile.DisplayName;
            return profile.DisplayName;
        }
        catch (Exception exception) when (IsFileProblem(exception) || exception is OperationCanceledException)
        {
            return null;
        }
    }

    public void Show(MainWindow owner)
    {
        try { owner.OpenForm("ProfileCatalog", Build); }
        catch (Exception exception) when (IsFileProblem(exception)) { MainWindow.ShowError(owner, exception); }

        void Build(FormView form)
        {
            var search = new TextBox();
            var list = new ListBox { Height = 250, DisplayMemberPath = nameof(Registration.Label) };
            var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100,
                MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var selectedActions = new List<Button>();
            int version = 0;
            form.AddActionRow(() => form.AddAsyncAction("RegisterProfile", RegisterAsync));
            form.AddColumns(() => form.AddGroup("Profiles", () =>
            {
                form.AddField("SearchProfiles", search);
                list.Margin = new Thickness(0, 10, 0, 0);
                form.AddDisplay("Profiles", list);
            }), () => form.AddGroup("ProfileDetails", () =>
            {
                form.AddDisplay("ProfileDetails", details);
                form.AddActionRow(() =>
                {
                    selectedActions.Add(form.AddAction("ConnectProfile", () =>
                    {
                        if (list.SelectedItem is Registration item) form.TabOwner?.OpenProfile(item.Path);
                    }));
                    selectedActions.Add(form.AddAsyncAction("EditProfile", () => EditSelectedAsync(duplicate: false)));
                    selectedActions.Add(form.AddAsyncAction("DuplicateProfile", () => EditSelectedAsync(duplicate: true)));
                });
                form.AddActionRow(() =>
                {
                    selectedActions.Add(form.AddAction("ToggleFavorite", () => ChangeSelected(remove: false)));
                    selectedActions.Add(form.AddAction("RemoveRegistration", () => ChangeSelected(remove: true)));
                    selectedActions.Add(form.AddAction("RecycleProfile", Recycle));
                });
            }));
            foreach (Button button in selectedActions) button.IsEnabled = false;
            form.AddLocalizedUpdate(() => { if (list.SelectedItem is null) details.Text = Text("SelectProfile"); });
            search.TextChanged += (_, _) => Reload();
            list.SelectionChanged += async (_, _) =>
            {
                int selection = ++version;
                foreach (Button button in selectedActions) button.IsEnabled = list.SelectedItem is not null;
                details.Text = list.SelectedItem is null ? Text("SelectProfile") : "";
                if (list.SelectedItem is not Registration item) return;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    ConnectionProfile profile = await ConnectionProfile.LoadAsync(item.Path, timeout.Token);
                    if (selection != version) return;
                    string destination = profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host}:{profile.Port}";
                    string protocol = profile.Type == "serial" ? "COM" : profile.Type.ToUpperInvariant();
                    details.Text = $"{profile.DisplayName}\n{protocol} · {destination}\n\n{item.Path}";
                }
                catch (Exception exception) when (IsFileProblem(exception) || exception is OperationCanceledException)
                {
                    if (selection == version) details.Text = $"{Text("ProfileUnavailable")}\n{item.Path}";
                }
            };
            form.Loaded += (_, _) => { ProfileSaved += Reload; Reload(); };
            form.Unloaded += (_, _) => ProfileSaved -= Reload;

            async Task RegisterAsync()
            {
                var dialog = new OpenFileDialog { Filter = Filter };
                if (app.Modal(() => dialog.ShowDialog(form.OwnerWindow)) != true) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                ConnectionProfile profile = await ConnectionProfile.LoadAsync(dialog.FileName, timeout.Token);
                await RememberAsync(dialog.FileName, profile, used: false);
                Reload();
            }

            void Recycle()
            {
                if (list.SelectedItem is not Registration item) return;
                if (app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, $"{Text("RecycleProfileConfirm")}\n\n{item.Path}",
                    Text("RecycleProfile"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No)) != MessageBoxResult.Yes) return;
                try
                {
                    ProfileRecycling.Recycle(form.OwnerWindow, item.Path);
                    ChangeSelected(remove: true);
                }
                catch (Exception exception) when (IsFileProblem(exception)) { MainWindow.ShowError(form.OwnerWindow, exception); }
            }

            Task EditSelectedAsync(bool duplicate) => list.SelectedItem is Registration item && form.TabOwner is { } host
                ? EditAsync(host, item.Path, duplicate) : Task.CompletedTask;

            void ChangeSelected(bool remove)
            {
                if (list.SelectedItem is not Registration selected) return;
                try
                {
                    LocalState.Change(state =>
                    {
                        JsonArray registrations = state["registrations"]!.AsArray();
                        var target = registrations.OfType<JsonObject>().Single(item =>
                            item["registrationId"]?.GetValue<Guid>() == selected.Id);
                        if (remove) registrations.Remove(target);
                        else target["favorite"] = !selected.Favorite;
                    });
                    Reload();
                }
                catch (Exception exception) when (IsFileProblem(exception)) { MainWindow.ShowError(form.OwnerWindow, exception); }
            }

            void Reload()
            {
                try
                {
                    string query = search.Text;
                    Guid? selected = (list.SelectedItem as Registration)?.Id;
                    var items = ReadRegistrations().Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        item.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                    list.ItemsSource = items;
                    list.SelectedItem = items.FirstOrDefault(item => item.Id == selected);
                }
                catch (Exception exception) when (IsFileProblem(exception))
                {
                    list.ItemsSource = null;
                    details.Text = Text("LocalStateInvalid");
                }
            }
        }
    }

    private async Task EditAsync(MainWindow? owner, string path, bool duplicate)
    {
        string title = duplicate ? "DuplicateProfile" : "EditProfile";
        using var loadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string identity = await NormalizeLocationAsync(path, loadTimeout.Token);
        if (ActivateEditor(title, identity)) return;
        ConnectionProfile profile = await ConnectionProfile.LoadAsync(path, loadTimeout.Token);
        if (owner is not null && (!owner.IsLoaded || owner.IsClosing) || ActivateEditor(title, identity)) return;
        if (duplicate) profile = profile.Duplicate();
        new ProfileEditorWindow(app, title, identity, profile, creating: false, fromExplorer: owner is null).Show();
    }

    internal async Task EditFromExplorerAsync(string path)
    {
        try { await EditAsync(null, path, duplicate: false); }
        catch (Exception exception) when (IsFileProblem(exception) || exception is OperationCanceledException)
        {
            app.Modal(() => ThemedMessageBox.Show(
                $"{path}\n\n{exception.Message}", Text("EditProfile"), icon: MessageBoxImage.Error));
        }
    }

    internal static async Task<string> NormalizeLocationAsync(string path, CancellationToken token)
    {
        await ApplicationResources.FilePreparations.WaitAsync(token);
        Task<string> normalize = Task.Run(() =>
        {
            try { return ProfileLocation.Normalize(path); }
            finally { ApplicationResources.FilePreparations.Release(); }
        });
        return await normalize.WaitAsync(token);
    }

    public void CreateProfile()
    {
        if (!ActivateEditor("NewProfile", null))
            new ProfileEditorWindow(app, "NewProfile", null, ProfileEditor.NewProfile("telnet"), creating: true).Show();
    }

    private bool ActivateEditor(string title, string? identity)
    {
        var editor = app.Windows.OfType<ProfileEditorWindow>().FirstOrDefault(window =>
            window.TitleKey == title && string.Equals(window.Identity, identity, StringComparison.Ordinal));
        if (editor is null) return false;
        if (editor.WindowState == WindowState.Minimized) editor.WindowState = WindowState.Normal;
        editor.Activate();
        return true;
    }

    public void AddEditor(FormView form, ConnectionProfile profile, string? path, bool creating = false,
        Action<string>? savedAs = null, bool fromExplorer = false)
    {
        var editor = new ProfileEditor(form, profile, app, creating, fromExplorer);
        form.AddEditorButtons(async () =>
        {
            ConnectionProfile draft = editor.GetDraft();
            if (path is null)
            {
                string? destination = ChooseNewPath(form.OwnerWindow);
                if (destination is null) return false;
                await draft.SaveNewAsync(destination);
                editor.UseSavedRevision(draft);
                path = await RememberAsync(destination, draft, used: false);
                savedAs?.Invoke(path);
            }
            else
            {
                await draft.SaveEditAsync();
                editor.UseSavedRevision(draft);
                names[path] = draft.DisplayName;
            }
            ProfileSaved?.Invoke();
            return true;
        });
    }

    public string? ChooseNewPath(Window owner)
    {
        var dialog = new SaveFileDialog { Filter = Filter, AddExtension = true, DefaultExt = ".ttty", OverwritePrompt = false };
        try
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(documents)) throw new IOException(Text("ProfileSaveLocation"));
            string suggested = Path.Combine(documents, "TabTty", "Profiles");
            Directory.CreateDirectory(suggested);
            dialog.InitialDirectory = suggested;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            app.Modal(() => ThemedMessageBox.Show(owner, Text("ProfileSaveLocation"), Text("Profiles")));
        }
        return app.Modal(() => dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    internal static bool IsFileProblem(Exception exception) => exception is IOException or InvalidDataException
        or JsonException or ArgumentException or FormatException or OverflowException or InvalidOperationException or UnauthorizedAccessException;

    internal sealed record Registration(Guid Id, string Path, string Name, bool Favorite, DateTimeOffset? LastUsed)
    {
        public string Label => Favorite ? $"★ {Name}" : Name;
    }
}
