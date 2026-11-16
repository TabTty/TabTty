using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace TabTty.Core;

public sealed class CredentialVault : IDisposable
{
    private static readonly SemaphoreSlim DerivationWorker = new(1);
    private readonly object gate = new();
    private readonly string path;
    private byte[] root;
    private byte[] encrypted;
    private byte[] digest;
    private bool disposed;
    private bool stale;
    private byte[]? unresolvedRoot;

    private CredentialVault(string path, Guid id, byte[] root, byte[] encrypted)
    {
        this.path = path;
        this.root = root;
        this.encrypted = encrypted;
        digest = SHA256.HashData(encrypted);
        Id = id;
    }

    public Guid Id { get; }
    internal bool IsStale => Volatile.Read(ref stale);

    public static Task<CredentialVault> CreateAsync(string path, ReadOnlyMemory<char> master,
        CancellationToken cancellationToken = default) => CreateAsync(path, master, replaceEmpty: false, cancellationToken);

    internal static Task<CredentialVault> InitializeEmptyAsync(string path, ReadOnlyMemory<char> master,
        CancellationToken cancellationToken = default) => CreateAsync(path, master, replaceEmpty: true, cancellationToken);

    private static Task<CredentialVault> CreateAsync(string path, ReadOnlyMemory<char> master, bool replaceEmpty,
        CancellationToken cancellationToken) =>
        WithMasterAsync(master, (password, token) =>
        {
            string destination = Path.GetFullPath(path);
            byte[] root = new byte[32];
            try
            {
                Guid id = OpenSsl.NewId();
                OpenSsl.Fill(root);
                byte[] wrapper = VaultContainer.Wrap(id, password, root);
                byte[] encrypted = VaultContainer.Encrypt(wrapper, root, VaultPayload.Empty(id));
                CommitNew(destination, encrypted, token, replaceEmpty);
                return new CredentialVault(destination, id, root, encrypted);
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

    private static CredentialVault Unlock(string path, byte[] password, CancellationToken token)
    {
        byte[] encrypted = ReadFile(path);
        Guid id = VaultContainer.Validate(encrypted);
        byte[] root = new byte[32];
        try
        {
            VaultContainer.Unwrap(encrypted, password, root);
            byte[] plaintext = VaultContainer.Decrypt(encrypted, root);
            try { VaultPayload.Read(plaintext, id, null, out _, out _); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            token.ThrowIfCancellationRequested();
            return new CredentialVault(path, id, root, encrypted);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(root);
            throw;
        }
    }

    public static Task<CredentialVault> RecoverAsync(string destination, string backup, ReadOnlyMemory<char> master,
        CancellationToken cancellationToken = default) =>
        WithMasterAsync(master, (password, token) =>
        {
            string target = Path.GetFullPath(destination);
            string source = Path.GetFullPath(backup);
            if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a separate encrypted backup.", nameof(backup));

            byte[]? original = ReadRecoveryTarget(target);
            Guid? expectedId = original is null ? null : ReadVaultId(original);
            using CredentialVault verified = Unlock(source, password, token);
            if (expectedId is { } id && verified.Id != id)
                throw new InvalidDataException("The backup belongs to another credential vault.");
            return RecoverVerified(target, original, verified, token);
        }, cancellationToken);

    private static Guid? ReadVaultId(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes.AsSpan(0, 8).SequenceEqual("TTTYKEY\0"u8) &&
            (bytes[8] != 0 || bytes[9] > 1 || (bytes[9] == 1 && (bytes[10] != 0 || bytes[11] != 0))))
            throw new InvalidDataException("A newer credential vault format cannot be replaced.");
        try { return VaultContainer.Validate(bytes); }
        catch (InvalidDataException) { return null; }
    }

    private static CredentialVault RecoverVerified(string path, byte[]? original, CredentialVault verified,
        CancellationToken token)
    {
        verified.CheckCurrent();
        if (original is null) CommitNew(path, verified.encrypted, token);
        else
        {
            string recovery = path + $".recovery-{Guid.NewGuid():N}.tttykey";
            string temporary = WriteCandidate(path, verified.encrypted, token);
            bool preserveCandidate = false;
            try
            {
                using var writer = LockWriter(path);
                if (ReadRecoveryTarget(path) is not { } actual || !actual.AsSpan().SequenceEqual(original))
                {
                    preserveCandidate = true;
                    throw new IOException("The credential vault changed before recovery.");
                }
                token.ThrowIfCancellationRequested();
                Exception? error = null;
                try { File.Replace(temporary, path, recovery); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { error = exception; }
                preserveCandidate = true;
                byte[]? installed = ReadRecoveryTarget(path);
                if (installed is not null && installed.AsSpan().SequenceEqual(verified.encrypted) &&
                    ReadRecoveryTarget(recovery) is { } preserved && preserved.AsSpan().SequenceEqual(original))
                    preserveCandidate = false;
                else if (error is not null && installed is not null && installed.AsSpan().SequenceEqual(original))
                {
                    preserveCandidate = false;
                    throw new IOException("Credential vault recovery failed.", error);
                }
                else throw new IOException("Credential vault recovery could not be verified.", error);
            }
            finally { if (!preserveCandidate) DeleteOwnedCandidate(temporary); }
        }
        return new CredentialVault(path, verified.Id, verified.root.ToArray(), verified.encrypted);
    }

    public Task<IReadOnlyList<VaultEntryInfo>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
        UseAsync((plaintext, token) =>
        {
            token.ThrowIfCancellationRequested();
            return VaultPayload.Read(plaintext, Id, null, out _, out _);
        }, cancellationToken);

    public Task<VaultSecret> GetPasswordAsync(Guid vaultId, Guid entryId, CancellationToken cancellationToken = default) =>
        GetSecretAsync(vaultId, entryId, "password", cancellationToken);

    public Task<VaultSecret> GetPrivateKeyAsync(Guid vaultId, Guid entryId, CancellationToken cancellationToken = default) =>
        GetSecretAsync(vaultId, entryId, "privateKey", cancellationToken);

    private Task<VaultSecret> GetSecretAsync(Guid vaultId, Guid entryId, string expected, CancellationToken cancellationToken)
    {
        if (vaultId != Id) throw new CredentialLookupException(MacroErrorCode.VaultMismatch, "The selected vault does not match the credential reference.");
        return UseAsync((plaintext, token) =>
        {
            token.ThrowIfCancellationRequested();
            var entries = VaultPayload.Read(plaintext, Id, entryId, out byte[]? secret, out _);
            if (secret is null) throw new CredentialLookupException(MacroErrorCode.CredentialUnavailable, "The credential entry is missing.");
            if (!entries.Any(entry => entry.Id == entryId && entry.Kind == expected))
            {
                CryptographicOperations.ZeroMemory(secret);
                throw new CredentialLookupException(MacroErrorCode.CredentialKindMismatch, "The credential entry has a different kind.");
            }
            if (!token.IsCancellationRequested) return new VaultSecret(secret);
            CryptographicOperations.ZeroMemory(secret);
            throw new OperationCanceledException(token);
        }, cancellationToken);
    }

    public Task<Guid> AddPasswordAsync(string name, string? username, ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken = default) => UseAsync((plaintext, token) =>
        {
            Guid id = OpenSsl.NewId();
            byte[] payload = VaultPayload.AddEntry(plaintext, Id, new VaultEntryInfo(id, name, username),
                password.Span);
            try { Commit(VaultContainer.Encrypt(encrypted, root, payload), token); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            return id;
        }, cancellationToken);

    public async Task<Guid> ImportPrivateKeyAsync(string name, string source, CancellationToken cancellationToken = default,
        ReadOnlyMemory<byte>? passphrase = null)
    {
        await DerivationWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UseAsync((plaintext, token) =>
            {
                byte[] fileBytes = new byte[1024 * 1024 + 1];
                byte[]? pkcs8 = null;
                try
                {
                    using (FileStream file = File.OpenRead(source))
                    {
                        int length = file.ReadAtLeast(fileBytes, fileBytes.Length, throwOnEndOfStream: false);
                        using SshPrivateKey key = SshPrivateKey.Import(fileBytes.AsSpan(0, length), passphrase);
                        pkcs8 = key.ExportPkcs8();
                        Guid id = OpenSsl.NewId();
                        var entry = new VaultEntryInfo(id, name, null, "privateKey", key.Algorithm, key.Fingerprint);
                        byte[] payload = VaultPayload.AddEntry(plaintext, Id, entry, pkcs8);
                        try { Commit(VaultContainer.Encrypt(encrypted, root, payload), token); }
                        finally { CryptographicOperations.ZeroMemory(payload); }
                        return id;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(fileBytes);
                    if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { DerivationWorker.Release(); }
    }

    public Task BackupAsync(string destination, CancellationToken cancellationToken = default) =>
        RunAsync(token =>
        {
            CommitNew(Path.GetFullPath(destination), encrypted, token);
            return true;
        }, cancellationToken);

    public Task UpdateEntryAsync(Guid entryId, string name, string? username, ReadOnlyMemory<byte>? password = null,
        CancellationToken cancellationToken = default) => UseAsync((plaintext, token) =>
        {
            byte[] updated = VaultPayload.UpdateEntry(plaintext, Id, entryId, name, username,
                password.GetValueOrDefault().Span, password.HasValue);
            try { Commit(VaultContainer.Encrypt(encrypted, root, updated), token); }
            finally { CryptographicOperations.ZeroMemory(updated); }
            return true;
        }, cancellationToken);

    public Task DeleteEntryAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        UseAsync((plaintext, token) =>
        {
            byte[] updated = VaultPayload.DeleteEntry(plaintext, Id, entryId);
            try { Commit(VaultContainer.Encrypt(encrypted, root, updated), token); }
            finally { CryptographicOperations.ZeroMemory(updated); }
            return true;
        }, cancellationToken);

    public async Task RestoreAsync(string backup, ReadOnlyMemory<char> master, CancellationToken cancellationToken = default)
    {
        if (Path.GetFullPath(backup).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a separate encrypted backup.", nameof(backup));
        using CredentialVault candidate = await UnlockAsync(backup, master, cancellationToken).ConfigureAwait(false);
        if (candidate.Id != Id) throw new InvalidDataException("The backup belongs to another credential vault.");
        byte[] replacement;
        byte[] snapshot;
        lock (candidate.gate)
        {
            candidate.CheckCurrent();
            replacement = candidate.root.ToArray();
            snapshot = candidate.encrypted;
        }
        try
        {
            await RunAsync(token =>
            {
                string recovery = path + $".recovery-{Guid.NewGuid():N}.tttykey";
                CommitNew(recovery, encrypted, token);
                try
                {
                    Commit(snapshot, token);
                    CryptographicOperations.ZeroMemory(root);
                    root = replacement;
                    replacement = [];
                }
                catch when (stale)
                {
                    unresolvedRoot = replacement;
                    replacement = [];
                    throw;
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(replacement); }
    }

    public Task ChangeMasterPasswordAsync(ReadOnlyMemory<char> master, CancellationToken cancellationToken = default) =>
        WithMasterAsync(master, (password, token) =>
        {
            lock (gate)
            {
                CheckCurrent();
                byte[] plaintext = VaultContainer.Decrypt(encrypted, root);
                byte[] replacement = new byte[32];
                try
                {
                    OpenSsl.Fill(replacement);
                    byte[] wrapper = VaultContainer.Wrap(Id, password, replacement);
                    Commit(VaultContainer.Encrypt(wrapper, replacement, plaintext), token);
                    CryptographicOperations.ZeroMemory(root);
                    root = replacement;
                    replacement = [];
                }
                catch when (stale)
                {
                    unresolvedRoot = replacement;
                    replacement = [];
                    throw;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    CryptographicOperations.ZeroMemory(replacement);
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
            if (unresolvedRoot is not null) CryptographicOperations.ZeroMemory(unresolvedRoot);
        }
    }

    private Task<T> UseAsync<T>(Func<byte[], CancellationToken, T> action, CancellationToken token) =>
        RunAsync(cancellation =>
        {
            byte[] plaintext = VaultContainer.Decrypt(encrypted, root);
            try { return action(plaintext, cancellation); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }, token);

    private Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken token) => Task.Run(() =>
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            CheckCurrent();
            return action(token);
        }
    }, token);

    private static async Task<T> WithMasterAsync<T>(ReadOnlyMemory<char> master,
        Func<byte[], CancellationToken, T> operation, CancellationToken token)
    {
        if (master.Length > VaultContainer.MaximumMasterInputLength)
            throw new ArgumentOutOfRangeException(nameof(master), VaultContainer.MasterLengthMessage);
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

    private void Commit(byte[] candidate, CancellationToken token)
    {
        string temporary = WriteCandidate(path, candidate, token);
        bool preserveCandidate = false;
        byte[] expected = SHA256.HashData(candidate);
        try
        {
            using var writer = LockWriter(path);
            CheckCurrent();
            token.ThrowIfCancellationRequested();
            IOException? replacementError = null;
            try { File.Replace(temporary, path, path + ".bak"); }
            catch (IOException exception) { replacementError = exception; }
            catch (UnauthorizedAccessException exception)
            {
                replacementError = new IOException("Credential vault replacement failed.", exception);
            }
            byte[] actual;
            try { actual = SHA256.HashData(ReadFile(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                preserveCandidate = stale = true;
                throw new IOException("Credential vault replacement could not be verified.", exception);
            }
            if (actual.AsSpan().SequenceEqual(expected))
            {
                encrypted = candidate;
                digest = expected;
                return;
            }
            if (actual.AsSpan().SequenceEqual(digest) && replacementError is not null) throw replacementError;
            preserveCandidate = stale = true;
            throw new IOException("Credential vault replacement conflicted with another file change.");
        }
        finally
        {
            if (!preserveCandidate) DeleteOwnedCandidate(temporary);
        }
    }

    private static void CommitNew(string path, byte[] candidate, CancellationToken token, bool replaceEmpty = false)
    {
        string temporary = WriteCandidate(path, candidate, token);
        string? parked = null;
        bool installed = false;
        try
        {
            using var writer = LockWriter(path);
            token.ThrowIfCancellationRequested();
            if (replaceEmpty)
            {
                var target = new FileInfo(path);
                if (target.Length != 0 || target.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The credential vault is no longer an empty file.");
                string aside = Path.Combine(target.DirectoryName!, $".tabtty-empty-{Guid.NewGuid():N}.tmp");
                File.Move(path, aside, overwrite: false);
                parked = aside;
                var moved = new FileInfo(aside);
                if (moved.Length != 0 || moved.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The credential vault changed during initialization.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            installed = true;
            if (!ReadFile(path).AsSpan().SequenceEqual(candidate))
                throw new IOException("The new credential vault could not be verified.");
        }
        finally
        {
            DeleteOwnedCandidate(temporary);
            if (parked is not null)
            {
                if (installed) DeleteOwnedCandidate(parked);
                else
                {
                    // A competing writer may already have installed a vault. Keep the parked file in that case.
                    try { File.Move(parked, path, overwrite: false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    internal static string? ArchiveFile(string path)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        string name = Path.GetFileNameWithoutExtension(full);
        if (name.Length > 180) name = "vault";
        string archived = Path.Combine(directory, $"{name}.archived-{Guid.NewGuid():N}.tttykey");
        try
        {
            using var writer = LockWriter(full);
            FileAttributes attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The credential vault must be an ordinary file.");
            File.Move(full, archived, overwrite: false);
            return archived;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string WriteCandidate(string path, byte[] bytes, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".tabtty-{Guid.NewGuid():N}.tmp");
        bool owns = false;
        try
        {
            using FileStream file = OpenPrivate(temporary);
            owns = true;
            token.ThrowIfCancellationRequested();
            file.Write(bytes);
            file.Flush(flushToDisk: true);
            return temporary;
        }
        catch
        {
            if (owns) DeleteOwnedCandidate(temporary);
            throw;
        }
    }

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
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, options, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = options,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    private static byte[]? ReadRecoveryTarget(string path)
    {
        try { return ReadFile(path, allowTruncated: true); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static byte[] ReadFile(string path, bool allowTruncated = false)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = file.Length;
        if ((!allowTruncated && length < VaultContainer.HeaderSize + VaultContainer.TagSize) ||
            length > VaultContainer.HeaderSize + VaultContainer.TagSize + VaultContainer.MaxPayload)
            throw new InvalidDataException("Invalid credential vault file size.");
        byte[] bytes = new byte[(int)length];
        file.ReadExactly(bytes);
        if (file.ReadByte() >= 0) throw new InvalidDataException("Invalid credential vault file size.");
        return bytes;
    }

    private static void DeleteOwnedCandidate(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
