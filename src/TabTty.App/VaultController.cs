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
    internal bool IsSelectedUnlocked => selected?.Unlocked is not null;
    internal bool HasActiveOperations => selectionTransition is not null || files.Values.Any(file => file.HasActiveWork);
    internal bool IsInitializingFor(Window host) => files.Values.Any(file => file.IsInitializingFor(host));
    internal event Action? ManagementChanged;

    private VaultFileController Selected => selected ??= GetFile(ProfileLocation.Normalize(app.VaultPath));

    private VaultFileController GetFile(string path)
    {
        if (!files.TryGetValue(path, out VaultFileController? file))
        {
            // A reserved path may have acquired its on-disk spelling since the reservation.
            foreach (VaultFileController candidate in files.Values)
                if (ProfileLocation.SameVaultOpeningPath(candidate.FilePath, path))
                    return candidate;
            file = new VaultFileController(app, path, NotifyManagementChanged, ReleaseIfUnused);
            files.Add(path, file);
        }
        return file;
    }

    internal bool IsSelected(VaultFileController file) => ReferenceEquals(file, Selected);

    internal bool MustRestart(VaultFileController file) =>
        (file.IsStale && IsSelected(file)) || staleSelectedPaths.Contains(file.FilePath) ||
        staleSelectedPaths.Any(path => ProfileLocation.SameVaultOpeningPath(path, file.FilePath));

    internal void RememberStalePath(VaultFileController file)
    {
        if (file.IsStale) staleSelectedPaths.Add(ProfileLocation.VaultOpeningPath(file.FilePath));
    }

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
        VaultFileController next = GetFile(target);
        if (prior == next) return null;
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

    public void SelectionChanged()
    {
        VaultFileController prior = Selected;
        selected = selectionTransition?.Next ?? prior;
        if (prior != selected)
        {
            SelectionEpoch++;
            prior.ChangeRole();
            selected.ChangeRole();
            RememberStalePath(prior);
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
    public Task LockFromMenuAsync(MainWindow owner) => Selected.LockFromMenuAsync(owner);
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
        CancellationToken token, TerminalTab? tab = null) => Selected.CheckSshCredentialsAsync(owner, request, token, tab);

    public Task<VaultSecret?> ReadSavedPasswordAsync(Window owner, CredentialReference reference, CancellationToken token,
        TerminalTab? tab = null) => Selected.ReadSavedPasswordAsync(owner, reference, token, tab: tab);

    public Task<VaultSecret?> ReadPrivateKeyAsync(Window owner, SshPrivateKeyRequest request, CancellationToken token,
        TerminalTab? tab = null) => Selected.ReadPrivateKeyAsync(owner, request, token, tab);

    public Task<VaultSecret?> ReadMacroPasswordAsync(Window owner, CredentialReference reference, CancellationToken token,
        TerminalTab? tab = null) => Selected.ReadMacroPasswordAsync(owner, reference, token, tab);

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
        VaultEntryBinding? listedBinding = null;
        long listedRevision = -1, listedRole = -1;
        IReadOnlyList<VaultEntryInfo>? listedEntries = null;
        bool listedUnlocked = false;
        int statusRevision = 0;

        var menu = new ContextMenu();
        list.ContextMenu = menu;
        VaultEntrySelection? menuTarget = null;
        MenuItem editEntry = AddEntryAction("VaultEditEntry", (file, window, selection, entry) => file.EditEntryAsync(window, selection, entry, form));
        MenuItem copyMacroReference = AddEntryAction("CopyMacroReference", static (_, _, _, entry) =>
        {
            if (entry.Kind == "password")
                Clipboard.SetText(JsonSerializer.Serialize(entry.Name));
            return Task.CompletedTask;
        });
        menu.Items.Add(new Separator());
        var deleteEntries = new MenuItem();
        form.AddLocalizedUpdate(() => deleteEntries.Header = Text("VaultDeleteEntry"));
        deleteEntries.Click += async (_, _) =>
        {
            if (menuTarget is { } selected)
                await DeleteSelectionAsync(selected);
        };
        menu.Items.Add(deleteEntries);

        form.AddActionRow(() =>
        {
            requiresVault.Add(Add("AddVaultPassword", (file, window) => file.AddPasswordAsync(window)));
            requiresVault.Add(Add("ImportPrivateKey", (file, window) => file.ImportPrivateKeyAsync(window)));
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
            menuTarget = CaptureSelection(list.SelectedItems.Cast<VaultEntryInfo>());
            editEntry.IsEnabled = menuTarget is { Entries.Length: 1 };
            deleteEntries.IsEnabled = menuTarget is not null;
            copyMacroReference.Visibility = menuTarget is { Entries: [{ Kind: "password" }] }
                ? Visibility.Visible : Visibility.Collapsed;
        };
        list.PreviewMouseLeftButtonDown += async (_, args) =>
        {
            if (args.ClickCount != 2 || columns.RowAt(args.OriginalSource)?.Content is not VaultEntryInfo entry ||
                CaptureSelection([entry]) is not { } selected) return;
            args.Handled = true;
            await RunAction(selected, (file, window, entries) => file.EditEntryAsync(window, selected, entries[0], form));
        };
        list.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.Delete || args.KeyboardDevice.Modifiers != ModifierKeys.None ||
                CaptureSelection(list.SelectedItems.Cast<VaultEntryInfo>()) is not { } selected) return;
            args.Handled = true;
            if (args.IsRepeat) return;
            await DeleteSelectionAsync(selected);
        };
        form.Loaded += (_, _) => { ManagementChanged += UpdateEntries; UpdateEntries(); };
        form.Unloaded += (_, _) =>
        {
            ManagementChanged -= UpdateEntries;
            listedBinding?.Retire();
            listedBinding = null;
        };

        Button Add(string key, Func<VaultFileController, Window, Task> action) => form.AddAsyncAction(key, () =>
        {
            Window window = form.OwnerWindow;
            VaultFileController file = target();
            return file.RunAsync(window, () => action(file, window), form);
        });

        MenuItem AddEntryAction(string key, Func<VaultFileController, Window, VaultEntrySelection, VaultEntryInfo, Task> action)
        {
            var item = new MenuItem();
            form.AddLocalizedUpdate(() => item.Header = Text(key));
            item.Click += async (_, _) =>
            {
                if (menuTarget is { Entries.Length: 1 } selected)
                    await RunAction(selected, (file, window, entries) => action(file, window, selected, entries[0]));
            };
            menu.Items.Add(item);
            return item;
        }

        VaultEntrySelection? CaptureSelection(IEnumerable<VaultEntryInfo> entries)
        {
            if (listedBinding is not { Active: true } binding || !listedUnlocked) return null;
            var identities = entries.Select(entry => (entry.Name, entry.Kind)).ToArray();
            return identities.Length == 0 ? null : new(binding, listedRole, listedRevision, identities);
        }

        Task DeleteSelectionAsync(VaultEntrySelection selected) =>
            RunAction(selected, (file, window, entries) => file.DeleteEntriesAsync(window, selected, entries, form));

        Task RunAction(VaultEntrySelection selected,
            Func<VaultFileController, Window, VaultEntryInfo[], Task> action) => form.RunAsync(() =>
        {
            Window window = form.OwnerWindow;
            VaultFileController file = selected.Binding.File;
            if (!ReferenceEquals(file, target())) return ShowStaleSelection();
            return file.RunAsync(window, async () =>
            {
                try { await action(file, window, file.ResolveEntries(selected)); }
                catch (VaultEntrySelectionChangedException) { await ShowStaleSelection(); }
            }, form);

            Task ShowStaleSelection()
            {
                UpdateEntries();
                RefreshList(null);
                form.SetStatusResource("VaultEntrySelectionChanged");
                return Task.CompletedTask;
            }
        });

        void UpdateEntries() => UpdateEntriesAndStatus(refreshLabels: false);

        void UpdateEntriesAndStatus(bool refreshLabels)
        {
            VaultFileController file = target();
            vaultPath.Text = string.Format(Text("VaultPathDisplay"), file.FilePath);
            if (file.LastArchivePath is { } archive)
                vaultPath.Text += "\n" + string.Format(Text("VaultArchiveDestination"), archive);
            AutomationProperties.SetName(vaultPath, vaultPath.Text);
            bool sameFile = ReferenceEquals(listedFile, file);
            bool unlocked = file.Unlocked is not null;
            if (!sameFile || listedBinding?.Matches(file.Unlocked) != true)
            {
                listedBinding?.Retire();
                listedBinding = new(file);
            }
            listedRevision = file.ManagementRevision;
            listedRole = file.RoleGeneration;
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
                form.SetStatusResource("VaultEntriesLocked");
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
