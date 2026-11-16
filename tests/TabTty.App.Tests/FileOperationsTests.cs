using System.IO;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckFileOperations()
    {
        var io = new ControlledFiles();
        var owner = new FileOperationOwnership();
        var ops = new FileOperations(io, owner, CancellationToken.None);
        var a = new TransferItem(new TransferPath("a"), "a", "a", ControlledFiles.File);
        var source = new TransferPath("out");
        var response = new TaskCompletionSource<TransferChoice>();
        Task<string> cancelled = ops.Delete(source, [a], _ => new(response.Task))!;
        ops.Cancel();
        if (await cancelled.WaitAsync(TimeSpan.FromSeconds(5)) != "TransferCancelled" || !ops.CanStart)
            throw new InvalidOperationException("A cancelled confirmation retained admission.");
        response.SetResult(TransferChoice.Overwrite);
        if (io.Removed.Count != 0)
            throw new InvalidOperationException("An obsolete deletion approval reached the provider.");

        var folder = new TransferItem(new TransferPath("tree"), "tree", "tree", ControlledFiles.Directory);
        var other = new TransferItem(new TransferPath("other"), "other", "other", ControlledFiles.File);
        io.Children =
        [
            new(new TransferPath("tree/first"), "first", "first", ControlledFiles.File),
            new(new TransferPath("tree/fail"), "fail", "fail", ControlledFiles.File),
            new(new TransferPath("tree/unfinished"), "unfinished", "unfinished", ControlledFiles.File)
        ];
        await ops.Delete(new TransferPath("root"u8.ToArray()), [folder, other],
            _ => ValueTask.FromResult(TransferChoice.Overwrite))!;
        if (!io.Removed.SequenceEqual(["tree/first", "other"]) ||
            !ops.Results.Any(x => x.Path == "tree/fail" && x.Reason == "SftpPermission") ||
            !ops.Results.Any(x => x.Path == "tree/unfinished" && x.Reason == "TransferUnfinished"))
            throw new InvalidOperationException("Deletion did not preserve the failed tree and continue the other tree.");

        io.FailRecheck = true;
        var questions = new List<TransferQuestionKind>();
        await ops.Delete(source, [a], question =>
        {
            questions.Add(question.Kind);
            return ValueTask.FromResult(TransferChoice.Overwrite);
        })!;
        if (!questions.SequenceEqual([TransferQuestionKind.Delete]) || io.RecycleCalls != 0)
            throw new InvalidOperationException("A failed identity recheck permitted a permanent-delete fallback.");
        io.FailRecheck = false;
        questions.Clear();
        string unclearRecycle = await ops.Delete(source, [a], question =>
        {
            questions.Add(question.Kind);
            return ValueTask.FromResult(TransferChoice.Overwrite);
        })!;
        if (unclearRecycle != "SftpUnknownOutcome" || !questions.SequenceEqual([TransferQuestionKind.Delete]) ||
            io.RecycleCalls != 1 || !owner.NeedsRefresh)
            throw new InvalidOperationException("Unknown recycling permitted another destructive operation.");
        owner.Refreshed();

        int overwriteQuestions = 0;
        io.ChangeCopyTarget = true;
        string copied = await ops.Copy([a], source, question =>
        {
            if (question.Kind != TransferQuestionKind.Overwrite || question.Existing?.Size != ++overwriteQuestions)
                throw new InvalidOperationException("A changed copy target reused the earlier approval.");
            return ValueTask.FromResult(TransferChoice.Overwrite);
        })!;
        if (copied != "TransferFinished" || overwriteQuestions != 2)
            throw new InvalidOperationException("Copy did not obtain a fresh approval for a changed target.");
        io.ChangeCopyTarget = false;

        io.LoseCommit = true;
        if (await ops.Copy([a], source, _ => ValueTask.FromResult(TransferChoice.Overwrite))! != "SftpUnknownOutcome" ||
            ops.CanRetry || !owner.NeedsRefresh || io.Removed.Any(path => path.EndsWith(".part")))
            throw new InvalidOperationException("Uncertain commit effects were retried or cleaned up.");
        owner.Refreshed();
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Track(provider.Task);
        var reopened = new FileOperations(io, owner, CancellationToken.None);
        var reconnected = new FileOperations(io, new FileOperationOwnership(), CancellationToken.None);
        if (reopened.Results.Count != 0 || reconnected.Results.Count != 0 ||
            await reconnected.Create(source, "independent")! != "TransferFinished" || !owner.IsBusy)
            throw new InvalidOperationException("A new view restored prior results or a new connection released old ownership.");
        if (reopened.Create(source, "later") is not null)
            throw new InvalidOperationException("Reopened view admitted a mutation during pending I/O.");
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Changed += () => { if (!owner.IsBusy) retired.TrySetResult(); };
        provider.SetResult();
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (await reopened.Create(source, "later")! != "TransferFinished")
            throw new InvalidOperationException("Provider completion did not reopen admission.");
        Console.WriteLine("PASS CheckFileOperations");
    }

    private sealed class ControlledFiles : IFileOperationIo
    {
        public static readonly TransferAttributes File = new(TransferKind.File, 1, null);
        public static readonly TransferAttributes Directory = new(TransferKind.Directory, null, null);
        public TransferItem[] Children = [];
        public List<string> Removed { get; } = [];
        public bool LoseCommit;
        public bool FailRecheck;
        public bool ChangeCopyTarget;
        private int targetReads;
        public int RecycleCalls;
        public bool IsOpen => true;
        public string Display(TransferPath path) => path.Local ?? System.Text.Encoding.UTF8.GetString(path.Remote!);
        public TransferPath Child(TransferPath path, string name) => new(Display(path) + "/" + name) { ParentPath = path };
        public TransferPath Parent(TransferPath path) => path.ParentPath!;
        public Task<TransferItem[]> ListAsync(TransferPath path, CancellationToken token) => Task.FromResult(Children);
        public Task<TransferAttributes?> StatAsync(TransferPath path, CancellationToken token) =>
            Task.FromResult<TransferAttributes?>(path.Local == "tree" ? Directory :
                path.Local == "out/a" && ChangeCopyTarget ? File with { Size = Math.Min(++targetReads, 2) } : File);
        public Task<bool> IsCaseSensitiveAsync(TransferPath path, CancellationToken token) => Task.FromResult(true);
        public Task CreateAsync(TransferPath path, CancellationToken token) => Task.CompletedTask;
        public Task RenameAsync(TransferPath source, TransferPath target, bool replace, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryCommitLocalAsync(TransferPath source, TransferPath target, TransferAttributes? approved, CancellationToken token) =>
            LoseCommit ? throw new SftpException(SftpFailure.UnknownOutcome) : Task.FromResult(true);
        public Task DeleteAsync(TransferPath path, TransferKind kind, CancellationToken token)
        {
            if (path.Local == "tree/fail") throw new SftpException(SftpFailure.Permission);
            Removed.Add(Display(path));
            return Task.CompletedTask;
        }
        public Task RecycleAsync(TransferPath path, CancellationToken token)
        {
            RecycleCalls++;
            throw new SftpException(SftpFailure.UnknownOutcome);
        }
        public Task<bool> StageAsync(TransferPath source, TransferPath temporary, TransferAttributes snapshot, Action<long> progress, Action created, CancellationToken token)
        {
            created();
            return Task.FromResult(false);
        }
        public Task RecheckAsync(TransferPath source, TransferAttributes expected, long? length, CancellationToken token) =>
            FailRecheck ? throw new IOException("Synthetic recheck failure.") : Task.CompletedTask;
    }
}
