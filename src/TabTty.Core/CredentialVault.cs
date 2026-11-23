using System.Security.AccessControl;
using System.Security.Cryptography;

namespace TabTty.Core;

internal enum VaultInitializationStep { ParkOriginal, InstallCandidate, RestoreOriginal }

public sealed class CredentialVault : IDisposable
{
    private static readonly SemaphoreSlim DerivationWorker = new(1);
    private static readonly ResourceQuota OperationCapacity = new(256, message: "Too many vault operations are pending.",
        collectOnPressure: false);
    private static readonly ResourceQuota DerivationCapacity = new(32, message: "Too many vault derivations are pending.",
        collectOnPressure: false);
    private static readonly ResourceQuota SecretInputs = new(8 * 1024 * 1024,
        message: "The vault input budget was exhausted.", collectOnPressure: false);
    private readonly object gate = new();
    private readonly string path;
    private byte[] root;
    private byte[] encrypted;
    private byte[] digest;
    internal sealed record ManagementSnapshot(long Revision, IReadOnlyList<VaultEntryInfo> Entries);
    private ManagementSnapshot metadata;
    private bool disposed;
    private bool stale;
    private sealed record PendingPublication(byte[] Image, byte[] Digest, ManagementSnapshot Metadata, byte[]? Root,
        ResourceReservation Result)
    {
        internal Guid Operation { get; set; }
    }
    private PendingPublication? unresolvedPublication;

    private CredentialVault(string path, Guid id, byte[] root, byte[] encrypted, IReadOnlyList<VaultEntryInfo> entries)
    {
        this.path = path;
        this.root = root;
        this.encrypted = encrypted;
        metadata = new(0, entries);
        digest = SHA256.HashData(encrypted);
        Id = id;
    }

    public Guid Id { get; }
    internal long Revision => ReadCommittedMetadata().Revision;
    internal ManagementSnapshot ReadCommittedMetadata() => Volatile.Read(ref metadata);
    internal bool IsStale => Volatile.Read(ref stale);
    internal bool HasUnverifiedPublication => Volatile.Read(ref unresolvedPublication) is not null;
    internal FilePublicationResult? InitializationResult { get; private set; }

    public static Task<CredentialVault> CreateAsync(string path, ReadOnlyMemory<char> master,
        CancellationToken cancellationToken = default) => CreateAsync(path, master, emptyOriginal: null, cancellationToken);

    internal static Task<CredentialVault> InitializeEmptyAsync(string path, ReadOnlyMemory<char> master,
        VaultFileEvidence evidence, CancellationToken cancellationToken = default,
        Action<VaultInitializationStep, string, string>? move = null, Action<string>? validateStage = null) =>
        evidence.Length == 0 ? CreateAsync(path, master, evidence, cancellationToken, move, validateStage) :
            throw new IOException("The credential vault is no longer an empty file.");

    private static Task<CredentialVault> CreateAsync(string path, ReadOnlyMemory<char> master, VaultFileEvidence? emptyOriginal,
        CancellationToken cancellationToken, Action<VaultInitializationStep, string, string>? move = null,
        Action<string>? validateStage = null) =>
        WithMasterAsync(master, (password, token) =>
        {
            using var result = VaultPublicationCapacity.Results.Reserve(1);
            string destination = Path.GetFullPath(path);
            byte[] root = new byte[32];
            try
            {
                Guid id = OpenSsl.NewId();
                OpenSsl.Fill(root);
                byte[] wrapper = VaultContainer.Wrap(id, password, root);
                byte[] encrypted = VaultContainer.Encrypt(wrapper, root, VaultPayload.Empty(id));
                var generation = new CredentialVault(destination, id, root, encrypted, []);
                generation.InitializationResult = CommitNew(destination, encrypted, token, emptyOriginal,
                    move: move, validateStage: validateStage);
                return generation;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(root);
                throw;
            }
        }, cancellationToken);

    public static Task<CredentialVault> UnlockAsync(string path, ReadOnlyMemory<char> master,
        CancellationToken cancellationToken = default) =>
        WithMasterAsync(master, (password, token) => Unlock(Path.GetFullPath(path), password, token), cancellationToken);

    internal static Task<CredentialVault> UnlockBoundAsync(string path, ReadOnlyMemory<char> master,
        VaultFileEvidence evidence, CancellationToken token) =>
        WithMasterAsync(master, (password, lifetime) => Unlock(Path.GetFullPath(path), password, lifetime, evidence), token);

    internal static Task<CredentialVault?> ReconcileLockedPublicationAsync(string path, ReadOnlyMemory<char> master,
        CancellationToken token = default) => WithMasterAsync(master, (password, lifetime) =>
    {
        CredentialVault? adopted = null;
        using var writer = LockWriter(path);
        var result = VerifiedFilePublication.Reconcile(path, verified =>
            adopted = Unlock(verified, password, lifetime, reinspection: true));
        if (result is { Status: not FilePublicationStatus.Verified })
            throw new FilePublicationException("The credential vault publication or preservation could not be verified.", result);
        return adopted;
    }, token);

    private static CredentialVault Unlock(string path, byte[] password, CancellationToken token,
        VaultFileEvidence? expected = null, bool reinspection = false)
    {
        if (!reinspection && VerifiedFilePublication.HasPendingPublication(path))
            throw new IOException("The credential vault has an unresolved publication.");
        byte[] encrypted = ReadFile(path, confirmed: expected);
        Guid id = VaultContainer.Validate(encrypted);
        byte[] root = new byte[32];
        try
        {
            VaultContainer.Unwrap(encrypted, password, root);
            byte[] plaintext = VaultContainer.Decrypt(encrypted, root);
            IReadOnlyList<VaultEntryInfo> entries;
            try { entries = ReadEntries(plaintext, id); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            token.ThrowIfCancellationRequested();
            return new CredentialVault(path, id, root, encrypted, entries);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(root);
            throw;
        }
    }

    internal sealed record RestoreBasis(string Target, string Backup, VaultFileEvidence? Original,
        VaultFileEvidence BackupImage, long? Revision);

    internal static RestoreBasis PrepareRestore(string target, string backup, CredentialVault? owner)
    {
        target = Path.GetFullPath(target);
        backup = Path.GetFullPath(backup);
        if (string.Equals(target, backup, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a separate encrypted backup.", nameof(backup));
        if (owner is not null && !string.Equals(target, owner.path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected vault and reviewed destination differ.");
        VaultFileEvidence? original = owner is null ? InspectFile(target) : owner.InspectFileForManagement();
        VaultFileEvidence source = InspectFile(backup) ?? throw new FileNotFoundException("The encrypted backup is unavailable.", backup);
        if (original is not null && original.Volume == source.Volume && original.Index == source.Index)
            throw new ArgumentException("Choose a separate encrypted backup.", nameof(backup));
        return new(target, backup, original, source, owner?.Revision);
    }

    public static Task<CredentialVault> RecoverAsync(string destination, string backup, ReadOnlyMemory<char> master,
        CancellationToken cancellationToken = default) =>
        RestoreFileAsync(destination, backup, master, null, null, cancellationToken);

    internal static Task<CredentialVault> RestoreFileAsync(RestoreBasis request, ReadOnlyMemory<char> master,
        CredentialVault? owner, CancellationToken token = default, Action<string, string, string?>? issue = null) =>
        RestoreFileAsync(request.Target, request.Backup, master, owner, request, token, issue);

    private static Task<CredentialVault> RestoreFileAsync(string destination, string backup, ReadOnlyMemory<char> master,
        CredentialVault? owner, RestoreBasis? consent, CancellationToken cancellationToken, Action<string, string, string?>? issue = null) =>
        WithMasterAsync(master, (password, token) =>
        {
            var basis = consent ?? PrepareRestore(destination, backup, owner);
            byte[]? original = ReadRecoveryTarget(basis.Target, basis.Original);
            if ((basis.Original is null) != (original is null))
                throw new IOException("The vault changed after the restore review.");
            Guid? expected = owner?.Id ?? (original is null ? null : ReadVaultId(original));
            using CredentialVault verified = Unlock(basis.Backup, password, token, basis.BackupImage);
            if (expected is { } id && verified.Id != id)
                throw new InvalidDataException("The backup belongs to another credential vault.");
            verified.CheckCurrent();
            if (owner is null) return RecoverVerified(basis.Target, original, verified, token, issue);

            lock (owner.gate)
            {
                owner.CheckCurrent();
                owner.RequireRevision(basis.Revision);
                byte[] replacement = verified.root.ToArray();
                try
                {
                    owner.Commit(verified.encrypted, verified.metadata.Entries, token, RecoveryPath(basis.Target), issue,
                        replacement: replacement);
                }
                finally { owner.ClearUnownedRoot(replacement); }
            }
            return owner;
        }, cancellationToken);

    private static string RecoveryPath(string target) => target + $".recovery-{Guid.NewGuid():N}.tttykey";

    private static Guid? ReadVaultId(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes.AsSpan(0, 8).SequenceEqual("TTTYKEY\0"u8) &&
            (bytes[8] != 0 || bytes[9] > 1 || (bytes[9] == 1 && (bytes[10] != 0 || bytes[11] != 0))))
            throw new InvalidDataException("A newer credential vault format cannot be replaced.");
        try { return VaultContainer.Validate(bytes); }
        catch (InvalidDataException) { return null; }
    }

    private static CredentialVault RecoverVerified(string path, byte[]? original, CredentialVault verified,
        CancellationToken token, Action<string, string, string?>? issue = null)
    {
        using var resultSlot = VaultPublicationCapacity.Results.Reserve(1);
        var generation = new CredentialVault(path, verified.Id, verified.root.ToArray(), verified.encrypted, verified.metadata.Entries);
        try
        {
            if (original is null) CommitNew(path, verified.encrypted, token, issue: issue);
            else
            {
                using var writer = LockWriter(path);
                string recovery = RecoveryPath(path);
                FilePublicationResult result = VerifiedFilePublication.Publish(path, verified.encrypted,
                    SHA256.HashData(original), recovery, MaximumVaultBytes, token, candidate => OpenPrivate(candidate),
                    recovery: true, issue: issue);
                if (result.Status != FilePublicationStatus.Verified)
                    throw new FilePublicationException("Credential vault recovery or original preservation could not be verified.", result);
            }
            return generation;
        }
        catch
        {
            generation.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<VaultEntryInfo>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
        (await ReadMetadataAsync(cancellationToken).ConfigureAwait(false)).Entries;

    internal Task<(long Revision, IReadOnlyList<VaultEntryInfo> Entries)> ReadMetadataAsync(CancellationToken token = default) =>
        RunAsync(_ => (metadata.Revision, metadata.Entries), token);

    public Task<VaultSecret> GetPasswordAsync(string name, CancellationToken cancellationToken = default) =>
        GetSecretAsync(name, "password", cancellationToken);

    public Task<VaultSecret> GetPrivateKeyAsync(string name, CancellationToken cancellationToken = default) =>
        GetSecretAsync(name, "privateKey", cancellationToken);

    internal static void MatchCredential(IReadOnlyList<VaultEntryInfo> entries, string name,
        string expected)
    {
        var entry = entries.FirstOrDefault(candidate => candidate.Name == name) ??
            throw new CredentialLookupException(MacroErrorCode.CredentialUnavailable, "The credential entry is missing.");
        if (entry.Kind != expected)
            throw new CredentialLookupException(MacroErrorCode.CredentialKindMismatch, "The credential entry has a different kind.");
    }

    private Task<VaultSecret> GetSecretAsync(string name, string expected,
        CancellationToken cancellationToken)
    {
        return UseAsync((plaintext, token) =>
        {
            token.ThrowIfCancellationRequested();
            var entries = VaultPayload.Read(plaintext, Id, name, out byte[]? secret, out _);
            try
            {
                MatchCredential(entries, name, expected);
                token.ThrowIfCancellationRequested();
                return new VaultSecret(secret!);
            }
            catch
            {
                if (secret is not null) CryptographicOperations.ZeroMemory(secret);
                throw;
            }
        }, cancellationToken);
    }

    public Task<string> AddPasswordAsync(string name, ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken = default) => AddPasswordAsync(name, password, null, cancellationToken);

    internal Task<string> AddPasswordAsync(string name, ReadOnlyMemory<byte> password,
        long? expectedRevision, CancellationToken cancellationToken = default) =>
        WithSecretInputAsync(password, owned => UseAsync((plaintext, token) =>
        {
            RequireRevision(expectedRevision);
            byte[] payload = VaultPayload.AddEntry(plaintext, Id, new VaultEntryInfo(name),
                owned.GetValueOrDefault().Span);
            try { CommitEntries(payload, token); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            return name;
        }, cancellationToken));

    internal sealed class PrivateKeySource(byte[] image, ResourceReservation ownership) : IDisposable
    {
        internal ReadOnlyMemory<byte> Image { get; } = image;
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(image);
            ownership.Dispose();
        }
    }

    internal static PrivateKeySource ReadPrivateKeySource(string source)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("The private key file exceeds its supported size.");
        var ownership = SecretInputs.Reserve(length);
        try
        {
            byte[] image = new byte[(int)length];
            try
            {
                stream.ReadExactly(image);
                if (stream.ReadByte() >= 0) throw new IOException("The private key file changed while being read.");
                return new(image, ownership);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(image);
                throw;
            }
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    public async Task<string> ImportPrivateKeyAsync(string name, string source, CancellationToken cancellationToken = default,
        ReadOnlyMemory<byte>? passphrase = null)
    {
        using var acquired = await Task.Run(() => ReadPrivateKeySource(source), cancellationToken).ConfigureAwait(false);
        return await ImportPrivateKeySourceAsync(name, acquired, passphrase, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal Task<string> ImportPrivateKeySourceAsync(string name, PrivateKeySource source,
        ReadOnlyMemory<byte>? passphrase = null, long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        WithSecretInputAsync(passphrase, owned => ImportOwnedAsync(name, source, owned, expectedRevision, cancellationToken));

    internal Task<string> ReplacePrivateKeySourceAsync(string originalName, string name, PrivateKeySource source,
        ReadOnlyMemory<byte>? passphrase, long expectedRevision, CancellationToken cancellationToken = default) =>
        WithSecretInputAsync(passphrase, owned => ImportOwnedAsync(name, source, owned, expectedRevision, cancellationToken,
            originalName));

    private async Task<string> ImportOwnedAsync(string name, PrivateKeySource source,
        ReadOnlyMemory<byte>? passphrase, long? expectedRevision, CancellationToken cancellationToken,
        string? originalName = null)
    {
        using var request = DerivationCapacity.Reserve(1);
        await DerivationWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UseAsync((plaintext, token) =>
            {
                RequireRevision(expectedRevision);
                using SshPrivateKey key = SshPrivateKey.Import(source.Image.Span, passphrase);
                token.ThrowIfCancellationRequested();
                byte[] pkcs8 = key.ExportPkcs8();
                try
                {
                    var entry = new VaultEntryInfo(name, "privateKey", key.Algorithm, key.Fingerprint);
                    byte[] payload = originalName is null
                        ? VaultPayload.AddEntry(plaintext, Id, entry, pkcs8)
                        : VaultPayload.UpdateEntry(plaintext, Id, originalName, name, pkcs8, replaceSecret: true,
                            replacementKey: entry);
                    try { CommitEntries(payload, token); }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                    return name;
                }
                finally { CryptographicOperations.ZeroMemory(pkcs8); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { DerivationWorker.Release(); }
    }

    public Task BackupAsync(string destination, CancellationToken cancellationToken = default) =>
        BackupCoreAsync(destination, rescue: false, cancellationToken);

    internal Task BackupRetainedGenerationAsync(string destination, CancellationToken cancellationToken = default) =>
        BackupCoreAsync(destination, rescue: true, cancellationToken);

    private Task BackupCoreAsync(string destination, bool rescue, CancellationToken token) => Task.Run(() =>
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!rescue) CheckCurrent();
            string target = Path.GetFullPath(destination);
            if (target.Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a separate encrypted backup destination.");
            CommitNew(target, encrypted, token);
        }
    }, token);

    internal VaultFileEvidence? InspectFileForManagement()
    {
        lock (gate)
        {
            CheckCurrent();
            VaultFileEvidence? result = InspectFile(path);
            if (result?.Digest != Convert.ToHexString(digest))
            {
                stale = true;
                throw new CredentialVaultStaleException("The credential vault changed before review.");
            }
            return result;
        }
    }

    internal static VaultFileEvidence? InspectFile(string path) => VaultFileEvidence.Read(path, MaximumVaultBytes);

    public Task UpdateEntryAsync(string originalName, string name, ReadOnlyMemory<byte>? password = null,
        CancellationToken cancellationToken = default) =>
        UpdateEntryAsync(originalName, name, password, null, cancellationToken);

    internal Task UpdateEntryAsync(string originalName, string name, ReadOnlyMemory<byte>? password,
        long? expectedRevision, CancellationToken cancellationToken = default) =>
        WithSecretInputAsync(password, owned => UseAsync((plaintext, token) =>
        {
            RequireRevision(expectedRevision);
            byte[] updated = VaultPayload.UpdateEntry(plaintext, Id, originalName, name,
                owned.GetValueOrDefault().Span, owned.HasValue);
            try { CommitEntries(updated, token); }
            finally { CryptographicOperations.ZeroMemory(updated); }
            return true;
        }, cancellationToken));

    public Task DeleteEntryAsync(string originalName, CancellationToken cancellationToken = default) =>
        DeleteEntryAsync(originalName, null, cancellationToken);

    internal Task DeleteEntryAsync(string originalName, long? expectedRevision, CancellationToken cancellationToken = default) =>
        DeleteEntriesAsync([originalName], expectedRevision, cancellationToken);

    internal Task DeleteEntriesAsync(IEnumerable<string> names, long? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var selected = names.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return Task.CompletedTask;
        return UseAsync((plaintext, token) =>
        {
            RequireRevision(expectedRevision);
            byte[] updated = VaultPayload.DeleteEntries(plaintext, Id, selected);
            try { CommitEntries(updated, token); }
            finally { CryptographicOperations.ZeroMemory(updated); }
            return true;
        }, cancellationToken);
    }

    private void RequireRevision(long? expected)
    {
        if (expected is { } basis && Revision != basis)
            throw new InvalidOperationException("The reviewed vault revision changed before the operation.");
    }

    public Task RestoreAsync(string backup, ReadOnlyMemory<char> master, CancellationToken cancellationToken = default) =>
        RestoreFileAsync(path, backup, master, this, null, cancellationToken);

    public Task ChangeMasterPasswordAsync(ReadOnlyMemory<char> master, CancellationToken cancellationToken = default) =>
        ChangeMasterPasswordAsync(master, null, cancellationToken);

    internal Task ChangeMasterPasswordAsync(ReadOnlyMemory<char> master, Action<string, string, string?>? issue,
        CancellationToken cancellationToken = default, Action<string>? validateStaged = null, Action<byte[]>? fillRoot = null) =>
        WithMasterAsync(master, (password, token) =>
        {
            lock (gate)
            {
                CheckCurrent();
                byte[] plaintext = VaultContainer.Decrypt(encrypted, root);
                byte[] replacement = new byte[32];
                try
                {
                    if (fillRoot is null) OpenSsl.Fill(replacement);
                    else fillRoot(replacement);
                    token.ThrowIfCancellationRequested();
                    byte[] wrapper = VaultContainer.Wrap(Id, password, replacement);
                    Commit(VaultContainer.Encrypt(wrapper, replacement, plaintext), metadata.Entries, token,
                        issue: issue, replacement: replacement, validateStaged: validateStaged);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    ClearUnownedRoot(replacement);
                }
                return true;
            }
        }, cancellationToken);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            CryptographicOperations.ZeroMemory(root);
            if (unresolvedPublication?.Root is { } replacement) CryptographicOperations.ZeroMemory(replacement);
            unresolvedPublication?.Result.Dispose();
            Volatile.Write(ref unresolvedPublication, null);
            encrypted = [];
            digest = [];
            Volatile.Write(ref metadata, new(metadata.Revision, []));
        }
    }

    private Task<T> UseAsync<T>(Func<byte[], CancellationToken, T> action, CancellationToken token) =>
        RunAsync(cancellation =>
        {
            byte[] plaintext = VaultContainer.Decrypt(encrypted, root);
            try { return action(plaintext, cancellation); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }, token);

    private static async Task<T> WithSecretInputAsync<T>(ReadOnlyMemory<byte>? input,
        Func<ReadOnlyMemory<byte>?, Task<T>> operation)
    {
        if (input is not { } supplied) return await operation(null).ConfigureAwait(false);
        using var memory = SecretInputs.Reserve(supplied.Length);
        byte[] owned = supplied.ToArray();
        try { return await operation(owned).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var admission = OperationCapacity.Reserve(1);
        return await Task.Run(() =>
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                CheckCurrent();
                return action(token);
            }
        }, token).ConfigureAwait(false);
    }

    private static async Task<T> WithMasterAsync<T>(ReadOnlyMemory<char> master,
        Func<byte[], CancellationToken, T> operation, CancellationToken token)
    {
        if (master.Length > VaultContainer.MaximumMasterInputLength)
            throw new ArgumentOutOfRangeException(nameof(master), VaultContainer.MasterLengthMessage);
        token.ThrowIfCancellationRequested();
        using var admission = DerivationCapacity.Reserve(1);
        using var memory = SecretInputs.Reserve(checked(master.Length * 16L + 128));
        char[] input = master.ToArray();
        try
        {
            await DerivationWorker.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    byte[] password = VaultContainer.NormalizeMaster(input);
                    Array.Clear(input);
                    try { return operation(password, token); }
                    finally { CryptographicOperations.ZeroMemory(password); }
                }, token).ConfigureAwait(false);
            }
            finally { DerivationWorker.Release(); }
        }
        finally { Array.Clear(input); }
    }

    private void CheckCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (unresolvedPublication is not null || VerifiedFilePublication.IsUnresolved(path))
            throw new IOException("The credential vault publication must be verified before new use.");
        if (stale) throw new CredentialVaultStaleException("The credential vault changed. Restart TabTty to open it again.");
        try
        {
            if (SHA256.HashData(ReadFile(path)).AsSpan().SequenceEqual(digest)) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // An unavailable target cannot be treated as the last authenticated generation.
        }
        stale = true;
        throw new CredentialVaultStaleException("The credential vault changed or became unavailable. Restart TabTty to open it again.");
    }

    private const int MaximumVaultBytes = VaultContainer.HeaderSize + VaultContainer.TagSize + VaultContainer.MaxPayload;

    internal async Task<bool> ReconcilePublicationAsync() =>
        (await ReconcilePublicationResultAsync().ConfigureAwait(false))?.Status == FilePublicationStatus.Verified;

    internal Task<FilePublicationResult?> ReconcilePublicationResultAsync() => Task.Run(() =>
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (unresolvedPublication is not { } pending) return null;
            using var writer = LockWriter(path);
            var result = VerifiedFilePublication.Reconcile(path, verifiedPath =>
            {
                if (stale) throw new CredentialVaultStaleException("The credential vault changed independently.");
                byte[] plaintext = VaultContainer.Decrypt(pending.Image, pending.Root ?? root);
                try { VaultPayload.Read(plaintext, Id, null, out _, out _); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }

                Adopt(pending);
                Volatile.Write(ref unresolvedPublication, null);
                pending.Result.Dispose();
            }, pending.Operation);
            if (result is { Status: FilePublicationStatus.Unchanged, EvidenceRetired: true })
            {
                Volatile.Write(ref unresolvedPublication, null);
                if (pending.Root is { } discarded) ClearUnownedRoot(discarded);
                pending.Result.Dispose();
            }
            return result;
        }
    });

    private static IReadOnlyList<VaultEntryInfo> ReadEntries(byte[] plaintext, Guid id)
    {
        const long entryCharge = 256;
        var charge = VaultPublicationCapacity.Metadata.Reserve(256 + 2L * plaintext.Length +
            entryCharge * VaultPayload.MaxEntries);
        try
        {
            var parsed = VaultPayload.Read(plaintext, id, null, out _, out _);
            long bytes = 256 + parsed.Sum(entry => entryCharge + 2L * (entry.Name.Length +
                entry.Kind.Length + (entry.PublicType?.Length ?? 0) +
                (entry.Fingerprint?.Length ?? 0)));
            charge.Resize(bytes);
            return new VaultMetadata(parsed, charge);
        }
        catch
        {
            charge.Dispose();
            throw;
        }
    }

    private void CommitEntries(byte[] plaintext, CancellationToken token) =>
        Commit(VaultContainer.Encrypt(encrypted, root, plaintext), ReadEntries(plaintext, Id), token);

    private void ClearUnownedRoot(byte[] replacement)
    {
        if (!ReferenceEquals(replacement, root) && !ReferenceEquals(replacement, unresolvedPublication?.Root))
            CryptographicOperations.ZeroMemory(replacement);
    }

    private void Adopt(PendingPublication candidate)
    {
        if (candidate.Root is { } replacement)
        {
            CryptographicOperations.ZeroMemory(root);
            root = replacement;
        }
        encrypted = candidate.Image;
        digest = candidate.Digest;
        Volatile.Write(ref metadata, candidate.Metadata);
    }

    private void Commit(byte[] candidate, IReadOnlyList<VaultEntryInfo> candidateEntries, CancellationToken token,
        string? recovery = null, Action<string, string, string?>? issue = null, byte[]? replacement = null,
        Action<string>? validateStaged = null)
    {
        var proposal = new ManagementSnapshot(checked(Revision + 1), candidateEntries);
        var candidateDigest = SHA256.HashData(candidate);
        var resultSlot = VaultPublicationCapacity.Results.Reserve(1);
        try
        {
            var prepared = new PendingPublication(candidate, candidateDigest, proposal, replacement, resultSlot);
            using var writer = LockWriter(path);
            CheckCurrent();
            FilePublicationResult result;
            try
            {
                result = VerifiedFilePublication.Publish(path, candidate, digest, recovery ?? path + ".bak",
                    MaximumVaultBytes, token, candidatePath => OpenPrivate(candidatePath), validate: validateStaged,
                    recovery: recovery is not null, issue: issue);
            }
            catch (FilePublicationException error)
            {
                Retain(error.Result);
                throw;
            }
            if (result.Status == FilePublicationStatus.Verified)
            {
                Adopt(prepared);
                return;
            }
            if (VerifiedFilePublication.IsUnresolved(path)) Retain(result);
            else if (result.Status != FilePublicationStatus.Unchanged) stale = true;
            throw new FilePublicationException(result.Status == FilePublicationStatus.Unchanged
                ? "Credential vault replacement did not change the destination."
                : "Credential vault replacement or original preservation could not be verified.", result);

            void Retain(FilePublicationResult outcome)
            {
                prepared.Operation = outcome.OperationId;
                Volatile.Write(ref unresolvedPublication, prepared);
            }
        }
        finally
        {
            if (!ReferenceEquals(unresolvedPublication?.Result, resultSlot)) resultSlot.Dispose();
        }
    }

    private static FilePublicationResult CommitNew(string path, byte[] candidate, CancellationToken token,
        VaultFileEvidence? emptyOriginal = null, Action<string, string, string?>? issue = null,
        Action<VaultInitializationStep, string, string>? move = null, Action<string>? validateStage = null)
    {
        if (emptyOriginal is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = LockWriter(path);
            FilePublicationResult result = VerifiedFilePublication.Publish(path, candidate, null, null,
                MaximumVaultBytes, token, staged => OpenPrivate(staged), issue: issue);
            if (result.Status != FilePublicationStatus.Verified)
                throw new FilePublicationException("The new credential vault could not be verified.", result);
            return result;
        }
        string directory = Path.GetDirectoryName(path)!;
        Guid operationId = Guid.NewGuid();
        string temporary = Path.Combine(directory, $".tabtty-{operationId:N}.tmp");
        string aside = Path.Combine(directory, $".tabtty-empty-{operationId:N}.tmp");
        byte[] candidateDigest = SHA256.HashData(candidate);
        using var emptyWriter = LockWriter(path);
        using var reservation = VerifiedFilePublication.ReserveNamespace([path, temporary, aside], candidate.LongLength);
        string targetName = FileObjectIdentity.NamespaceKeys(path)[0];
        var original = new FileObjectIdentity(emptyOriginal.Volume, emptyOriginal.Index);
        byte[] originalDigest = Convert.FromHexString(emptyOriginal.Digest);
        FileObjectIdentity? prepared = null;
        bool parkingIssued = false;
        Exception? mutationError = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (InspectFile(path) != emptyOriginal)
                throw new IOException("The credential vault is no longer the confirmed empty file.");
            using (FileStream file = OpenPrivate(temporary))
            {
                prepared = FileObjectIdentity.Read(file.SafeFileHandle);
                VerifiedFilePublication.RegisterPreparedFile(reservation, prepared.Value);
                file.Write(candidate);
                file.Flush(flushToDisk: true);
            }
            validateStage?.Invoke(temporary);
            var expectedStage = new VaultFileEvidence(candidate.LongLength, prepared.Value.Volume,
                prepared.Value.Index, Convert.ToHexString(candidateDigest));
            if (InspectFile(temporary) != expectedStage)
                throw new IOException("The prepared initialization file changed before publication.");
            token.ThrowIfCancellationRequested();
            if (InspectFile(path) != emptyOriginal)
                throw new IOException("The credential vault is no longer the confirmed empty file.");
            parkingIssued = true;
            Move(VaultInitializationStep.ParkOriginal, path, aside);
            if (InspectFile(aside) != emptyOriginal)
                throw new IOException("The credential vault changed during initialization.");
            token.ThrowIfCancellationRequested();
            Move(VaultInitializationStep.InstallCandidate, temporary, path);
        }
        catch (Exception error) when (IsInitializationFailure(error))
        {
            mutationError = error;
            if (!parkingIssued) throw;
        }
        finally
        {
            if (!parkingIssued && prepared is { } file)
            {
                try { FileObjectIdentity.DeleteIfSame(temporary, file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new FilePublicationException("The unissued initialization candidate could not be removed.",
                        InspectOutcome(retainUnresolved: true, cleanupError: error));
                }
            }
        }

        var observed = InspectOutcome();
        if (observed.Status == FilePublicationStatus.Verified)
        {
            try { FileObjectIdentity.DeleteIfSame(aside, original, expectedLength: 0); }
            catch (Exception error) when (IsInitializationFailure(error))
            {
                VerifiedFilePublication.RetainParkedEmptyFile(reservation, operationId, path, aside, original);
                return observed with { CleanupError = error };
            }
            return observed;
        }

        // A synchronous move has returned, and the original can be put back
        // only while the destination is proven absent.
        if (observed.Target == FileImageStatus.Missing && observed.Preservation == FileImageStatus.Original)
        {
            try { Move(VaultInitializationStep.RestoreOriginal, aside, path); }
            catch (Exception error) when (IsInitializationFailure(error)) { mutationError = error; }
            observed = InspectOutcome();
        }
        if (observed.Target == FileImageStatus.Original && observed.Preservation == FileImageStatus.Missing &&
            observed.Candidate is FileImageStatus.Candidate or FileImageStatus.Missing)
        {
            try { FileObjectIdentity.DeleteIfSame(temporary, prepared!.Value); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new FilePublicationException("The original empty vault was preserved and cleanup failed.",
                    InspectOutcome(retainUnresolved: true, cleanupError: error));
            }
            observed = InspectOutcome();
            if (observed.Target == FileImageStatus.Original && observed.Preservation == FileImageStatus.Missing &&
                observed.Candidate == FileImageStatus.Missing)
            {
                if (mutationError is OperationCanceledException stopped) throw stopped;
                throw new FilePublicationException("The original empty vault was preserved.",
                    observed with { Status = FilePublicationStatus.Unchanged });
            }
        }
        throw new FilePublicationException("The original empty vault requires further inspection.",
            InspectOutcome(retainUnresolved: true));

        FilePublicationResult InspectOutcome(bool retainUnresolved = false, Exception? cleanupError = null) =>
            VerifiedFilePublication.InspectGuardedPublication(reservation, operationId, path, aside,
                temporary, candidateDigest, originalDigest, targetName, prepared!.Value, original, MaximumVaultBytes,
                mutationError, retainUnresolved, beforeIssue: !parkingIssued, cleanupError: cleanupError);

        void Move(VaultInitializationStep step, string source, string target)
        {
            if (move is null) File.Move(source, target, overwrite: false);
            else move(step, source, target);
        }
    }

    private static bool IsInitializationFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException;

    internal static string? ArchiveFile(string path)
    {
        var evidence = InspectFile(path);
        return evidence is null ? null : ArchiveFile(path, evidence);
    }

    internal static string ArchiveFile(string path, VaultFileEvidence evidence, Action<string, string>? move = null)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        string name = Path.GetFileNameWithoutExtension(full);
        if (name.Length > 180) name = "vault";
        Guid operationId = Guid.NewGuid();
        string archived = Path.Combine(directory, $"{name}.archived-{operationId:N}.tttykey");
        using var writer = LockWriter(full);
        using var reservation = VerifiedFilePublication.ReserveNamespace([full, archived], evidence.Length);
        if (InspectFile(full) != evidence)
            throw new IOException("The credential vault changed after confirmation.");

        Exception? error = null;
        try
        {
            if (move is null) File.Move(full, archived, overwrite: false);
            else move(full, archived);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { error = problem; }
        var archive = new VerifiedFilePublication.FileArchiveEvidence(operationId, full, archived, evidence);
        var (source, destination) = InspectArchive(archive);
        if (source == FileImageStatus.Missing && destination == FileImageStatus.Original) return archived;
        if (source != FileImageStatus.Original || destination != FileImageStatus.Missing)
            VerifiedFilePublication.RetainUnresolvedArchive(reservation, archive);
        throw ArchiveFailure(archive, source, destination, error);
    }

    internal static string? ReconcileArchive(string path)
    {
        var pending = VerifiedFilePublication.GetUnresolvedArchive(path);
        if (pending is null) return null;
        using var writer = LockWriter(pending.Source);
        using var reservation = VerifiedFilePublication.ReserveArchiveReinspection(pending);
        if (VerifiedFilePublication.GetUnresolvedArchive(path) != pending)
            throw new IOException("The vault archive awaiting inspection has changed.");
        var (source, destination) = InspectArchive(pending);
        if (source != FileImageStatus.Missing || destination != FileImageStatus.Original)
            throw ArchiveFailure(pending, source, destination, error: null);
        VerifiedFilePublication.RetireArchive(pending);
        return pending.Destination;
    }

    private static (FileImageStatus Source, FileImageStatus Destination) InspectArchive(VerifiedFilePublication.FileArchiveEvidence pending) =>
        (Inspect(pending.Source, pending.Image), Inspect(pending.Destination, pending.Image));

    private static FileImageStatus Inspect(string path, VaultFileEvidence expected)
    {
        try
        {
            var current = InspectFile(path);
            if (current is null) return FileImageStatus.Missing;
            return current == expected ? FileImageStatus.Original : FileImageStatus.Other;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FileImageStatus.Unavailable;
        }
    }

    private static IOException ArchiveFailure(VerifiedFilePublication.FileArchiveEvidence pending, FileImageStatus source,
        FileImageStatus destination, Exception? error) =>
        new($"The vault archive could not be verified. Source {source}: {pending.Source}. " +
            $"Destination {destination}: {pending.Destination}.", error);

    private static FileStream LockWriter(string path)
    {
        // The lock survives replacement of the vault and is removed by the OS when closed.
        string name = Path.GetFileName(path).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
        return OpenPrivate(Path.Combine(Path.GetDirectoryName(path)!, $".tabtty-{key}.lock"), FileOptions.DeleteOnClose);
    }

    private static FileStream OpenPrivate(string path, FileOptions options = FileOptions.None)
    {
        if (OperatingSystem.IsWindows())
            return PrivateWindowsFile.Create(path, FileSystemRights.FullControl, FileShare.None, 4096, options);

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = options,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    private static byte[]? ReadRecoveryTarget(string path, VaultFileEvidence? confirmed)
    {
        try { return ReadFile(path, allowTruncated: true, confirmed: confirmed); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static byte[] ReadFile(string path, bool allowTruncated = false, VaultFileEvidence? confirmed = null)
    {
        if (confirmed is not null &&
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The credential vault changed during the unlock review.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = file.Length;
        if ((!allowTruncated && length < VaultContainer.HeaderSize + VaultContainer.TagSize) ||
            length > VaultContainer.HeaderSize + VaultContainer.TagSize + VaultContainer.MaxPayload)
            throw new InvalidDataException("Invalid credential vault file size.");
        byte[] bytes = new byte[(int)length];
        file.ReadExactly(bytes);
        if (file.ReadByte() >= 0) throw new InvalidDataException("Invalid credential vault file size.");
        if (confirmed is not null)
        {
            var identity = FileObjectIdentity.Read(file.SafeFileHandle);
            if (confirmed != new VaultFileEvidence(length, identity.Volume, identity.Index,
                Convert.ToHexString(SHA256.HashData(bytes))))
                throw new IOException("The credential vault changed during the unlock review.");
        }
        return bytes;
    }

}
