using System.IO;

namespace TabTty.Core;

internal enum TransferChoice { Overwrite, Skip, Cancel }

internal sealed class TransferBatch(IFileOperationIo files)
{
    private const int MaxDepth = 128;
    private readonly List<Plan> failed = [];
    private readonly HashSet<Plan> completed = new(ReferenceEqualityComparer.Instance);
    private int entries;
    private long bytes;
    public bool CanRetry => failed.Count != 0;
    public void ClearRetry() => failed.Clear();

    public async Task RunAsync(TransferItem[] source, TransferPath destination,
        Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Action<string, string> result, Action<string, long, long?> progress, CancellationToken token)
    {
        failed.Clear();
        completed.Clear();
        entries = 0;
        bytes = 0;
        Plan[] plan = await PlanAsync(source, destination, 0, result, token);
        await RunPlansAsync(plan, conflict, result, progress, token);
    }

    public async Task RetryAsync(Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Action<string, string> result, Action<string, long, long?> progress, CancellationToken token)
    {
        Plan[] retry = failed.ToArray();
        failed.Clear();
        completed.Clear();
        await RunPlansAsync(retry, conflict, result, progress, token);
    }

    private async Task<Plan[]> PlanAsync(TransferItem[] items, TransferPath target, int depth,
        Action<string, string> result, CancellationToken token)
    {
        if (depth > MaxDepth) throw new SftpException(SftpFailure.Capacity);
        var paths = new List<Plan>();
        foreach (TransferItem item in items)
        {
            token.ThrowIfCancellationRequested();
            bytes += files.Display(item.Path).Length * 2L + 256;
            if (++entries > SftpClient.MaxEntries || bytes > 16 * 1024 * 1024)
                throw new SftpException(SftpFailure.Capacity);
            if (item.Attributes.Kind == TransferKind.Link)
            {
                result(item.Label, "TransferSkippedType");
                continue;
            }
            try
            {
                if (item.Name is null) throw new SftpException(SftpFailure.InvalidName);
                paths.Add(new Plan(item, files.Child(target, item.Name)));
            }
            catch (Exception error) when (IsFileError(error)) { result(item.Label, ErrorKey(error)); }
        }

        var plan = new List<Plan>();
        bool caseSensitive = await files.IsCaseSensitiveAsync(target, token);
        foreach (IGrouping<string, Plan> group in paths.GroupBy(path => path.Target.Local ?? path.Source.Name!.Normalize(),
            caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
        {
            Plan[] siblings = group.ToArray();
            if (siblings.Length > 1)
            {
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
                if (current.Kind is not (TransferKind.File or TransferKind.Directory))
                {
                    result(item.Source.Label, "TransferSkippedType");
                    continue;
                }
                item = item with { Source = item.Source with { Attributes = current } };
                if (current.Kind == TransferKind.Directory)
                {
                    TransferItem[] children = await files.ListAsync(item.Source.Path, token);
                    item = item with { Children = await PlanAsync(children, item.Target, depth + 1, result, token) };
                }
                plan.Add(item);
            }
            catch (SftpException error) when (error.Failure == SftpFailure.Capacity) { throw; }
            catch (Exception error) when (IsFileError(error)) { result(item.Source.Label, ErrorKey(error)); }
        }
        return plan.ToArray();
    }

    private async Task RunPlansAsync(Plan[] plan, Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Action<string, string> result, Action<string, long, long?> progress, CancellationToken token)
    {
        for (int index = 0; index < plan.Length; index++)
        {
            Plan item = plan[index];
            try
            {
                token.ThrowIfCancellationRequested();
                TransferAttributes current = await files.StatAsync(item.Source.Path, token)
                    ?? throw new SftpException(SftpFailure.Missing);
                if (item.Source.Attributes.Kind != current.Kind) throw new SftpException(SftpFailure.SourceChanged);
                if (current.Kind == TransferKind.Directory)
                {
                    TransferAttributes? target = await files.StatAsync(item.Target, token);
                    if (target is null) await files.CreateAsync(item.Target, token);
                    else if (target.Kind != TransferKind.Directory) throw new SftpException(SftpFailure.InvalidName);
                    result(files.Display(item.Target), "TransferDone");
                    completed.Add(item);
                    await RunPlansAsync(item.Children!, conflict, result, progress, token);
                }
                else
                {
                    await CopyAsync(item with { Source = item.Source with { Attributes = current } }, conflict, result, progress, token);
                    completed.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                foreach (Plan remaining in plan[index..]) ReportTree(remaining, result);
                throw;
            }
            catch (Exception error) when (IsFileError(error))
            {
                if (!completed.Contains(item))
                {
                    result(files.Display(item.Target), ErrorKey(error));
                    completed.Add(item);
                    if (error is not SftpException { Failure: SftpFailure.UnknownOutcome }) failed.Add(item);
                }
                if (item.Children is { } children)
                    foreach (Plan child in children) ReportTree(child, result);
                if (error is SftpException { Failure: SftpFailure.Disconnected or SftpFailure.Timeout or SftpFailure.InvalidReply or SftpFailure.UnknownOutcome })
                {
                    foreach (Plan remaining in plan[(index + 1)..]) ReportTree(remaining, result);
                    throw;
                }
            }
        }
    }

    private async Task CopyAsync(Plan item, Func<TransferItem, TransferPath, TransferAttributes, ValueTask<TransferChoice>> conflict,
        Action<string, string> result, Action<string, long, long?> progress, CancellationToken token)
    {
        string label = files.Display(item.Target);
        TransferAttributes? approval = await files.StatAsync(item.Target, token);
        if (approval is not null && !await ApproveAsync(approval)) { result(label, "TransferSkipped"); return; }
        TransferPath temporary = files.Child(files.Parent(item.Target), $".tabtty-{Guid.NewGuid():N}.part");
        bool created = false;
        try
        {
            progress(label, 0, item.Source.Attributes.Size);
            bool warning = await files.StageAsync(item.Source.Path, temporary, item.Source.Attributes,
                count => progress(label, count, item.Source.Attributes.Size), () => created = true, token);
            while (true)
            {
                await files.RecheckAsync(item.Source.Path, item.Source.Attributes, null, token);
                TransferAttributes? now = await files.StatAsync(item.Target, token);
                if (now is not null && (approval is null || ConflictChanged(approval, now)))
                {
                    if (!await ApproveAsync(now)) { result(label, "TransferSkipped"); return; }
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
                result(label, warning ? "TransferMetadataWarning" : "TransferDone");
                return;
            }
        }
        catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
        {
            if (created) result(files.Display(temporary), "TransferTemporaryRemains");
            created = false;
            throw;
        }
        finally
        {
            if (created)
            {
                try
                {
                    if (await files.StatAsync(temporary, CancellationToken.None) is { } remaining)
                    {
                        if (remaining.Kind == TransferKind.File)
                            await files.DeleteAsync(temporary, TransferKind.File, CancellationToken.None);
                        else result(files.Display(temporary), "TransferTemporaryRemains");
                    }
                }
                catch (Exception error) when (IsFileError(error)) { result(files.Display(temporary), "TransferTemporaryRemains"); }
            }
        }

        async ValueTask<bool> ApproveAsync(TransferAttributes existing)
        {
            if (existing.Kind != TransferKind.File) throw new SftpException(SftpFailure.InvalidName);
            TransferChoice choice = await conflict(item.Source, item.Target, existing);
            token.ThrowIfCancellationRequested();
            if (choice == TransferChoice.Cancel) throw new OperationCanceledException();
            return choice == TransferChoice.Overwrite;
        }
    }

    private static bool ConflictChanged(TransferAttributes approved, TransferAttributes current) =>
        approved.Kind != current.Kind || approved.Size != current.Size || approved.Modified != current.Modified;

    private void ReportTree(Plan plan, Action<string, string> result)
    {
        if (completed.Add(plan)) result(files.Display(plan.Target), "TransferUnfinished");
        if (plan.Children is { } children)
            foreach (Plan child in children) ReportTree(child, result);
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

    private sealed record Plan(TransferItem Source, TransferPath Target, Plan[]? Children = null);
}
