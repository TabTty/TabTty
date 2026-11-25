using System.Collections.ObjectModel;

namespace TabTty.Core;

internal sealed record TransferItem(TransferPath Path, string? Name, string Label, TransferAttributes Attributes);

internal sealed class TransferPath
{
    public string? Local { get; }
    public byte[]? Remote { get; }
    public TransferFileIdentity? DirectoryIdentity { get; set; }
    internal FileObjectIdentity? StagedIdentity { get; set; }
    public TransferPath? ParentPath { get; init; }
    public TransferPath(string local) => Local = local;
    public TransferPath(byte[] remote) => Remote = remote;
}

internal readonly record struct TransferFileIdentity(uint Volume, ulong File);

internal interface IFileOperationIo
{
    bool IsOpen { get; }
    string Display(TransferPath path);
    TransferPath Child(TransferPath directory, string name);
    TransferPath Parent(TransferPath path);
    Task<TransferItem[]> ListAsync(TransferPath directory, CancellationToken token);
    Task<TransferAttributes?> StatAsync(TransferPath path, CancellationToken token);
    Task<bool> IsCaseSensitiveAsync(TransferPath path, CancellationToken token);
    Task CreateAsync(TransferPath path, CancellationToken token);
    Task RenameAsync(TransferPath source, TransferPath target, bool replace, CancellationToken token);
    Task<bool> TryCommitLocalAsync(TransferPath source, TransferPath target, TransferAttributes? approved, CancellationToken token);
    Task DeleteAsync(TransferPath path, TransferKind kind, CancellationToken token);
    Task RecycleAsync(TransferPath path, CancellationToken token);
    Task<bool> StageAsync(TransferPath source, TransferPath temporary, TransferAttributes snapshot,
        Action<long> progress, Action created, CancellationToken token);
    Task RecheckAsync(TransferPath source, TransferAttributes expected, long? length, CancellationToken token);
}

internal enum TransferQuestionKind { Overwrite, Delete, PermanentDelete }
internal sealed record TransferQuestion(TransferQuestionKind Kind, TransferPath Path,
    TransferItem? Source = null, TransferAttributes? Existing = null, IReadOnlyList<TransferItem>? Selection = null, CancellationToken Lifetime = default);
internal abstract record FileOperationUpdate
{
    internal sealed record Started : FileOperationUpdate;
    internal sealed record Entry(string Path, string Reason) : FileOperationUpdate;
    internal sealed record Progress(string Path, long Bytes, long? Total) : FileOperationUpdate;
}

// The owner is connection-scoped and has no reference to a transfer window.
internal sealed class FileOperationOwnership
{
    private readonly object gate = new();
    private bool active;
    private bool needsRefresh;
    private int pending;
    private long revision;
    public event Action? Changed;
    public bool IsBusy { get { lock (gate) return active || pending != 0; } }
    public bool NeedsRefresh { get { lock (gate) return needsRefresh; } }
    public long Revision { get { lock (gate) return revision; } }
    public bool TryEnter()
    {
        lock (gate)
        {
            if (active || pending != 0 || needsRefresh) return false;
            active = true;
            revision++;
            return true;
        }
    }
    public void Leave() { lock (gate) active = false; Changed?.Invoke(); }
    public void RequireRefresh() { lock (gate) needsRefresh = true; }
    public void Refreshed()
    {
        lock (gate)
        {
            if (!active && pending == 0) needsRefresh = false;
        }
        Changed?.Invoke();
    }
    public void Track(Task task)
    {
        lock (gate) pending++;
        _ = RetireAsync(task);
    }
    private async Task RetireAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) when (task.IsFaulted || task.IsCanceled) { }
        finally
        {
            lock (gate) pending--;
            Changed?.Invoke();
        }
    }
}

internal sealed class FileOperations
{
    private static readonly SemaphoreSlim Batches = new(4);
    private TransferBatch? transferBatch;
    private TransferBatch batch => transferBatch ??= new(files, ReserveUnresolved);
    private const int DetailCharacters = 2_000_000;
    private const string Omission = "TransferResultsLimit";
    private const int DetailEntries = SftpClient.MaxEntries - 1;
    private readonly IFileOperationIo files;
    private readonly FileOperationOwnership ownership;
    private readonly CancellationToken lifetime;
    private readonly ObservableCollection<FileOperationUpdate.Entry> current = [];
    private CancellationTokenSource? running;
    private int characters;
    private bool omitted;
    private int reservedEntries;
    private int reservedCharacters;

    public bool IsRunning => running is not null;
    public bool CanStart => !lifetime.IsCancellationRequested && files.IsOpen && !ownership.IsBusy && !ownership.NeedsRefresh;
    public bool CanRetry => CanStart && batch.CanRetry;
    public ReadOnlyObservableCollection<FileOperationUpdate.Entry> Results { get; }
    private int DetailCount => current.Count - (omitted ? 1 : 0);

    public FileOperations(IFileOperationIo files, FileOperationOwnership ownership, CancellationToken lifetime)
    {
        this.files = files;
        this.ownership = ownership;
        this.lifetime = lifetime;
        Results = new(current);
    }

    public FileOperationUpdate.Entry[] UnresolvedForClose() =>
        Results.Where(entry => IsUnresolved(entry.Reason)).ToArray();
    public event Action<FileOperationUpdate>? Changed;
    public void Cancel() => running?.Cancel();

    public Task<string>? Copy(TransferItem[] source, TransferPath target, Func<TransferQuestion, ValueTask<TransferChoice>> confirm)
        => Start(token => batch.RunAsync(source.ToArray(), target,
            (item, path, attributes) => AskAsync(new(TransferQuestionKind.Overwrite, path, item, attributes), confirm, token),
            AddResult, Report, token), transfer: true);

    public Task<string>? Retry(Func<TransferQuestion, ValueTask<TransferChoice>> confirm)
    {
        if (!CanRetry) return null;
        var previous = current.ToArray();
        return Start(async token =>
        {
            var updated = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                await batch.RetryAsync(
                    (item, path, attributes) => AskAsync(new(TransferQuestionKind.Overwrite, path, item, attributes), confirm, token),
                    (path, key) =>
                    {
                        updated.Add(path);
                        AddResult(path, key);
                    }, Report, token);
            }
            finally
            {
                foreach (var entry in previous)
                    if (entry.Reason == Omission) MarkOmitted();
                    else if (IsUnresolved(entry.Reason) || !updated.Contains(entry.Path)) AddResult(entry.Path, entry.Reason);
            }
        }, transfer: true);
    }

    public Task<string>? Create(TransferPath parent, string name) => Start(async token =>
    {
        TransferPath path = files.Child(parent, name);
        await files.CreateAsync(path, token);
        AddResult(files.Display(path), "TransferDone");
    });

    public Task<string>? Rename(TransferPath parent, TransferItem item, string name) => Start(async token =>
    {
        TransferPath target = files.Child(parent, name);
        if (await files.StatAsync(target, token) is not null) throw new SftpException(SftpFailure.ServerError);
        if (await files.StatAsync(item.Path, token) is not { } current || ChangedAttributes(item.Attributes, current))
            throw new SftpException(SftpFailure.SourceChanged);
        try { await files.RenameAsync(item.Path, target, replace: false, token); }
        catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
        {
            AddResult(files.Display(target), "SftpUnknownOutcome");
            throw;
        }
        AddResult(files.Display(target), "TransferDone");
    });

    public Task<string>? Delete(TransferPath parent, TransferItem[] items,
        Func<TransferQuestion, ValueTask<TransferChoice>> confirm) => Start(async token =>
    {
        TransferItem[] selection = items.ToArray();
        if (await AskAsync(new(TransferQuestionKind.Delete, parent, Selection: Array.AsReadOnly(selection)), confirm, token)
            != TransferChoice.Overwrite) throw new OperationCanceledException();
        await DeleteTreeAsync(parent.Remote is not null, selection, confirm, token);
    });

    private Task<string>? Start(Func<CancellationToken, Task> execute, bool transfer = false)
    {
        if (!CanStart || !ownership.TryEnter()) return null;
        if (transfer && !Batches.Wait(0))
        {
            ownership.Leave();
            return Task.FromResult("SftpCapacity");
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        running = source;
        characters = 0;
        omitted = false;
        if (!transfer) batch.ClearRetry();
        return RunAsync();

        async Task<string> RunAsync()
        {
            try
            {
                current.Clear();
                Changed?.Invoke(new FileOperationUpdate.Started());
                await execute(source.Token);
                return "TransferFinished";
            }
            catch (OperationCanceledException) { return "TransferCancelled"; }
            catch (Exception error) when (TransferBatch.IsFileError(error))
            {
                if (error is SftpException { Failure: SftpFailure.UnknownOutcome }) ownership.RequireRefresh();
                return TransferBatch.ErrorKey(error);
            }
            finally
            {
                running = null;
                source.Dispose();
                if (transfer) Batches.Release();
                ownership.Leave();
            }
        }
    }

    private static async ValueTask<TransferChoice> AskAsync(TransferQuestion question,
        Func<TransferQuestion, ValueTask<TransferChoice>> confirm, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<TransferChoice> answer = confirm(question with { Lifetime = token }).AsTask();
        try
        {
            TransferChoice choice = await answer.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return choice;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = answer.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private void AddResult(string path, string key)
    {
        bool priority = IsUnresolved(key);
        int size = path.Length + key.Length;
        if (priority) MakeRoom(1, size);
        if (DetailCount + reservedEntries >= DetailEntries ||
            size > DetailCharacters - Omission.Length - characters - reservedCharacters)
        {
            MarkOmitted();
            return;
        }
        characters += size;
        var entry = new FileOperationUpdate.Entry(path, key);
        current.Insert(DetailCount, entry);
        Changed?.Invoke(entry);
    }

    private static bool IsUnresolved(string key) => key is "TransferTemporaryRemains" or "SftpUnknownOutcome";

    private void MarkOmitted()
    {
        if (omitted) return;
        omitted = true;
        var notice = new FileOperationUpdate.Entry("", Omission);
        current.Add(notice);
        Changed?.Invoke(notice);
    }

    private void MakeRoom(int entries, int size)
    {
        int index = DetailCount - 1;
        while (DetailCount + entries > DetailEntries || characters + size > DetailCharacters - Omission.Length)
        {
            while (index >= 0 && IsUnresolved(current[index].Reason)) index--;
            if (index < 0) break;
            var entry = current[index];
            characters -= entry.Path.Length + entry.Reason.Length;
            current.RemoveAt(index--);
            MarkOmitted();
        }
    }

    private IDisposable ReserveUnresolved(string temporary, string destination)
    {
        const int entries = 2;
        int size = temporary.Length + destination.Length + "TransferTemporaryRemains".Length + "SftpUnknownOutcome".Length;
        MakeRoom(entries, size);
        if (DetailCount + entries > DetailEntries || characters + size > DetailCharacters - Omission.Length)
            throw new SftpException(SftpFailure.Capacity);
        reservedEntries += entries;
        reservedCharacters += size;
        return new DetailReservation(() => { reservedEntries -= entries; reservedCharacters -= size; });
    }

    private sealed class DetailReservation(Action release) : IDisposable
    {
        private Action? retire = release;
        public void Dispose() => Interlocked.Exchange(ref retire, null)?.Invoke();
    }

    private void Report(string path, long bytes, long? total) =>
        Changed?.Invoke(new FileOperationUpdate.Progress(path, bytes, total));

    private static bool ChangedAttributes(TransferAttributes before, TransferAttributes after) =>
        (before.Kind != TransferKind.Unknown && after.Kind != TransferKind.Unknown && before.Kind != after.Kind) ||
        (before.Size.HasValue && after.Size.HasValue && before.Size != after.Size) ||
        (before.Modified.HasValue && after.Modified.HasValue && before.Modified != after.Modified);

    private async Task DeleteTreeAsync(bool remote, TransferItem[] items, Func<TransferQuestion, ValueTask<TransferChoice>> confirm, CancellationToken token)
    {
        var plans = new List<Deletion>();
        int count = 0;
        long bytes = 0;
        foreach (TransferItem item in items)
        {
            token.ThrowIfCancellationRequested();
            try { plans.Add(await BuildAsync(item, 0)); }
            catch (Exception error) when (TransferBatch.IsFileError(error)) { AddResult(item.Label, TransferBatch.ErrorKey(error)); }
        }
        int index = 0;
        try
        {
            for (; index < plans.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                Deletion plan = plans[index];
                if (!remote)
                {
                    try
                    {
                        await files.RecheckAsync(plan.Item.Path, plan.Item.Attributes, null, token);
                    }
                    catch (Exception error) when (TransferBatch.IsFileError(error))
                    {
                        plan.Reported = true;
                        AddResult(plan.Item.Label, TransferBatch.ErrorKey(error));
                        continue;
                    }
                    try
                    {
                        await files.RecycleAsync(plan.Item.Path, token);
                        plan.Reported = true;
                        AddResult(plan.Item.Label, "TransferDeleted");
                        continue;
                    }
                    catch (IOException error) when (error is not SftpException)
                    {
                        if (await AskAsync(new(TransferQuestionKind.PermanentDelete, plan.Item.Path, plan.Item), confirm, token) != TransferChoice.Overwrite)
                        { ReportUnfinished(plan); continue; }
                    }
                    catch (Exception error) when (TransferBatch.IsFileError(error))
                    {
                        plan.Reported = true;
                        AddResult(plan.Item.Label, TransferBatch.ErrorKey(error));
                        if (error is SftpException { Failure: SftpFailure.UnknownOutcome }) throw;
                        continue;
                    }
                }
                await RemoveAsync(plan);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SftpException { Failure: SftpFailure.UnknownOutcome or SftpFailure.Disconnected })
        {
            foreach (Deletion remaining in plans.Skip(index)) ReportUnfinished(remaining);
            throw;
        }

        async Task<Deletion> BuildAsync(TransferItem item, int depth)
        {
            token.ThrowIfCancellationRequested();
            bytes += files.Display(item.Path).Length * 2L + 192;
            if (++count > SftpClient.MaxEntries || depth > 128 || bytes > 16 * 1024 * 1024)
                throw new SftpException(SftpFailure.Capacity);
            TransferAttributes attributes = await files.StatAsync(item.Path, token) ?? throw new SftpException(SftpFailure.Missing);
            if (attributes.Kind == TransferKind.Unknown) throw new SftpException(SftpFailure.InvalidName);
            if (attributes.Kind != item.Attributes.Kind && item.Attributes.Kind != TransferKind.Unknown)
                throw new SftpException(SftpFailure.SourceChanged);
            var children = new List<Deletion>();
            if (attributes.Kind == TransferKind.Directory)
                foreach (TransferItem child in await files.ListAsync(item.Path, token)) children.Add(await BuildAsync(child, depth + 1));
            return new Deletion(item with { Attributes = attributes }, children);
        }

        async Task<bool> RemoveAsync(Deletion entry)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                TransferKind expected = entry.Item.Attributes.Kind;
                TransferAttributes? current = await files.StatAsync(entry.Item.Path, token);
                if (current is null || current.Kind != expected || ChangedAttributes(entry.Item.Attributes, current))
                    throw new SftpException(SftpFailure.SourceChanged);
                for (int child = 0; child < entry.Children.Count; child++)
                {
                    if (await RemoveAsync(entry.Children[child])) continue;
                    foreach (Deletion remaining in entry.Children.Skip(child + 1)) ReportUnfinished(remaining);
                    entry.Reported = true;
                    AddResult(files.Display(entry.Item.Path), "TransferUnfinished");
                    return false;
                }
                token.ThrowIfCancellationRequested();
                await files.DeleteAsync(entry.Item.Path, expected, token);
                entry.Reported = true;
                AddResult(files.Display(entry.Item.Path), "TransferDeleted");
                return true;
            }
            catch (Exception error) when (TransferBatch.IsFileError(error))
            {
                entry.Reported = true;
                AddResult(files.Display(entry.Item.Path), TransferBatch.ErrorKey(error));
                if (error is SftpException { Failure: SftpFailure.UnknownOutcome or SftpFailure.Disconnected }) throw;
                return false;
            }
        }

        void ReportUnfinished(Deletion entry)
        {
            if (!entry.Reported)
            {
                entry.Reported = true;
                AddResult(files.Display(entry.Item.Path), "TransferUnfinished");
            }
            foreach (Deletion child in entry.Children) ReportUnfinished(child);
        }
    }


    private sealed record Deletion(TransferItem Item, List<Deletion> Children) { public bool Reported; }
}
