using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class VaultEntryBinding(VaultFileController file)
{
    private readonly WeakReference<CredentialVault>? owner = file.Unlocked is { } opened ? new(opened) : null;
    internal VaultFileController File { get; } = file;
    internal bool Active { get; private set; } = true;
    internal bool Matches(CredentialVault? current) => owner is null ? current is null :
        owner.TryGetTarget(out CredentialVault? opened) && ReferenceEquals(opened, current);
    internal void Retire() => Active = false;
}

internal sealed record VaultEntrySelection(VaultEntryBinding Binding, long RoleGeneration, long Revision,
    (string Name, string Kind)[] Entries);

internal sealed class VaultEntrySelectionChangedException() : InvalidOperationException(Text("VaultEntrySelectionChanged"));

internal sealed record VaultFileHooks(Action<VaultInitializationStep, string, string>? MoveForInitialization = null,
    Func<Task>? BeforeCredentialHandoff = null, Func<Window, string?>? SelectRestoreFile = null,
    Action<string, string, string?>? IssueRestore = null, Action<string, string>? MoveForArchive = null);

internal sealed class VaultFileUnavailableException(string message) : IOException(message);

internal sealed class VaultFileController(App app, string filePath, Action changed, Action<VaultFileController> idle) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim operation = new(1);
    private VaultUnlockInteraction? interaction;
    private bool unlockingExistingFile;
    private VaultUnlockInteraction unlockInteraction => interaction ??= new(() => idle(this), opened =>
    {
        vault = opened;
        RefreshManagement();
    });
    private CredentialVault? vault;
    private long managementRevision = -1;
    internal long ManagementRevision => managementRevision;
    internal long RoleGeneration { get; private set; }
    internal void ChangeRole() => RoleGeneration++;
    public IReadOnlyList<VaultEntryInfo> ManagementEntries { get; private set; } = [];
    public CredentialVault? Unlocked => vault;
    public string FilePath => filePath;
    public string? LastArchivePath { get; private set; }
    public bool IsBusy => operation.CurrentCount == 0 || interaction?.IsPending == true;
    internal bool HasActiveWork => IsBusy || credentialReads != 0;
    public bool IsStale => vault?.IsStale == true;
    private bool disposed;
    private bool archiving;
    private Window? initializationHost;
    private Window? masterChangeHost;
    private int credentialReads;

    internal bool CanDismissMasterChange(Window host) => ReferenceEquals(masterChangeHost, host);

    internal bool IsInitializingFor(Window host) => ReferenceEquals(initializationHost, host);

    private static string Filter => $"{Text("VaultFiles")} (*.tttykey)|*.tttykey";

    public Task UnlockFromMenuAsync(MainWindow owner) => RunAsync(owner, async () =>
    {
        if (vault is not null || await UnlockFileAsync(owner, allowInitialization: true) is not { } opened || !owner.IsLoaded)
            return;
        if (opened.InitializationResult is not null) owner.OpenForm("ManageVault", app.Vault.BuildManagement);
    });

    internal Task LockFromMenuAsync(MainWindow owner) => RunAsync(owner, () =>
    {
        if (vault is not { } opened) return Task.CompletedTask;
        if (credentialReads != 0 || interaction?.IsPending == true)
            throw new InvalidOperationException(Text("VaultBusy"));
        if (opened.HasUnverifiedPublication)
            throw new IOException(Text("StoreSaveUnverified"));

        app.Vault.RememberStalePath(this);
        ClearUnlocked();
        return Task.CompletedTask;
    });

    public Task ManageAsync(MainWindow owner) => RunAsync(owner, async () =>
    {
        if (!owner.IsLoaded) return;
        if (vault is not { HasUnverifiedPublication: true } && !VerifiedFilePublication.IsUnresolved(FilePath))
        {
            if (await UnlockFileAsync(owner, allowInitialization: true) is null &&
                await Task.Run(() => Inspect(FilePath)) != VaultFileState.Existing) return;
        }
        if (owner.IsLoaded) owner.OpenForm("ManageVault", app.Vault.BuildManagement);
    });

    internal async Task ReconcilePublicationAsync(Window owner, FormView? form = null)
    {
        var report = CaptureReport(owner, form);
        if (vault is { HasUnverifiedPublication: true } opened)
        {
            using var progress = form?.BeginProgress("CheckingPreviousSave");
            var result = await opened.ReconcilePublicationResultAsync();
            if (result is null) throw new IOException(Text("StoreSaveUnverified"));
            JsonStoreRecovery.RequireVerified(result);
        }
        else if (vault is null && VerifiedFilePublication.IsUnresolved(FilePath))
        {
            CheckAvailable();
            char[]? password = await PromptMasterAsync(owner, "RecheckFilePublication", confirm: false, path: FilePath);
            if (password is null) return;
            try
            {
                using var progress = form?.BeginProgress("CheckingPreviousSave");
                vault = await CredentialVault.ReconcileLockedPublicationAsync(FilePath, password);
            }
            finally { Array.Clear(password); }
        }
        else
        {
            string[] retained;
            using (form?.BeginProgress("CheckingPreviousSave"))
                retained = await Task.Run(() => VerifiedFilePublication.ReinspectRetainedStages(FilePath));
            if (!ShowRetainedFilePaths(report, retained)) form?.SetStatusResource("NoFilePublicationToRecheck");
            return;
        }
        RefreshManagement();
        ShowRetainedFilePaths(report, VerifiedFilePublication.GetRetainedStagePaths(FilePath));
    }

    public Task RestoreAsync(Window owner, FormView? form = null) =>
        RunAsync(owner, () => RestoreFileAsync(owner, form), form, reportTitle: "RestoreVault");

    public async Task<bool> ArchiveAsync(Window owner, FormView? form = null)
    {
        bool archived = false;
        await RunAsync(owner, async () => archived = await ArchiveFileAsync(owner, form), form, reportTitle: "ArchiveVault");
        return archived;
    }

    private async Task<bool> ArchiveFileAsync(Window owner, FormView? form)
    {
        if (unlockInteraction.IsPending) throw new InvalidOperationException(Text("VaultBusy"));
        string path = FilePath;
        var report = CaptureReport(owner, form, "ArchiveVault");
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
                using var progress = form?.BeginProgress("FileOperationPending");
                archived = await Task.Run(() => CredentialVault.ArchiveFile(path, evidence, app.VaultFiles?.MoveForArchive));
            }
            LastArchivePath = archived;
            ClearUnlocked();
            string result = $"{Text("VaultArchived")}\n\n{archived}";
            if (owner is VaultEditorWindow && owner.IsLoaded)
                app.Modal(() => ThemedMessageBox.ShowVault(owner, result, Text("ArchiveVault")));
            else if (form is null) report(new object(), result);
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

    private void ClearUnlocked()
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
        }, closesOnError: true);
        return opened;
    }

    private Action<object, string> CaptureReport(Window owner, FormView? form = null, string title = "Vault")
    {
        FormView? view = form ?? owner.Content as FormView;
        var selectedVaultReport = app.StatusReports.Capture(null, Text(title));
        return (key, message) =>
        {
            if (view is not null && (view.TabOwner is { IsVisible: true } || view.Tab is null && owner.IsVisible))
                view.Reports.Report(key, message);
            else if (owner is MainWindow && (owner.IsVisible || app.LastTabWindow is not null))
                selectedVaultReport(key, message);
            else app.Modal(() => ThemedMessageBox.ShowVault(owner.IsVisible ? owner : null, message, Text("Vault"),
                MessageBoxButton.OK, MessageBoxImage.Warning));
        };
    }

    public async Task RunAsync(Window owner, Func<Task> action, FormView? form = null, bool closesOnError = false,
        string reportTitle = "Vault")
    {
        var report = CaptureReport(owner, form, reportTitle);
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
            bool hasReportHost = (form?.TabOwner ?? owner).IsVisible ||
                (form is null && owner is MainWindow && app.LastTabWindow is not null);
            if (disposed || !hasReportHost && !CanDismissMasterChange(owner)) return;
            string message = exception switch
            {
                CredentialVaultStaleException => Text(app.Vault.MustRestart(this) ? "SelectedVaultStale" : "EditorVaultStale"),
                ResourceLimitException => Text("VaultBusy"),
                DuplicateCredentialNameException => Text("DuplicateCredentialName"),
                CryptographicException => Text("VaultAuthenticationFailed"),
                DllNotFoundException or EntryPointNotFoundException => Text("VaultCryptoUnavailable"),
                FilePublicationException publication => JsonStoreRecovery.DescribeFailure(publication.Result),
                _ => exception.Message,
            };
            string details = $"{FilePath}\n\n{message}";
            if (closesOnError)
                app.Modal(() => ThemedMessageBox.ShowVault(owner, details, Text("Vault"), MessageBoxButton.OK, MessageBoxImage.Error));
            else report(exception is FilePublicationException failed ? failed.Result.OperationId : exception, details);
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

    public Task<SshCredentialAvailability> CheckSshCredentialsAsync(Window owner, SshSavedCredentialRequest request,
        CancellationToken token, TerminalTab? tab = null) => ReadCredentialAsync(async () =>
    {
        long epoch = app.Vault.SelectionEpoch;
        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
        CredentialVault unlocked;
        try
        {
            unlocked = await UnlockFileAsync(owner, token, sshPrompt: true, tab: tab) ?? throw new SshInteractionCancelledException();
        }
        catch (VaultFileUnavailableException error)
        {
            if (!IsCurrentSelection(epoch)) throw new SshCredentialFailureException(Text("ManagementTabOutdated"));
            string reason = error.Message + "\n" + Text("SelectOrRestoreVault");
            if (request.Password is not null) unavailable.Add("password", reason);
            if (request.PrivateKey is not null) unavailable.Add("publicKey", reason);
            return new SshCredentialAvailability(unavailable);
        }

        var (revision, entries) = await unlocked.ReadMetadataAsync(token);
        token.ThrowIfCancellationRequested();
        if (!CanHandOffCredential(tab?.DecisionHost ?? owner, unlocked, epoch, revision))
            throw new SshCredentialFailureException(Text("ManagementTabOutdated"));
        if (request.Password is { } password)
            CredentialVault.MatchCredential(entries, password.Name, "password");
        if (request.PrivateKey is { } key)
        {
            try { CredentialVault.MatchCredential(entries, key.Name, "privateKey"); }
            catch (CredentialLookupException error) when (error.Code == MacroErrorCode.CredentialUnavailable)
            { unavailable.Add("publicKey", Text("SavedPrivateKeyMissing")); }
        }
        return new SshCredentialAvailability(unavailable);
    });

    public Task<VaultSecret?> ReadSavedPasswordAsync(Window owner, CredentialReference reference,
        CancellationToken token, bool sshAuthentication = true, TerminalTab? tab = null) => ReadCredentialAsync<VaultSecret?>(async () =>
    {
        if (reference.Kind != "password")
            throw new CredentialLookupException(MacroErrorCode.CredentialKindMismatch, Text("VaultCredentialKindMismatch"));
        long epoch = app.Vault.SelectionEpoch;
        CredentialVault? unlocked;
        try { unlocked = await UnlockFileAsync(owner, token, sshPrompt: true, tab: tab); }
        catch (VaultFileUnavailableException error) when (sshAuthentication)
        {
            if (!IsCurrentSelection(epoch)) throw new SshCredentialFailureException(Text("ManagementTabOutdated"));
            throw new SshMethodUnavailableException(error.Message + "\n" + Text("SelectOrRestoreVault"), forAttempt: true);
        }
        if (unlocked is null) return null;
        long revision = unlocked.Revision;
        var material = await unlocked.GetPasswordAsync(reference.Name, token);
        return await KeepSelectedSecretAsync(material,
            owner, unlocked, epoch, revision, token, tab);
    });

    internal async Task<VaultSecret?> ReadMacroPasswordAsync(Window owner, CredentialReference reference, CancellationToken token,
        TerminalTab? tab = null)
    {
        try
        {
            var secret = await ReadSavedPasswordAsync(owner, reference, token, sshAuthentication: false, tab);
            if (token.IsCancellationRequested)
            {
                secret?.Dispose();
                token.ThrowIfCancellationRequested();
            }
            return secret;
        }
        catch (CredentialLookupException error) { throw new MacroError(error.Code); }
        catch (CredentialVaultStaleException) { throw new MacroError(MacroErrorCode.VaultStale); }
        catch (VaultFileUnavailableException error)
        { throw new MacroError(MacroErrorCode.CredentialUnavailable, error.Message + "\n" + Text("SelectOrRestoreVault")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        { throw new MacroError(MacroErrorCode.CredentialUnavailable); }
    }

    public Task<VaultSecret?> ReadPrivateKeyAsync(Window owner, SshPrivateKeyRequest request,
        CancellationToken token, TerminalTab? tab = null) => ReadCredentialAsync<VaultSecret?>(async () =>
    {
        CredentialReference? reference = request.SavedKey;
        if (reference is { Kind: not "privateKey" })
            throw new CredentialLookupException(MacroErrorCode.CredentialKindMismatch, Text("VaultCredentialKindMismatch"));
        long epoch = app.Vault.SelectionEpoch;
        CredentialVault? unlocked;
        try { unlocked = await UnlockFileAsync(owner, token, sshPrompt: true, tab: tab); }
        catch (VaultFileUnavailableException error)
        {
            if (!IsCurrentSelection(epoch)) throw new SshCredentialFailureException(Text("ManagementTabOutdated"));
            throw new SshMethodUnavailableException(error.Message + "\n" +
                Text(reference is null ? "ImportKeyBeforeConnecting" : "SelectOrRestoreVault"), forAttempt: true);
        }
        if (unlocked is null) return null;
        if (reference is null)
        {
            await app.SshPrompts.WaitAsync(token);
            try
            {
                if (await PickEntryAsync(owner, unlocked, "privateKey", token, request, tab: tab) is not { } entry) return null;
                reference = new CredentialReference(entry.Name, "privateKey");
            }
            finally { app.SshPrompts.Release(); }
        }
        long revision = unlocked.Revision;
        VaultSecret material;
        try { material = await unlocked.GetPrivateKeyAsync(reference.Name, token); }
        catch (CredentialLookupException error) when (error.Code == MacroErrorCode.CredentialUnavailable)
        { throw new SshMethodUnavailableException(Text("SavedPrivateKeyMissing"), forAttempt: true); }
        return await KeepSelectedSecretAsync(material, owner, unlocked, epoch, revision, token, tab);
    });

    private async Task<T> ReadCredentialAsync<T>(Func<Task<T>> read)
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
        long revision, CancellationToken token, TerminalTab? tab)
    {
        bool accepted = false;
        try
        {
            if (app.VaultFiles?.BeforeCredentialHandoff is { } before) await before();
            accepted = !token.IsCancellationRequested && CanHandOffCredential(tab?.DecisionHost ?? host, unlocked, epoch, revision);
            return accepted ? secret : null;
        }
        finally
        {
            if (!accepted) secret.Dispose();
        }
    }

    private bool CanHandOffCredential(Window host, CredentialVault unlocked, long epoch, long revision) =>
        host.IsVisible && IsCurrentSelection(epoch) && !archiving && !unlocked.IsStale && !unlocked.HasUnverifiedPublication &&
        ReferenceEquals(vault, unlocked) && unlocked.Revision == revision;

    private bool IsCurrentSelection(long epoch) => !app.Vault.IsSelectionReserved(this) &&
        app.Vault.SelectionEpoch == epoch && app.Vault.IsSelected(this);

    public async Task ChooseCredentialAsync(Window owner, string kind, Action<string> choose)
    {
        bool manage = false;
        await RunAsync(owner, async () =>
        {
            if (await UnlockFileAsync(owner, allowInitialization: true) is not { } unlocked || !owner.IsLoaded) return;
            if (await PickEntryAsync(owner, unlocked, kind, CancellationToken.None, manage: () => manage = true) is { } entry)
                choose(entry.Name);
        });
        if (manage && owner.IsLoaded && app.Vault.IsSelected(this)) await app.Vault.ShowManagementFromPickerAsync(owner);
    }

    private async Task<VaultEntryInfo?> PickEntryAsync(Window owner, CredentialVault unlocked, string kind,
        CancellationToken token, SshPrivateKeyRequest? request = null, Action? manage = null, TerminalTab? tab = null)
    {
        var entries = await unlocked.ListEntriesAsync(token);
        owner = tab?.DecisionHost ?? owner;
        if (!owner.IsLoaded) return null;
        var (form, list) = CreateCredentialPicker(owner, entries, kind, request);
        bool skip = false;
        if (request is not null && list.Items.Count == 0)
            throw new SshMethodUnavailableException(Text("NoEligiblePrivateKey"));
        if (request?.CanTryAnotherMethod == true)
            form.AddAction("TryAnotherSshMethod", () => { skip = true; form.Finish(false); });
        if (manage is not null)
            form.AddAction("ManageVault", () => { manage(); form.DialogResult = false; });
        bool selected = await TabDecision.ShowFormAsync(app, form, token, tab);
        token.ThrowIfCancellationRequested();
        if (skip) throw new SshMethodUnavailableException(null);
        return selected ? ((CredentialChoice)list.SelectedItem).Entry : null;
    }

    private sealed record CredentialChoice(VaultEntryInfo Entry)
    {
        public override string ToString() => string.Join('\n', new[] { PermissionDisplay.Literal(Entry.Name),
            Entry.PublicType, Entry.Fingerprint }.Where(value => value is not null));
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
        Button accept = form.AddButtons(() =>
        {
            if (list.SelectedItem is not CredentialChoice) throw new ArgumentException(Text(title));
        }, acceptKey: request is null ? title : "Authenticate", cancelDefault: request is not null,
            cancelKey: request is null ? "Cancel" : "CancelSshConnection");
        if (request is null)
        {
            list.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (args.ClickCount != 2 || args.OriginalSource is not DependencyObject origin ||
                    ItemsControl.ContainerFromElement(list, origin) is not ListBoxItem { Content: CredentialChoice choice }) return;
                args.Handled = true;
                list.SelectedItem = choice;
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
        }
        return (form, list);
    }

    public async Task ImportPrivateKeyAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        long revision = unlocked.Revision;
        if (SelectPrivateKeyFile(owner, "ImportPrivateKey") is not { } path) return;
        var form = new MainWindow.FormWindow(owner, Text("ImportPrivateKey"), WindowIcons.Vault);
        form.AddStatusBar();
        var name = new TextBox { Text = Path.GetFileName(path), MaxLength = 512 };
        var validName = AddCredentialName(form, name, unlocked);
        form.AddAsyncButtons(async () =>
        {
            if (!validName()) return false;
            string keyName = name.Text;
            if (!await ImportKeyFileAsync(form, path, "ImportPrivateKey", async (source, passphrase) =>
            {
                if (!ReferenceEquals(vault, unlocked) || unlocked.Revision != revision)
                    throw new InvalidOperationException(Text("ManagementTabOutdated"));
                using var progress = form.BeginProgress("SavingChanges");
                await unlocked.ImportPrivateKeySourceAsync(keyName, source, passphrase, revision);
            })) return false;
            RefreshManagement();
            return true;
        });
        app.Modal(form.ShowDialog);
    }

    private string? SelectPrivateKeyFile(Window owner, string actionKey)
    {
        var picker = new OpenFileDialog { Title = Text(actionKey), Multiselect = false };
        return app.Modal(() => picker.ShowDialog(owner)) == true ? picker.FileName : null;
    }

    private async Task<bool> ImportKeyFileAsync(Window owner, string path, string actionKey,
        Func<CredentialVault.PrivateKeySource, ReadOnlyMemory<byte>?, Task> import)
    {
        using var source = await Task.Run(() => CredentialVault.ReadPrivateKeySource(path));
        try { await import(source, null); }
        catch (PrivateKeyPassphraseRequiredException)
        {
            var prompt = new MainWindow.FormWindow(owner, Text("SourceKeyPassphrase"), WindowIcons.Vault);
            var password = new PasswordBox { MaxLength = 16 * 1024 };
            prompt.AddField("SourceKeyPassphrase", password);
            prompt.Loaded += (_, _) => password.Focus();
            prompt.AddButtons(() => { }, acceptKey: actionKey);
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
                        await import(source, bytes);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                finally { Array.Clear(characters); }
            }
            finally { password.Clear(); }
        }
        return true;
    }

    private async Task<CredentialVault?> UnlockFileAsync(Window owner, CancellationToken token = default, bool sshPrompt = false,
        bool allowInitialization = false, TerminalTab? tab = null)
    {
        CheckAvailable();
        if (archiving) throw new InvalidOperationException(Text("VaultBusy"));
        if (vault is { HasUnverifiedPublication: true }) throw new IOException(Text("StoreSaveUnverified"));
        if (vault is not null) return vault;
        // Join the live, verified existing-file request before yielding to another
        // file inspection. Its original consumer may close its window immediately.
        if (!allowInitialization && !unlockingExistingFile)
        {
            if (VerifiedFilePublication.IsUnresolved(FilePath)) throw new IOException(Text("StoreSaveUnverified"));
            VaultFileState state = await Task.Run(() => Inspect(FilePath), token);
            CheckAvailable();
            if (vault is not null) return vault;
            if (state != VaultFileState.Existing) throw UnavailableVault(owner, state);
        }
        return await unlockInteraction.WaitAsync(owner, async (hosts, sharedToken) =>
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
                if (evidence is { Length: > 0 })
                {
                    unlockingExistingFile = true;
                    try { return await UnlockAsync(host, path, VaultFileState.Existing, sharedToken, evidence, hosts); }
                    finally { unlockingExistingFile = false; }
                }

                var state = evidence is null ? VaultFileState.Missing : VaultFileState.Empty;
                if (hosts.InitializationHost is not { } initializer || !app.Vault.IsSelected(this))
                    throw UnavailableVault(host, state);
                var initialized = await UnlockAsync(initializer, path, state, sharedToken, evidence, hosts);
                try
                {
                    if (initialized is not null && hosts.InitializationHost is { } currentHost)
                        ReportInitializationCleanup(currentHost, initialized);
                    return initialized;
                }
                catch
                {
                    initialized?.Dispose();
                    throw;
                }
            }
            finally { if (sshPrompt) app.SshPrompts.Release(); }
        }, token, allowInitialization, tab);
    }

    private VaultFileUnavailableException UnavailableVault(Window owner, VaultFileState state) => new(
        owner is VaultEditorWindow ? Text("VaultEditorNeedsExistingFile") :
            string.Format(Text(state == VaultFileState.Missing ? "VaultUnavailableMissing" : "VaultUnavailableEmpty"), FilePath));

    internal async Task<string?> DescribeMissingTargetAsync(Window host)
    {
        if (vault is not null || VerifiedFilePublication.IsUnresolved(FilePath)) return null;
        VaultFileState state;
        try { state = await Task.Run(() => Inspect(FilePath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (disposed || vault is not null || state == VaultFileState.Existing) return null;
        return UnavailableVault(host, state).Message + "\n" + Text("OpenVaultManagementToCreate");
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

    private void ReportInitializationCleanup(Window owner, CredentialVault initialized)
    {
        if (initialized.InitializationResult is { CleanupError: not null, PreservationPath: { } parked })
            CaptureReport(owner)(initialized.InitializationResult.OperationId, $"{Text("PublicationCleanupFailed")}\n\n{parked}");
    }

    private async Task<CredentialVault?> UnlockAsync(Window owner, string path, VaultFileState state,
        CancellationToken token, VaultFileEvidence? evidence, VaultUnlockInteraction.UnlockHosts hosts)
    {
        bool create = state != VaultFileState.Existing;
        char[]? password = await PromptMasterAsync(owner, create ? "InitializeVault" : "UnlockVault", confirm: create, token,
            path: state == VaultFileState.Missing ? null : path, hosts: hosts,
            explanation: state == VaultFileState.Empty ? "InitializeEmptyVaultDescription" : null);
        if (password is null) return null;
        if (create) initializationHost = hosts.InitializationHost ?? owner;
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
            return unlocked;
        }
        finally
        {
            if (create) initializationHost = null;
            Array.Clear(password);
        }
    }

    public async Task AddPasswordAsync(Window owner)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        long revision = unlocked.Revision;
        var form = new MainWindow.FormWindow(owner, Text("AddVaultPassword"), WindowIcons.Vault);
        form.AddStatusBar();
        var name = new TextBox { MaxLength = 512 };
        var password = new PasswordBox { MaxLength = 16 * 1024 };
        var validName = AddCredentialName(form, name, unlocked);
        form.AddField("VaultPassword", password);
        form.AddAsyncButtons(async () =>
        {
            if (!validName()) return false;
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
                    using (form.BeginProgress("SavingChanges"))
                        await unlocked.AddPasswordAsync(name.Text, bytes, revision);
                    RefreshManagement();
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { Array.Clear(input); password.Clear(); }
            return true;
        });
        try { app.Modal(form.ShowDialog); }
        finally { password.Clear(); }
    }

    private CredentialVault RequireCurrentSelection(VaultEntrySelection selection)
    {
        if (!selection.Binding.Active || selection.Binding.File != this || selection.RoleGeneration != RoleGeneration ||
            vault is not { HasUnverifiedPublication: false, IsStale: false } unlocked ||
            !selection.Binding.Matches(unlocked) ||
            unlocked.Revision != selection.Revision || managementRevision != selection.Revision)
            throw new VaultEntrySelectionChangedException();
        return unlocked;
    }

    internal VaultEntryInfo[] ResolveEntries(VaultEntrySelection selection)
    {
        RequireCurrentSelection(selection);
        var byIdentity = ManagementEntries.ToDictionary(entry => (entry.Name, entry.Kind));
        var resolved = new VaultEntryInfo[selection.Entries.Length];
        for (int index = 0; index < resolved.Length; index++)
        {
            if (!byIdentity.TryGetValue(selection.Entries[index], out var entry))
                throw new VaultEntrySelectionChangedException();
            resolved[index] = entry;
        }
        return resolved;
    }

    public async Task DeleteEntriesAsync(Window owner, VaultEntrySelection selection, IReadOnlyList<VaultEntryInfo> entries, FormView form)
    {
        var unlocked = RequireCurrentSelection(selection);
        string warning = entries.Count == 1 ? Text("DeleteCredentialWarning") :
            $"{string.Format(Text("DeleteCredentialsWarning"), entries.Count)}\n\n" +
            string.Join("\n", entries.Select(entry => PermissionDisplay.Literal(entry.Name)));
        if (await ConfirmImpactAsync(owner, unlocked, entries, warning, selection))
        {
            RequireCurrentSelection(selection);
            using (form.BeginProgress("SavingChanges"))
                await unlocked.DeleteEntriesAsync(entries.Select(entry => entry.Name), selection.Revision);
            RefreshManagement();
        }
    }

    public async Task EditEntryAsync(Window owner, VaultEntrySelection selection, VaultEntryInfo entry, FormView management)
    {
        var unlocked = RequireCurrentSelection(selection);
        long revision = selection.Revision;
        var form = new MainWindow.FormWindow(owner, Text("EditCredential"), WindowIcons.Vault);
        form.AddStatusBar();
        Action? showRenameResult = null;
        VaultEntrySelectionChangedException? stale = null;
        var name = new TextBox { Text = entry.Name, MaxLength = 512 };
        var replace = new CheckBox { Margin = new Thickness(0, 10, 0, 3) };
        var password = new PasswordBox { MaxLength = 16 * 1024, IsEnabled = false };
        var keyFile = new TextBox { IsReadOnly = true };
        var validName = AddCredentialName(form, name, unlocked, entry.Name);
        if (entry.Kind == "password")
        {
            replace.Checked += (_, _) => password.IsEnabled = true;
            replace.Unchecked += (_, _) => { password.Clear(); password.IsEnabled = false; };
            form.AddField("ReplacePassword", replace, inlineCheckBox: true);
            form.AddField("VaultPassword", password);
        }
        else
        {
            form.AddField("ReplacePrivateKey", keyFile, actionKey: "BrowsePath", action: () =>
            {
                if (SelectPrivateKeyFile(form, "ReplacePrivateKey") is { } path) keyFile.Text = path;
                return Task.CompletedTask;
            });
        }
        form.AddAsyncButtons(async () =>
        {
            try { return await SaveEntryAsync(); }
            catch (VaultEntrySelectionChangedException error) { stale = error; return true; }
        }, acceptKey: "OK", acceptOnEnter: false);
        try { app.Modal(form.ShowDialog); }
        finally { password.Clear(); }
        if (stale is not null) throw stale;
        showRenameResult?.Invoke();

        async Task<bool> SaveEntryAsync()
        {
            RequireCurrentSelection(selection);
            string newName = name.Text;
            if (!validName()) return false;
            bool passwordChanged = replace.IsChecked == true;
            string? replacementPath = keyFile.Text.Length == 0 ? null : keyFile.Text;
            bool secretChanged = passwordChanged || replacementPath is not null;
            ProfileCatalog.CredentialImpact? update = null;
            if (newName != entry.Name)
            {
                long epoch = app.Vault.SelectionEpoch;
                bool selected = app.Vault.IsSelected(this);
                var impact = await FindImpactAsync([entry]);
                if (!form.IsLoaded) return false;
                string warning = string.Format(Text("RenameCredentialWarning"),
                    PermissionDisplay.Literal(entry.Name), PermissionDisplay.Literal(newName));
                if (selected) warning += "\n\n" + Text("RenameCredentialProfileScope");
                if (secretChanged) warning += "\n\n" + Text("UpdateCredentialWarning");
                MessageBoxResult choice = ConfirmRename(form, selected ? ImpactMessage(warning, impact) : warning, selected);
                if (choice == MessageBoxResult.Cancel) return false;
                RequireCurrentSelection(selection);
                if (selected != app.Vault.IsSelected(this) || selected && !IsCurrentSelection(epoch))
                    throw new InvalidOperationException(Text("ManagementTabOutdated"));
                if (choice == MessageBoxResult.Yes) update = impact;
            }
            else if (secretChanged && !await ConfirmImpactAsync(form, unlocked, [entry], Text("UpdateCredentialWarning"), selection))
                return false;
            RequireCurrentSelection(selection);
            if (replacementPath is not null)
            {
                if (!await ImportKeyFileAsync(form, replacementPath, "ReplacePrivateKey", async (source, passphrase) =>
                {
                    RequireCurrentSelection(selection);
                    using var progress = form.BeginProgress("SavingChanges");
                    await unlocked.ReplacePrivateKeySourceAsync(entry.Name, newName, source, passphrase, revision);
                })) return false;
            }
            else
            {
                char[] characters = passwordChanged ? ReadPassword(password) : [];
                byte[]? bytes = null;
                try
                {
                    if (passwordChanged) bytes = StrictUtf8.GetBytes(characters);
                    Array.Clear(characters);
                    using (form.BeginProgress("SavingChanges"))
                        await unlocked.UpdateEntryAsync(entry.Name, newName,
                            bytes is null ? null : new ReadOnlyMemory<byte>(bytes), revision);
                }
                finally
                {
                    Array.Clear(characters);
                    if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                    password.Clear();
                }
            }
            RefreshManagement();
            if (update is not null)
            {
                var outcome = await app.Profiles.UpdateCredentialReferencesAsync(update, entry.Kind, newName);
                if (form.IsLoaded)
                {
                    string report = string.Format(Text("CredentialRenameUpdatedProfiles"), outcome.Updated);
                    if (outcome.Problems.Count != 0)
                        report += "\n\n" + Text("CredentialRenameProfileProblems") + "\n" + string.Join("\n\n", outcome.Problems);
                    if (update.Unavailable != 0)
                        report += "\n\n" + string.Format(Text("ReferenceScanUnavailable"), update.Unavailable);
                    showRenameResult = update.Unavailable != 0 || outcome.Problems.Count != 0
                        ? () => management.Reports.Report(new object(), report)
                        : () => management.SetStatusMessage(report);
                }
            }
            return true;
        }
    }

    private MessageBoxResult ConfirmRename(Window owner, string message, bool selected)
    {
        var choice = MessageBoxResult.Cancel;
        Window dialog = ThemedMessageBox.Create(owner, message, Text("EditCredential"),
            selected ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel,
            result => choice = result == MessageBoxResult.OK ? MessageBoxResult.No : result,
            label: result => Text(result switch
            {
                MessageBoxResult.Yes => "RenameAndUpdateProfiles",
                MessageBoxResult.No or MessageBoxResult.OK => "RenameCredentialOnly",
                _ => "Cancel",
            }), windowIcon: WindowIcons.Vault);
        dialog.Width = 620;
        app.Modal(dialog.ShowDialog);
        return choice;
    }

    private static Func<bool> AddCredentialName(MainWindow.FormWindow form, TextBox name, CredentialVault unlocked,
        string? originalName = null)
    {
        form.AddField("CredentialName", name);
        var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        form.AddDisplay("CredentialName", error);
        name.TextChanged += (_, _) => ShowError("");
        return () =>
        {
            string message = !VaultPayload.IsValidName(name.Text) ? Text("InvalidCredentialName") :
                name.Text != originalName && unlocked.ReadCommittedMetadata().Entries.Any(entry => entry.Name == name.Text)
                    ? Text("DuplicateCredentialName") : "";
            ShowError(message);
            return message.Length == 0;
        };

        void ShowError(string message)
        {
            error.Text = message;
            error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(name, message);
        }
    }

    private Task<ProfileCatalog.CredentialImpact> FindImpactAsync(IReadOnlyList<VaultEntryInfo> entries) => app.Vault.IsSelected(this)
        ? app.Profiles.FindCredentialReferencesAsync(entries)
        : Task.FromResult(ProfileCatalog.CredentialImpact.Empty);

    private static string ImpactMessage(string warning, ProfileCatalog.CredentialImpact impact)
    {
        string message = $"{warning}\n\n{Text("KnownCredentialReferences")}\n" +
            (impact.Profiles.Count == 0 ? Text("NoKnownCredentialReferences") :
                string.Join("\n", impact.Paths.Select(path => PermissionDisplay.Literal(path)))) +
            "\n\n" + Text("ReferenceScanScope");
        if (impact.Unavailable != 0)
            message += "\n" + string.Format(Text("ReferenceScanUnavailable"), impact.Unavailable);
        return message;
    }

    private async Task<bool> ConfirmImpactAsync(Window owner, CredentialVault unlocked, IReadOnlyList<VaultEntryInfo> entries,
        string warning, VaultEntrySelection? selection = null)
    {
        long revision = unlocked.Revision;
        long selectionEpoch = app.Vault.SelectionEpoch;
        var impact = await FindImpactAsync(entries);
        if (!owner.IsLoaded) return false;
        string message = ImpactMessage(warning, impact);
        if (app.Modal(() => ThemedMessageBox.ShowVault(owner, message, Text("Vault"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) return false;
        var current = await FindImpactAsync(entries);
        if (selection is not null) RequireCurrentSelection(selection);
        if (!ReferenceEquals(vault, unlocked) || unlocked.Revision != revision ||
            app.Vault.SelectionEpoch != selectionEpoch ||
            current.Unavailable != impact.Unavailable || !current.Paths.SequenceEqual(impact.Paths, StringComparer.Ordinal))
            throw new InvalidOperationException(Text("ManagementTabOutdated"));
        return owner.IsLoaded;
    }

    private async Task RestoreFileAsync(Window owner, FormView? form)
    {
        var report = CaptureReport(owner, form, "RestoreVault");
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
        char[]? password = await PromptMasterAsync(owner, "RestoreVault", confirm: false, path: basis.Backup);
        if (password is null) return;
        try
        {
            using var progress = form?.BeginProgress("FileOperationPending");
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
        var retained = VerifiedFilePublication.GetRetainedStagePaths(FilePath);
        if (form is null && owner is MainWindow)
        {
            string result = string.Format(Text("VaultRestoreCompleted"), basis.Backup, target);
            if (retained.Length > 0)
                result += "\n\n" + Text("PublicationRetainedStages") + "\n\n" + string.Join("\n", retained);
            report(new object(), result);
        }
        else ShowRetainedFilePaths(report, retained);
    }

    private static bool ShowRetainedFilePaths(Action<object, string> report, string[] paths)
    {
        if (paths.Length == 0) return false;
        string retained = string.Join("\n", paths);
        report(("retained-stages", retained), Text("PublicationRetainedStages") + "\n\n" + retained);
        return true;
    }

    private string? SelectRestoreBackup(Window owner)
    {
        var dialog = new OpenFileDialog { Filter = Filter, Multiselect = false };
        return app.Modal(() => dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    public async Task BackupAsync(Window owner, FormView? form = null)
    {
        CredentialVault? unlocked = vault ?? await UnlockFileAsync(owner);
        if (unlocked is null || !owner.IsLoaded) return;
        bool rescue = unlocked.IsStale;
        if (rescue && app.Modal(() => ThemedMessageBox.ShowVault(owner, Text("VaultRescueCopyExplanation"),
                Text("BackupVault"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel)) != MessageBoxResult.OK) return;
        var dialog = new SaveFileDialog { Filter = Filter, DefaultExt = ".tttykey", OverwritePrompt = false };
        if (app.Modal(() => dialog.ShowDialog(owner)) != true) return;
        using var progress = form?.BeginProgress("FileOperationPending");
        if (rescue) await unlocked.BackupRetainedGenerationAsync(dialog.FileName);
        else await unlocked.BackupAsync(dialog.FileName);
    }

    public async Task ChangeMasterAsync(Window owner, FormView? form = null)
    {
        if (await UnlockFileAsync(owner) is not { } unlocked || !owner.IsLoaded) return;
        char[]? password = await PromptMasterAsync(owner, "ChangeMaster", confirm: true);
        if (password is null) return;
        // RunAsync keeps the file and application alive even after this host closes.
        masterChangeHost = owner;
        try
        {
            using var progress = form?.BeginProgress("SavingChanges");
            await unlocked.ChangeMasterPasswordAsync(password, app.IssueMasterPassword);
        }
        finally { Array.Clear(password); }
        RefreshManagement();
    }

    private async Task<char[]?> PromptMasterAsync(Window owner, string title, bool confirm, CancellationToken token = default,
        string? path = null, VaultUnlockInteraction.UnlockHosts? hosts = null, string? explanation = null)
    {
        var form = new MainWindow.FormWindow(hosts is null ? owner : null, Text(title), WindowIcons.Vault);
        Window? host = owner;
        var password = new PasswordBox { MaxLength = VaultContainer.MaximumMasterInputLength };
        var repeated = new PasswordBox { MaxLength = VaultContainer.MaximumMasterInputLength };
        if (path is not null)
        {
            bool unlocking = title == "UnlockVault";
            string pathLabel = unlocking ? "VaultUnlockPathDisplay" : "VaultPathDisplay";
            var pathDisplay = new TextBlock
            {
                Text = string.Format(Text(pathLabel), path),
                TextWrapping = TextWrapping.Wrap,
                Margin = unlocking ? new Thickness(0, 0, 0, 16) : new Thickness(5, 6, 5, 6),
            };
            form.AddDisplay("VaultPath", pathDisplay);
            AutomationProperties.SetName(pathDisplay, pathDisplay.Text);
        }
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
            if (form.ShowActivated) password.Focus();
        };
        char[]? result = null;
        form.AddButtons(() =>
        {
            if (hosts is not null && (token.IsCancellationRequested ||
                host?.IsVisible != true || !ReferenceEquals(CurrentHost(), host)))
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
        TabDecision? decision = null;
        if (hosts is not null) decision = new(form, shared: true, hostMoved: HostChanged);
        try
        {
            if (hosts is not null) hosts.Changed += HostChanged;
            if (decision is not null)
            {
                var tabs = hosts!.Tabs;
                bool modal = tabs.Length == 0;
                decision.SetTargets(tabs);
                await decision.ShowAsync(token, modal);
                if (!(modal ? form.DialogResult == true : form.Accepted)) return null;
            }
            else if (!await TabDecision.ShowFormAsync(app, form, token)) return null;
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
                if (decision is not null) decision.SetTargets(hosts!.Tabs);
                Window? next = CurrentHost();
                if (next is null || token.IsCancellationRequested)
                {
                    form.Close();
                    return;
                }
                if (ReferenceEquals(next, host)) return;
                host = next;
                PositionAtHost();
            });
        }

        Window? CurrentHost() => confirm ? hosts?.InitializationHost : hosts?.Current;

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
