using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class VaultController(App app) : IDisposable
{
    internal const int ManagementDeliveryLimit = 64;
    internal static readonly ResourceQuota ManagementDeliveries = new(ManagementDeliveryLimit,
        message: "Too many vault management results are being delivered.", collectOnPressure: false);
    private readonly Dictionary<string, VaultFileController> files = new(StringComparer.Ordinal);
    private readonly Dictionary<VaultFileController, VaultEditorWindow> editors = [];
    private readonly HashSet<string> staleSelectedPaths = new(StringComparer.Ordinal);
    private VaultFileController? selected;
    private (VaultFileController Previous, VaultFileController Next)? selectionTransition;
    internal long SelectionEpoch { get; private set; }
    internal bool HasActiveOperations => selectionTransition is not null || files.Values.Any(file => file.HasActiveWork);
    internal bool IsInitializingFor(Window host) => files.Values.Any(file => file.IsInitializingFor(host));
    private event Action? ManagementChanged;

    private VaultFileController Selected => selected ??= GetFile(ProfileLocation.Normalize(app.VaultPath));

    private VaultFileController GetFile(string path)
    {
        if (!files.TryGetValue(path, out VaultFileController? file))
        {
            file = new VaultFileController(app, path, NotifyManagementChanged, ReleaseIfUnused);
            files.Add(path, file);
        }
        return file;
    }

    internal bool IsSelected(VaultFileController file) => ReferenceEquals(file, Selected);

    internal bool MustRestart(VaultFileController file) =>
        staleSelectedPaths.Contains(file.FilePath) || (file.IsStale && IsSelected(file));

    private void ReleaseIfUnused(VaultFileController file)
    {
        if (!IsSelected(file) && !IsSelectionReserved(file) && !file.HasActiveWork && !editors.ContainsKey(file) &&
            files.TryGetValue(file.FilePath, out var current) && current == file)
        {
            files.Remove(file.FilePath);
            file.Dispose();
        }
        app.VaultWorkSettled();
    }

    internal bool IsSelectionReserved(VaultFileController file) =>
        selectionTransition is { } change && (change.Previous == file || change.Next == file);

    internal IDisposable? ReservePathChange(string priorPath, string nextPath)
    {
        if (selectionTransition is not null) throw new InvalidOperationException(Text("VaultBusy"));
        if (string.Equals(priorPath, nextPath, StringComparison.Ordinal)) return null;
        VaultFileController prior = Selected;
        string target = ProfileLocation.Normalize(nextPath);
        if (prior.FilePath == target) return null;
        VaultFileController next = GetFile(target);
        if (prior.IsBusy || next.IsBusy) throw new InvalidOperationException(Text("VaultBusy"));
        selectionTransition = (prior, next);
        return new SelectionReservation(this);
    }

    private sealed class SelectionReservation(VaultController owner) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (owner.selectionTransition is not { } transition) return;
            owner.selectionTransition = null;
            owner.ReleaseIfUnused(transition.Previous);
            owner.ReleaseIfUnused(transition.Next);
        }
    }

    public void SelectionChanged(string priorPath)
    {
        VaultFileController prior = selected ?? GetFile(ProfileLocation.Normalize(priorPath));
        selected = GetFile(ProfileLocation.Normalize(app.VaultPath));
        if (prior != selected)
        {
            SelectionEpoch++;
            if (prior.IsStale) staleSelectedPaths.Add(prior.FilePath);
            ReleaseIfUnused(prior);
        }
        NotifyManagementChanged();
    }

    internal async Task OpenEditorAsync(string path)
    {
        string location;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            location = await ProfileCatalog.NormalizeLocationAsync(App.NormalizeVaultPath(path), timeout.Token);
        }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception) || exception is OperationCanceledException)
        {
            app.Modal(() => ThemedMessageBox.ShowVault(null,
                $"{path}\n\n{exception.Message}", Text("Vault"), icon: MessageBoxImage.Error));
            return;
        }

        VaultFileController file = GetFile(location);
        if (editors.TryGetValue(file, out VaultEditorWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var editor = new VaultEditorWindow(file, form => BuildManagement(form, () => file));
        editors.Add(file, editor);
        editor.Closed += (_, _) =>
        {
            editors.Remove(file);
            ReleaseIfUnused(file);
            app.EditorClosed(editor);
        };
        editor.Show();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (!editor.IsVisible) return;
        if (await file.OpenEditorAsync(editor)) editor.EnableManagement();
        else editor.Close();
    }

    internal void BuildManagement(FormView form) => BuildManagement(form, () => Selected);

    private void NotifyManagementChanged()
    {
        if (ManagementChanged is not { } changed) return;
        foreach (Action update in changed.GetInvocationList())
        {
            try { update(); }
            catch (InvalidOperationException) { }
        }
    }

    public Task UnlockFromMenuAsync(MainWindow owner) => Selected.UnlockFromMenuAsync(owner);
    public Task ManageAsync(MainWindow owner) => Selected.ManageAsync(owner);
    public Task RestoreFromMenuAsync(MainWindow owner) => Selected.RestoreAsync(owner);
    public Task ArchiveAsync(MainWindow owner) => Selected.ArchiveAsync(owner);

    public Task ChoosePasswordAsync(Window owner, Action<string> choose) =>
        Selected.ChooseCredentialAsync(owner, "password", choose);

    public Task ChoosePrivateKeyAsync(Window owner, Action<string> choose) =>
        Selected.ChooseCredentialAsync(owner, "privateKey", choose);

    internal async Task ShowManagementFromPickerAsync(Window source)
    {
        var owner = source as MainWindow ?? app.LastTabWindow;
        if (owner is { CanAcceptTabs: true })
        {
            await ManageAsync(owner);
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
        }
        else await OpenEditorAsync(Selected.FilePath);
    }

    public Task<SshCredentialAvailability> CheckSshCredentialsAsync(Window owner, SshSavedCredentialRequest request,
        CancellationToken token) => Selected.CheckSshCredentialsAsync(owner, request, token);

    public Task<VaultSecret?> ReadSavedPasswordAsync(Window owner, CredentialReference reference, CancellationToken token) =>
        Selected.ReadSavedPasswordAsync(owner, reference, token);

    public Task<VaultSecret?> ReadPrivateKeyAsync(Window owner, SshPrivateKeyRequest request, CancellationToken token) =>
        Selected.ReadPrivateKeyAsync(owner, request, token);

    public Task<VaultSecret?> ReadMacroPasswordAsync(Window owner, CredentialReference reference, CancellationToken token) =>
        Selected.ReadMacroPasswordAsync(owner, reference, token);

    internal static char[] ReadPassword(PasswordBox box) => VaultFileController.ReadPassword(box);

    public void Dispose()
    {
        foreach (VaultFileController file in files.Values) file.Dispose();
        files.Clear();
        editors.Clear();
    }

    private void BuildManagement(FormView form, Func<VaultFileController> target)
    {
        form.CanDismissBusyView = () => target().CanDismissMasterChange(form.OwnerWindow);
        var list = new ListView { SelectionMode = SelectionMode.Extended };
        var columns = new DetailsColumns(list);
        columns.Add("ProfileListName", nameof(VaultEntryInfo.Name), 220);
        var kindColumn = columns.Add("VaultEntryType", nameof(VaultEntryInfo.PublicType), 160);
        columns.Add("VaultEntryFingerprint", nameof(VaultEntryInfo.Fingerprint), 410);
        var search = new TextBox();
        var searchLabel = new Label { Target = search, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLabeledBy(search, searchLabel);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        searchRow.Children.Add(search);
        var contents = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top);
        contents.Children.Add(searchRow);
        contents.Children.Add(list);
        var frame = new Border
        {
            Margin = new Thickness(24, 0, 24, 0), BorderThickness = new Thickness(1),
            Padding = new Thickness(8), UseLayoutRounding = true, Child = contents,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");

        var vaultPath = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var requiresVault = new List<Button>();
        VaultFileController? listedFile = null;
        IReadOnlyList<VaultEntryInfo>? listedEntries = null;
        bool listedUnlocked = false;
        int statusRevision = 0;

        var menu = new ContextMenu();
        list.ContextMenu = menu;
        (VaultFileController File, VaultEntryInfo[] Entries)? menuTarget = null;
        MenuItem editEntry = AddEntryAction("VaultEditEntry", static (file, window, entry) => file.EditEntryAsync(window, entry));
        MenuItem copyMacroReference = AddEntryAction("CopyMacroReference", static (file, _, entry) =>
        {
            if (entry.Kind == "password")
                Clipboard.SetText(JsonSerializer.Serialize(file.CurrentCredentialName(entry)));
            return Task.CompletedTask;
        });
        menu.Items.Add(new Separator());
        var deleteEntries = new MenuItem();
        form.AddLocalizedUpdate(() => deleteEntries.Header = Text("VaultDeleteEntry"));
        deleteEntries.Click += async (_, _) =>
        {
            if (menuTarget is { } selected)
                await RunAction(selected.File, (file, window) => file.DeleteEntriesAsync(window, selected.Entries, form));
        };
        menu.Items.Add(deleteEntries);

        form.AddActionRow(() =>
        {
            requiresVault.Add(Add("AddVaultPassword", (file, window) => file.AddPasswordAsync(window, form)));
            requiresVault.Add(Add("ImportPrivateKey", (file, window) => file.ImportPrivateKeyAsync(window, form)));
        });
        form.AddFillingDisplay("VaultEntryList", frame, () => form.AddGroup("VaultFileOperations", () =>
        {
            form.AddDisplay("VaultPath", vaultPath);
            form.AddActionRow(() =>
            {
                requiresVault.Add(Add("BackupVault", (file, window) => file.BackupAsync(window, form)));
                form.AddAsyncAction("RestoreVault", () => target().RestoreAsync(form.OwnerWindow, form));
                requiresVault.Add(Add("ChangeMaster", (file, window) => file.ChangeMasterAsync(window, form)));
                Add("RecheckFilePublication", (file, window) => file.ReconcilePublicationAsync(window, form));
                form.AddAsyncAction("ArchiveVault", async () =>
                {
                    Window window = form.OwnerWindow;
                    VaultFileController file = target();
                    bool archived = await file.ArchiveAsync(window, form);
                    if (archived && window is VaultEditorWindow editor) editor.CloseAfterArchive();
                });
            });
        }));
        form.AddStatusBar();

        form.AddLocalizedUpdate(() =>
        {
            columns.UpdateHeaders();
            kindColumn.DisplayMemberBinding = new Binding(nameof(VaultEntryInfo.PublicType))
            {
                TargetNullValue = Text("VaultPassword"),
            };
            searchLabel.Content = Text("SearchProfiles");
            AutomationProperties.SetName(list, Text("VaultEntries"));
            UpdateEntriesAndStatus(refreshLabels: true);
        });
        search.TextChanged += (_, _) => RefreshList(SelectedNames());
        list.ContextMenuOpening += (_, args) =>
        {
            VaultEntryInfo? entry = args.CursorLeft == -1 ? list.SelectedItem as VaultEntryInfo
                : columns.RowAt(args.OriginalSource)?.Content as VaultEntryInfo;
            if (entry is null || listedFile?.Unlocked is null)
            {
                args.Handled = true;
                return;
            }
            if (!list.SelectedItems.Contains(entry))
            {
                list.SelectedItem = entry;
                (list.ItemContainerGenerator.ContainerFromItem(entry) as ListViewItem)?.Focus();
            }
        };
        menu.Opened += (_, _) =>
        {
            var entries = list.SelectedItems.Cast<VaultEntryInfo>().ToArray();
            menuTarget = listedFile is { Unlocked: not null } file && entries.Length != 0 ? (file, entries) : null;
            editEntry.IsEnabled = menuTarget is { Entries.Length: 1 };
            deleteEntries.IsEnabled = menuTarget is not null;
            copyMacroReference.Visibility = menuTarget is { Entries: [{ Kind: "password" }] }
                ? Visibility.Visible : Visibility.Collapsed;
        };
        list.PreviewMouseLeftButtonDown += async (_, args) =>
        {
            if (args.ClickCount != 2 || columns.RowAt(args.OriginalSource)?.Content is not VaultEntryInfo entry ||
                listedFile is not { Unlocked: not null } file) return;
            args.Handled = true;
            await RunEntryAction(file, entry, static (current, window, item) => current.EditEntryAsync(window, item));
        };
        list.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.Delete || args.KeyboardDevice.Modifiers != ModifierKeys.None ||
                listedFile is not { Unlocked: not null } file || list.SelectedItems.Count == 0) return;
            args.Handled = true;
            if (args.IsRepeat) return;
            var entries = list.SelectedItems.Cast<VaultEntryInfo>().ToArray();
            await RunAction(file, (current, window) => current.DeleteEntriesAsync(window, entries, form));
        };
        form.Loaded += (_, _) => { ManagementChanged += UpdateEntries; UpdateEntries(); };
        form.Unloaded += (_, _) => ManagementChanged -= UpdateEntries;

        Button Add(string key, Func<VaultFileController, Window, Task> action) => form.AddAsyncAction(key, () =>
        {
            Window window = form.OwnerWindow;
            VaultFileController file = target();
            return file.RunAsync(window, () => action(file, window));
        });

        MenuItem AddEntryAction(string key, Func<VaultFileController, Window, VaultEntryInfo, Task> action)
        {
            var item = new MenuItem();
            form.AddLocalizedUpdate(() => item.Header = Text(key));
            item.Click += async (_, _) =>
            {
                if (menuTarget is { Entries: [var entry] } selected)
                    await RunEntryAction(selected.File, entry, action);
            };
            menu.Items.Add(item);
            return item;
        }

        Task RunEntryAction(VaultFileController file, VaultEntryInfo entry,
            Func<VaultFileController, Window, VaultEntryInfo, Task> action) =>
            RunAction(file, (current, window) => action(current, window, entry));

        Task RunAction(VaultFileController file, Func<VaultFileController, Window, Task> action) => form.RunAsync(() =>
        {
            if (!ReferenceEquals(file, target()) || file.Unlocked is null) return Task.CompletedTask;
            Window window = form.OwnerWindow;
            return file.RunAsync(window, () => action(file, window));
        });

        void UpdateEntries() => UpdateEntriesAndStatus(refreshLabels: false);

        void UpdateEntriesAndStatus(bool refreshLabels)
        {
            VaultFileController file = target();
            vaultPath.Text = string.Format(Text("VaultPathDisplay"), file.FilePath);
            AutomationProperties.SetName(vaultPath, vaultPath.Text);
            bool sameFile = ReferenceEquals(listedFile, file);
            bool unlocked = file.Unlocked is not null;
            if (!sameFile || !ReferenceEquals(listedEntries, file.ManagementEntries) || listedUnlocked != unlocked || refreshLabels)
            {
                listedFile = file;
                listedEntries = file.ManagementEntries;
                listedUnlocked = unlocked;
                RefreshList(sameFile ? SelectedNames() : null);
            }
            int revision = ++statusRevision;
            foreach (Button action in requiresVault) action.IsEnabled = true;
            if (file.Unlocked is null)
            {
                form.SetStatusMessage(Text("VaultEntriesLocked"));
                if (form.IsLoaded) _ = DescribeMissingAsync(file, revision);
            }
        }

        HashSet<string> SelectedNames() =>
            list.SelectedItems.Cast<VaultEntryInfo>().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        void RefreshList(HashSet<string>? selection)
        {
            if (listedEntries is null) return;
            string query = search.Text;
            string password = Text("VaultPassword");
            var visible = listedEntries.Where(entry =>
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (entry.PublicType ?? password).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Fingerprint?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            list.ItemsSource = visible;
            columns.ApplySort();
            if (selection is not null)
                foreach (VaultEntryInfo entry in visible.Where(entry => selection.Contains(entry.Name)))
                    list.SelectedItems.Add(entry);
            if (listedFile?.Unlocked is not null)
                form.SetStatusMessage(visible.Length != 0 ? "" : Text(listedEntries.Count == 0 ? "VaultEntriesEmpty" : "NoSearchMatch"));
        }

        async Task DescribeMissingAsync(VaultFileController file, int revision)
        {
            string? missing = await file.DescribeMissingTargetAsync(form.OwnerWindow);
            if (missing is null || statusRevision != revision || !form.IsLoaded || !ReferenceEquals(file, target())) return;
            form.SetStatusMessage(missing);
            foreach (Button action in requiresVault) action.IsEnabled = false;
        }
    }

}
