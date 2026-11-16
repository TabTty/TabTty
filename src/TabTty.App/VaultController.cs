using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class VaultController(App app) : IDisposable
{
    private readonly Dictionary<string, VaultFileController> files = new(StringComparer.Ordinal);
    private readonly Dictionary<VaultFileController, VaultEditorWindow> editors = [];
    private readonly HashSet<string> staleSelectedPaths = new(StringComparer.Ordinal);
    private VaultFileController? selected;
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
        if (IsSelected(file) || file.IsBusy || editors.ContainsKey(file)) return;
        if (!files.TryGetValue(file.FilePath, out var current) || current != file) return;
        files.Remove(file.FilePath);
        file.Dispose();
    }

    public void CheckPathChange(string priorPath, string nextPath)
    {
        if (string.Equals(priorPath, nextPath, StringComparison.Ordinal)) return;
        VaultFileController prior = Selected;
        string target = ProfileLocation.Normalize(nextPath);
        if (prior.FilePath == target) return;
        if (prior.IsBusy || (files.TryGetValue(target, out var replacement) && replacement.IsBusy))
            throw new InvalidOperationException(Text("VaultBusy"));
    }

    public void SelectionChanged(string priorPath)
    {
        VaultFileController prior = selected ?? GetFile(ProfileLocation.Normalize(priorPath));
        selected = GetFile(ProfileLocation.Normalize(app.VaultPath));
        if (prior != selected)
        {
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
            app.Modal(() => ThemedMessageBox.Show(
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

    private void NotifyManagementChanged() => ManagementChanged?.Invoke();

    public Task UnlockFromMenuAsync(MainWindow owner) => Selected.UnlockFromMenuAsync(owner);
    public Task ManageAsync(MainWindow owner) => Selected.ManageAsync(owner);
    public Task RestoreFromMenuAsync(MainWindow owner) => Selected.RestoreFromMenuAsync(owner);
    public Task ArchiveAsync(MainWindow owner) => Selected.ArchiveAsync(owner);

    public Task ChoosePasswordAsync(Window owner, Action<CredentialReference, string> choose, bool allowInitialize = true) =>
        Selected.ChooseCredentialAsync(owner, "password", choose, allowInitialize);

    public Task ChoosePrivateKeyAsync(Window owner, Action<CredentialReference, string> choose, bool allowInitialize = true) =>
        Selected.ChooseCredentialAsync(owner, "privateKey", choose, allowInitialize);

    public Task<VaultSecret?> ReadSavedPasswordAsync(Window owner, CredentialReference reference, CancellationToken token) =>
        Selected.ReadSavedPasswordAsync(owner, reference, token);

    public Task<VaultSecret?> ReadPrivateKeyAsync(Window owner, CredentialReference? reference, CancellationToken token) =>
        Selected.ReadPrivateKeyAsync(owner, reference, token);

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
        var list = new ListBox { DisplayMemberPath = nameof(VaultEntryInfo.Name), Height = 250 };
        var metadata = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100,
            MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var vaultPath = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 85,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var selectedActions = new List<Button>();
        form.AddActionRow(() =>
        {
            Add("AddVaultPassword", static (file, window) => file.AddPasswordAsync(window), refresh: true);
            Add("ImportPrivateKey", static (file, window) => file.ImportPrivateKeyAsync(window), refresh: true);
        });
        form.AddColumns(() => form.AddGroup("VaultEntryList", () => form.AddDisplay("VaultEntries", list)),
            () => form.AddGroup("CredentialDetails", () =>
            {
                form.AddDisplay("CredentialDetails", metadata);
                form.AddActionRow(() =>
                {
                    selectedActions.Add(Add("EditCredential", (file, window) => WithSelectedAsync(file, window, file.EditEntryAsync), refresh: true));
                    selectedActions.Add(Add("DeleteCredential", (file, window) => WithSelectedAsync(file, window, file.DeleteEntryAsync), refresh: true));
                    selectedActions.Add(Add("CopyMacroReference", (file, window) => WithSelectedAsync(file, window, (_, unlocked, entry) =>
                    {
                        string kind = entry.Kind == "password" ? "Password" : "PrivateKey";
                        Clipboard.SetText($"Credential(\"{unlocked.Id:D}\", \"{entry.Id:D}\", CredentialKind.{kind})");
                        return Task.CompletedTask;
                    })));
                });
            }));
        form.AddGroup("VaultFileOperations", () =>
        {
            form.AddField("VaultPath", vaultPath);
            form.AddActionRow(() =>
            {
                Add("BackupVault", static (file, window) => file.BackupAsync(window));
                Add("RestoreVault", static (file, window) => file.RestoreAsync(window));
                Add("ChangeMaster", static (file, window) => file.ChangeMasterAsync(window));
                form.AddAsyncAction("ArchiveVault", async () =>
                {
                    Window window = form.OwnerWindow;
                    VaultFileController file = target();
                    bool archived = false;
                    await file.RunAsync(window, async () => archived = await file.ArchiveFileAsync(window));
                    if (archived && window is VaultEditorWindow editor) editor.CloseAfterArchive();
                });
            });
        });
        list.SelectionChanged += (_, _) => UpdateDetails();
        form.AddLocalizedUpdate(UpdateDetails);
        form.Loaded += (_, _) => { ManagementChanged += UpdateEntries; UpdateEntries(); };
        form.Unloaded += (_, _) => ManagementChanged -= UpdateEntries;
        UpdateEntries();

        Button Add(string key, Func<VaultFileController, Window, Task> action, bool refresh = false) => form.AddAsyncAction(key, () =>
        {
            Window window = form.OwnerWindow;
            VaultFileController file = target();
            return file.RunAsync(window, async () =>
            {
                await action(file, window);
                if (refresh) await file.RefreshManagementAsync();
            });
        });

        async Task WithSelectedAsync(VaultFileController file, Window window,
            Func<Window, CredentialVault, VaultEntryInfo, Task> action)
        {
            if (file.Unlocked is { } unlocked && list.SelectedItem is VaultEntryInfo entry)
                await action(window, unlocked, entry);
        }

        void UpdateEntries()
        {
            Guid? selected = (list.SelectedItem as VaultEntryInfo)?.Id;
            VaultFileController file = target();
            vaultPath.Text = file.FilePath;
            if (!ReferenceEquals(list.ItemsSource, file.ManagementEntries))
            {
                list.ItemsSource = file.ManagementEntries;
                list.SelectedItem = file.ManagementEntries.FirstOrDefault(entry => entry.Id == selected);
            }
            UpdateDetails();
        }

        void UpdateDetails()
        {
            VaultFileController file = target();
            var entry = list.SelectedItem as VaultEntryInfo;
            foreach (Button action in selectedActions) action.IsEnabled = entry is not null && file.Unlocked is not null;
            if (entry is null)
            {
                metadata.Text = Text(file.Unlocked is null ? "VaultEntriesLocked" : file.ManagementEntries.Count == 0
                    ? "VaultEntriesEmpty" : "SelectVaultEntry");
                return;
            }
            metadata.Text = string.Join('\n', new[] { entry.Name,
                entry.Kind == "password" ? Text("VaultPassword") : entry.PublicType ?? Text("PublicKey"),
                entry.Username, entry.Fingerprint, entry.Id.ToString("D") }.Where(value => !string.IsNullOrEmpty(value)));
        }
    }

}
