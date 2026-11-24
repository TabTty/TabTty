using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class ProfileCatalog(App app, Action<string, string, string?>? issueProfiles = null,
    Action<string>? validateProfileStage = null)
{
    private readonly Dictionary<string, ProfileDetails> metadata = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim registrationChanges = new(1);
    private readonly Dictionary<string, PendingProfileSave> pendingSaves = new(StringComparer.Ordinal);
    private sealed record PendingProfileSave(string? Original, string Attempted, ProfileEdit? Edit, long Reset, long Removal)
    {
        internal ConnectionProfile? Saved { get; set; }
        internal FilePublicationResult? Publication { get; set; }
        internal FileObjectIdentity? Identity { get; set; }
        internal string? CompletedPath { get; set; }
    }
    private event Action? SaveRecoveryChanged;
    private event Action? ProfileSaved;
    private event Action? CatalogFolderChanged;
    private event Action? RegistrationUsed;
    private (long Sequence, string? Location) lastCreatedProfile;
    private bool stateErrorShown;
    internal static string Filter => $"{Text("Profiles")} (*.ttty)|*.ttty";

    public void ReportStateFailure(Window owner)
    {
        if (stateErrorShown) return;
        stateErrorShown = true;
        MainWindow.ShowError(owner, new IOException(Text("LocalStateInvalid")));
    }

    internal sealed record CredentialProfileMatch(Guid RegistrationId, string Path, string Version);
    internal sealed record CredentialImpact(IReadOnlyList<CredentialProfileMatch> Profiles, int Unavailable)
    {
        internal static readonly CredentialImpact Empty = new([], 0);
        internal IEnumerable<string> Paths => Profiles.Select(profile => profile.Path);
    }

    internal async Task<CredentialImpact> FindCredentialReferencesAsync(IReadOnlyList<VaultEntryInfo> entries)
    {
        var selected = entries.Select(entry => (entry.Name, entry.Kind)).ToHashSet();
        var matches = new List<CredentialProfileMatch>();
        int unavailable = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var registration in ReadRegistrations(LocalState.Read(), allFolders: true).OrderBy(registration => registration.Path, StringComparer.Ordinal))
        {
            if (timeout.IsCancellationRequested) { unavailable++; continue; }
            try
            {
                using var itemTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                itemTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                ConnectionProfile profile = await ConnectionProfile.LoadAsync(registration.Path, itemTimeout.Token);
                if (profile.Type == "ssh" &&
                    (profile.SshPassword is { } password && selected.Contains((password.Name, "password")) ||
                     profile.SshPrivateKey is { } key && selected.Contains((key.Name, "privateKey"))))
                    matches.Add(new(registration.Id, registration.Path, profile.TrustVersion));
            }
            catch (Exception exception) when (IsFileProblem(exception) || exception is OperationCanceledException)
            { unavailable++; }
        }
        return new(matches, unavailable);
    }

    internal async Task<(int Updated, IReadOnlyList<string> Problems)> UpdateCredentialReferencesAsync(
        CredentialImpact impact, string kind, string newName)
    {
        int updated = 0;
        var problems = new List<string>();
        foreach (var match in impact.Profiles)
        {
            string path = PermissionDisplay.Literal(match.Path);
            try
            {
                ConnectionProfile profile;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    profile = await ConnectionProfile.LoadAsync(match.Path, timeout.Token);
                if (ReadTrust(match.Path).Registration != match.RegistrationId)
                    throw new IOException(Text("CredentialRenameUnregistered"));
                if (profile.TrustVersion != match.Version)
                    throw new IOException(Text("CredentialRenameProfileChanged"));
                if (app.Windows.OfType<ProfileEditorWindow>().Any(window => window.TitleKey == "EditProfile" &&
                    window.HasPendingEdits && string.Equals(window.Identity, match.Path, StringComparison.Ordinal)))
                    throw new IOException(Text("CredentialRenamePendingProfileEdits"));

                var draft = JsonNode.Parse(profile.ToEditorJson())!.AsObject();
                draft["connection"]!["authentication"]![kind] = newName;
                ConnectionProfile edited = profile.Edit(draft.ToJsonString());
                await edited.BeginSaveEdit(issueProfiles, validateProfileStage).ObserveAsync();
                updated++;
                if (BackupCleanupWarning(edited) is { } warning) problems.Add(warning);
            }
            catch (FilePublicationException exception)
            {
                problems.Add($"{path}\n{JsonStoreRecovery.DescribeFailure(exception.Result)}");
            }
            catch (OperationCanceledException)
            {
                problems.Add($"{path}\n{Text("CredentialRenameProfileReadTimedOut")}");
            }
            catch (Exception exception) when (IsFileProblem(exception))
            {
                problems.Add($"{path}\n{exception.Message}");
            }
        }
        if (updated != 0) ProfileSaved?.Invoke();
        return (updated, problems);
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
            RememberMetadata(location, profile);
            bool changed = false;
            LocalState.Change(state =>
            {
                if (FindRegistration(state, location) is not { } registration) return;
                changed = used || registration["groupName"]?.GetValue<string>() != profile.GroupName;
                StoreRegistrationGroup(registration, profile.GroupName);
                if (used) registration["lastUsed"] = DateTimeOffset.UtcNow;
            });
            stateErrorShown = false;
            if (changed) RegistrationUsed?.Invoke();
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

    public IReadOnlyList<Registration> ReadRegistrations() => ReadRegistrations(LocalState.Read());

    private IReadOnlyList<Registration> ReadRegistrations(JsonObject state, bool allFolders = false)
    {
        string folder = allFolders ? "" : ActiveFolder;
        return state["registrations"]!.AsArray().OfType<JsonObject>().Select(node =>
        {
            string path = node["path"]!.GetValue<string>();
            metadata.TryGetValue(path, out var details);
            return new Registration(node["registrationId"]!.GetValue<Guid>(), path,
                Path.GetFileNameWithoutExtension(path),
                details is null or { Unavailable: true } ? node["groupName"]?.GetValue<string>() : details.GroupName,
                node["favorite"]!.GetValue<bool>(), node["lastUsed"]?.GetValue<DateTimeOffset>(),
                details?.Protocol, details?.Destination, details?.Unavailable ?? false);
        }).Where(item => allFolders || Path.GetDirectoryName(item.Path) == folder)
            .OrderByDescending(item => item.Favorite).ThenByDescending(item => item.LastUsed)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal async Task ReadRegistrationDetailsAsync(IReadOnlyList<Registration> registrations, CancellationToken token)
    {
        foreach (var batch in registrations.Chunk(4))
        {
            if (token.IsCancellationRequested) return;
            await Task.WhenAll(batch.Select(item => ReadDetailsAsync(item.Path, token)));
        }
    }

    private async Task ReadDetailsAsync(string path, CancellationToken cancellationToken)
    {
        metadata.TryGetValue(path, out var before);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            ConnectionProfile profile = await ConnectionProfile.LoadAsync(path, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(metadata.GetValueOrDefault(path), before))
                RememberMetadata(path, profile);
        }
        catch (Exception exception) when (IsFileProblem(exception) || exception is OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(metadata.GetValueOrDefault(path), before))
                metadata[path] = new(before?.GroupName, null, null, Unavailable: true);
        }
    }

    private void RememberMetadata(string location, ConnectionProfile profile) =>
        metadata[location] = new(profile.GroupName,
            profile.Type == "serial" ? "COM" : profile.Type.ToUpperInvariant(),
            profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host}:{profile.Port}", Unavailable: false);

    private sealed record ProfileDetails(string? GroupName, string? Protocol, string? Destination, bool Unavailable);

    private static void StoreRegistrationGroup(JsonObject registration, string? groupName)
    {
        if (groupName is null) registration.Remove("groupName");
        else registration["groupName"] = groupName;
    }

    public void Show(MainWindow owner)
    {
        try { owner.OpenForm("ProfileCatalog", Build); }
        catch (Exception exception) when (IsFileProblem(exception)) { MainWindow.ShowError(owner, exception); }

        void Build(FormView form)
        {
            var search = new TextBox();
            var groupTabs = new TabControl { Margin = new Thickness(24, 0, 24, 12) };
            groupTabs.SetResourceReference(FrameworkElement.StyleProperty, "DetailsTabs");
            var allGroups = CreateGroupTab(null);
            var ungrouped = CreateGroupTab("");
            var list = new ListView { SelectionMode = SelectionMode.Extended };
            var columns = new DetailsColumns(list);
            var favoriteCell = new FrameworkElementFactory(typeof(Button));
            favoriteCell.SetValue(Control.PaddingProperty, new Thickness(0));
            favoriteCell.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            favoriteCell.SetValue(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            favoriteCell.SetValue(Control.FontSizeProperty, 14d);
            favoriteCell.SetValue(FrameworkElement.MinWidthProperty, 28d);
            favoriteCell.SetValue(FrameworkElement.MinHeightProperty, 20d);
            favoriteCell.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Registration.FavoriteIcon)));
            favoriteCell.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(Registration.FavoriteAction)));
            favoriteCell.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Registration.FavoriteAction)));
            columns.Add("ProfileListFavorite", nameof(Registration.Favorite), 48,
                new DataTemplate { VisualTree = favoriteCell }, showHeader: false, initialDirection: ListSortDirection.Descending);
            columns.Add("ProfileListName", nameof(Registration.Name), 175);
            columns.Add("ProfileListProtocol", nameof(Registration.Protocol), 80);
            columns.Add("ProfileListDestination", nameof(Registration.Destination), 160);
            var lastUsed = columns.Add("ProfileListLastUsed", nameof(Registration.LastUsed), 145,
                initialDirection: ListSortDirection.Descending);
            lastUsed.DisplayMemberBinding = new Binding(nameof(Registration.LastUsedLocal)) { StringFormat = "g" };
            columns.Add("ProfileListFilePath", nameof(Registration.Path), 320);
            var searchLabel = new Label { Target = search, VerticalAlignment = VerticalAlignment.Center };
            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(searchLabel, Dock.Left);
            searchRow.Children.Add(searchLabel);
            searchRow.Children.Add(search);
            AutomationProperties.SetLabeledBy(search, searchLabel);
            var contents = new DockPanel();
            DockPanel.SetDock(searchRow, Dock.Top);
            contents.Children.Add(searchRow);
            contents.Children.Add(list);
            TabItem? contentTab = null;
            IReadOnlyList<Registration> registrations = [];
            bool reloading = false;
            long lastSelectedCreation = 0;
            CancellationTokenSource? detailsLoad = null;
            var menu = new ContextMenu();
            list.ContextMenu = menu;
            Guid? menuRegistration = null;
            string[] menuPaths = [];
            var connect = new MenuItem();
            connect.Click += (_, _) => form.TabOwner?.OpenProfiles(menuPaths, form);
            menu.Items.Add(connect);
            AddMenuAction("EditProfile", item => EditItemAsync(item, duplicate: false));
            AddMenuAction("DuplicateProfile", item => EditItemAsync(item, duplicate: true));
            var favoriteMenuItem = AddMenuAction("ProfileListFavorite", item => ChangeSelectedAsync(remove: false, item));
            menu.Items.Add(new Separator());
            AddMenuAction("RemoveRegistration", item => ChangeSelectedAsync(remove: true, item));
            AddMenuAction("RecycleProfile", RecycleAsync);
            form.AddActionRow(() =>
            {
                form.AddAction("AddProfile", CreateProfile);
                form.AddAsyncAction("ImportProfileFile", ImportAsync);
            });
            form.AddFillingDisplay("ProfileGroups", groupTabs);
            form.AddStatusBar();
            form.AddLocalizedUpdate(() =>
            {
                allGroups.Header = Text("AllProfiles");
                ungrouped.Header = Text("UngroupedProfiles");
                searchLabel.Content = Text("SearchProfiles");
                AutomationProperties.SetName(list, Text("ProfileCatalog"));
                columns.UpdateHeaders();
                if (list.ItemsSource is not null) RefreshList();
            });
            list.ContextMenuOpening += (_, args) =>
            {
                var origin = args.CursorLeft == -1 ? Keyboard.FocusedElement : args.OriginalSource;
                var row = origin is null ? null : columns.RowAt(origin);
                var item = row?.Content as Registration;
                if (args.CursorLeft == -1) item ??= list.SelectedItem as Registration;
                if (item is null)
                {
                    args.Handled = true;
                    return;
                }
                if (!list.SelectedItems.Contains(item))
                {
                    list.UnselectAll();
                    list.SelectedItem = item;
                }
                row?.Focus();
                var selected = list.SelectedItems.Cast<Registration>().ToHashSet();
                menuPaths = list.Items.Cast<Registration>().Where(selected.Contains).Select(entry => entry.Path).ToArray();
                bool multiple = menuPaths.Length > 1;
                menuRegistration = multiple ? null : item.Id;
                connect.Header = multiple ? string.Format(Text("ConnectSelectedProfiles"), menuPaths.Length) : Text("ConnectProfile");
                foreach (Control entry in menu.Items.Cast<Control>().Skip(1))
                    entry.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;
                favoriteMenuItem.Header = item.FavoriteAction;
            };
            list.PreviewMouseLeftButtonDown += async (_, args) =>
            {
                if (args.ClickCount != 2 || columns.RowAt(args.OriginalSource)?.Content is not Registration item ||
                    MainWindow.Ancestor<Button>(args.OriginalSource as DependencyObject) is not null) return;
                args.Handled = true;
                await form.RunAsync(() => EditItemAsync(item, duplicate: false));
            };
            list.PreviewKeyDown += async (_, args) =>
            {
                if (args.Key != Key.Delete || args.KeyboardDevice.Modifiers != ModifierKeys.None ||
                    list.SelectedItems.Count != 1 || list.SelectedItem is not Registration item) return;
                args.Handled = true;
                if (!args.IsRepeat) await RunRowActionAsync(item, RecycleAsync);
            };
            list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(async (_, args) =>
            {
                if (args.OriginalSource is not Button { DataContext: Registration item }) return;
                args.Handled = true;
                list.SelectedItem = item;
                await form.RunAsync(() => ChangeSelectedAsync(remove: false, item));
            }));
            search.TextChanged += (_, _) =>
            {
                if (!reloading) RefreshList();
            };
            groupTabs.SelectionChanged += (_, args) =>
            {
                if (args.Source == groupTabs && !reloading) RefreshList();
            };
            form.Loaded += (_, _) =>
            {
                ProfileSaved += Reload;
                CatalogFolderChanged += ReloadFolder;
                ReloadFolder();
            };
            RegistrationUsed += ReloadAfterConnection;
            form.Unloaded += (_, _) =>
            {
                ProfileSaved -= Reload;
                CatalogFolderChanged -= ReloadFolder;
                RegistrationUsed -= ReloadAfterConnection;
                detailsLoad?.Cancel();
            };

            static TabItem CreateGroupTab(string? name) => new()
            {
                Tag = name, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };

            MenuItem AddMenuAction(string key, Func<Registration, Task> action)
            {
                var entry = new MenuItem();
                form.AddLocalizedUpdate(() => entry.Header = Text(key));
                entry.Click += async (_, _) =>
                {
                    if (registrations.FirstOrDefault(item => item.Id == menuRegistration) is { } item)
                        await RunRowActionAsync(item, action);
                };
                menu.Items.Add(entry);
                return entry;
            }

            Task RunRowActionAsync(Registration item, Func<Registration, Task> action) =>
                form.RunAsync(() => action(item));

            void ReloadFolder()
            {
                detailsLoad?.Cancel();
                Reload();
                _ = LoadDetailsAsync();
            }

            async Task LoadDetailsAsync()
            {
                using var load = new CancellationTokenSource();
                detailsLoad = load;
                try
                {
                    await ReadRegistrationDetailsAsync(registrations, load.Token);
                    if (!load.IsCancellationRequested) Reload();
                }
                finally { if (ReferenceEquals(detailsLoad, load)) detailsLoad = null; }
            }

            async Task ImportAsync()
            {
                var dialog = new OpenFileDialog { Filter = Filter };
                if (app.Modal(() => dialog.ShowDialog(form.OwnerWindow)) != true) return;
                try { await ImportFileAsync(form.OwnerWindow, dialog.FileName, CancellationToken.None); }
                catch (SshInteractionCancelledException) { }
            }

            async Task RecycleAsync(Registration item)
            {
                if (app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, $"{Text("RecycleProfileConfirm")}\n\n{item.Path}",
                    Text("RecycleProfile"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No)) != MessageBoxResult.Yes) return;
                await registrationChanges.WaitAsync();
                try
                {
                    if (ReadTrust(item.Path).Registration != item.Id) return;
                    ProfileRecycling.Recycle(form.OwnerWindow, item.Path);
                    ChangeSelected(remove: true, item);
                }
                catch (Exception exception) when (IsFileProblem(exception)) { MainWindow.ShowError(form.OwnerWindow, exception); }
                finally { registrationChanges.Release(); }
            }

            Task EditItemAsync(Registration item, bool duplicate) => form.TabOwner is { } host
                ? EditAsync(host, item.Path, duplicate) : Task.CompletedTask;

            async Task ChangeSelectedAsync(bool remove, Registration selected)
            {
                await registrationChanges.WaitAsync();
                try { ChangeSelected(remove, selected); }
                finally { registrationChanges.Release(); }
            }

            void ChangeSelected(bool remove, Registration selected)
            {
                try
                {
                    if (ReadTrust(selected.Path).Registration != selected.Id) return;
                    LocalState.Change(state =>
                    {
                        JsonArray registrations = state["registrations"]!.AsArray();
                        var target = registrations.OfType<JsonObject>().Single(item =>
                            item["registrationId"]?.GetValue<Guid>() == selected.Id);
                        if (remove)
                        {
                            registrations.Remove(target);
                            (state[TrustKey] as JsonObject)?.Remove(selected.Path);
                        }
                        else target["favorite"] = !target["favorite"]!.GetValue<bool>();
                    });
                    if (remove) removals[selected.Path] = removals.GetValueOrDefault(selected.Path) + 1;
                    ProfileSaved?.Invoke();
                    if (!remove && list.SelectedItem is Registration item && item.Id == selected.Id)
                        list.ScrollIntoView(item);
                }
                catch (Exception exception) when (IsFileProblem(exception)) { MainWindow.ShowError(form.OwnerWindow, exception); }
            }

            void ReloadAfterConnection() => ReloadCore(followMovedSelection: false);

            void Reload() => ReloadCore(followMovedSelection: true);

            void ReloadCore(bool followMovedSelection)
            {
                try
                {
                    registrations = ReadRegistrations();
                    var creation = lastCreatedProfile;
                    var created = creation.Sequence != lastSelectedCreation
                        ? registrations.FirstOrDefault(item => item.Path == creation.Location) : null;
                    lastSelectedCreation = creation.Sequence;
                    Guid? selectedId = (list.SelectedItem as Registration)?.Id;
                    Registration? selected = created ?? registrations.FirstOrDefault(item => item.Id == selectedId);
                    HashSet<Guid> selectedIds = created is not null ? [created.Id] :
                        list.SelectedItems.Cast<Registration>().Select(item => item.Id).ToHashSet();
                    string? group = (groupTabs.SelectedItem as TabItem)?.Tag as string;
                    if (group is not null && selected is not null &&
                        (created is not null || followMovedSelection && list.SelectedItems.Count == 1))
                        group = selected.GroupName ?? "";
                    reloading = true;
                    if (created is not null && !MatchesSearch(created, search.Text)) search.Clear();
                    RefreshGroupTabs(group, preserveEmpty: !followMovedSelection);
                    RefreshList(selectedIds);
                    if (created is not null && list.SelectedItem is { } item) list.ScrollIntoView(item);
                }
                catch (Exception exception) when (IsFileProblem(exception))
                {
                    registrations = [];
                    list.ItemsSource = null;
                    ReportStateFailure(form.OwnerWindow);
                }
                finally { reloading = false; }
            }

            void RefreshGroupTabs(string? selected, bool preserveEmpty = false)
            {
                var names = registrations.Select(item => item.GroupName ?? "");
                if (preserveEmpty && selected is not null) names = names.Append(selected);
                names = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
                string?[] keys = [null, .. names];
                if (!keys.SequenceEqual(groupTabs.Items.OfType<TabItem>().Select(item => item.Tag as string)))
                {
                    groupTabs.Items.Clear();
                    groupTabs.Items.Add(allGroups);
                    foreach (string name in keys.Skip(1).OfType<string>())
                    {
                        if (name.Length == 0) groupTabs.Items.Add(ungrouped);
                        else
                        {
                            string label = PermissionDisplay.Literal(name);
                            TabItem tab = CreateGroupTab(name);
                            tab.Header = new TextBlock
                            {
                                Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200,
                            };
                            tab.ToolTip = label;
                            groupTabs.Items.Add(tab);
                        }
                    }
                }
                groupTabs.SelectedItem = groupTabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                    StringComparer.OrdinalIgnoreCase.Equals(item.Tag as string, selected)) ?? allGroups;
            }

            void RefreshList(HashSet<Guid>? selected = null)
            {
                selected ??= list.SelectedItems.Cast<Registration>()
                    .Select(item => item.Id).ToHashSet();
                var tab = groupTabs.SelectedItem as TabItem;
                if (tab != contentTab)
                {
                    if (contentTab is not null) contentTab.Content = null;
                    if (tab is not null) tab.Content = contents;
                    contentTab = tab;
                }
                string? group = (groupTabs.SelectedItem as TabItem)?.Tag as string;
                string query = search.Text;
                var items = registrations.Where(item =>
                    (group is null || StringComparer.OrdinalIgnoreCase.Equals(item.GroupName ?? "", group)) &&
                    MatchesSearch(item, query)).ToArray();
                list.ItemsSource = items;
                columns.ApplySort();
                foreach (var item in items)
                    if (selected.Contains(item.Id)) list.SelectedItems.Add(item);
            }

            static bool MatchesSearch(Registration item, string query) =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.GroupName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    private async Task EditAsync(MainWindow? owner, string path, bool duplicate)
    {
        string title = duplicate ? "DuplicateProfile" : "EditProfile";
        using var loadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string identity = await NormalizeLocationAsync(path, loadTimeout.Token);
        if (ActivateEditor(title, identity)) return;
        ConnectionProfile profile = await ConnectionProfile.LoadAsync(identity, loadTimeout.Token);
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
            new ProfileEditorWindow(app, "NewProfile", null, ProfileEditor.NewProfile("ssh"), creating: true).Show();
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
        Action<string, string>? onSaved = null, bool fromExplorer = false)
    {
        var editor = new ProfileEditor(form, profile, app, creating, fromExplorer);
        PendingProfileSave? ownRequest = null;
        Action? savedBaseline = null;
        form.DescribeUnresolvedPublication = () => CurrentRecovery() is { } pending
            ? pending.Publication is { } publication
                ? JsonStoreRecovery.DescribeFailure(publication)
                : Text(RecoveryLabels(pending).Message)
            : null;

        async Task RecheckAsync()
        {
            await registrationChanges.WaitAsync();
            try
            {
                if (CurrentRecovery() is not { } pending) return;
                using var progress = form.BeginProgress("CheckingPreviousSave");
                if (pending.Publication is { } publication)
                {
                    ConnectionProfile? committed;
                    try { committed = await ConnectionProfile.ReconcileSaveAsync(pending.Attempted, publication.OperationId); }
                    catch (FilePublicationException error)
                    {
                        if (!VerifiedFilePublication.CanReconcile(error.Result) || error.Result.Target == FileImageStatus.Other)
                            RetirePendingSave(pending);
                        else pending.Publication = error.Result;
                        throw;
                    }
                    if (committed is null)
                    {
                        RetirePendingSave(pending);
                        return;
                    }
                    pending.Saved = committed;
                    pending.Publication = null;
                    SaveRecoveryChanged?.Invoke();
                }
                await CompleteSaveAsync(pending);
                progress.Dispose();
                ShowCleanupWarning(profile, pending.Attempted);
            }
            finally
            {
                registrationChanges.Release();
                SaveRecoveryChanged?.Invoke();
            }
        }
        form.AddEditorButtons(async () =>
        {
            await registrationChanges.WaitAsync();
            try
            {
                if (CurrentRecovery() is { } recovery)
                    throw new IOException(Text(RecoveryLabels(recovery).Message));
                ConnectionProfile draft = editor.GetDraft();
                Action baseline = form.CaptureSaveBaseline();
                using var progress = form.BeginProgress("SavingChanges");
                long reset = trustReset;
                string attemptedPath;
                ProfileEdit? pendingEdit = null;
                if (path is null)
                {
                    attemptedPath = ProfilePathFor(app.ProfileSaveDirectory, editor.ProfileName);
                    if (!await ReviewProfileRegistrationAsync(form.OwnerWindow, attemptedPath, draft, CancellationToken.None))
                        return false;
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        string location = await NormalizeLocationAsync(attemptedPath, timeout.Token);
                        if (ReadTrust(location).Registration is not null)
                            throw new IOException(Text("ProfileFileInUse"));
                    }
                }
                else
                {
                    attemptedPath = path;
                    pendingEdit = await PrepareEditAsync(path, profile, editor.ProfileName, fromExplorer);
                }
                var pending = new PendingProfileSave(path, attemptedPath, pendingEdit, reset, removals.GetValueOrDefault(attemptedPath));
                var operation = path is null ? draft.BeginSaveNew(attemptedPath, issueProfiles, validateProfileStage)
                    : draft.BeginSaveEdit(issueProfiles, validateProfileStage);
                try { await operation.ObserveAsync(); }
                catch (FilePublicationException error) when (error.Result.OperationId == operation.Id &&
                    VerifiedFilePublication.CanReconcile(error.Result))
                {
                    pending.Publication = error.Result;
                    RetainPendingSave();
                    throw;
                }
                pending.Saved = draft;
                RetainPendingSave();
                try { await CompleteSaveAsync(pending); }
                catch (Exception error) when (IsFileProblem(error))
                {
                    throw new IOException(Text(RecoveryLabels(pending).Message) + "\n\n" + error.Message, error);
                }
                progress.Dispose();
                ShowCleanupWarning(profile, pending.Attempted);
                return true;

                void RetainPendingSave()
                {
                    ownRequest = pending;
                    savedBaseline = baseline;
                    pendingSaves[attemptedPath] = pending;
                    if (pending.Edit?.Destination is { } destination) pendingSaves[destination] = pending;
                }
            }
            finally
            {
                registrationChanges.Release();
                SaveRecoveryChanged?.Invoke();
            }
        }, isCreating: creating ? () => path is null : null);
        Action refreshRecovery = form.AddEditorSaveRecovery(() => CurrentRecovery() is { } pending
            ? RecoveryLabels(pending) : null, RecheckAsync);
        form.Loaded += (_, _) =>
        {
            SaveRecoveryChanged += refreshRecovery;
            refreshRecovery();
        };
        form.Unloaded += (_, _) => SaveRecoveryChanged -= refreshRecovery;
        form.AddStatusBar();

        PendingProfileSave? CurrentRecovery()
        {
            string? location = path ?? ownRequest?.Attempted;
            return location is null ? null : pendingSaves.GetValueOrDefault(location);
        }

        async Task CompleteSaveAsync(PendingProfileSave completion)
        {
            ConnectionProfile saved = completion.Saved!;
            path = await CompleteVerifiedSaveAsync(completion);
            profile = saved;
            editor.UseSavedRevision(saved);
            if (ReferenceEquals(ownRequest, completion))
            {
                savedBaseline?.Invoke();
                ownRequest = null;
                savedBaseline = null;
            }
            onSaved?.Invoke(path, saved.DisplayName);
        }

        void ShowCleanupWarning(ConnectionProfile saved, string attemptedPath)
        {
            if (BackupCleanupWarning(saved) is { } warning)
                app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, warning, Text("EditProfile"),
                    icon: MessageBoxImage.Warning));
            JsonStoreRecovery.ShowRetainedStages(app, form, VerifiedFilePublication.GetRetainedStagePaths(attemptedPath));
        }
    }

    private static (string Message, string Action) RecoveryLabels(PendingProfileSave pending)
    {
        if (pending.Publication is not null) return ("ProfileSaveResultUnknown", "CheckProfileSaveResult");
        return pending.Edit?.Destination is not null
            ? ("ProfileFileNameChangeIncomplete", "RetryProfileFileNameChange")
            : ("ProfileRegistrationIncomplete", "RetryProfileRegistration");
    }

    private void RetirePendingSave(PendingProfileSave pending)
    {
        var locations = pendingSaves.Where(pair => ReferenceEquals(pair.Value, pending)).Select(pair => pair.Key).ToArray();
        foreach (string location in locations) pendingSaves.Remove(location);
    }

    // Runs under registrationChanges. Several editors can observe the same pending save.
    private async Task<string> CompleteVerifiedSaveAsync(PendingProfileSave completion)
    {
        ConnectionProfile saved = completion.Saved!;
        string? previous = completion.Original;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string location = completion.CompletedPath ?? (completion.Edit?.Destination is { } destination && saved.IsSavedAt(destination)
            ? destination : completion.Attempted);
        string normalized = await NormalizeLocationAsync(location, timeout.Token);
        var identity = await saved.CheckSavedRevisionAsync(normalized, timeout.Token);
        if (identity is null || completion.Identity is { } expected && identity != expected)
        {
            RetirePendingSave(completion);
            throw new IOException(Text("ProfileTrustVersionChanged"));
        }
        if (completion.CompletedPath is { } completedPath) return completedPath;
        completion.Identity = identity;
        var authority = ReadTrust(completion.Attempted);
        if (authority.Reset != completion.Reset || authority.Removal != completion.Removal)
        {
            RetirePendingSave(completion);
            throw new IOException(Text("ProfileTrustWriteFailed"));
        }

        string path;
        if (previous is null)
        {
            if (authority.Registration is not null && authority.Version != saved.TrustVersion)
                throw new IOException(Text("ProfileFileInUse"));
            path = authority.Registration is not null ? normalized :
                await RegisterSavedAsync(completion.Attempted, saved, completion.Reset, selectNewProfile: true);
        }
        else
        {
            path = completion.Edit is { } edit ? await FinishEditAsync(previous, saved, edit) : normalized;
        }

        completion.CompletedPath = path;
        RetirePendingSave(completion);
        if (previous is not null) ProfileSaved?.Invoke();
        return path;
    }

    private static string? BackupCleanupWarning(ConnectionProfile profile) => profile.RetainedSaveBackup is { } path
        ? string.Format(Text("ProfileBackupCleanupFailed"), PermissionDisplay.Literal(path)) : null;

    internal static bool IsFileProblem(Exception exception) => exception is IOException or InvalidDataException
        or JsonException or ArgumentException or FormatException or OverflowException or InvalidOperationException
        or UnauthorizedAccessException or MacroSourceException;

    internal sealed record Registration(Guid Id, string Path, string Name, string? GroupName, bool Favorite,
        DateTimeOffset? LastUsed, string? Protocol, string? Target, bool Unavailable)
    {
        public string FavoriteIcon => Favorite ? "★" : "☆";
        public string FavoriteAction => Text(Favorite ? "ProfileFavoriteRemove" : "ProfileFavoriteAdd");
        public string? Destination => Unavailable ? Text("ProfileUnavailable") : Target;
        public DateTime? LastUsedLocal => LastUsed?.LocalDateTime;
    }
}
