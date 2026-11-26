using System.IO;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TransferFiles(SftpClient client, TimeSpan wait, FileOperationOwnership? ownership = null,
    nint recycleOwner = 0, Func<ReadOnlyMemory<byte>, Task>? beforeLocalWrite = null) : IFileOperationIo
{
    private static readonly SemaphoreSlim LocalCalls = new(8);
    private static readonly SemaphoreSlim LocalCommit = new(1);

    public bool IsOpen => client.IsOpen;

    public string Display(TransferPath path) => path.Local is { } local
        ? PermissionDisplay.Literal(local) : client.DisplayPath(path.Remote!);

    public async Task<TransferPath> ResolveAsync(bool remote, string path, TransferPath? current, CancellationToken token)
    {
        if (!remote)
        {
            string full;
            try { full = OrdinaryWindowsPath.Resolve(path, current?.Local); }
            catch (InvalidDataException) { throw new SftpException(SftpFailure.InvalidName); }
            await LocalAsync(() => RequireDirectory(full), token);
            return new TransferPath(full);
        }
        byte[] wire = client.EncodePath(path);
        if (!path.StartsWith('/') && current?.Remote is { } basePath)
            wire = basePath.AsSpan().EndsWith("/"u8) ? [.. basePath, .. wire] : [.. basePath, (byte)'/', .. wire];
        if (wire.Length > 32 * 1024) throw new SftpException(SftpFailure.InvalidName);
        return new TransferPath(await client.ResolveAsync(wire, token));
    }

    public Task<TransferPath> ParentAsync(TransferPath path, CancellationToken token) => path.Local is { } local
        ? Task.FromResult(new TransferPath(Path.GetDirectoryName(local) ?? local))
        : ResolveAsync(true, "..", path, token);

    public async Task<TransferItem[]> ListAsync(TransferPath path, CancellationToken token)
    {
        if (path.Local is { } local)
            return await LocalAsync(() =>
            {
                using var directory = OpenDirectory(path);
                path.DirectoryIdentity ??= directory.Identity;
                var items = new List<TransferItem>();
                long bytes = 0;
                foreach (string entry in Directory.EnumerateFileSystemEntries(local))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(entry);
                    string label = PermissionDisplay.Literal(name);
                    bytes += 2L * (entry.Length + name.Length + label.Length) + 256;
                    if (items.Count == SftpClient.MaxEntries || bytes > 16 * 1024 * 1024)
                        throw new SftpException(SftpFailure.Capacity);
                    if (LocalStat(entry) is { } attributes)
                        items.Add(new TransferItem(new TransferPath(entry) { ParentPath = path }, name, label, attributes));
                }
                return items.ToArray();
            }, token);
        await RequireRemoteDirectoryAsync(path, token);
        SftpEntry[] entries = await client.ListAsync(path.Remote!, token);
        var rows = new TransferItem[entries.Length];
        long rowBytes = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[index];
            var child = new TransferPath(SftpClient.Child(path.Remote!, entry.Name)) { ParentPath = path };
            string? name = null;
            string label;
            try { name = client.DecodeName(entry.Name); label = PermissionDisplay.Literal(name); }
            catch (SftpException error) when (error.Failure == SftpFailure.InvalidName)
            {
                label = SftpClient.DisplayBytes(entry.Name);
            }
            rowBytes += child.Remote!.Length + 2L * ((name?.Length ?? 0) + label.Length) + 256;
            if (rowBytes > 16 * 1024 * 1024) throw new SftpException(SftpFailure.Capacity);
            rows[index] = new TransferItem(child, name, label, entry.Attributes);
        }
        return rows;
    }

    public TransferPath Child(TransferPath directory, string name)
    {
        if (directory.Remote is { } remote)
            return new TransferPath(SftpClient.Child(remote, client.EncodeName(name))) { ParentPath = directory };
        if (name.Length == 0 || name is "." or ".." || name[^1] is '.' or ' ' ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || OrdinaryWindowsPath.IsReservedFileName(name))
            throw new SftpException(SftpFailure.InvalidName);
        string path = Path.Combine(directory.Local!, name);
        if (path.Length > 32767) throw new SftpException(SftpFailure.InvalidName);
        return new TransferPath(path) { ParentPath = directory };
    }

    public TransferPath Parent(TransferPath path)
    {
        if (path.ParentPath is { } known) return known;
        if (path.Local is { } local) return new TransferPath(Path.GetDirectoryName(local)!);
        byte[] remote = path.Remote!;
        int slash = Array.LastIndexOf(remote, (byte)'/');
        return new TransferPath(slash < 0 ? [] : remote[..Math.Max(1, slash)]);
    }

    public Task<TransferAttributes?> StatAsync(TransferPath path, CancellationToken token) => path.Local is { } local
        ? LocalAsync(() =>
        {
            string? parent = Path.GetDirectoryName(local);
            using var directory = parent is null ? null : OpenDirectory(Parent(path));
            return TransferWindowsPath.Stat(local);
        }, token) : client.StatAsync(path.Remote!, token);

    public Task<bool> IsCaseSensitiveAsync(TransferPath directory, CancellationToken token) => directory.Local is { } local
        ? LocalAsync(() =>
        {
            if (!Directory.Exists(local)) return false;
            using var handle = OpenDirectory(directory);
            return handle.IsCaseSensitive;
        }, token) : Task.FromResult(false);

    public async Task CreateAsync(TransferPath path, CancellationToken token)
    {
        if (await StatAsync(path, token) is not null) throw new SftpException(SftpFailure.ServerError);
        if (path.Local is { } local)
            await LocalAsync(() =>
            {
                using var directory = OpenDirectory(Parent(path));
                directory.CreateDirectory(local);
                using var created = OpenDirectory(path);
                if (!PrivateWindowsFile.HasPrivateAccess(new DirectoryInfo(local)))
                    throw new SftpException(SftpFailure.DownloadPermissions);
                path.DirectoryIdentity ??= created.Identity;
                return true;
            }, token, mutation: true);
        else
        {
            await RequireRemoteDirectoryAsync(Parent(path), token);
            await client.CreateDirectoryAsync(path.Remote!, token);
        }
    }

    public async Task RenameAsync(TransferPath source, TransferPath target, bool replace, CancellationToken token)
    {
        if (source.Local is { } local)
        {
            await LocalAsync(() =>
            {
                using var destination = OpenDirectory(Parent(target));
                using var directory = OpenDirectory(Parent(source));
                if (LocalStat(local) is { Kind: TransferKind.Directory }) Directory.Move(local, target.Local!);
                else File.Move(local, target.Local!, replace);
                return true;
            }, token, mutation: true);
        }
        else
        {
            await RequireRemoteDirectoryAsync(Parent(source), token);
            await client.RenameAsync(source.Remote!, target.Remote!, replace, token);
        }
    }

    public Task<bool> TryCommitLocalAsync(TransferPath source, TransferPath target, TransferAttributes? approved,
        CancellationToken token) => LocalAsync(() =>
    {
        using var directory = OpenDirectory(Parent(target));
        if (source.StagedIdentity is not { } identity) throw new SftpException(SftpFailure.SourceChanged);
        using var marker = TransferWindowsPath.OpenVerifiedDownloadMark(source.Local!, identity);
        if (!TransferAttributes.SameOverwriteTarget(approved, TransferWindowsPath.Stat(target.Local!))) return false;
        if (approved is { LocalIdentity: null }) throw new SftpException(SftpFailure.CannotReplace);
        if (!FileObjectIdentity.MoveIfSame(source.Local!, identity, target.Local!, approved is not null))
            throw new SftpException(SftpFailure.SourceChanged);
        return true;
    }, token, mutation: true);

    public async Task DeleteAsync(TransferPath path, TransferKind kind, CancellationToken token)
    {
        if (path.Local is { } local)
            await LocalAsync(() =>
            {
                using var directory = OpenDirectory(Parent(path));
                if (path.StagedIdentity is { } identity)
                {
                    if (!FileObjectIdentity.DeleteIfSame(local, identity))
                        throw new SftpException(SftpFailure.SourceChanged);
                    return true;
                }
                if (kind == TransferKind.Directory && path.DirectoryIdentity is { })
                    using (OpenDirectory(path)) { }
                FileAttributes current = File.GetAttributes(local);
                if (Kind(current) != kind) throw new SftpException(SftpFailure.ServerError);
                if (current.HasFlag(FileAttributes.Directory)) Directory.Delete(local);
                else File.Delete(local);
                return true;
            }, token, mutation: true);
        else
        {
            await RequireRemoteDirectoryAsync(Parent(path), token);
            if ((await client.StatAsync(path.Remote!, token))?.Kind != kind)
                throw new SftpException(SftpFailure.SourceChanged);
            await client.RemoveAsync(path.Remote!, kind == TransferKind.Directory, token);
        }
    }

    public Task RecycleAsync(TransferPath path, CancellationToken token) => LocalAsync(() =>
    {
        using var directory = OpenDirectory(Parent(path));
        if (path.DirectoryIdentity is { }) using (OpenDirectory(path)) { }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { ProfileRecycling.RecycleItem(recycleOwner, path.Local!); done.SetResult(); }
            catch (ProfileRecycling.RecycleOutcomeException error)
            {
                done.SetException(new SftpException(SftpFailure.UnknownOutcome, error));
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        done.Task.GetAwaiter().GetResult();
        return true;
    }, token, mutation: true);

    public async Task DeleteMovedSourceAsync(TransferPath path, TransferAttributes expected, CancellationToken token)
    {
        if (path.Local is { } local)
        {
            await LocalAsync(() =>
            {
                using var directory = OpenDirectory(Parent(path));
                TransferWindowsPath.DeleteMovedSource(local, expected);
                return true;
            }, token, mutation: true);
            return;
        }
        await RequireRemoteDirectoryAsync(Parent(path), token);
        var actual = await client.StatAsync(path.Remote!, token);
        bool directoryEntry = expected.Kind == TransferKind.Directory;
        if (directoryEntry ? actual?.Kind != TransferKind.Directory : !TransferAttributes.SameOverwriteTarget(expected, actual))
            throw new SftpException(SftpFailure.SourceChanged);
        await client.RemoveAsync(path.Remote!, directoryEntry, token);
    }

    public async Task<bool> StageAsync(TransferPath source, TransferPath temporary, TransferAttributes snapshot,
        Action<long> progress, Action created, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The provider owns its block and stream workspace until local I/O
        // and stream disposal actually return, including after channel close.
        using var storage = SftpClient.ReserveProviderStorage(2 * SftpClient.BlockSize);
        bool metadataWarning = false;
        byte[]? handle = null;
        FileStream? stream = null;
        try
        {
            if (source.Local is not null)
            {
                stream = await OpenLocalAsync(source, FileMode.Open, token);
                if (snapshot.LocalIdentity is not null &&
                    !TransferAttributes.SameOverwriteTarget(snapshot, TransferWindowsPath.ReadAttributesFrom(stream.SafeFileHandle)))
                    throw new SftpException(SftpFailure.SourceChanged);
                long expectedSize = stream.Length;
                if (snapshot.Size is { } listedSize && listedSize != expectedSize)
                    throw new SftpException(SftpFailure.SourceChanged);
                await RequireRemoteDirectoryAsync(Parent(temporary), token);
                handle = await client.OpenAsync(temporary.Remote!, write: true, token);
                created();
                byte[] buffer = new byte[SftpClient.BlockSize];
                long offset = 0;
                while (true)
                {
                    int count = await LocalStreamAsync(inner => stream.ReadAsync(buffer, inner).AsTask(), token);
                    if (count == 0) break;
                    long next = NextOffset(offset, count, expectedSize);
                    await client.WriteAsync(handle, offset, buffer.AsMemory(0, count), token);
                    offset = next;
                    progress(offset);
                }
                if (offset != expectedSize) throw new SftpException(SftpFailure.SourceChanged);
                await RecheckAsync(source, snapshot, offset, token);
                if (snapshot.Modified is { } modified)
                {
                    // SFTP v3 has a single flag for both timestamps. Disclose only the source modification time.
                    try { await client.SetTimeAsync(handle, modified, accessed: null, token); }
                    catch (Exception error) when (error is IOException or OverflowException) { metadataWarning = true; }
                }
                byte[] completed = handle;
                handle = null;
                await client.CloseAsync(completed, CancellationToken.None);
            }
            else
            {
                await RequireRemoteDirectoryAsync(Parent(source), token);
                handle = await client.OpenAsync(source.Remote!, write: false, token);
                TransferAttributes opened = await client.GetAttributesAsync(handle, token);
                if (Changed(snapshot, opened)) throw new SftpException(SftpFailure.SourceChanged);
                long? expectedSize = opened.Size ?? snapshot.Size;
                stream = await OpenLocalAsync(temporary, FileMode.CreateNew, token);
                created();
                await LocalStreamAsync(inner => Task.Run(() =>
                {
                    using var directory = OpenDirectory(Parent(temporary));
                    TransferWindowsPath.MarkDownload(temporary.Local!, stream);
                    return true;
                }, inner), token);
                long offset = 0;
                while (await client.ReadAsync(handle, offset, SftpClient.BlockSize, token) is { } data)
                {
                    long next = NextOffset(offset, data.Length, expectedSize);
                    await LocalStreamAsync(async inner =>
                    {
                        if (beforeLocalWrite is not null) await beforeLocalWrite(data);
                        await stream.WriteAsync(data, inner);
                        return true;
                    }, token);
                    offset = next;
                    progress(offset);
                }
                TransferAttributes completed = await client.GetAttributesAsync(handle, token);
                if (Changed(opened, completed) || (expectedSize ?? completed.Size) is { } size && offset != size)
                    throw new SftpException(SftpFailure.SourceChanged);
                await RecheckAsync(source, snapshot, offset, token);
                await LocalStreamAsync(async inner => { await stream.FlushAsync(inner); return true; }, token);
                if ((opened.Modified ?? snapshot.Modified) is { } modified)
                {
                    try { await LocalStreamAsync(inner => Task.Run(() =>
                    {
                        File.SetLastWriteTimeUtc(stream.SafeFileHandle, modified.UtcDateTime);
                        return true;
                    }, inner), token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                    { metadataWarning = true; }
                }
            }
            return metadataWarning;
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
            if (handle is not null) await client.TryCloseAsync(handle);
        }
    }

    public async Task RecheckAsync(TransferPath source, TransferAttributes expected, long? length, CancellationToken token)
    {
        TransferAttributes? actual = await StatAsync(source, token);
        if (actual is null || Changed(expected, actual) ||
            (length.HasValue && (expected.Size ?? actual.Size) is { } size && length != size))
            throw new SftpException(SftpFailure.SourceChanged);
    }

    internal static long NextOffset(long offset, int count, long? expectedSize)
    {
        if (offset > (expectedSize ?? long.MaxValue) - count) throw new SftpException(SftpFailure.SourceChanged);
        return offset + count;
    }

    public static bool Changed(TransferAttributes before, TransferAttributes after) =>
        (before.Kind != TransferKind.Unknown && after.Kind != TransferKind.Unknown && before.Kind != after.Kind) ||
        (before.Size.HasValue && after.Size.HasValue && before.Size != after.Size) ||
        (before.Modified.HasValue && after.Modified.HasValue && before.Modified != after.Modified) ||
        (before.LocalIdentity.HasValue && before.LocalIdentity != after.LocalIdentity);

    private async Task RequireRemoteDirectoryAsync(TransferPath directory, CancellationToken token)
    {
        var chain = new Stack<TransferPath>();
        for (TransferPath? current = directory; current is not null; current = current.ParentPath) chain.Push(current);
        while (chain.TryPop(out TransferPath? current))
        {
            if ((await client.StatAsync(current.Remote!, token))?.Kind != TransferKind.Directory)
                throw new SftpException(SftpFailure.InvalidName);
        }
    }

    private static TransferKind Kind(FileAttributes attributes) => attributes.HasFlag(FileAttributes.ReparsePoint)
        ? TransferKind.Link : attributes.HasFlag(FileAttributes.Directory) ? TransferKind.Directory : TransferKind.File;

    private static TransferAttributes? LocalStat(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        TransferKind kind = Kind(attributes);
        if (kind == TransferKind.Link) return new(kind, null, null);
        FileSystemInfo info = kind == TransferKind.Directory ? new DirectoryInfo(path) : new FileInfo(path);
        return new(kind, info is FileInfo file ? file.Length : null, info.LastWriteTimeUtc);
    }

    private static bool RequireDirectory(string path)
    {
        using var directory = new TransferWindowsPath(path);
        return true;
    }

    private TransferWindowsPath OpenDirectory(TransferPath directory)
    {
        var opened = new TransferWindowsPath(directory.Local!);
        try
        {
            for (TransferPath? current = directory; current is not null; current = current.ParentPath)
            {
                if (current.DirectoryIdentity is not { } expected) continue;
                using var ancestor = current == directory ? null : new TransferWindowsPath(current.Local!);
                if ((ancestor ?? opened).Identity != expected) throw new SftpException(SftpFailure.SourceChanged);
            }
            return opened;
        }
        catch { opened.Dispose(); throw; }
    }

    private async Task<FileStream> OpenLocalAsync(TransferPath path, FileMode mode, CancellationToken token)
    {
        return await LocalAsync(() =>
        {
            using var directory = OpenDirectory(Parent(path));
            if (mode == FileMode.Open) return TransferWindowsPath.OpenRead(path.Local!);
            var stream = TransferWindowsPath.CreateDownload(path.Local!);
            try
            {
                path.StagedIdentity = FileObjectIdentity.Read(stream.SafeFileHandle);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }, token, discard: stream =>
        {
            stream.Dispose();
            if (path.StagedIdentity is { } identity)
            {
                using var directory = OpenDirectory(Parent(path));
                FileObjectIdentity.DeleteIfSame(path.Local!, identity);
            }
        });
    }

    private async Task<T> LocalStreamAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait);
        await LocalCalls.WaitAsync(deadline.Token);
        try { return await call(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new SftpException(SftpFailure.Timeout); }
        finally { LocalCalls.Release(); }
    }

    private async Task<T> LocalAsync<T>(Func<T> call, CancellationToken token, bool mutation = false, Action<T>? discard = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait);
        CancellationToken bounded = deadline.Token;
        int issued = 0;
        Task<T> operation = Task.Run(async () =>
        {
            if (mutation) await LocalCommit.WaitAsync(bounded);
            try
            {
                await LocalCalls.WaitAsync(bounded);
                try
                {
                    Interlocked.Exchange(ref issued, 1);
                    bounded.ThrowIfCancellationRequested();
                    return call();
                }
                finally { LocalCalls.Release(); }
            }
            finally { if (mutation) LocalCommit.Release(); }
        });
        try { return await operation.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!operation.IsCompletedSuccessfully)
        {
            Task cleanup = ObserveAsync(operation, discard);
            ownership?.Track(cleanup);
            if (mutation && Volatile.Read(ref issued) != 0 && !operation.IsCanceled)
                throw new SftpException(SftpFailure.UnknownOutcome);
            if (!token.IsCancellationRequested) throw new SftpException(SftpFailure.Timeout);
            throw;
        }
    }

    private static async Task ObserveAsync<T>(Task<T> operation, Action<T>? discard)
    {
        try { T value = await operation; discard?.Invoke(value); }
        catch (Exception) { }
    }
}
