using System.IO;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TransferFiles(SftpClient client, TimeSpan wait, FileOperationOwnership? ownership = null, nint recycleOwner = 0) : IFileOperationIo
{
    private static readonly SemaphoreSlim LocalCalls = new(8);
    private static readonly SemaphoreSlim LocalCommit = new(1);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public bool IsOpen => client.IsOpen;

    public string Display(TransferPath path) => path.Local ?? client.DisplayPath(path.Remote!);

    public async Task<TransferPath> ResolveAsync(bool remote, string path, TransferPath? current, CancellationToken token)
    {
        if (!remote)
        {
            if (string.IsNullOrEmpty(path)) throw new SftpException(SftpFailure.InvalidName);
            string full = current?.Local is { } parent ? Path.GetFullPath(path, parent) : Path.GetFullPath(path);
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
                    bytes += entry.Length * 4L + 256;
                    if (items.Count == SftpClient.MaxEntries || bytes > 16 * 1024 * 1024)
                        throw new SftpException(SftpFailure.Capacity);
                    string name = Path.GetFileName(entry);
                    if (LocalStat(entry) is { } attributes)
                        items.Add(new TransferItem(new TransferPath(entry) { ParentPath = path }, name, name, attributes));
                }
                return items.ToArray();
            }, token);
        await RequireRemoteDirectoryAsync(path, token);
        SftpEntry[] entries = await client.ListAsync(path.Remote!, token);
        return entries.Select(entry =>
        {
            var child = new TransferPath(SftpClient.Child(path.Remote!, entry.Name)) { ParentPath = path };
            try { return new TransferItem(child, client.DecodeName(entry.Name), client.DisplayName(entry.Name), entry.Attributes); }
            catch (SftpException error) when (error.Failure == SftpFailure.InvalidName)
            {
                return new TransferItem(child, null, $"[{Convert.ToHexString(entry.Name)}]", entry.Attributes);
            }
        }).ToArray();
    }

    public TransferPath Child(TransferPath directory, string name)
    {
        if (directory.Remote is { } remote)
            return new TransferPath(SftpClient.Child(remote, client.EncodeName(name))) { ParentPath = directory };
        if (name.Length == 0 || name is "." or ".." || name[^1] is '.' or ' ' ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved.Contains(name.Split('.')[0].TrimEnd()))
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
            return LocalStat(local);
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
                TransferWindowsPath.CreateDirectory(local);
                using var created = OpenDirectory(path);
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
        TransferAttributes? current = LocalStat(target.Local!);
        if (current?.Kind != approved?.Kind || current?.Size != approved?.Size || current?.Modified != approved?.Modified)
            return false;
        if (LocalStat(source.Local!) is not { Kind: TransferKind.File }) throw new SftpException(SftpFailure.SourceChanged);
        File.Move(source.Local!, target.Local!, approved is not null);
        return true;
    }, token, mutation: true);

    public async Task DeleteAsync(TransferPath path, TransferKind kind, CancellationToken token)
    {
        if (path.Local is { } local)
            await LocalAsync(() =>
            {
                using var directory = OpenDirectory(Parent(path));
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

    public async Task<bool> StageAsync(TransferPath source, TransferPath temporary, TransferAttributes snapshot,
        Action<long> progress, Action created, CancellationToken token)
    {
        bool metadataWarning = false;
        byte[]? handle = null;
        FileStream? stream = null;
        try
        {
            if (source.Local is not null)
            {
                stream = await OpenLocalAsync(source, FileMode.Open, FileAccess.Read, token);
                await RequireRemoteDirectoryAsync(Parent(temporary), token);
                handle = await client.OpenAsync(temporary.Remote!, write: true, token);
                created();
                byte[] buffer = new byte[SftpClient.BlockSize];
                long offset = 0;
                while (true)
                {
                    int count = await LocalStreamAsync(inner => stream.ReadAsync(buffer, inner).AsTask(), token);
                    if (count == 0) break;
                    await client.WriteAsync(handle, offset, buffer.AsMemory(0, count), token);
                    offset += count;
                    progress(offset);
                }
                await RecheckAsync(source, snapshot, offset, token);
                if (snapshot.Modified is { } modified)
                {
                    try { await client.SetTimeAsync(handle, modified, snapshot.Accessed, token); }
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
                stream = await OpenLocalAsync(temporary, FileMode.CreateNew, FileAccess.Write, token);
                created();
                long offset = 0;
                while (await client.ReadAsync(handle, offset, SftpClient.BlockSize, token) is { } data)
                {
                    await LocalStreamAsync(async inner => { await stream.WriteAsync(data, inner); return true; }, token);
                    offset += data.Length;
                    progress(offset);
                }
                if (Changed(opened, await client.GetAttributesAsync(handle, token)))
                    throw new SftpException(SftpFailure.SourceChanged);
                await RecheckAsync(source, snapshot, offset, token);
                await LocalStreamAsync(async inner => { await stream.FlushAsync(inner); return true; }, token);
                await stream.DisposeAsync();
                stream = null;
                if (snapshot.Modified is { } modified)
                {
                    try { await LocalAsync(() =>
                    {
                        using var directory = OpenDirectory(Parent(temporary));
                        File.SetLastWriteTimeUtc(temporary.Local!, modified.UtcDateTime);
                        return true;
                    }, token); }
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
        if (actual is null || Changed(expected, actual) || (length.HasValue && expected.Size is { } size && length != size))
            throw new SftpException(SftpFailure.SourceChanged);
    }

    public static bool Changed(TransferAttributes before, TransferAttributes after) =>
        (before.Kind != TransferKind.Unknown && after.Kind != TransferKind.Unknown && before.Kind != after.Kind) ||
        (before.Size.HasValue && after.Size.HasValue && before.Size != after.Size) ||
        (before.Modified.HasValue && after.Modified.HasValue && before.Modified != after.Modified);

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
        return new(kind, info is FileInfo file ? file.Length : null, info.LastWriteTimeUtc, info.LastAccessTimeUtc);
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

    private async Task<FileStream> OpenLocalAsync(TransferPath path, FileMode mode, FileAccess access, CancellationToken token)
    {
        return await LocalAsync(() =>
        {
            using var directory = OpenDirectory(Parent(path));
            return mode == FileMode.Open ? TransferWindowsPath.OpenRead(path.Local!) :
                new FileStream(path.Local!, mode, access, FileShare.Read, SftpClient.BlockSize, FileOptions.Asynchronous);
        }, token, discard: stream =>
        {
            stream.Dispose();
            if (mode == FileMode.CreateNew)
            {
                using var directory = OpenDirectory(Parent(path));
                File.Delete(path.Local!);
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
