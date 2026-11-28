using System.Collections.ObjectModel;

namespace TabTty.Core;

internal sealed record TransferItem(TransferPath Path, string? Name, string Label, TransferAttributes Attributes);

internal sealed class TransferPath
{
    public string? Local { get; }
    public byte[]? Remote { get; }
    public TransferFileIdentity? DirectoryIdentity { get; set; }
    internal FileObjectIdentity? StagedIdentity { get; set; }
    internal bool DownloadStage { get; set; }
    public TransferPath? ParentPath { get; init; }
    public TransferPath(string local) => Local = local;
    public TransferPath(byte[] remote) => Remote = remote;

    public bool SamePath(TransferPath other) => Local is { } local
        ? other.Local is { } target && string.Equals(Path.TrimEndingDirectorySeparator(local),
            Path.TrimEndingDirectorySeparator(target), StringComparison.Ordinal)
        : other.Remote is { } remote && Remote.AsSpan().SequenceEqual(remote);
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
    Task DeleteMovedSourceAsync(TransferPath path, TransferAttributes expected, CancellationToken token);
    Task RecycleAsync(TransferPath path, CancellationToken token);
    Task<bool> StageAsync(TransferPath source, TransferPath temporary, TransferAttributes snapshot,
        Action<long> progress, Action created, CancellationToken token);
    Task RecheckAsync(TransferPath source, TransferAttributes expected, long? length, CancellationToken token);
    bool SupportsHistory => false;
    Task<byte[]> FingerprintAsync(TransferPath path, TransferAttributes expected, CancellationToken token) =>
        Task.FromException<byte[]>(new SftpException(SftpFailure.Unsupported));
    Task<TransferPath> ResolveDirectoryAsync(TransferPath path, CancellationToken token) => Task.FromResult(path);
}

internal enum TransferQuestionKind { Overwrite, Delete, PermanentDelete, Irreversible }
internal enum FileMutation { CreateFolder, Rename, Delete }
internal sealed record TransferQuestion(TransferQuestionKind Kind, TransferPath Path,
    TransferItem? Source = null, TransferAttributes? Existing = null, IReadOnlyList<TransferItem>? Selection = null,
    CancellationToken Lifetime = default, bool Permanent = false);
internal abstract record FileOperationUpdate
{
    internal sealed record Started : FileOperationUpdate;
    internal sealed record Entry(string Path, string Reason, FileMutation? Mutation = null, string? Destination = null) : FileOperationUpdate
    {
        public string DisplayPath => Destination is null ? Path : $"{Path} → {Destination}";
        internal int DetailLength => Path.Length + Reason.Length + (Destination?.Length ?? 0);
    }
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
    private long refreshGeneration;
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
    public void RequireRefresh() { lock (gate) { needsRefresh = true; refreshGeneration++; } }
    public long? RefreshRevision
    {
        get { lock (gate) return needsRefresh && !active && pending == 0 ? refreshGeneration : null; }
    }
    public void Refreshed()
    {
        if (RefreshRevision is { } observed) Refreshed(observed);
    }
    public void Refreshed(long observed)
    {
        lock (gate)
        {
            if (!needsRefresh || active || pending != 0 || observed != refreshGeneration) return;
            needsRefresh = false;
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

internal sealed partial class FileOperations
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
        => Transfer(source, target, confirm, move: false);

    public Task<string>? Move(TransferItem[] source, TransferPath target, Func<TransferQuestion, ValueTask<TransferChoice>> confirm)
        => Transfer(source, target, confirm, move: true);

    private Task<string>? Transfer(TransferItem[] source, TransferPath target,
        Func<TransferQuestion, ValueTask<TransferChoice>> confirm, bool move)
    {
        var effects = new TransferRun(files.SupportsHistory) { Move = move };
        return Start(token => RunTransferAsync(source, target, confirm, effects, token), transfer: true, effects: effects);
    }

    private Task RunTransferAsync(TransferItem[] source, TransferPath target, Func<TransferQuestion, ValueTask<TransferChoice>> confirm,
        TransferRun effects, CancellationToken token, IReadOnlyDictionary<TransferItem, string>? names = null,
        IReadOnlyDictionary<string, TransferSnapshot>? expected = null) =>
        batch.RunAsync(source, target,
            (item, path, attributes) => AskAsync(new(TransferQuestionKind.Overwrite, path, item, attributes), confirm, token),
            path => WarnIrreversibleAsync(path, confirm, token), AddResult, Report, effects, token, names, expected);

    private static async ValueTask WarnIrreversibleAsync(TransferPath path, Func<TransferQuestion, ValueTask<TransferChoice>> confirm,
        CancellationToken token)
    {
        if (await AskAsync(new(TransferQuestionKind.Irreversible, path), confirm, token) != TransferChoice.Overwrite)
            throw new OperationCanceledException();
    }

    public Task<string>? Retry(Func<TransferQuestion, ValueTask<TransferChoice>> confirm)
    {
        if (!CanRetry) return null;
        var previous = current.ToArray();
        var effects = new TransferRun(files.SupportsHistory);
        return Start(async token =>
        {
            var updated = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                await batch.RetryAsync(
                    (item, path, attributes) => AskAsync(new(TransferQuestionKind.Overwrite, path, item, attributes), confirm, token),
                    path => WarnIrreversibleAsync(path, confirm, token),
                    (path, key) =>
                    {
                        updated.Add(path);
                        AddResult(path, key);
                    }, Report, effects, token);
            }
            finally
            {
                foreach (var entry in previous)
                    if (entry.Reason == Omission) MarkOmitted();
                    else if (IsUnresolved(entry.Reason) || !updated.Contains(entry.Path)) AddResult(entry);
            }
        }, transfer: true, effects: effects);
    }

    public Task<string>? Create(TransferPath parent, string name) => Start(async token =>
    {
        TransferPath path = files.Child(parent, name);
        try { await files.CreateAsync(path, token); }
        catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
        {
            AddResult(new(files.Display(path), "SftpUnknownOutcome", FileMutation.CreateFolder));
            throw;
        }
        activeEffects!.Modified = true;
        AddResult(files.Display(path), "TransferDone");
    });

    public Task<string>? Rename(TransferPath parent, TransferItem item, string name) =>
        Start(token => RenameItemAsync(parent, item, name, token));

    public Task<string>? MoveToFolder(TransferItem[] selection, TransferPath folder)
    {
        var effects = new TransferRun(files.SupportsHistory) { Move = true };
        return Start(token => MoveWithinAsync(selection, folder, effects, token), effects: effects);
    }

    private async Task MoveWithinAsync(IEnumerable<TransferItem> selection, TransferPath folder, TransferRun effects, CancellationToken token,
        IReadOnlyDictionary<string, TransferSnapshot>? expected = null, Func<TransferItem, Task>? remoteFallback = null)
    {
        foreach (TransferItem item in selection)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((item.Path.Local is null) != (folder.Local is null)) throw new SftpException(SftpFailure.Unsupported);
                if (item.Name is null) throw new SftpException(SftpFailure.InvalidName);
                TransferPath target = files.Child(folder, item.Name);
                TransferSnapshot? snapshot = expected?.GetValueOrDefault(TransferSnapshot.NameOf(item.Path));
                try { await MoveWithHistoryAsync(item, target, effects, token, snapshot); }
                catch (SftpException error) when (remoteFallback is not null && item.Path.Remote is not null &&
                    snapshot is not null && error.Failure is SftpFailure.Unsupported or SftpFailure.ServerError)
                {
                    if (await files.StatAsync(target, token) is not null) throw;
                    await snapshot.VerifyAsync(files, item.Path, token);
                    await remoteFallback(item);
                }
            }
            catch (Exception error) when (TransferBatch.IsFileError(error) &&
                error is not SftpException { Failure: SftpFailure.UnknownOutcome or SftpFailure.Disconnected })
            {
                effects.Failed = true;
                AddResult(item.Label, TransferBatch.ErrorKey(error));
            }
        }
    }

    private Task RenameItemAsync(TransferPath parent, TransferItem item, string name, CancellationToken token)
        => RenameItemAsync(item, files.Child(parent, name), token);

    private async Task RenameItemAsync(TransferItem item, TransferPath target, CancellationToken token)
    {
        if (await files.StatAsync(target, token) is not null) throw new SftpException(SftpFailure.ServerError);
        if (await files.StatAsync(item.Path, token) is not { } current || ChangedAttributes(item.Attributes, current))
            throw new SftpException(SftpFailure.SourceChanged);
        try { await files.RenameAsync(item.Path, target, replace: false, token); }
        catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
        {
            AddResult(new(files.Display(item.Path), "SftpUnknownOutcome", FileMutation.Rename, files.Display(target)));
            throw;
        }
        activeEffects!.Modified = true;
        AddResult(files.Display(target), "TransferDone");
    }

    public Task<string>? Delete(TransferPath parent, TransferItem[] items,
        Func<TransferQuestion, ValueTask<TransferChoice>> confirm, bool permanent = false) => Start(async token =>
    {
        TransferItem[] selection = items.ToArray();
        bool direct = permanent || parent.Remote is not null;
        if (direct)
        {
            var verified = new List<TransferItem>(selection.Length);
            foreach (TransferItem item in selection)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    TransferAttributes? actual = await files.StatAsync(item.Path, token);
                    if (actual is null || ChangedAttributes(item.Attributes, actual))
                        throw new SftpException(SftpFailure.SourceChanged);
                    verified.Add(item with { Attributes = actual });
                }
                catch (Exception error) when (TransferBatch.IsFileError(error)) { AddResult(item.Label, TransferBatch.ErrorKey(error)); }
            }
            selection = verified.ToArray();
            if (selection.Length == 0) return;
        }
        if (await AskAsync(new(TransferQuestionKind.Delete, parent, Selection: Array.AsReadOnly(selection), Permanent: direct), confirm, token)
            != TransferChoice.Overwrite) throw new OperationCanceledException();
        await DeleteTreeAsync(direct, selection, confirm, token);
    });

    private Task<string>? Start(Func<CancellationToken, Task> execute, bool transfer = false, TransferRun? effects = null,
        Func<TransferRun, HistoryEntry>? nextHistory = null)
    {
        if (!CanStart || !ownership.TryEnter()) return null;
        if (transfer && !Batches.Wait(0))
        {
            ownership.Leave();
            return Task.FromResult("SftpCapacity");
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var run = effects ?? new TransferRun(false);
        activeEffects = run;
        running = source;
        characters = 0;
        omitted = false;
        if (!transfer) batch.ClearRetry();
        return RunAsync();

        async Task<string> RunAsync()
        {
            bool finished = false;
            try
            {
                current.Clear();
                Changed?.Invoke(new FileOperationUpdate.Started());
                await execute(source.Token);
                finished = true;
                return "TransferFinished";
            }
            catch (OperationCanceledException) { return "TransferCancelled"; }
            catch (Exception error) when (TransferBatch.IsFileError(error))
            {
                if (error is SftpException { Failure: SftpFailure.UnknownOutcome })
                {
                    run.Uncertain = true;
                    ownership.RequireRefresh();
                }
                return TransferBatch.ErrorKey(error);
            }
            finally
            {
                if (lifetime.IsCancellationRequested) history = null;
                else if (run.Modified || run.Uncertain)
                    history = finished && !run.Failed && !run.Uncertain && run.DescribesChanges &&
                        (run.Changes.Count != 0 || nextHistory is not null)
                        ? nextHistory?.Invoke(run) ?? new(run.Changes.ToArray(), run.Move, Redo: false) : null;
                if (finished && run.Modified && run.HistoryUnavailable && !lifetime.IsCancellationRequested)
                    AddResult("", "TransferHistoryUnavailable");
                activeEffects = null;
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

    private void AddResult(string path, string key) => AddResult(new(path, key));

    private void AddResult(FileOperationUpdate.Entry entry)
    {
        bool priority = IsUnresolved(entry.Reason);
        int size = entry.DetailLength;
        if (priority) MakeRoom(1, size);
        if (DetailCount + reservedEntries >= DetailEntries ||
            size > DetailCharacters - Omission.Length - characters - reservedCharacters)
        {
            MarkOmitted();
            return;
        }
        characters += size;
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
            characters -= entry.DetailLength;
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
        (before.Modified.HasValue && after.Modified.HasValue && before.Modified != after.Modified) ||
        (before.LocalIdentity.HasValue && before.LocalIdentity != after.LocalIdentity);

    private async Task DeleteTreeAsync(bool permanent, TransferItem[] items, Func<TransferQuestion, ValueTask<TransferChoice>> confirm, CancellationToken token)
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
                if (!permanent)
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
                        activeEffects!.Modified = true;
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
                        if (error is SftpException { Failure: SftpFailure.UnknownOutcome })
                        {
                            AddResult(new(files.Display(plan.Item.Path), "SftpUnknownOutcome", FileMutation.Delete));
                            throw;
                        }
                        AddResult(plan.Item.Label, TransferBatch.ErrorKey(error));
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
            if (ChangedAttributes(item.Attributes, attributes))
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
                try
                {
                    if ((expected is TransferKind.File or TransferKind.Directory) && entry.Item.Attributes.LocalIdentity is not null)
                        await files.DeleteMovedSourceAsync(entry.Item.Path, entry.Item.Attributes, token);
                    else
                        await files.DeleteAsync(entry.Item.Path, expected, token);
                }
                catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
                {
                    entry.Reported = true;
                    AddResult(new(files.Display(entry.Item.Path), "SftpUnknownOutcome", FileMutation.Delete));
                    throw;
                }
                activeEffects!.Modified = true;
                entry.Reported = true;
                AddResult(files.Display(entry.Item.Path), "TransferDeleted");
                return true;
            }
            catch (Exception error) when (error is not SftpException { Failure: SftpFailure.UnknownOutcome } &&
                TransferBatch.IsFileError(error))
            {
                entry.Reported = true;
                AddResult(files.Display(entry.Item.Path), TransferBatch.ErrorKey(error));
                if (error is SftpException { Failure: SftpFailure.Disconnected }) throw;
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
