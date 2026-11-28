using System.Globalization;

namespace TabTty.Core;

internal sealed partial class FileOperations
{
    private sealed record HistoryEntry(TransferChange[] Changes, bool Move, bool Redo);

    private HistoryEntry? history;
    private TransferRun? activeEffects;

    public bool CanUndo => CanStart && history is { Redo: false };
    public bool CanRedo => CanStart && history is { Redo: true };

    public void ClearHistory() => history = null;

    public Task<string>? Undo(Func<TransferQuestion, ValueTask<TransferChoice>> confirm) => ReplayHistory(false, confirm);
    public Task<string>? Redo(Func<TransferQuestion, ValueTask<TransferChoice>> confirm) => ReplayHistory(true, confirm);

    private Task<string>? ReplayHistory(bool redo, Func<TransferQuestion, ValueTask<TransferChoice>> confirm)
    {
        if (history is not { } entry || entry.Redo != redo) return null;
        var run = new TransferRun(true) { Move = entry.Move, AllowReplacement = false, StopOnFailure = true };
        bool removeCopies = !redo && !entry.Move;
        TransferChange[] actions = !redo && entry.Move ? entry.Changes.Select(change => change.Reverse()).ToArray() : entry.Changes;
        return Start(async token =>
        {
            foreach (TransferChange action in actions)
            {
                await CheckAsync(action.Source, action.Before, token);
                await CheckAsync(action.Target, removeCopies ? action.After : null, token);
            }
            if (removeCopies)
            {
                await UndoCopyAsync(actions, confirm, run, token);
                return;
            }
            foreach (TransferChange action in actions)
            {
                await CheckAsync(action.Source, action.Before, token);
                await CheckAsync(action.Target, null, token);
                if (action.Renamed)
                    await MoveWithHistoryAsync(new(action.Source, null, files.Display(action.Source), action.Before.Attributes),
                        action.Target, run, token, action.Before);
                else
                    await batch.ReplayAsync(action, run, AddResult, Report, token);
            }
        }, transfer: actions.Any(action => !action.Renamed) && !removeCopies, effects: run, nextHistory: effects =>
            removeCopies ? entry with { Redo = true } : new(redo ? effects.Changes.ToArray() :
                effects.Changes.Select(change => change.Reverse()).ToArray(), entry.Move, Redo: !redo));
    }

    private async Task CheckAsync(TransferPath path, TransferSnapshot? expected, CancellationToken token)
    {
        try
        {
            if (expected is not null) await expected.VerifyAsync(files, path, token);
            else if (await files.StatAsync(path, token) is not null) throw new SftpException(SftpFailure.CannotReplace);
        }
        catch (Exception error) when (TransferBatch.IsFileError(error))
        {
            AddResult(files.Display(path), TransferBatch.ErrorKey(error));
            throw;
        }
    }

    private async Task UndoCopyAsync(TransferChange[] changes, Func<TransferQuestion, ValueTask<TransferChoice>> confirm,
        TransferRun run, CancellationToken token)
    {
        bool remote = changes[0].Target.Remote is not null;
        if (remote)
        {
            TransferItem[] copies = changes.Select(CopyItem).ToArray();
            if (await AskAsync(new(TransferQuestionKind.Delete, files.Parent(copies[0].Path), Selection: copies, Permanent: true),
                confirm, token) != TransferChoice.Overwrite) throw new OperationCanceledException();
            foreach (TransferChange change in changes)
            {
                await CheckAsync(change.Source, change.Before, token);
                await CheckAsync(change.Target, change.After, token);
            }
        }
        foreach (TransferChange change in changes)
        {
            await CheckAsync(change.Source, change.Before, token);
            await CheckAsync(change.Target, change.After, token);
            if (!remote)
            {
                try
                {
                    await files.RecycleAsync(change.Target, token);
                    run.Modified = true;
                    AddResult(files.Display(change.Target), "TransferDeleted");
                    continue;
                }
                catch (IOException error) when (error is not SftpException)
                {
                    if (await AskAsync(new(TransferQuestionKind.PermanentDelete, change.Target, CopyItem(change), Permanent: true),
                        confirm, token) != TransferChoice.Overwrite) throw new OperationCanceledException();
                    await CheckAsync(change.Source, change.Before, token);
                    await CheckAsync(change.Target, change.After, token);
                }
                catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
                {
                    AddResult(new(files.Display(change.Target), "SftpUnknownOutcome", FileMutation.Delete));
                    throw;
                }
            }
            await RemoveCopyAsync(change, run, token);
        }

        TransferItem CopyItem(TransferChange change) => new(change.Target, null, files.Display(change.Target), change.After.Attributes);
    }

    private async Task RemoveCopyAsync(TransferChange change, TransferRun run, CancellationToken token)
    {
        // A parent snapshot is still exact before its children are removed.
        await CheckAsync(change.Target, change.After, token);
        foreach (TransferChange child in change.Children) await RemoveCopyAsync(child, run, token);
        if (change.After.Attributes.Kind == TransferKind.Directory && (await files.ListAsync(change.Target, token)).Length != 0)
            throw new SftpException(SftpFailure.SourceChanged);
        try { await files.DeleteMovedSourceAsync(change.Target, change.After.Attributes, token); }
        catch (Exception error) when (TransferBatch.IsFileError(error))
        {
            AddResult(error is SftpException { Failure: SftpFailure.UnknownOutcome }
                ? new(files.Display(change.Target), "SftpUnknownOutcome", FileMutation.Delete)
                : new(files.Display(change.Target), TransferBatch.ErrorKey(error)));
            throw;
        }
        run.Modified = true;
        AddResult(files.Display(change.Target), "TransferDeleted");
    }

    private async Task MoveWithHistoryAsync(TransferItem item, TransferPath target, TransferRun run, CancellationToken token,
        TransferSnapshot? snapshot = null)
    {
        if (run.DescribesChanges && snapshot is null)
        {
            try { snapshot = await TransferSnapshot.CaptureTreeAsync(files, item.Path, token, run.Budget); }
            catch (Exception error) when (!run.StopOnFailure && TransferBatch.IsFileError(error))
            {
                run.DescribesChanges = false;
                run.HistoryUnavailable = true;
            }
        }
        if (snapshot is not null) await snapshot.VerifyAsync(files, item.Path, token);
        try { await RenameItemAsync(item, target, token); }
        catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome)
        {
            run.UnknownSources.Add(item.Path);
            throw;
        }
        if (snapshot is null) return;
        await CheckAsync(target, snapshot, token);
        run.Changes.Add(new(item.Path, target, snapshot, snapshot, [], Renamed: true));
    }

    public async Task<ClipboardEntry[]> CaptureClipboardAsync(TransferItem[] items, CancellationToken token)
    {
        var budget = new TransferSnapshotBudget();
        var captured = new List<ClipboardEntry>(items.Length);
        foreach (TransferItem item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                TransferSnapshot snapshot = await TransferSnapshot.CaptureTreeAsync(files, item.Path, token, budget,
                    allowUnsupportedChildren: true);
                if (ChangedAttributes(item.Attributes, snapshot.Attributes)) throw new SftpException(SftpFailure.SourceChanged);
                captured.Add(new(item, snapshot));
            }
            catch (Exception error) when (TransferBatch.IsFileError(error)) { captured.Add(new(item, null, TransferBatch.ErrorKey(error))); }
        }
        return captured.ToArray();
    }

    public Task<string>? Paste(Task<ClipboardEntry[]> capture, TransferPath target, bool cut, bool sameSide, string copySuffix,
        Func<TransferQuestion, ValueTask<TransferChoice>> confirm, Action<ClipboardEntry[]> retainCut)
    {
        var run = new TransferRun(files.SupportsHistory) { Move = cut, AllowReplacement = !cut || !sameSide };
        return Start(async token =>
        {
            ClipboardEntry[] entries = await capture.WaitAsync(token);
            var accepted = new List<TransferItem>();
            var names = new Dictionary<TransferItem, string>();
            var expected = new Dictionary<string, TransferSnapshot>(StringComparer.Ordinal);
            HashSet<string>? usedNames = null;
            try
            {
                TransferPath destination = await files.ResolveDirectoryAsync(target, token);
                foreach (ClipboardEntry entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.Snapshot is null)
                        {
                            AddResult(entry.Item.Label, entry.Error ?? "SftpSourceChanged");
                            continue;
                        }
                        await entry.Snapshot.VerifyAsync(files, entry.Item.Path, token);
                        TransferItem item = entry.Item with { Attributes = entry.Snapshot.Attributes };
                        if (item.Name is null) throw new SftpException(SftpFailure.InvalidName);
                        if (sameSide && item.Attributes.Kind == TransferKind.Directory &&
                            await IsDirectoryWithinAsync(item.Path, destination, token))
                            throw new SftpException(SftpFailure.InvalidName);
                        if (sameSide && await IsSameDirectoryAsync(files.Parent(item.Path), destination, token))
                        {
                            if (cut) { AddResult(item.Label, "TransferSkipped"); continue; }
                            usedNames ??= new(await files.IsCaseSensitiveAsync(target, token)
                                ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                            names.Add(item, await DuplicateNameAsync(item, target, copySuffix, usedNames, token));
                        }
                        expected.Add(TransferSnapshot.NameOf(item.Path), entry.Snapshot);
                        accepted.Add(item);
                    }
                    catch (Exception error) when (TransferBatch.IsFileError(error)) { AddResult(entry.Item.Label, TransferBatch.ErrorKey(error)); }
                }

                if (accepted.Count == 0) return;
                if (cut && sameSide && !expected.Values.Any(snapshot => snapshot.HasUnsupportedEntries) &&
                    await CanRenameAsync(accepted[0].Path, target, token))
                    await MoveWithinAsync(accepted, target, run, token, expected,
                        item => RunTransferAsync([item], target, confirm, run, token, expected: expected));
                else
                    await RunTransferAsync(accepted.ToArray(), target, confirm, run, token, names, expected);
            }
            finally
            {
                if (cut && !lifetime.IsCancellationRequested)
                {
                    var remaining = new List<ClipboardEntry>();
                    foreach (ClipboardEntry entry in entries)
                    {
                        if (entry.Snapshot is null || run.UnknownSources.Contains(entry.Item.Path)) continue;
                        try
                        {
                            await entry.Snapshot.VerifyAsync(files, entry.Item.Path, lifetime);
                            remaining.Add(entry);
                        }
                        catch (Exception error) when (TransferBatch.IsFileError(error)) { }
                    }
                    retainCut(remaining.ToArray());
                }
            }
        }, transfer: true, effects: run);
    }

    private async Task<bool> CanRenameAsync(TransferPath source, TransferPath target, CancellationToken token)
    {
        if (source.Remote is not null) return true;
        if ((await files.StatAsync(files.Parent(source), token))?.LocalIdentity is { } from &&
            (await files.StatAsync(target, token))?.LocalIdentity is { } to) return from.Volume == to.Volume;
        return string.Equals(Path.GetPathRoot(source.Local), Path.GetPathRoot(target.Local), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsSameDirectoryAsync(TransferPath source, TransferPath target, CancellationToken token)
    {
        if (source.Remote is not null) return (await files.ResolveDirectoryAsync(source, token)).SamePath(target);
        return (await files.StatAsync(source, token))?.LocalIdentity is { } identity &&
            (await files.StatAsync(target, token))?.LocalIdentity == identity || source.SamePath(target);
    }

    private async Task<bool> IsDirectoryWithinAsync(TransferPath source, TransferPath target, CancellationToken token)
    {
        if (source.Remote is not null)
        {
            byte[] root = (await files.ResolveDirectoryAsync(source, token)).Remote!;
            byte[] candidate = target.Remote!;
            return candidate.AsSpan().StartsWith(root) && (root[^1] == '/' || candidate.Length == root.Length || candidate[root.Length] == '/');
        }
        var identity = (await files.StatAsync(source, token))?.LocalIdentity;
        for (string? directory = Path.TrimEndingDirectorySeparator(target.Local!); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var path = new TransferPath(directory);
            if (source.SamePath(path) || identity is not null && (await files.StatAsync(path, token))?.LocalIdentity == identity) return true;
        }
        return false;
    }

    private async Task<string> DuplicateNameAsync(TransferItem item, TransferPath directory, string suffix,
        HashSet<string> used, CancellationToken token)
    {
        string original = item.Name!;
        int dot = item.Attributes.Kind == TransferKind.File ? original.LastIndexOf('.') : -1;
        string stem = dot > 0 ? original[..dot] : original;
        string extension = dot > 0 ? original[dot..] : "";
        for (int number = 1; number <= SftpClient.MaxEntries; number++)
        {
            string sequence = number == 1 ? "" : " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
            string name = stem + suffix + sequence + extension;
            TransferPath path = files.Child(directory, name);
            string key = directory.Remote is not null ? name.Normalize() : name;
            if (used.Contains(key) || await files.StatAsync(path, token) is not null) continue;
            used.Add(key);
            return name;
        }
        throw new SftpException(SftpFailure.Capacity);
    }
}
