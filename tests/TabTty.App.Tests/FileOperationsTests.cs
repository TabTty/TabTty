using System.IO;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckFileOperations(string root)
    {
        string folderPath = Path.Combine(root, "private-transfer-folder");
        using (var parent = new TransferWindowsPath(root))
        {
            parent.CreateDirectory(folderPath);
            if (!PrivateWindowsFile.HasPrivateAccess(new DirectoryInfo(folderPath)))
                throw new InvalidOperationException("A newly created transfer folder lacks its private inheritable ACL.");
            try
            {
                parent.CreateDirectory(folderPath);
                throw new InvalidOperationException("Transfer folder creation silently reused an existing folder.");
            }
            catch (IOException) { }
        }

        if (TransferFiles.NextOffset(5, 3, 8) != 8 || TransferFiles.NextOffset(5, 3, null) != 8)
            throw new InvalidOperationException("A valid transfer byte range was rejected.");
        try
        {
            TransferFiles.NextOffset(5, 4, 8);
            throw new InvalidOperationException("A transfer block exceeded the declared length.");
        }
        catch (SftpException error) when (error.Failure == SftpFailure.SourceChanged) { }

        string stage = Path.Combine(root, "transfer-stage.part"), target = Path.Combine(root, "transfer-target.txt");
        string replacement = Path.Combine(root, "transfer-other.part");
        File.WriteAllText(stage, "staged");
        File.WriteAllText(replacement, "other");
        FileObjectIdentity identity;
        using (var file = File.OpenRead(stage)) identity = FileObjectIdentity.Read(file.SafeFileHandle);
        if (FileObjectIdentity.MoveIfSame(replacement, identity, target, false) ||
            FileObjectIdentity.DeleteIfSame(replacement, identity) || !File.Exists(replacement) ||
            !FileObjectIdentity.MoveIfSame(stage, identity, target, false) || File.ReadAllText(target) != "staged")
            throw new InvalidOperationException("Local transfer publication or cleanup lost the staged file identity.");

        File.WriteAllText(target + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\n");
        using (var file = TransferWindowsPath.CreateDownload(stage))
        {
            identity = FileObjectIdentity.Read(file.SafeFileHandle);
            TransferWindowsPath.MarkDownload(stage, file);
            file.Write("download"u8);
        }
        using (TransferWindowsPath.OpenVerifiedDownloadMark(stage, identity))
            if (!FileObjectIdentity.MoveIfSame(stage, identity, target, replace: true))
                throw new InvalidOperationException("The verified download could not be published.");
        if (File.ReadAllText(target) != "download" ||
            File.ReadAllText(target + ":Zone.Identifier") != "[ZoneTransfer]\r\nZoneId=3\r\n")
            throw new InvalidOperationException("A replaced download did not retain its own Internet zone.");
        using (var published = File.OpenRead(target))
            if (!PrivateWindowsFile.HasPrivateAccess(published))
                throw new InvalidOperationException("A replaced download lost its private access rules.");
        File.WriteAllText(target + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\n");
        try
        {
            using var invalid = TransferWindowsPath.OpenVerifiedDownloadMark(target, identity);
            throw new InvalidOperationException("A changed zone mark passed the download publication check.");
        }
        catch (SftpException error) when (error.Failure == SftpFailure.DownloadOrigin) { }

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

        var unknown = new ControlledFiles { LoseMutation = true };
        var uncertainOwner = new FileOperationOwnership();
        var uncertain = new FileOperations(unknown, uncertainOwner, CancellationToken.None);
        var remote = new TransferPath("/remote"u8.ToArray());
        var original = new TransferItem(new TransferPath("/remote/original"u8.ToArray()), "original", "original", ControlledFiles.File);
        await CheckUnknown(uncertain.Create(remote, "folder"), FileMutation.CreateFolder, "/remote/folder");
        await CheckUnknown(uncertain.Rename(remote, original, "renamed"), FileMutation.Rename, "/remote/original", "/remote/renamed");
        var subtree = new TransferItem(new TransferPath("tree"), "tree", "tree", ControlledFiles.Directory);
        unknown.Children = [original];
        await CheckUnknown(uncertain.Delete(remote, [subtree], _ => ValueTask.FromResult(TransferChoice.Overwrite)),
            FileMutation.Delete, "/remote/original");
        if (unknown.MutationCalls != 3) throw new InvalidOperationException("Refreshing retried an uncertain mutation.");

        async Task CheckUnknown(Task<string>? action, FileMutation kind, string path, string? destination = null)
        {
            if (action is null || await action != "SftpUnknownOutcome" ||
                uncertain.UnresolvedForClose() is not [{ } result] || result != new FileOperationUpdate.Entry(path, "SftpUnknownOutcome", kind, destination))
                throw new InvalidOperationException("An uncertain mutation lost its operation kind or full paths.");
            long stale = uncertainOwner.RefreshRevision!.Value;
            uncertainOwner.RequireRefresh();
            uncertainOwner.Refreshed(stale);
            if (!uncertainOwner.NeedsRefresh) throw new InvalidOperationException("A stale listing cleared a newer unknown outcome.");
            uncertainOwner.Refreshed();
            if (uncertain.UnresolvedForClose() is not [{ } after] || after != result)
                throw new InvalidOperationException("Refreshing changed the retained uncertain mutation result.");
        }

        string sameSizeFile = Path.Combine(root, "same-size.txt");
        File.WriteAllText(sameSizeFile, "equal");
        File.SetLastWriteTimeUtc(sameSizeFile, File.GetLastWriteTimeUtc(replacement));
        var firstFile = TransferWindowsPath.Stat(replacement)!;
        var secondFile = TransferWindowsPath.Stat(sameSizeFile)!;
        if (firstFile.LocalIdentity is null || secondFile.LocalIdentity is null || firstFile.Size != secondFile.Size ||
            firstFile.Modified != secondFile.Modified || firstFile.LocalIdentity == secondFile.LocalIdentity)
            throw new InvalidOperationException("Local status did not distinguish ordinary files with equal metadata.");
        TransferAttributes[] versions = [firstFile with { Size = 1 }, firstFile, secondFile];
        io.CopyTargetVersions = new Queue<TransferAttributes>(versions);
        int overwriteQuestions = 0;
        string copied = await ops.Copy([a], source, question =>
        {
            if (question.Kind != TransferQuestionKind.Overwrite || question.Existing != versions[overwriteQuestions++])
                throw new InvalidOperationException("A changed copy target reused the earlier approval.");
            return ValueTask.FromResult(TransferChoice.Overwrite);
        })!;
        if (copied != "TransferFinished" || overwriteQuestions != versions.Length)
            throw new InvalidOperationException("Copy did not obtain a fresh approval for a changed target.");
        io.CopyTargetVersions.Clear();

        var b = new TransferItem(new TransferPath("b"), "b", "b", ControlledFiles.File);
        var c = new TransferItem(new TransferPath("c"), "c", "c", ControlledFiles.File);
        io.Staged.Clear();
        io.FailStageFor = "b";
        await ops.Copy([a, b, c], source, _ => ValueTask.FromResult(TransferChoice.Overwrite))!;
        if (ops.Results.Count != 3 || !ops.CanRetry || !io.Staged.SequenceEqual(["a", "b", "c"]))
            throw new InvalidOperationException("A partial batch did not retain its results and retryable item.");
        io.Staged.Clear();
        io.FailStageFor = null;
        await ops.Retry(_ => ValueTask.FromResult(TransferChoice.Overwrite))!;
        if (!io.Staged.SequenceEqual(["b"]) || ops.CanRetry || ops.Results.Count != 3 ||
            ops.Results.Any(entry => entry.Reason != "TransferDone"))
            throw new InvalidOperationException("Retry copied completed files or discarded their results.");

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

        var faults = new ControlledFiles { FailManyCleanups = true };
        var reported = new FileOperations(faults, new FileOperationOwnership(), CancellationToken.None);
        var liveResults = reported.Results;
        bool observedDisplacement = false;
        ((System.Collections.Specialized.INotifyCollectionChanged)liveResults).CollectionChanged += (_, change) =>
        {
            observedDisplacement |= change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove;
            if (liveResults.Take(Math.Max(0, liveResults.Count - 1)).Any(entry => entry.Reason == "TransferResultsLimit"))
                throw new InvalidOperationException("The omission notice was followed by a result.");
        };
        var batch = Enumerable.Range(0, 35).Select(index =>
        {
            string name = index == 34 ? "unknown" : index >= 22 ? $"cleanup{index}" : $"ordinary{index}";
            return new TransferItem(new TransferPath(name), name, name, ControlledFiles.File);
        }).ToArray();
        if (await reported.Copy(batch, source, _ => ValueTask.FromResult(TransferChoice.Overwrite))! != "SftpUnknownOutcome")
            throw new InvalidOperationException("The uncertain rename did not stop the transfer.");
        var unresolved = reported.UnresolvedForClose();
        if (unresolved.Length != 13 || unresolved.Count(entry => entry.Reason == "TransferTemporaryRemains") != 12 ||
            unresolved.Count(entry => entry.Reason == "SftpUnknownOutcome" && entry.Path == "out/unknown") != 1 ||
            !ReferenceEquals(liveResults, reported.Results) || !observedDisplacement ||
            liveResults.Last().Reason != "TransferResultsLimit")
            throw new InvalidOperationException("The latest result budget omitted an unresolved path.");
        await CheckMoveSafetyAsync(root);
        Console.WriteLine("PASS CheckFileOperations");
    }

    private static async Task CheckMoveSafetyAsync(string root)
    {
        string local = Path.Combine(root, "move-source.txt");
        File.WriteAllText(local, "original");
        var before = TransferWindowsPath.Stat(local)!;
        File.WriteAllText(local, "modified");
        File.SetLastWriteTimeUtc(local, before.Modified!.Value.UtcDateTime.AddMinutes(1));
        try
        {
            TransferWindowsPath.DeleteMovedSource(local, before);
            throw new InvalidOperationException("Move removed a changed source file.");
        }
        catch (SftpException error) when (error.Failure == SftpFailure.SourceChanged) { }
        if (File.ReadAllText(local) != "modified") throw new InvalidOperationException("Move did not retain the changed source.");

        var stamp = new TransferAttributes(TransferKind.File, 1, DateTimeOffset.UnixEpoch);
        var staged = stamp with { LocalIdentity = (1, Guid.NewGuid()) };
        var source = new TransferItem(new TransferPath("/move"u8.ToArray()), "move", "move", stamp);
        foreach (var (loseReply, sameDestination, choice, removesSource) in new[]
        {
            (false, true, TransferChoice.Overwrite, true),
            (false, true, TransferChoice.Skip, false),
            (false, false, TransferChoice.Overwrite, false),
            (true, true, TransferChoice.Overwrite, false),
        })
        {
            int destinationReads = 0;
            var io = new ControlledFiles
            {
                LoseCommit = loseReply,
                Inspect = path => path.Local is null ? stamp :
                    path.Local == "out/move" && ++destinationReads > 2 && !sameDestination
                        ? staged with { LocalIdentity = (1, Guid.NewGuid()) } : staged,
            };
            var ownership = new FileOperationOwnership();
            var ops = new FileOperations(io, ownership, CancellationToken.None);
            string status = await ops.Move([source], new("out"), _ => ValueTask.FromResult(choice))!;
            if (io.Removed.Contains("/move") != removesSource || ops.CanRetry || ownership.NeedsRefresh != loseReply ||
                status != (loseReply ? "SftpUnknownOutcome" : "TransferFinished"))
                throw new InvalidOperationException("Move removed a source without a verified destination or offered a copy retry.");
        }
        Console.WriteLine("PASS MoveRetainsSourcesOnSkipAndUnverifiedCommit");
    }

    private sealed class ControlledFiles : IFileOperationIo
    {
        public static readonly TransferAttributes File = new(TransferKind.File, 1, null);
        public static readonly TransferAttributes Directory = new(TransferKind.Directory, null, null);
        public TransferItem[] Children = [];
        public List<string> Removed { get; } = [];
        public bool LoseCommit;
        public bool LoseMutation;
        public int MutationCalls;
        public bool FailRecheck;
        public bool FailManyCleanups;
        public string? FailStageFor;
        public List<string> Staged { get; } = [];
        public Queue<TransferAttributes> CopyTargetVersions = new();
        public Func<TransferPath, TransferAttributes?>? Inspect;
        public int RecycleCalls;
        public bool IsOpen => true;
        public string Display(TransferPath path)
        {
            string value = path.Local ?? System.Text.Encoding.UTF8.GetString(path.Remote!);
            return FailManyCleanups && value.StartsWith("out/ordinary", StringComparison.Ordinal)
                ? value + new string('x', 100_000) : value;
        }
        public TransferPath Child(TransferPath path, string name) => new(Display(path) + "/" + name) { ParentPath = path };
        public TransferPath Parent(TransferPath path) => path.ParentPath!;
        public Task<TransferItem[]> ListAsync(TransferPath path, CancellationToken token) => Task.FromResult(Children);
        public Task<TransferAttributes?> StatAsync(TransferPath path, CancellationToken token) =>
            Task.FromResult<TransferAttributes?>(Inspect is not null ? Inspect(path) : path.Local == "/remote/renamed" ? null : path.Local == "tree" ? Directory :
                path.Local == "out/a" && CopyTargetVersions.Count > 0 ?
                    (CopyTargetVersions.Count == 1 ? CopyTargetVersions.Peek() : CopyTargetVersions.Dequeue()) : File);
        public Task<bool> IsCaseSensitiveAsync(TransferPath path, CancellationToken token) => Task.FromResult(true);
        public Task CreateAsync(TransferPath path, CancellationToken token)
        {
            if (LoseMutation) return UnknownMutation();
            return Task.CompletedTask;
        }
        public Task RenameAsync(TransferPath source, TransferPath target, bool replace, CancellationToken token) =>
            LoseMutation ? UnknownMutation() : throw new NotSupportedException();
        public Task<bool> TryCommitLocalAsync(TransferPath source, TransferPath target, TransferAttributes? approved, CancellationToken token) =>
            LoseCommit || FailManyCleanups && target.Local == "out/unknown"
                ? throw new SftpException(SftpFailure.UnknownOutcome) : Task.FromResult(true);
        public Task DeleteAsync(TransferPath path, TransferKind kind, CancellationToken token)
        {
            if (LoseMutation) return UnknownMutation();
            if (path.Local == "tree/fail" || FailManyCleanups && path.Local?.EndsWith(".part") == true)
                throw new SftpException(SftpFailure.Permission);
            Removed.Add(Display(path));
            return Task.CompletedTask;
        }
        public Task DeleteMovedSourceAsync(TransferPath path, TransferAttributes expected, CancellationToken token) =>
            DeleteAsync(path, expected.Kind, token);
        private Task UnknownMutation()
        {
            MutationCalls++;
            throw new SftpException(SftpFailure.UnknownOutcome);
        }
        public Task RecycleAsync(TransferPath path, CancellationToken token)
        {
            RecycleCalls++;
            throw new SftpException(SftpFailure.UnknownOutcome);
        }
        public Task<bool> StageAsync(TransferPath source, TransferPath temporary, TransferAttributes snapshot, Action<long> progress, Action created, CancellationToken token)
        {
            Staged.Add(Display(source));
            if (FailStageFor is not null && source.Local == FailStageFor) throw new IOException("Synthetic staging failure.");
            if (FailManyCleanups && source.Local == "unknown") return Task.FromResult(false);
            created();
            if (FailManyCleanups && source.Local?.StartsWith("cleanup", StringComparison.Ordinal) == true)
                throw new IOException("Synthetic interruption after creating a temporary path.");
            return Task.FromResult(false);
        }
        public Task RecheckAsync(TransferPath source, TransferAttributes expected, long? length, CancellationToken token) =>
            FailRecheck ? throw new IOException("Synthetic recheck failure.") : Task.CompletedTask;
    }
}
