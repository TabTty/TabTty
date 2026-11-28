using System.IO;

namespace TabTty.Core;

internal enum TransferChoice { Overwrite, Skip, Cancel }

internal sealed class TransferBatch(IFileOperationIo files, Func<string, string, IDisposable>? reserveUnresolved = null)
{
    private const int MaxDepth = 128;
    private readonly List<Plan> failed = [];
    public bool CanRetry => failed.Count != 0;
    public void ClearRetry() => failed.Clear();

    public async Task RunAsync(TransferItem[] source, TransferPath destination,
        Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Func<TransferPath, ValueTask> warn, Action<string, string> result, Action<string, long, long?> progress,
        TransferRun run, CancellationToken token, IReadOnlyDictionary<TransferItem, string>? names = null,
        IReadOnlyDictionary<string, TransferSnapshot>? expected = null)
    {
        failed.Clear();
        var traversal = new Traversal(run, warn);
        Plan[] plan = await PlanAsync(source, destination, 0, traversal, result, token, names, expected);
        await WarnBeforeChangesAsync(plan, traversal, token);
        await RunPlansAsync(plan, traversal, conflict, result, progress, run.Changes, token);
    }

    public async Task RetryAsync(Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Func<TransferPath, ValueTask> warn, Action<string, string> result, Action<string, long, long?> progress,
        TransferRun run, CancellationToken token)
    {
        Plan[] retry = failed.ToArray();
        failed.Clear();
        var traversal = new Traversal(run, warn);
        await WarnBeforeChangesAsync(retry, traversal, token);
        await RunPlansAsync(retry, traversal, conflict, result, progress, run.Changes, token);
    }

    public Task ReplayAsync(TransferChange change, TransferRun run, Action<string, string> result,
        Action<string, long, long?> progress, CancellationToken token) =>
        RunPlansAsync([ReplayPlan(change)], new Traversal(run, _ => ValueTask.CompletedTask),
            (_, _, _) => ValueTask.FromResult(TransferChoice.Cancel), result, progress, run.Changes, token);

    private Plan ReplayPlan(TransferChange change, TransferPath? parent = null)
    {
        parent ??= files.Parent(change.Target);
        // A replay creates new objects at these names, including new directory identities.
        TransferPath target = change.Target.Local is { } local
            ? new TransferPath(local) { ParentPath = parent }
            : new TransferPath(change.Target.Remote!) { ParentPath = parent };
        return new(new TransferItem(change.Source, null, "", change.Before.Attributes), target,
            change.Before.Attributes.Kind == TransferKind.Directory ? change.Children.Select(child => ReplayPlan(child, target)).ToArray() : null,
            change.Before.Members, RequireNew: true, Expected: change.Before);
    }

    private async Task WarnBeforeChangesAsync(Plan[] plan, Traversal traversal, CancellationToken token)
    {
        if (!files.SupportsHistory || !traversal.Run.AllowReplacement) return;
        TransferPath? first = null;
        await FindCollisionsAsync(plan);
        if (first is not null) await traversal.Warn(first);

        async Task FindCollisionsAsync(Plan[] children)
        {
            foreach (Plan item in children)
            {
                token.ThrowIfCancellationRequested();
                TransferAttributes? target;
                try { target = await files.StatAsync(item.Target, token); }
                catch (Exception error) when (IsFileError(error)) { continue; }
                if (item.RequireNew || target is null || target.Kind != item.Source.Attributes.Kind) continue;
                traversal.Approved[item.Target] = target;
                first ??= item.Target;
                if (item.Children is { } nested) await FindCollisionsAsync(nested);
            }
        }
    }

    private async Task<Plan[]> PlanAsync(TransferItem[] items, TransferPath target, int depth, Traversal traversal,
        Action<string, string> result, CancellationToken token, IReadOnlyDictionary<TransferItem, string>? names = null,
        IReadOnlyDictionary<string, TransferSnapshot>? expected = null)
    {
        if (depth > MaxDepth) throw new SftpException(SftpFailure.Capacity);
        var paths = new List<Plan>();
        foreach (TransferItem item in items)
        {
            token.ThrowIfCancellationRequested();
            traversal.Bytes += files.Display(item.Path).Length * 2L + 256;
            if (++traversal.Entries > SftpClient.MaxEntries || traversal.Bytes > 16 * 1024 * 1024)
                throw new SftpException(SftpFailure.Capacity);
            if (item.Attributes.Kind == TransferKind.Link)
            {
                result(item.Label, "TransferSkippedType");
                continue;
            }
            try
            {
                if (item.Name is null) throw new SftpException(SftpFailure.InvalidName);
                string? duplicate = names?.GetValueOrDefault(item);
                string destinationName = duplicate ?? item.Name;
                paths.Add(new Plan(item, files.Child(target, destinationName), RequireNew: duplicate is not null,
                    Expected: expected?.GetValueOrDefault(TransferSnapshot.NameOf(item.Path)), NameKey: destinationName.Normalize()));
            }
            catch (Exception error) when (IsFileError(error)) { traversal.Run.Failed = true; result(item.Label, ErrorKey(error)); }
        }

        var plan = new List<Plan>();
        bool caseSensitive = await files.IsCaseSensitiveAsync(target, token);
        foreach (IGrouping<string, Plan> group in paths.GroupBy(path => path.Target.Local ?? path.NameKey!,
            caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
        {
            Plan[] siblings = group.ToArray();
            if (siblings.Length > 1)
            {
                traversal.Run.Failed = true;
                foreach (Plan sibling in siblings) result(sibling.Source.Label, "TransferCollision");
                continue;
            }
            Plan item = siblings[0];
            try
            {
                TransferAttributes? current = await files.StatAsync(item.Source.Path, token);
                if (current is null) throw new SftpException(SftpFailure.Missing);
                if (item.Source.Attributes.Kind != TransferKind.Unknown && current.Kind != item.Source.Attributes.Kind)
                    throw new SftpException(SftpFailure.SourceChanged);
                if (item.Expected is { } snapshot && !TransferAttributes.SameOverwriteTarget(snapshot.Attributes, current))
                    throw new SftpException(SftpFailure.SourceChanged);
                if (traversal.Run.Move && !StillMatchesListing(item.Source.Attributes, current))
                    throw new SftpException(SftpFailure.SourceChanged);
                if (current.Kind is not (TransferKind.File or TransferKind.Directory))
                {
                    result(item.Source.Label, "TransferSkippedType");
                    continue;
                }
                item = item with { Source = item.Source with { Attributes = current } };
                if (current.Kind == TransferKind.Directory)
                {
                    TransferItem[] children = await files.ListAsync(item.Source.Path, token);
                    string[]? members = files.SupportsHistory ? children.Select(child => TransferSnapshot.NameOf(child.Path)).ToArray() : null;
                    if (item.Expected?.Members is { } previous && (members is null || !previous.ToHashSet(StringComparer.Ordinal).SetEquals(members)))
                        throw new SftpException(SftpFailure.SourceChanged);
                    item = item with { Children = await PlanAsync(children, item.Target, depth + 1, traversal, result, token,
                        expected: item.Expected?.Children), Members = members };
                }
                plan.Add(item);
            }
            catch (SftpException error) when (error.Failure == SftpFailure.Capacity) { throw; }
            catch (Exception error) when (IsFileError(error)) { traversal.Run.Failed = true; result(item.Source.Label, ErrorKey(error)); }
        }
        return plan.ToArray();
    }

    private async Task RunPlansAsync(Plan[] plan, Traversal traversal,
        Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Action<string, string> result, Action<string, long, long?> progress, List<TransferChange> changes, CancellationToken token)
    {
        for (int index = 0; index < plan.Length; index++)
        {
            Plan item = plan[index];
            try
            {
                token.ThrowIfCancellationRequested();
                TransferAttributes current = await files.StatAsync(item.Source.Path, token)
                    ?? throw new SftpException(SftpFailure.Missing);
                if (item.Source.Attributes.Kind != current.Kind ||
                    (traversal.Run.Move || item.Expected is not null) && !TransferAttributes.SameOverwriteTarget(item.Source.Attributes, current))
                    throw new SftpException(SftpFailure.SourceChanged);
                CopyCommit? commit;
                var children = new List<TransferChange>();
                if (current.Kind == TransferKind.Directory)
                {
                    TransferAttributes? target = await files.StatAsync(item.Target, token);
                    if (target is null)
                    {
                        await files.CreateAsync(item.Target, token);
                        traversal.Run.Modified = true;
                    }
                    else
                    {
                        if (target.Kind != TransferKind.Directory) throw new SftpException(SftpFailure.InvalidName);
                        await ApproveReplacementAsync(item, target, traversal);
                        traversal.Run.DescribesChanges = false;
                    }
                    result(files.Display(item.Target), "TransferDone");
                    traversal.Reported.Add(item);
                    TransferAttributes? created = traversal.Run.Move || traversal.Run.DescribesChanges
                        ? target ?? await files.StatAsync(item.Target, token) : null;
                    TransferSnapshot? before = await SnapshotAsync(item.Source.Path, current, traversal, token);
                    await RunPlansAsync(item.Children!, traversal, conflict, result, progress, children, token);
                    TransferSnapshot? after = null;
                    if (before is not null && created is not null && traversal.Run.DescribesChanges)
                    {
                        TransferAttributes? finalized = await files.StatAsync(item.Target, token);
                        if (finalized?.Kind != TransferKind.Directory || finalized.LocalIdentity != created.LocalIdentity)
                            throw new SftpException(SftpFailure.SourceChanged);
                        after = await SnapshotAsync(item.Target, finalized, traversal, token);
                        before = AddChildren(before, children, source: true, item.Members);
                        if (after is not null) after = AddChildren(after, children, source: false, null);
                    }
                    if (traversal.Run.DescribesChanges && after is null) DisableHistory(traversal);
                    commit = new CopyCommit(created, before, after);
                }
                else
                {
                    commit = await CopyAsync(item with { Source = item.Source with { Attributes = current } },
                        traversal, conflict, result, progress, token);
                    traversal.Reported.Add(item);
                }
                if (traversal.Run.Move && !await FinishMoveAsync(item, commit, result, token) && commit is not null)
                {
                    traversal.Run.Failed = true;
                    if (traversal.Run.StopOnFailure) throw new SftpException(SftpFailure.SourceChanged);
                }
                if (commit is { Before: { } savedSource, After: { } savedTarget } && traversal.Run.DescribesChanges)
                    changes.Add(new TransferChange(item.Source.Path, item.Target, savedSource, savedTarget, children.ToArray()));
            }
            catch (OperationCanceledException)
            {
                foreach (Plan remaining in plan[index..]) ReportTree(remaining, traversal, result);
                throw;
            }
            catch (Exception error) when (IsFileError(error))
            {
                traversal.Run.Failed = true;
                if (error is SftpException { Failure: SftpFailure.UnknownOutcome })
                {
                    traversal.Run.Uncertain = true;
                    traversal.Run.UnknownSources.Add(item.Source.Path);
                }
                if (traversal.Reported.Add(item))
                {
                    result(files.Display(item.Target), ErrorKey(error));
                    if (!traversal.Run.Move && !traversal.Run.StopOnFailure && error is not SftpException { Failure: SftpFailure.UnknownOutcome }) failed.Add(item);
                }
                if (item.Children is { } children)
                    foreach (Plan child in children) ReportTree(child, traversal, result);
                if (traversal.Run.StopOnFailure || error is SftpException { Failure: SftpFailure.Disconnected or SftpFailure.Timeout or SftpFailure.InvalidReply or SftpFailure.UnknownOutcome })
                {
                    foreach (Plan remaining in plan[(index + 1)..]) ReportTree(remaining, traversal, result);
                    throw;
                }
            }
        }
    }

    private async Task<CopyCommit?> CopyAsync(Plan item, Traversal traversal,
        Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Action<string, string> result, Action<string, long, long?> progress, CancellationToken token)
    {
        string label = files.Display(item.Target);
        TransferAttributes? approval = await files.StatAsync(item.Target, token);
        if (approval is not null && !await ApproveAsync(approval)) { result(label, "TransferSkipped"); return null; }
        TransferPath temporary = files.Child(files.Parent(item.Target), $".tabtty-{Guid.NewGuid():N}.part");
        using var detail = reserveUnresolved?.Invoke(files.Display(temporary), label);
        bool created = false;
        try
        {
            TransferSnapshot? source = item.Expected ?? await SnapshotAsync(item.Source.Path, item.Source.Attributes, traversal, token);
            progress(label, 0, item.Source.Attributes.Size);
            bool warning = await files.StageAsync(item.Source.Path, temporary, item.Source.Attributes,
                count => progress(label, count, item.Source.Attributes.Size), () => created = true, token);
            TransferAttributes? staged = (!warning && traversal.Run.Move || source is not null)
                ? await files.StatAsync(temporary, token) : null;
            TransferSnapshot? target = staged is not null && source is not null ? await SnapshotAsync(temporary, staged, traversal, token) : null;
            if (item.Expected is { Fingerprint: not null } && target is null)
            {
                if (staged is null) throw new SftpException(SftpFailure.SourceChanged);
                target = await TransferSnapshot.CaptureAsync(files, temporary, staged, token);
            }
            if (target?.Fingerprint is { } fingerprint && !fingerprint.AsSpan().SequenceEqual(source!.Fingerprint))
                throw new SftpException(SftpFailure.SourceChanged);
            if (traversal.Run.DescribesChanges && target is null) DisableHistory(traversal);
            while (true)
            {
                await files.RecheckAsync(item.Source.Path, item.Source.Attributes, null, token);
                TransferAttributes? now = await files.StatAsync(item.Target, token);
                if (now is not null && !TransferAttributes.SameOverwriteTarget(approval, now))
                {
                    if (!await ApproveAsync(now)) { result(label, "TransferSkipped"); return null; }
                    approval = now;
                    continue;
                }
                token.ThrowIfCancellationRequested();
                if (item.Target.Local is not null)
                {
                    if (!await files.TryCommitLocalAsync(temporary, item.Target, now, token)) continue;
                }
                else
                {
                    try { await files.RenameAsync(temporary, item.Target, now is not null, token); }
                    catch (SftpException error) when (now is null && error.Failure == SftpFailure.ServerError)
                    {
                        if (await files.StatAsync(item.Target, token) is not null) continue;
                        throw;
                    }
                }
                created = false;
                traversal.Run.Modified = true;
                if (now is not null) traversal.Run.DescribesChanges = false;
                detail?.Dispose();
                result(label, warning ? "TransferMetadataWarning" : "TransferDone");
                return new CopyCommit(warning ? null : staged, source, target);
            }
        }
        catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
        {
            detail?.Dispose();
            if (created) ReportTemporary();
            created = false;
            throw;
        }
        finally
        {
            if (created)
            {
                detail?.Dispose();
                try
                {
                    if (await files.StatAsync(temporary, CancellationToken.None) is { } remaining)
                    {
                        if (remaining.Kind == TransferKind.File)
                            await files.DeleteAsync(temporary, TransferKind.File, CancellationToken.None);
                        else ReportTemporary();
                    }
                }
                catch (Exception error) when (IsFileError(error)) { ReportTemporary(); }
            }
        }

        async ValueTask<bool> ApproveAsync(TransferAttributes existing)
        {
            if (existing.Kind != TransferKind.File) throw new SftpException(SftpFailure.InvalidName);
            await ApproveReplacementAsync(item, existing, traversal);
            TransferChoice choice = await conflict(item.Source, item.Target, existing);
            token.ThrowIfCancellationRequested();
            if (choice == TransferChoice.Cancel) throw new OperationCanceledException();
            return choice == TransferChoice.Overwrite;
        }

        void ReportTemporary()
        {
            traversal.Run.Uncertain = true;
            result(files.Display(temporary), "TransferTemporaryRemains");
        }
    }

    private async Task<bool> FinishMoveAsync(Plan item, CopyCommit? commit, Action<string, string> result, CancellationToken token)
    {
        var source = item.Source;
        string path = files.Display(source.Path);
        try
        {
            if (commit?.Staged is not { } staged || !await DestinationMatchesAsync(staged) ||
                source.Attributes.Kind == TransferKind.Directory && (await files.ListAsync(source.Path, token)).Length != 0)
            {
                result(path, "TransferMoveSourceRetained");
                return false;
            }
            if (source.Attributes.Kind == TransferKind.File)
            {
                if (commit.Before is not null) await commit.Before.VerifyAsync(files, source.Path, token);
                if (commit.After is not null) await commit.After.VerifyAsync(files, item.Target, token);
            }
            token.ThrowIfCancellationRequested();
            await files.DeleteMovedSourceAsync(source.Path, source.Attributes, token);
            result(path, "TransferMoved");
            return true;
        }
        catch (OperationCanceledException)
        {
            result(path, "TransferMoveSourceRetained");
            throw;
        }
        catch (Exception error) when (IsFileError(error))
        {
            result(path, ErrorKey(error));
            if (error is SftpException { Failure: SftpFailure.UnknownOutcome or SftpFailure.Disconnected or SftpFailure.Timeout or SftpFailure.InvalidReply })
                throw;
            result(path, "TransferMoveSourceRetained");
            return false;
        }

        async Task<bool> DestinationMatchesAsync(TransferAttributes expected)
        {
            if (item.Target.Local is not null && expected.LocalIdentity is null) return false;
            if (source.Attributes.Kind == TransferKind.File &&
                (source.Attributes.Size is null || source.Attributes.Modified is null || expected.Size != source.Attributes.Size)) return false;
            TransferAttributes? actual = await files.StatAsync(item.Target, token);
            return actual is not null && (source.Attributes.Kind == TransferKind.Directory
                ? actual.Kind == TransferKind.Directory && actual.LocalIdentity == expected.LocalIdentity
                : TransferAttributes.SameOverwriteTarget(expected, actual));
        }
    }

    private async ValueTask ApproveReplacementAsync(Plan item, TransferAttributes existing, Traversal traversal)
    {
        if (item.RequireNew || !traversal.Run.AllowReplacement) throw new SftpException(SftpFailure.CannotReplace);
        if (!files.SupportsHistory || traversal.Approved.TryGetValue(item.Target, out var previously) &&
            TransferAttributes.SameOverwriteTarget(previously, existing)) return;
        await traversal.Warn(item.Target);
        traversal.Approved[item.Target] = existing;
    }

    private async Task<TransferSnapshot?> SnapshotAsync(TransferPath path, TransferAttributes attributes, Traversal traversal,
        CancellationToken token)
    {
        if (!traversal.Run.DescribesChanges) return null;
        try
        {
            traversal.Run.Budget.Add(path);
            return await TransferSnapshot.CaptureAsync(files, path, attributes, token);
        }
        catch (Exception error) when (!traversal.Run.StopOnFailure && IsFileError(error))
        {
            DisableHistory(traversal);
            return null;
        }
    }

    private static void DisableHistory(Traversal traversal)
    {
        traversal.Run.DescribesChanges = false;
        traversal.Run.HistoryUnavailable = true;
    }

    private static TransferSnapshot AddChildren(TransferSnapshot snapshot, List<TransferChange> children, bool source, string[]? members)
    {
        var entries = children.ToDictionary(child => TransferSnapshot.NameOf(source ? child.Source : child.Target),
            child => source ? child.Before : child.After, StringComparer.Ordinal);
        return snapshot with { Members = members ?? entries.Keys.ToArray(), Children = entries };
    }

    private static bool StillMatchesListing(TransferAttributes listed, TransferAttributes current) =>
        (listed.Size is null || listed.Size == current.Size) &&
        (listed.Modified is null || listed.Modified == current.Modified) &&
        (listed.LocalIdentity is null || listed.LocalIdentity == current.LocalIdentity);

    private void ReportTree(Plan plan, Traversal traversal, Action<string, string> result)
    {
        if (traversal.Reported.Add(plan)) result(files.Display(plan.Target), "TransferUnfinished");
        if (plan.Children is { } children)
            foreach (Plan child in children) ReportTree(child, traversal, result);
    }

    public static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidDataException or System.Net.Sockets.SocketException or TimeoutException or System.Runtime.InteropServices.COMException;

    public static string ErrorKey(Exception error) => error switch
    {
        SftpException sftp => "Sftp" + sftp.Failure,
        UnauthorizedAccessException => "SftpPermission",
        TimeoutException => "SftpTimeout",
        _ => "SftpServerError",
    };

    private sealed record Plan(TransferItem Source, TransferPath Target, Plan[]? Children = null,
        string[]? Members = null, bool RequireNew = false, TransferSnapshot? Expected = null, string? NameKey = null);

    private sealed record CopyCommit(TransferAttributes? Staged, TransferSnapshot? Before, TransferSnapshot? After);

    private sealed class Traversal(TransferRun run, Func<TransferPath, ValueTask> warn)
    {
        public TransferRun Run { get; } = run;
        public Func<TransferPath, ValueTask> Warn { get; } = warn;
        public readonly Dictionary<TransferPath, TransferAttributes> Approved = [];
        public readonly HashSet<Plan> Reported = new(ReferenceEqualityComparer.Instance);
        public int Entries;
        public long Bytes;
    }
}
