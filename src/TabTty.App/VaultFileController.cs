using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class VaultFileController(App app, string filePath, Action changed, Action<VaultFileController> idle) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim operation = new(1);
    private readonly VaultUnlockInteraction unlockInteraction = new();
    private CredentialVault? vault;
    public IReadOnlyList<VaultEntryInfo> ManagementEntries { get; private set; } = [];
    public CredentialVault? Unlocked => vault;
    public string FilePath => filePath;
    public bool IsBusy => operation.CurrentCount == 0 || unlockInteraction.IsPending;
    public bool IsStale => vault?.IsStale == true;
    private bool disposed;
    private bool archiving;

    private static string Filter => $"{Text("VaultFiles")} (*.tttykey)|*.tttykey";

    public Task UnlockFromMenuAsync(MainWindow owner) => RunAsync(owner, async () =>
    {
        if (await UnlockFileAsync(owner) is not null && owner.IsLoaded)
        {
            await RefreshManagementAsync();
            if (!owner.IsLoaded) return;
            app.Modal(() => ThemedMessageBox.Show(owner, Text("VaultUnlocked"), Text("Vault")));
        }
    });

    public Task ManageAsync(MainWindow owner) => RunAsync(owner, async () =>
    {
        if (await UnlockFileAsync(owner) is null || !owner.IsLoaded) return;
        await RefreshManagementAsync();
        if (owner.IsLoaded) owner.OpenForm("ManageVault", app.Vault.BuildManagement, pageTitleKey: "VaultEntries");
    });

    public Task RestoreFromMenuAsync(MainWindow owner) => RunAsync(owner, () => RestoreAsync(owner));

    public Task ArchiveAsync(MainWindow owner) => RunAsync(owner, () => ArchiveFileAsync(owner));

    public async Task<bool> ArchiveFileAsync(Window owner)
    {
        CheckAvailable();
        if (unlockInteraction.IsPending) throw new InvalidOperationException(Text("VaultBusy"));
        string path = FilePath;
        archiving = true;
        try
        {
            if (app.Modal(() => ThemedMessageBox.Show(owner, $"{Text("ArchiveVaultConfirm")}\n\n{path}",
                Text("ArchiveVault"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No)) != MessageBoxResult.Yes) return false;
            string? archived = await Task.Run(() => CredentialVault.ArchiveFile(path));
            if (archived is not null) ClearUnlocked();
            if (owner.IsLoaded)
                app.Modal(() => ThemedMessageBox.Show(owner, archived is null ? Text("VaultFileMissing") :
                    $"{Text("VaultArchived")}\n\n{archived}", Text("ArchiveVault")));
            return archived is not null;
        }
        finally { archiving = false; }
    }

    public async Task RefreshManagementAsync()
    {
        CredentialVault? unlocked = vault;
        IReadOnlyList<VaultEntryInfo> entries = unlocked is null ? [] : await unlocked.ListEntriesAsync();
        if (!ReferenceEquals(vault, unlocked)) return;
        ManagementEntries = entries;
        changed();
    }

    public void Dispose()
    {
        disposed = true;
        ClearUnlocked();
    }

    public void ClearUnlocked()
    {
        vault?.Dispose();
        vault = null;
        ManagementEntries = [];
        changed();
    }

    public async Task<bool> OpenEditorAsync(Window owner)
    {
        bool opened = false;
        await RunAsync(owner, async () =>
        {
            if (await UnlockFileAsync(owner, allowInitialize: false) is null) return;
            await RefreshManagementAsync();
            opened = true;
        });
        return opened;
    }

    public async Task RunAsync(Window owner, Func<Task> action)
    {
        await operation.WaitAsync();
        try
        {
            if (!disposed && owner.IsLoaded) await action();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or CryptographicException or DllNotFoundException or EntryPointNotFoundException
            or JsonException or InvalidOperationException)
        {
            if (!owner.IsLoaded || disposed) return;
            string message = exception switch
            {
                CredentialVaultStaleException => Text(app.Vault.MustRestart(this) ? "SelectedVaultStale" : "EditorVaultStale"),
                CryptographicException => Text("VaultAuthenticationFailed"),
                DllNotFoundException or EntryPointNotFoundException => Text("VaultCryptoUnavailable"),
                _ => exception.Message,
            };
            app.Modal(() => ThemedMessageBox.Show(owner, $"{FilePath}\n\n{message}", Text("Vault"),
                MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            operation.Release();
            idle(this);
        }
    }

    public async Task<VaultSecret?> ReadSavedPasswordAsync(Window owner, CredentialReference reference,
        CancellationToken token)
    {
        if (await UnlockFileAsync(owner, token, sshPrompt: true) is not { } unlocked) return null;
        return KeepSelectedSecret(await unlocked.GetPasswordAsync(reference.VaultId, reference.EntryId, token), unlocked);
    }

    internal async Task<VaultSecret?> ReadMacroPasswordAsync(Window owner, CredentialReference reference, CancellationToken token)
    {
        try
        {
            if (await UnlockFileAsync(owner, token, sshPrompt: true) is not { } unlocked) return null;
            VaultSecret secret = await unlocked.GetPasswordAsync(reference.VaultId, reference.EntryId, token);
            if (app.Vault.IsSelected(this) && ReferenceEquals(vault, unlocked) && !token.IsCancellationRequested) return secret;
            secret.Dispose();
            token.ThrowIfCancellationRequested();
            throw new MacroError(MacroErrorCode.VaultMismatch);
        }
        catch (CredentialLookupException error) { throw new MacroError(error.Code); }
        catch (CredentialVaultStaleException) { throw new MacroError(MacroErrorCode.VaultStale); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        { throw new MacroError(MacroErrorCode.CredentialUnavailable); }
    }

    public async Task<VaultSecret?> ReadPrivateKeyAsync(Window owner, CredentialReference? reference,
        CancellationToken token)
    {
        if (await UnlockFileAsync(owner, token, sshPrompt: true) is not { } unlocked) return null;
        if (reference is null)
        {
            await app.SshPrompts.WaitAsync(token);
            try
            {
                if (await PickEntryAsync(owner, unlocked, "privateKey", token) is not { } entry) return null;
                reference = new CredentialReference(unlocked.Id, entry.Id, "privateKey");
            }
            finally { app.SshPrompts.Release(); }
        }
        return KeepSelectedSecret(await unlocked.GetPrivateKeyAsync(reference.VaultId, reference.EntryId, token), unlocked);
    }

    private VaultSecret? KeepSelectedSecret(VaultSecret secret, CredentialVault unlocked)
    {
        if (app.Vault.IsSelected(this) && ReferenceEquals(vault, unlocked)) return secret;
        secret.Dispose();
        return null;
    }

    public Task ChooseCredentialAsync(Window owner, string kind, Action<CredentialReference, string> choose,
        bool allowInitialize) =>
        RunAsync(owner, async () =>
        {
            if (await UnlockFileAsync(owner, allowInitialize: allowInitialize) is not { } unlocked || !owner.IsLoaded) return;
            if (await PickEntryAsync(owner, unlocked, kind, CancellationToken.None) is { } entry)
                choose(new CredentialReference(unlocked.Id, entry.Id, kind), entry.Name);
        });

    private async Task<VaultEntryInfo?> PickEntryAsync(Window owner, CredentialVault unlocked, string kind,
        CancellationToken token)
    {
        var entries = await unlocked.ListEntriesAsync(token);
        if (!owner.IsLoaded) return null;
        string title = kind == "password" ? "SelectSavedPassword" : "SelectPrivateKey";
        var form = new MainWindow.FormWindow(owner, Text(title));
        var list = new ListBox { ItemsSource = entries.Where(entry => entry.Kind == kind).ToArray(),
            DisplayMemberPath = nameof(VaultEntryInfo.Name),
            MinHeight = 120, MaxHeight = 350 };
        form.AddField("VaultEntries", list);
        form.AddButtons(() =>
        {
            if (list.SelectedItem is not VaultEntryInfo) throw new ArgumentException(Text(title));
        });
        return SshHostTrust.ShowCancellable(app, form, token) == true ? list.SelectedItem as VaultEntryInfo : null;
    }

    public async Task ImportPrivateKeyAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        var dialog = new OpenFileDialog { Title = Text("ImportPrivateKey"), Multiselect = false };
        if (app.Modal(() => dialog.ShowDialog(owner)) != true) return;
        var form = new MainWindow.FormWindow(owner, Text("ImportPrivateKey"));
        var name = new TextBox { Text = Path.GetFileName(dialog.FileName), MaxLength = 512 };
        form.AddField("CredentialName", name);
        form.AddAsyncButtons(async () =>
        {
            try { await unlocked.ImportPrivateKeyAsync(name.Text, dialog.FileName); }
            catch (PrivateKeyPassphraseRequiredException)
            {
                var prompt = new MainWindow.FormWindow(form, Text("SourceKeyPassphrase"));
                var password = new PasswordBox { MaxLength = 16 * 1024 };
                prompt.AddField("SourceKeyPassphrase", password);
                prompt.Loaded += (_, _) => password.Focus();
                prompt.AddButtons(() => { }, acceptKey: "ImportPrivateKey");
                try
                {
                    if (app.Modal(prompt.ShowDialog) != true) return false;
                    char[] characters = ReadPassword(password);
                    try
                    {
                        byte[] bytes = new byte[StrictUtf8.GetByteCount(characters)];
                        try
                        {
                            StrictUtf8.GetBytes(characters, bytes);
                            Array.Clear(characters);
                            await unlocked.ImportPrivateKeyAsync(name.Text, dialog.FileName, passphrase: bytes);
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    finally { Array.Clear(characters); }
                }
                finally { password.Clear(); }
            }
            return true;
        });
        app.Modal(form.ShowDialog);
    }

    private Task<CredentialVault?> UnlockFileAsync(Window owner, CancellationToken token = default, bool sshPrompt = false,
        bool allowInitialize = true)
    {
        CheckAvailable();
        if (archiving) throw new InvalidOperationException(Text("VaultBusy"));
        if (vault is not null) return Task.FromResult<CredentialVault?>(vault);
        return unlockInteraction.WaitAsync(async sharedToken =>
        {
            if (sshPrompt) await app.SshPrompts.WaitAsync(sharedToken);
            try
            {
                if (!owner.IsLoaded) return null;
                string path = FilePath;
                VaultFileState state = await Task.Run(() => Inspect(path), sharedToken);
                if (!owner.IsLoaded || disposed) return null;
                if ((!allowInitialize || owner is VaultEditorWindow) && state != VaultFileState.Existing)
                    throw new InvalidDataException(Text("VaultEditorNeedsExistingFile"));
                return await UnlockAsync(owner, path, state, sharedToken);
            }
            finally { if (sshPrompt) app.SshPrompts.Release(); }
        }, token);
    }

    private void CheckAvailable()
    {
        if (disposed) throw new ObjectDisposedException(nameof(VaultFileController));
        if (IsStale || app.Vault.MustRestart(this))
            throw new CredentialVaultStaleException(Text("SelectedVaultStale"));
    }

    private static VaultFileState Inspect(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException(Text("InvalidVaultPath"));
            return new FileInfo(path).Length == 0 ? VaultFileState.Empty : VaultFileState.Existing;
        }
        catch (FileNotFoundException) { return VaultFileState.Missing; }
        catch (DirectoryNotFoundException) { return VaultFileState.Missing; }
    }

    private enum VaultFileState { Missing, Empty, Existing }

    private async Task<CredentialVault?> UnlockAsync(Window owner, string path, VaultFileState state,
        CancellationToken token)
    {
        bool create = state != VaultFileState.Existing;
        char[]? password = PromptMaster(owner, create ? "InitializeVault" : "UnlockVault", confirm: create, token,
            path: path);
        if (password is null) return null;
        try
        {
            CredentialVault unlocked = await (state switch
            {
                VaultFileState.Missing => CredentialVault.CreateAsync(path, password, token),
                VaultFileState.Empty => CredentialVault.InitializeEmptyAsync(path, password, token),
                _ => CredentialVault.UnlockAsync(path, password, token),
            });
            if (disposed || token.IsCancellationRequested)
            {
                unlocked.Dispose();
                return null;
            }
            vault = unlocked;
            return vault;
        }
        finally { Array.Clear(password); }
    }

    public async Task AddPasswordAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        var form = new MainWindow.FormWindow(owner, Text("AddVaultPassword"));
        var name = new TextBox { MaxLength = 512 };
        var username = new TextBox { MaxLength = 1024 };
        var password = new PasswordBox { MaxLength = 16 * 1024 };
        form.AddField("CredentialName", name);
        form.AddField("VaultUsername", username);
        form.AddField("VaultPassword", password);
        form.AddButtons(() => { });
        try
        {
            if (app.Modal(form.ShowDialog) != true) return;
            char[] input = ReadPassword(password);
            try
            {
                byte[] bytes = new byte[StrictUtf8.GetByteCount(input)];
                try
                {
                    StrictUtf8.GetBytes(input, bytes);
                    Array.Clear(input);
                    await unlocked.AddPasswordAsync(name.Text, username.Text.Length == 0 ? null : username.Text, bytes);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { Array.Clear(input); }
        }
        finally { password.Clear(); }
    }

    public async Task DeleteEntryAsync(Window owner, CredentialVault unlocked, VaultEntryInfo entry)
    {
        if (await ConfirmImpactAsync(owner, unlocked, entry, "DeleteCredentialWarning"))
            await unlocked.DeleteEntryAsync(entry.Id);
    }

    public async Task EditEntryAsync(Window owner, CredentialVault unlocked, VaultEntryInfo entry)
    {
        var form = new MainWindow.FormWindow(owner, Text("EditCredential"));
        var name = new TextBox { Text = entry.Name, MaxLength = 512 };
        var username = new TextBox { Text = entry.Username ?? "", MaxLength = 1024 };
        var replace = new CheckBox();
        var password = new PasswordBox { MaxLength = 16 * 1024, IsEnabled = false };
        replace.Checked += (_, _) => password.IsEnabled = true;
        replace.Unchecked += (_, _) => { password.Clear(); password.IsEnabled = false; };
        form.AddField("CredentialName", name);
        form.AddField("VaultUsername", username);
        if (entry.Kind == "password")
        {
            form.AddField("ReplacePassword", replace);
            form.AddField("VaultPassword", password);
        }
        form.AddAsyncButtons(async () =>
        {
            if (replace.IsChecked == true &&
                !await ConfirmImpactAsync(form, unlocked, entry, "UpdateCredentialWarning")) return false;
            char[] characters = replace.IsChecked == true ? ReadPassword(password) : [];
            byte[]? bytes = null;
            try
            {
                if (replace.IsChecked == true) bytes = StrictUtf8.GetBytes(characters);
                Array.Clear(characters);
                await unlocked.UpdateEntryAsync(entry.Id, name.Text,
                    username.Text.Length == 0 ? null : username.Text,
                    bytes is null ? null : new ReadOnlyMemory<byte>(bytes));
                return true;
            }
            finally
            {
                Array.Clear(characters);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                password.Clear();
            }
        });
        try { app.Modal(form.ShowDialog); }
        finally { password.Clear(); }
    }

    private async Task<bool> ConfirmImpactAsync(Window owner, CredentialVault unlocked, VaultEntryInfo entry,
        string warning)
    {
        var impact = await app.Profiles.FindCredentialReferencesAsync(unlocked.Id, entry.Id, entry.Kind);
        if (!owner.IsLoaded) return false;
        string message = $"{Text(warning)}\n\n{Text("KnownCredentialReferences")}\n" +
            string.Join("\n", impact.Paths) + "\n\n" + Text("ReferenceScanScope");
        if (impact.Unavailable != 0)
            message += "\n" + string.Format(Text("ReferenceScanUnavailable"), impact.Unavailable);
        return app.Modal(() => ThemedMessageBox.Show(owner, message, Text("Vault"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes;
    }

    public async Task RestoreAsync(Window owner)
    {
        CheckAvailable();
        if (unlockInteraction.IsPending) throw new InvalidOperationException(Text("VaultBusy"));
        if (vault is { } unlocked)
        {
            var request = RequestRecovery(owner, FilePath);
            if (request is not { } chosen) return;
            try { await unlocked.RestoreAsync(chosen.Backup, chosen.Password); }
            finally { Array.Clear(chosen.Password); }
            await RefreshManagementAsync();
            return;
        }
        await unlockInteraction.WaitAsync(async () =>
        {
            string target = FilePath;
            var request = RequestRecovery(owner, target);
            if (request is not { } chosen) return null;
            try
            {
                CredentialVault restored = await CredentialVault.RecoverAsync(target, chosen.Backup, chosen.Password);
                if (!disposed) return vault = restored;
                restored.Dispose();
                return null;
            }
            finally { Array.Clear(chosen.Password); }
        });
        await RefreshManagementAsync();
    }

    private (string Backup, char[] Password)? RequestRecovery(Window owner, string target)
    {
        if (!owner.IsLoaded) return null;
        var dialog = new OpenFileDialog { Filter = Filter, Multiselect = false };
        if (app.Modal(() => dialog.ShowDialog(owner)) != true) return null;
        if (app.Modal(() => ThemedMessageBox.Show(owner, $"{Text("RestoreVaultWarning")}\n\n{target}", Text("RestoreVault"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) return null;
        char[]? password = PromptMaster(owner, "RestoreVault", confirm: false);
        return password is null ? null : (dialog.FileName, password);
    }

    public async Task BackupAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        var dialog = new SaveFileDialog { Filter = Filter, DefaultExt = ".tttykey", OverwritePrompt = false };
        if (app.Modal(() => dialog.ShowDialog(owner)) == true) await unlocked.BackupAsync(dialog.FileName);
    }

    public async Task ChangeMasterAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        char[]? password = PromptMaster(owner, "ChangeMaster", confirm: true);
        if (password is null) return;
        try { await unlocked.ChangeMasterPasswordAsync(password); }
        finally { Array.Clear(password); }
    }

    private char[]? PromptMaster(Window owner, string title, bool confirm, CancellationToken token = default,
        string? path = null)
    {
        var form = new MainWindow.FormWindow(owner, Text(title));
        var password = new PasswordBox { MaxLength = VaultContainer.MaximumMasterInputLength };
        var repeated = new PasswordBox { MaxLength = VaultContainer.MaximumMasterInputLength };
        if (path is not null)
            form.AddField("VaultPath", new TextBox { Text = path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        if (confirm) form.AddPrompt(FormatLength("MasterPasswordWithLength"), password);
        else form.AddField("MasterPassword", password);
        if (confirm) form.AddField("ConfirmMaster", repeated);
        form.Loaded += (_, _) => password.Focus();
        char[]? result = null;
        form.AddButtons(() =>
        {
            char[] first = ReadPassword(password);
            try
            {
                if (confirm)
                {
                    char[] second = ReadPassword(repeated);
                    bool equal = first.AsSpan().SequenceEqual(second);
                    Array.Clear(second);
                    if (!equal) throw new ArgumentException(Text("MasterMismatch"));
                }
                byte[] validated = VaultContainer.NormalizeMaster(first);
                CryptographicOperations.ZeroMemory(validated);
                result = first;
            }
            catch (ArgumentOutOfRangeException)
            {
                Array.Clear(first);
                throw new ArgumentException(FormatLength("MasterPasswordLength"));
            }
            catch
            {
                Array.Clear(first);
                throw;
            }
        }, acceptKey: confirm ? "Save" : "UnlockVault");
        try
        {
            if (SshHostTrust.ShowCancellable(app, form, token) != true) return null;
            char[]? accepted = result;
            result = null;
            return accepted;
        }
        finally
        {
            if (result is not null) Array.Clear(result);
            password.Clear();
            repeated.Clear();
        }

        static string FormatLength(string key) => string.Format(Text(key),
            VaultContainer.MinimumMasterLength, VaultContainer.MaximumMasterLength);
    }

    internal static char[] ReadPassword(PasswordBox box)
    {
        using var secret = box.SecurePassword;
        char[] result = new char[secret.Length];
        nint pointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try { Marshal.Copy(pointer, result, 0, result.Length); }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
        return result;
    }

}
