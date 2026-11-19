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

internal sealed record VaultFileHooks(Action<VaultInitializationStep, string, string>? MoveForInitialization = null,
    Func<Task>? BeforeCredentialHandoff = null, Func<Window, string?>? SelectRestoreFile = null,
    Action<string, string, string?>? IssueRestore = null, Action<string, string>? MoveForArchive = null);

internal sealed class VaultFileController(App app, string filePath, Action changed, Action<VaultFileController> idle) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim operation = new(1);
    private VaultUnlockInteraction? interaction;
    private VaultUnlockInteraction unlockInteraction => interaction ??= new(() => idle(this), opened => vault = opened);
    private CredentialVault? vault;
    private long managementRevision = -1;
    public IReadOnlyList<VaultEntryInfo> ManagementEntries { get; private set; } = [];
    public CredentialVault? Unlocked => vault;
    public string FilePath => filePath;
    public bool IsBusy => operation.CurrentCount == 0 || interaction?.IsPending == true;
    internal bool HasActiveWork => IsBusy || credentialReads != 0;
    public bool IsStale => vault?.IsStale == true;
    private bool disposed;
    private bool archiving;
    private Window? masterChangeHost;
    private int credentialReads;

    internal bool CanDismissMasterChange(Window host) => ReferenceEquals(masterChangeHost, host);

    private static string Filter => $"{Text("VaultFiles")} (*.tttykey)|*.tttykey";

    public Task UnlockFromMenuAsync(MainWindow owner) => RunAsync(owner, async () =>
    {
        if (await UnlockFileAsync(owner) is not null && owner.IsLoaded)
        {
            RefreshManagement();
            if (!owner.IsLoaded) return;
            app.Modal(() => ThemedMessageBox.ShowVault(owner, Text("VaultUnlocked"), Text("Vault")));
        }
    });

    public Task ManageAsync(MainWindow owner) => RunAsync(owner, async () =>
    {
        if (!owner.IsLoaded) return;
        if (vault is not { HasUnverifiedPublication: true } && !VerifiedFilePublication.IsUnresolved(FilePath) &&
            await Task.Run(() => Inspect(FilePath)) == VaultFileState.Existing &&
            await UnlockFileAsync(owner) is not null) RefreshManagement();
        if (owner.IsLoaded) owner.OpenForm("ManageVault", app.Vault.BuildManagement);
    });

    internal async Task ReconcilePublicationAsync(Window owner)
    {
        if (vault is { HasUnverifiedPublication: true } opened)
        {
            var result = await opened.ReconcilePublicationResultAsync();
            if (result is null) throw new IOException(Text("StoreSaveUnverified"));
            JsonStoreRecovery.RequireVerified(result);
        }
        else if (vault is null && VerifiedFilePublication.IsUnresolved(FilePath))
        {
            CheckAvailable();
            char[]? password = PromptMaster(owner, "RecheckFilePublication", confirm: false, path: FilePath);
            if (password is null) return;
            try { vault = await CredentialVault.ReconcileLockedPublicationAsync(FilePath, password); }
            finally { Array.Clear(password); }
        }
        else
        {
            var retained = await Task.Run(() => VerifiedFilePublication.ReinspectRetainedStages(FilePath));
            if (!ShowRetainedFilePaths(owner, retained))
                app.Modal(() => ThemedMessageBox.ShowVault(owner, Text("NoFilePublicationToRecheck"), Text("Vault")));
            return;
        }
        RefreshManagement();
        ShowRetainedFilePaths(owner, VerifiedFilePublication.GetRetainedStagePaths(FilePath));
    }

    public Task RestoreFromMenuAsync(MainWindow owner) => RunAsync(owner, () => RestoreAsync(owner));

    public Task ArchiveAsync(MainWindow owner) => RunAsync(owner, () => ArchiveFileAsync(owner));

    public async Task<bool> ArchiveFileAsync(Window owner)
    {
        if (unlockInteraction.IsPending) throw new InvalidOperationException(Text("VaultBusy"));
        string path = FilePath;
        archiving = true;
        try
        {
            string? archived = await Task.Run(() => CredentialVault.ReconcileArchive(path));
            if (archived is null)
            {
                CheckAvailable();
                CredentialVault? opened = vault;
                VaultFileEvidence? evidence = await Task.Run(() => opened is null ?
                    CredentialVault.InspectFile(path) : opened.InspectFileForManagement());
                if (evidence is null || !owner.IsLoaded) return false;
                if (app.Modal(() => ThemedMessageBox.ShowVault(owner, $"{Text("ArchiveVaultConfirm")}\n\n{path}",
                    Text("ArchiveVault"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No)) != MessageBoxResult.Yes) return false;
                archived = await Task.Run(() => CredentialVault.ArchiveFile(path, evidence, app.VaultFiles?.MoveForArchive));
            }
            ClearUnlocked();
            if (owner.IsLoaded)
                app.Modal(() => ThemedMessageBox.ShowVault(owner,  $"{Text("VaultArchived")}\n\n{archived}", Text("ArchiveVault")));
            return true;
        }
        finally { archiving = false; }
    }

    private void RefreshManagement()
    {
        CredentialVault? unlocked = vault;
        if (unlocked is null) return;
        if (unlocked.HasUnverifiedPublication || unlocked.IsStale) return;
        var metadata = unlocked.ReadCommittedMetadata();
        managementRevision = metadata.Revision;
        ManagementEntries = metadata.Entries;
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
        managementRevision = -1;
        changed();
    }

    public async Task<bool> OpenEditorAsync(Window owner)
    {
        bool opened = false;
        await RunAsync(owner, async () =>
        {
            if (await UnlockFileAsync(owner) is null) return;
            RefreshManagement();
            opened = true;
        });
        return opened;
    }

    public async Task RunAsync(Window owner, Func<Task> action)
    {
        bool admitted = await operation.WaitAsync(0);
        ResourceReservation? delivery = null;
        try
        {
            if (!admitted || app.Vault.IsSelectionReserved(this))
                throw new InvalidOperationException(Text("VaultBusy"));
            if (!disposed && owner.IsLoaded)
            {
                delivery = VaultController.ManagementDeliveries.Reserve(1);
                await action();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or CryptographicException or DllNotFoundException or EntryPointNotFoundException
            or JsonException or InvalidOperationException)
        {
            if (disposed || !owner.IsVisible && !CanDismissMasterChange(owner)) return;
            string message = exception switch
            {
                CredentialVaultStaleException => Text(app.Vault.MustRestart(this) ? "SelectedVaultStale" : "EditorVaultStale"),
                ResourceLimitException => Text("VaultBusy"),
                CryptographicException => Text("VaultAuthenticationFailed"),
                DllNotFoundException or EntryPointNotFoundException => Text("VaultCryptoUnavailable"),
                FilePublicationException publication => JsonStoreRecovery.DescribeFailure(publication.Result),
                _ => exception.Message,
            };
            app.Modal(() => ThemedMessageBox.ShowVault(owner, $"{FilePath}\n\n{message}", Text("Vault"),
                MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            delivery?.Dispose();
            if (admitted)
            {
                masterChangeHost = null;
                operation.Release();
                idle(this);
            }
        }
    }

    public Task<VaultSecret?> ReadSavedPasswordAsync(Window owner, CredentialReference reference,
        CancellationToken token) => ReadCredentialAsync(async () =>
    {
        if (reference.Kind != "password")
            throw new CredentialLookupException(MacroErrorCode.CredentialKindMismatch, Text("VaultCredentialKindMismatch"));
        long epoch = app.Vault.SelectionEpoch;
        if (await UnlockFileAsync(owner, token, sshPrompt: true) is not { } unlocked) return null;
        long revision = unlocked.Revision;
        return await KeepSelectedSecretAsync(await unlocked.GetPasswordAsync(reference.VaultId, reference.EntryId, token),
            owner, unlocked, epoch, revision, token);
    });

    internal async Task<VaultSecret?> ReadMacroPasswordAsync(Window owner, CredentialReference reference, CancellationToken token)
    {
        try
        {
            var secret = await ReadSavedPasswordAsync(owner, reference, token);
            token.ThrowIfCancellationRequested();
            return secret;
        }
        catch (CredentialLookupException error) { throw new MacroError(error.Code); }
        catch (CredentialVaultStaleException) { throw new MacroError(MacroErrorCode.VaultStale); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        { throw new MacroError(MacroErrorCode.CredentialUnavailable); }
    }

    public Task<VaultSecret?> ReadPrivateKeyAsync(Window owner, SshPrivateKeyRequest request,
        CancellationToken token) => ReadCredentialAsync(async () =>
    {
        CredentialReference? reference = request.SavedKey;
        if (reference is { Kind: not "privateKey" })
            throw new CredentialLookupException(MacroErrorCode.CredentialKindMismatch, Text("VaultCredentialKindMismatch"));
        long epoch = app.Vault.SelectionEpoch;
        if (await UnlockFileAsync(owner, token, sshPrompt: true) is not { } unlocked) return null;
        if (reference is null)
        {
            await app.SshPrompts.WaitAsync(token);
            try
            {
                if (await PickEntryAsync(owner, unlocked, "privateKey", token, request) is not { } entry) return null;
                reference = new CredentialReference(unlocked.Id, entry.Id, "privateKey");
            }
            finally { app.SshPrompts.Release(); }
        }
        long revision = unlocked.Revision;
        return await KeepSelectedSecretAsync(await unlocked.GetPrivateKeyAsync(reference.VaultId, reference.EntryId, token),
            owner, unlocked, epoch, revision, token);
    });

    private async Task<VaultSecret?> ReadCredentialAsync(Func<Task<VaultSecret?>> read)
    {
        credentialReads++;
        try { return await read(); }
        finally
        {
            credentialReads--;
            idle(this);
        }
    }

    private async Task<VaultSecret?> KeepSelectedSecretAsync(VaultSecret secret, Window host, CredentialVault unlocked, long epoch,
        long revision, CancellationToken token)
    {
        bool accepted = false;
        try
        {
            if (app.VaultFiles?.BeforeCredentialHandoff is { } before) await before();
            accepted = !token.IsCancellationRequested && host.IsVisible && !app.Vault.IsSelectionReserved(this) &&
                app.Vault.SelectionEpoch == epoch && app.Vault.IsSelected(this) &&
                !archiving && !unlocked.IsStale && !unlocked.HasUnverifiedPublication &&
                ReferenceEquals(vault, unlocked) && unlocked.Revision == revision;
            return accepted ? secret : null;
        }
        finally
        {
            if (!accepted) secret.Dispose();
        }
    }

    public Task ChooseCredentialAsync(Window owner, string kind, Action<CredentialReference, string> choose) =>
        RunAsync(owner, async () =>
        {
            if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
            if (await PickEntryAsync(owner, unlocked, kind, CancellationToken.None) is { } entry)
                choose(new CredentialReference(unlocked.Id, entry.Id, kind), $"{PermissionDisplay.Literal(entry.Name)} ({entry.Id:D})");
        });

    private async Task<VaultEntryInfo?> PickEntryAsync(Window owner, CredentialVault unlocked, string kind,
        CancellationToken token, SshPrivateKeyRequest? request = null)
    {
        var entries = await unlocked.ListEntriesAsync(token);
        if (!owner.IsLoaded) return null;
        var (form, list) = CreateCredentialPicker(owner, entries, kind, request);
        return SshHostTrust.ShowCancellable(app, form, token) == true
            ? ((CredentialChoice)list.SelectedItem).Entry : null;
    }

    private sealed record CredentialChoice(VaultEntryInfo Entry)
    {
        public override string ToString() => string.Join('\n', new[] { PermissionDisplay.Literal(Entry.Name),
            Entry.Username is { Length: > 0 } user ? PermissionDisplay.Literal(user) : null,
            Entry.PublicType, Entry.Fingerprint, Entry.Id.ToString("D") }.Where(value => value is not null));
    }

    internal static (MainWindow.FormWindow Form, ListBox Entries) CreateCredentialPicker(Window owner,
        IEnumerable<VaultEntryInfo> entries, string kind, SshPrivateKeyRequest? request)
    {
        string title = kind == "password" ? "SelectSavedPassword" : "SelectPrivateKey";
        var form = new MainWindow.FormWindow(owner, Text(title), WindowIcons.Vault) { Width = 540 };
        if (request is not null)
        {
            form.AddField("SshDestination", new TextBox { IsReadOnly = true, Text = request.Destination.ToString() });
            form.AddField("SshUsername", new TextBox { IsReadOnly = true, Text = PermissionDisplay.Literal(request.Username) });
        }
        if (kind == "password") form.AddNotice("SshSavedPasswordDestinationNotice");
        var list = new ListBox { ItemsSource = entries.Where(entry => entry.Kind == kind)
                .Select(entry => new CredentialChoice(entry)).ToArray(),
            MinHeight = 120, MaxHeight = 350 };
        form.AddField("VaultEntries", list);
        form.AddButtons(() =>
        {
            if (list.SelectedItem is not CredentialChoice) throw new ArgumentException(Text(title));
        }, acceptKey: request is null ? title : "Authenticate", cancelDefault: request is not null);
        return (form, list);
    }

    public async Task ImportPrivateKeyAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        long revision = unlocked.Revision;
        var dialog = new OpenFileDialog { Title = Text("ImportPrivateKey"), Multiselect = false };
        if (app.Modal(() => dialog.ShowDialog(owner)) != true) return;
        var form = new MainWindow.FormWindow(owner, Text("ImportPrivateKey"), WindowIcons.Vault);
        var name = new TextBox { Text = Path.GetFileName(dialog.FileName), MaxLength = 512 };
        form.AddField("CredentialName", name);
        form.AddAsyncButtons(async () =>
        {
            using var source = await Task.Run(() => CredentialVault.ReadPrivateKeySource(dialog.FileName));
            return await ImportAsync(source);
        });
        async Task<bool> ImportAsync(CredentialVault.PrivateKeySource source)
        {
            string keyName = name.Text;
            try { await ImportReviewedAsync(); }
            catch (PrivateKeyPassphraseRequiredException)
            {
                var prompt = new MainWindow.FormWindow(form, Text("SourceKeyPassphrase"), WindowIcons.Vault);
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
                            await ImportReviewedAsync(bytes);
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    finally { Array.Clear(characters); }
                }
                finally { password.Clear(); }
            }
            RefreshManagement();
            return true;

            Task<Guid> ImportReviewedAsync(ReadOnlyMemory<byte>? passphrase = null)
            {
                if (!ReferenceEquals(vault, unlocked) || unlocked.Revision != revision)
                    throw new InvalidOperationException(Text("ManagementTabOutdated"));
                return unlocked.ImportPrivateKeySourceAsync(keyName, source, passphrase, revision);
            }
        }
        app.Modal(form.ShowDialog);
    }

    private Task<CredentialVault?> UnlockFileAsync(Window owner, CancellationToken token = default, bool sshPrompt = false)
    {
        CheckAvailable();
        if (archiving) throw new InvalidOperationException(Text("VaultBusy"));
        if (vault is { HasUnverifiedPublication: true }) throw new IOException(Text("StoreSaveUnverified"));
        if (vault is not null) return Task.FromResult<CredentialVault?>(vault);
        return unlockInteraction.WaitAsync(owner, async (hosts, sharedToken) =>
        {
            if (sshPrompt) await app.SshPrompts.WaitAsync(sharedToken);
            try
            {
                string path = FilePath;
                var evidence = await Task.Run(() =>
                {
                    if (VerifiedFilePublication.IsUnresolved(path)) throw new IOException(Text("StoreSaveUnverified"));
                    return CredentialVault.InspectFile(path);
                }, sharedToken);
                if (disposed || hosts.Current is not { } host) return null;
                sharedToken.ThrowIfCancellationRequested();
                if (evidence is null || evidence.Length == 0)
                    throw new InvalidDataException(Text(host is VaultEditorWindow ?
                        "VaultEditorNeedsExistingFile" : "VaultFileMissing"));
                return await UnlockAsync(host, path, VaultFileState.Existing, sharedToken, evidence, hosts);
            }
            finally { if (sshPrompt) app.SshPrompts.Release(); }
        }, token);
    }

    private void CheckAvailable()
    {
        if (disposed) throw new ObjectDisposedException(nameof(VaultFileController));
        if (app.Vault.IsSelectionReserved(this)) throw new InvalidOperationException(Text("VaultBusy"));
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

    internal async Task InitializeAsync(Window owner)
    {
        CheckAvailable();
        if (owner is VaultEditorWindow || unlockInteraction.IsPending)
            throw new InvalidOperationException(Text("VaultBusy"));
        string path = FilePath;
        if (VerifiedFilePublication.IsUnresolved(path)) throw new IOException(Text("StoreSaveUnverified"));
        var evidence = await Task.Run(() => CredentialVault.InspectFile(path));
        if (!owner.IsLoaded || disposed) return;
        if (evidence?.Length > 0) throw new IOException(Text("VaultAlreadyExists"));
        var initialized = await UnlockAsync(owner, path, evidence is null ? VaultFileState.Missing : VaultFileState.Empty,
            CancellationToken.None, evidence);
        if (initialized is null) return;
        RefreshManagement();
        if (initialized.InitializationResult is { CleanupError: not null, PreservationPath: { } parked })
            app.Modal(() => ThemedMessageBox.ShowVault(owner, $"{Text("PublicationCleanupFailed")}\n\n{parked}",
                Text("InitializeVault"), icon: MessageBoxImage.Warning));
    }

    private async Task<CredentialVault?> UnlockAsync(Window owner, string path, VaultFileState state,
        CancellationToken token, VaultFileEvidence? evidence = null, VaultUnlockInteraction.UnlockHosts? hosts = null)
    {
        bool create = state != VaultFileState.Existing;
        string? explanation = state switch
        {
            VaultFileState.Missing => "CreateMissingVaultDescription",
            VaultFileState.Empty => "InitializeEmptyVaultDescription",
            _ => null,
        };
        char[]? password = PromptMaster(owner, create ? "InitializeVault" : "UnlockVault", confirm: create, token,
            path: path, hosts: hosts, explanation: explanation);
        if (password is null) return null;
        try
        {
            CredentialVault unlocked = await (state switch
            {
                VaultFileState.Missing => CredentialVault.CreateAsync(path, password, token),
                VaultFileState.Empty => CredentialVault.InitializeEmptyAsync(path, password, evidence!, token,
                    app.VaultFiles?.MoveForInitialization),
                _ => CredentialVault.UnlockBoundAsync(path, password, evidence!, token),
            });
            if (disposed || token.IsCancellationRequested)
            {
                unlocked.Dispose();
                return null;
            }
            if (hosts is null) vault = unlocked;
            return unlocked;
        }
        finally { Array.Clear(password); }
    }

    public async Task AddPasswordAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        long revision = unlocked.Revision;
        var form = new MainWindow.FormWindow(owner, Text("AddVaultPassword"), WindowIcons.Vault);
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
            if (!ReferenceEquals(vault, unlocked) || unlocked.Revision != revision)
                throw new InvalidOperationException(Text("ManagementTabOutdated"));
            char[] input = ReadPassword(password);
            try
            {
                byte[] bytes = new byte[StrictUtf8.GetByteCount(input)];
                try
                {
                    StrictUtf8.GetBytes(input, bytes);
                    Array.Clear(input);
                    await unlocked.AddPasswordAsync(name.Text, username.Text.Length == 0 ? null : username.Text, bytes, revision);
                    RefreshManagement();
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { Array.Clear(input); }
        }
        finally { password.Clear(); }
    }

    private (CredentialVault Owner, long Revision) CurrentEntry(VaultEntryInfo requested)
    {
        CheckAvailable();
        if (vault is not { } unlocked || unlocked.HasUnverifiedPublication || unlocked.Revision != managementRevision ||
            !ManagementEntries.Any(entry => entry.Id == requested.Id && entry.Kind == requested.Kind))
            throw new InvalidOperationException(Text("ManagementTabOutdated"));
        return (unlocked, managementRevision);
    }

    internal CredentialReference EntryReference(VaultEntryInfo entry)
    {
        var (unlocked, _) = CurrentEntry(entry);
        return new(unlocked.Id, entry.Id, entry.Kind);
    }

    public async Task DeleteEntryAsync(Window owner, VaultEntryInfo entry)
    {
        var (unlocked, revision) = CurrentEntry(entry);
        if (await ConfirmImpactAsync(owner, unlocked, entry, "DeleteCredentialWarning"))
        {
            await unlocked.DeleteEntryAsync(entry.Id, revision);
            RefreshManagement();
        }
    }

    public async Task EditEntryAsync(Window owner, VaultEntryInfo entry)
    {
        var (unlocked, revision) = CurrentEntry(entry);
        var form = new MainWindow.FormWindow(owner, Text("EditCredential"), WindowIcons.Vault);
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
                    bytes is null ? null : new ReadOnlyMemory<byte>(bytes), revision);
                RefreshManagement();
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
        long revision = unlocked.Revision;
        var impact = await app.Profiles.FindCredentialReferencesAsync(unlocked.Id, entry.Id, entry.Kind);
        if (!owner.IsLoaded) return false;
        string message = $"{Text(warning)}\n\n{Text("KnownCredentialReferences")}\n" +
            string.Join("\n", impact.Paths) + "\n\n" + Text("ReferenceScanScope");
        if (impact.Unavailable != 0)
            message += "\n" + string.Format(Text("ReferenceScanUnavailable"), impact.Unavailable);
        if (app.Modal(() => ThemedMessageBox.ShowVault(owner, message, Text("Vault"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) return false;
        var current = await app.Profiles.FindCredentialReferencesAsync(unlocked.Id, entry.Id, entry.Kind);
        if (!ReferenceEquals(vault, unlocked) || unlocked.Revision != revision ||
            current.Unavailable != impact.Unavailable || !current.Paths.SequenceEqual(impact.Paths, StringComparer.Ordinal))
            throw new InvalidOperationException(Text("ManagementTabOutdated"));
        return owner.IsLoaded;
    }

    public async Task RestoreAsync(Window owner)
    {
        CheckAvailable();
        if (unlockInteraction.IsPending) throw new InvalidOperationException(Text("VaultBusy"));
        if (!owner.IsLoaded) return;

        string target = FilePath;
        CredentialVault? current = vault;
        string? backup = app.VaultFiles?.SelectRestoreFile is { } select ? select(owner) : SelectRestoreBackup(owner);
        if (backup is null) return;
        var basis = await Task.Run(() => CredentialVault.PrepareRestore(target, backup, current));
        if (!owner.IsLoaded || !ReferenceEquals(current, vault)) return;

        if (app.Modal(() => ThemedMessageBox.ShowVault(owner, $"{Text("RestoreVaultWarning")}\n\n{target}\n\n{basis.Backup}",
            Text("RestoreVault"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No)) != MessageBoxResult.Yes) return;
        char[]? password = PromptMaster(owner, "RestoreVault", confirm: false, path: basis.Backup);
        if (password is null) return;
        try
        {
            CredentialVault restored = await CredentialVault.RestoreFileAsync(basis, password, current,
                issue: app.VaultFiles?.IssueRestore);
            if (disposed)
            {
                restored.Dispose();
                return;
            }
            vault = restored;
        }
        finally { Array.Clear(password); }
        RefreshManagement();
        ShowRetainedFilePaths(owner, VerifiedFilePublication.GetRetainedStagePaths(FilePath));
    }

    private bool ShowRetainedFilePaths(Window owner, string[] paths)
    {
        if (paths.Length == 0 || !owner.IsLoaded) return false;
        app.Modal(() => ThemedMessageBox.ShowVault(owner,
            Text("PublicationRetainedStages") + "\n\n" + string.Join("\n", paths), Text("Vault")));
        return true;
    }

    private string? SelectRestoreBackup(Window owner)
    {
        var dialog = new OpenFileDialog { Filter = Filter, Multiselect = false };
        return app.Modal(() => dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    public async Task BackupAsync(Window owner)
    {
        CredentialVault? unlocked = vault ?? await UnlockFileAsync(owner);
        if (unlocked is null || !owner.IsLoaded) return;
        bool rescue = unlocked.IsStale;
        if (rescue && app.Modal(() => ThemedMessageBox.ShowVault(owner, Text("VaultRescueCopyExplanation"),
                Text("BackupVault"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel)) != MessageBoxResult.OK) return;
        var dialog = new SaveFileDialog { Filter = Filter, DefaultExt = ".tttykey", OverwritePrompt = false };
        if (app.Modal(() => dialog.ShowDialog(owner)) != true) return;
        if (rescue) await unlocked.BackupRetainedGenerationAsync(dialog.FileName);
        else await unlocked.BackupAsync(dialog.FileName);
    }

    public async Task ChangeMasterAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        char[]? password = PromptMaster(owner, "ChangeMaster", confirm: true);
        if (password is null) return;
        // RunAsync keeps the file and application alive even after this host closes.
        masterChangeHost = owner;
        try { await unlocked.ChangeMasterPasswordAsync(password, app.IssueMasterPassword); }
        finally { Array.Clear(password); }
        RefreshManagement();
    }

    private char[]? PromptMaster(Window owner, string title, bool confirm, CancellationToken token = default,
        string? path = null, VaultUnlockInteraction.UnlockHosts? hosts = null, string? explanation = null)
    {
        var form = new MainWindow.FormWindow(hosts is null ? owner : null, Text(title), WindowIcons.Vault);
        Window? host = owner;
        var password = new PasswordBox { MaxLength = VaultContainer.MaximumMasterInputLength };
        var repeated = new PasswordBox { MaxLength = VaultContainer.MaximumMasterInputLength };
        if (path is not null)
            form.AddField("VaultPath", new TextBox { Text = path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        if (confirm)
        {
            string caption = FormatLength("MasterPasswordWithLength");
            form.AddPrompt(explanation is null ? caption : $"{Text(explanation)}\n\n{caption}", password);
        }
        else form.AddField("MasterPassword", password);
        if (confirm) form.AddField("ConfirmMaster", repeated);
        form.Loaded += (_, _) =>
        {
            if (hosts is not null) PositionAtHost();
            password.Focus();
        };
        char[]? result = null;
        form.AddButtons(() =>
        {
            if (hosts is not null && (token.IsCancellationRequested ||
                host?.IsVisible != true || !ReferenceEquals(hosts.Current, host)))
                throw new InvalidOperationException(Text("VaultBusy"));
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
            if (hosts is not null) hosts.Changed += HostChanged;
            if (SshHostTrust.ShowCancellable(app, form, token) != true) return null;
            char[]? accepted = result;
            result = null;
            return accepted;
        }
        finally
        {
            if (hosts is not null) hosts.Changed -= HostChanged;
            if (result is not null) Array.Clear(result);
            password.Clear();
            repeated.Clear();
        }

        void HostChanged()
        {
            form.Dispatcher.BeginInvoke(() =>
            {
                if (!form.IsVisible) return;
                Window? next = hosts!.Current;
                if (next is null || token.IsCancellationRequested)
                {
                    form.Close();
                    return;
                }
                if (ReferenceEquals(next, host)) return;
                host = next;
                PositionAtHost();
                form.Activate();
                password.Focus();
            });
        }

        void PositionAtHost()
        {
            if (host is not { WindowState: not WindowState.Minimized } current) return;
            form.Left = current.Left + Math.Max(0, (current.ActualWidth - form.ActualWidth) / 2);
            form.Top = current.Top + Math.Max(0, (current.ActualHeight - form.ActualHeight) / 2);
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
