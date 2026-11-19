using System.Security.Cryptography;

namespace TabTty.Core;

internal enum FilePublicationStatus { Verified, Unchanged, Conflict, UnverifiedPreservation }
internal enum FileImageStatus { Missing, Candidate, Original, Other, Unavailable }

internal sealed record FilePublicationResult(FilePublicationStatus Status, FileImageStatus Target,
    FileImageStatus Preservation, string TargetPath, string? PreservationPath, string CandidatePath,
    Exception? Error, Guid OperationId, FileImageStatus Candidate = FileImageStatus.Unavailable,
    Exception? CleanupError = null, bool EvidenceRetired = false);

internal sealed class FilePublicationException(string message, FilePublicationResult result) : IOException(message, result.Error)
{
    internal FilePublicationResult Result { get; } = result;
}

internal sealed record FilePublicationRequest(string Target, byte[] Candidate, byte[]? OriginalDigest,
    string? Preservation, int MaximumBytes, Func<string, FileStream>? Create = null, Action<string>? Validate = null,
    bool Recovery = false, bool CreateDirectory = false, Action<string, string, string?>? Issue = null);

internal enum FilePublicationPhase { Preparing, MutationIssued, Retired }

internal sealed class FilePublicationOperation(Guid id)
{
    private readonly TaskCompletionSource<FilePublicationResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int phase;

    internal Guid Id { get; } = id;
    internal FilePublicationPhase Phase => (FilePublicationPhase)Volatile.Read(ref phase);
    internal Task ActualRelease => completion.Task;
    internal Task<FilePublicationResult> ObserveAsync(CancellationToken observation = default) =>
        completion.Task.WaitAsync(observation);

    internal void MarkIssued() => Volatile.Write(ref phase, (int)FilePublicationPhase.MutationIssued);

    internal void Settle(FilePublicationResult? outcome, Exception? failure)
    {
        Volatile.Write(ref phase, (int)FilePublicationPhase.Retired);
        if (failure is OperationCanceledException stopped) completion.SetCanceled(stopped.CancellationToken);
        else if (failure is not null) completion.SetException(failure);
        else completion.SetResult(outcome!);
    }
}

internal static class VerifiedFilePublication
{
    private const int MaxActive = 4;
    private const int MaxOutstanding = 64;
    private static readonly object Gate = new();
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Evidence> Unresolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Guid, RetainedStage> RetainedStages = new();
    private static readonly Dictionary<string, PendingArchive> Archives =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ResourceQuota CandidateBytes = new(96L * 1024 * 1024, collectOnPressure: false);
    private static readonly ResourceQuota ArtifactBytes = new(256L * 1024 * 1024,
        message: "The file publication artifact budget reached its limit.", collectOnPressure: false);
    private static int activeOperations;
    private static int unresolvedRecords;

    private sealed record Evidence(Guid Id, string Target, string? Preservation, string Stage,
        byte[] CandidateDigest, byte[]? OriginalDigest, int Maximum, Exception? Error, HashSet<string> Namespace,
        string[] TargetAliases, FileObjectIdentity CandidateIdentity, FileObjectIdentity? OriginalIdentity = null,
        bool RequireInstalledIdentity = false, string[]? BeforeIssueTarget = null, Exception? CleanupError = null,
        Evidence? Previous = null, ResourceReservation? Artifacts = null, long CandidateLength = 0)
    {
        internal IEnumerable<Evidence> Records()
        {
            for (Evidence? item = this; item is not null; item = item.Previous) yield return item;
        }
    }

    internal sealed record FileArchiveEvidence(Guid OperationId, string Source, string Destination, VaultFileEvidence Image);
    private sealed record PendingArchive(FileArchiveEvidence Evidence, HashSet<string> Names, ResourceReservation? Artifacts);
    private sealed record RetainedStage(Guid Id, string Target, string Path, FileObjectIdentity Identity,
        HashSet<string> Names, ResourceReservation? Artifacts);

    private sealed class NamespaceLease(HashSet<string> names) : IDisposable
    {
        private ResourceReservation? artifacts;
        internal HashSet<string> Names => names;
        internal long ArtifactLength => artifacts?.Size ?? 0;

        internal void ReserveArtifacts(long bytes)
        {
            if (bytes != 0) artifacts = ArtifactBytes.Reserve(bytes);
        }

        internal ResourceReservation? TakeArtifacts() => Interlocked.Exchange(ref artifacts, null);

        internal void AddPhysicalNames(string[] paths, bool recovery, bool archiveReinspection = false)
        {
            var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                if (FileObjectIdentity.NamespaceKeys(path).Any(key => !physical.Add(key)))
                    throw new IOException("Publication paths alias one another.");

            lock (Gate)
            {
                if (physical.Overlaps(Active) || IntersectsUnresolved(physical, paths[0], recovery, archiveReinspection))
                    throw new IOException("The publication aliases an active or unresolved file operation.");
                Active.UnionWith(physical);
                names.UnionWith(physical);
            }
        }

        internal void AddPreparedFile(string key)
        {
            lock (Gate)
            {
                if (IntersectsUnresolved([key], target: null, recovery: false) || !Active.Add(key))
                    throw new IOException("The prepared file aliases an existing publication.");
                names.Add(key);
            }
        }

        public void Dispose()
        {
            TakeArtifacts()?.Dispose();
            lock (Gate)
            {
                foreach (string name in names) Active.Remove(name);
                activeOperations--;
            }
        }
    }

    internal static IDisposable ReserveNamespace(string[] paths, long artifactBytes) =>
        Reserve(paths, recovery: false, artifactBytes: artifactBytes);

    internal static void RegisterPreparedFile(IDisposable reservation, FileObjectIdentity prepared) =>
        ((NamespaceLease)reservation).AddPreparedFile(FileKey(prepared));

    internal static void RetainParkedEmptyFile(IDisposable reservation, Guid operationId, string target,
        string parked, FileObjectIdentity identity)
    {
        if (!StageStillOwned(parked, identity)) return;
        var lease = (NamespaceLease)reservation;
        var stage = HoldStage(operationId, target, parked, identity, lease.Names, lease.TakeArtifacts(), 0);
        lock (Gate) RetainedStages.Add(operationId, stage);
    }

    internal static IDisposable ReserveArchiveReinspection(FileArchiveEvidence archive) =>
        Reserve([archive.Source, archive.Destination], recovery: false, archiveReinspection: true);

    private static bool IntersectsUnresolved(IEnumerable<string> names, string? target, bool recovery,
        bool archiveReinspection = false) =>
        Unresolved.Any(pair =>
            (!recovery || !string.Equals(pair.Key, target, StringComparison.OrdinalIgnoreCase)) &&
            pair.Value.Records().Any(evidence => evidence.Namespace.Overlaps(names))) ||
        Archives.Any(pair =>
            (!archiveReinspection || !string.Equals(pair.Key, target, StringComparison.OrdinalIgnoreCase)) &&
            pair.Value.Names.Overlaps(names)) ||
        RetainedStages.Values.Any(stage => stage.Names.Overlaps(names));

    private static NamespaceLease Reserve(string[] paths, bool recovery, bool deferPhysical = false,
        bool archiveReinspection = false, bool publicationReinspection = false, long artifactBytes = 0)
    {
        string[] full = paths.Select(Path.GetFullPath).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in full)
            if (!names.Add("P:" + path)) throw new IOException("Publication paths alias one another.");

        NamespaceLease lease;
        lock (Gate)
        {
            if (activeOperations >= MaxActive || names.Overlaps(Active) ||
                IntersectsUnresolved(names, full[0], recovery, archiveReinspection) ||
                unresolvedRecords + RetainedStages.Count + Archives.Count + activeOperations >= MaxOutstanding &&
                !publicationReinspection && !archiveReinspection)
                throw new IOException("A file operation is pending or the previous publication requires recovery.");
            Active.UnionWith(names);
            activeOperations++;
            lease = new(names);
        }
        try
        {
            lease.ReserveArtifacts(artifactBytes);
            if (!deferPhysical) lease.AddPhysicalNames(full, recovery, archiveReinspection);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static bool IsUnresolved(string target)
    {
        string path = Path.GetFullPath(target);
        if (FindUnresolved(path) is not null) return true;
        lock (Gate)
        {
            if (Archives.ContainsKey(path)) return true;
            if (Archives.Count == 0) return false;
        }
        try
        {
            string[] names = FileObjectIdentity.NamespaceKeys(path);
            lock (Gate) return Archives.Values.Any(archive => archive.Names.Overlaps(names));
        }
        catch (IOException) { return false; }
    }

    internal static bool HasPendingPublication(string target)
        => IsPublicationActive(target) || IsUnresolved(target);

    internal static bool IsPublicationActive(string target)
    {
        string path = Path.GetFullPath(target);
        lock (Gate) return Active.Contains("P:" + path);
    }

    internal static FileArchiveEvidence? GetUnresolvedArchive(string source)
    {
        lock (Gate)
            return Archives.TryGetValue(Path.GetFullPath(source), out var pending) ? pending.Evidence : null;
    }

    internal static void RetainUnresolvedArchive(IDisposable reservation, FileArchiveEvidence evidence)
    {
        var lease = (NamespaceLease)reservation;
        lock (Gate) Archives.Add(evidence.Source, new(evidence, lease.Names, lease.TakeArtifacts()));
    }

    internal static void RetireArchive(FileArchiveEvidence evidence)
    {
        lock (Gate)
            if (Archives.TryGetValue(evidence.Source, out var pending) && pending.Evidence.OperationId == evidence.OperationId)
            {
                Archives.Remove(evidence.Source);
                pending.Artifacts?.Dispose();
            }
    }

    internal static FilePublicationResult InspectGuardedPublication(IDisposable reservation, Guid operationId,
        string target, string preservation, string staged, byte[] candidateDigest, byte[] originalDigest,
        string targetName, FileObjectIdentity prepared, FileObjectIdentity original, int maximum, Exception? error,
        bool retainUnresolved = false, bool beforeIssue = false, Exception? cleanupError = null)
    {
        var lease = (NamespaceLease)reservation;
        var evidence = new Evidence(operationId, target, preservation, staged, candidateDigest, originalDigest,
            maximum, error, lease.Names, [targetName, FileKey(prepared)], prepared, original,
            RequireInstalledIdentity: true, BeforeIssueTarget: beforeIssue ? [targetName, FileKey(original)] : null,
            CleanupError: cleanupError, CandidateLength: lease.ArtifactLength);
        var result = Observe(evidence);
        if (retainUnresolved && result.Status != FilePublicationStatus.Verified)
            Retain(evidence, lease);
        return result;
    }

    private static Evidence? FindUnresolved(string path)
    {
        lock (Gate)
        {
            if (Unresolved.TryGetValue(path, out var direct)) return direct;
            if (Unresolved.Count == 0) return null;
        }
        string[] keys;
        try { keys = FileObjectIdentity.NamespaceKeys(path); }
        catch (IOException) { return null; }
        lock (Gate) return Unresolved.Values.FirstOrDefault(current => current.Records().Any(evidence =>
            keys.Any(key => evidence.TargetAliases.Contains(key, StringComparer.OrdinalIgnoreCase))));
    }

    internal static FilePublicationResult? Reconcile(string target, Action<string>? adopt = null, Guid? operationId = null)
    {
        Evidence? evidence = FindUnresolved(Path.GetFullPath(target));
        if (evidence is null) return null;
        string path = evidence.Target;
        if (operationId is { } expected && evidence.Id != expected)
            throw new IOException("The publication awaiting reconciliation has changed.");
        string[] names = evidence.Preservation is null ? [path, evidence.Stage] : [path, evidence.Preservation, evidence.Stage];
        using var lease = Reserve(names, recovery: true, publicationReinspection: true);
        lock (Gate)
        {
            if (!Unresolved.TryGetValue(path, out var current) || current.Id != evidence.Id)
                throw new IOException("The publication awaiting reconciliation has changed.");
        }

        if (adopt is null) return Observe(evidence);
        // Keep the validated target and its preservation image stable through the format owner's adoption.
        var (observedTarget, targetStatus) = AcquireObservation(evidence.Target, evidence,
            preferOriginal: evidence.BeforeIssueTarget is not null);
        using var targetImage = observedTarget;
        var (original, originalStatus) = AcquireObservation(evidence.Preservation, evidence, preferOriginal: true);
        using var originalImage = original;
        FilePublicationResult result = Observe(evidence, targetStatus, originalStatus);
        if (result.Status == FilePublicationStatus.Unchanged && result.Candidate == FileImageStatus.Missing &&
            evidence.CleanupError is not null &&
            (!evidence.RequireInstalledIdentity || result.Preservation == FileImageStatus.Missing))
        {
            lock (Gate)
            {
                if (evidence.Previous is { } earlier) Unresolved[path] = earlier;
                else Unresolved.Remove(path);
                unresolvedRecords--;
                evidence.Artifacts?.Dispose();
            }
            return result with { EvidenceRetired = true };
        }
        if (result.Status != FilePublicationStatus.Verified) return result;
        adopt(evidence.Target);
        Retire(path);
        return result with { EvidenceRetired = true };
    }

    private static void Retain(Evidence evidence, NamespaceLease lease)
    {
        lock (Gate)
        {
            Unresolved.TryGetValue(evidence.Target, out var previous);
            Unresolved[evidence.Target] = evidence with { Previous = previous, Artifacts = lease.TakeArtifacts() };
            unresolvedRecords++;
        }
    }

    internal static string[] GetRetainedStagePaths(string target)
    {
        string path = Path.GetFullPath(target);
        lock (Gate)
            return RetainedStages.Values.Where(stage => string.Equals(stage.Target, path, StringComparison.OrdinalIgnoreCase))
                .Select(stage => stage.Path).ToArray();
    }

    internal static string[] ReinspectRetainedStages(string target)
    {
        string path = Path.GetFullPath(target);
        if (GetRetainedStagePaths(path).Length == 0) return [];
        using var lease = Reserve([path], recovery: true, deferPhysical: true, publicationReinspection: true);
        RetainedStage[] stages;
        lock (Gate)
            stages = RetainedStages.Values.Where(stage => string.Equals(stage.Target, path, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        foreach (var stage in stages)
        {
            if (StageStillOwned(stage.Path, stage.Identity)) continue;
            lock (Gate)
                if (RetainedStages.Remove(stage.Id)) stage.Artifacts?.Dispose();
        }
        return GetRetainedStagePaths(path);
    }

    private static void Retire(string target, Evidence? verified = null)
    {
        Evidence? previous;
        lock (Gate) Unresolved.TryGetValue(target, out previous);
        // The target reservation excludes new work through the handoff. The
        // older stage names stay owned by their original records until transfer.
        RetainedStage[] stages = (previous?.Records() ?? []).Concat(verified is null ? [] : [verified])
            .Where(record => StageStillOwned(record.Stage, record.CandidateIdentity))
            .Select(record => HoldStage(record.Id, record.Target, record.Stage, record.CandidateIdentity,
                record.Namespace, record.Artifacts, record.CandidateLength)).ToArray();
        lock (Gate)
        {
            foreach (var stage in stages) RetainedStages.Add(stage.Id, stage);
            if (Unresolved.Remove(target, out previous))
                foreach (var record in previous.Records())
                {
                    if (!RetainedStages.ContainsKey(record.Id)) record.Artifacts?.Dispose();
                    unresolvedRecords--;
                }
            if (verified is not null && !RetainedStages.ContainsKey(verified.Id)) verified.Artifacts?.Dispose();
        }
    }

    private static RetainedStage HoldStage(Guid id, string target, string stage, FileObjectIdentity identity,
        HashSet<string> ownedNames, ResourceReservation? artifacts, long bytes)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P:" + stage, FileKey(identity) };
        string nameSuffix = ":" + Path.GetFileName(stage);
        names.UnionWith(ownedNames.Where(key => key.StartsWith("D:", StringComparison.Ordinal) &&
            key.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase)));
        artifacts?.Resize(bytes);
        return new(id, target, stage, identity, names, artifacts);
    }

    private static bool StageStillOwned(string path, FileObjectIdentity identity)
    {
        try { return FileObjectIdentity.NamespaceKeys(path).Contains(FileKey(identity), StringComparer.Ordinal); }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
        {
            return false;
        }
        catch (IOException) { return true; }
    }

    internal static FilePublicationResult Publish(string target, byte[] candidate, byte[]? originalDigest,
        string? preservation, int maxBytes, CancellationToken token, Func<string, FileStream>? create = null,
        Action<string>? validate = null, bool recovery = false,
        Action<string, string, string?>? issue = null)
    {
        var request = new FilePublicationRequest(target, candidate, originalDigest, preservation,
            maxBytes, create, validate, recovery, Issue: issue);
        using var admitted = Admit(request, token);
        return PublishAdmitted(admitted, token);
    }

    internal static FilePublicationOperation Begin(FilePublicationRequest request, CancellationToken beforeIssue = default)
    {
        var admitted = Admit(request, beforeIssue);
        var observation = admitted.Observation;
        _ = Task.Run(() =>
        {
            FilePublicationResult? result = null;
            Exception? failure = null;
            try { result = PublishAdmitted(admitted, beforeIssue); }
            catch (Exception error) { failure = error; }
            finally
            {
                admitted.Dispose();
                observation.Settle(result, failure);
            }
        });
        return observation;
    }

    private sealed record Admission(FilePublicationOperation Observation, FilePublicationRequest Request, string Stage,
        string[] Names, NamespaceLease Namespace, ResourceReservation Charge) : IDisposable
    {
        public void Dispose()
        {
            Charge.Dispose();
            Namespace.Dispose();
        }
    }

    private static Admission Admit(FilePublicationRequest request, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Candidate.Length, request.MaximumBytes);
        token.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(request.Target);
        string? preservation = request.Preservation is null ? null : Path.GetFullPath(request.Preservation);
        Guid operationId = Guid.NewGuid();
        string staged = Path.Combine(Path.GetDirectoryName(target)!, $".tabtty-{operationId:N}.tmp");
        string[] paths = preservation is null ? [target, staged] : [target, preservation, staged];
        // Admission precedes opening the original. Charge its supported upper
        // bound until the preservation copy is verified or retained.
        long artifactBytes = request.Candidate.LongLength + (preservation is null ? 0L : request.MaximumBytes);
        var lease = Reserve(paths, request.Recovery, deferPhysical: true, artifactBytes: artifactBytes);
        ResourceReservation? charge = null;
        try
        {
            charge = CandidateBytes.Reserve(request.Candidate.LongLength);
            return new(new(operationId), request with
            {
                Target = target, Preservation = preservation, Candidate = request.Candidate.ToArray(),
                OriginalDigest = request.OriginalDigest?.ToArray(),
            }, staged, paths, lease, charge);
        }
        catch
        {
            charge?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private static FilePublicationResult PublishAdmitted(Admission admitted, CancellationToken token)
    {
        var (target, image, originalDigest, preservation, maxBytes, create, validate, recovery, createDirectory, issue) =
            admitted.Request;
        string staged = admitted.Stage;
        var lease = admitted.Namespace;
        Guid operationId = admitted.Observation.Id;
        token.ThrowIfCancellationRequested();
        if (createDirectory) Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        lease.AddPhysicalNames(admitted.Names, recovery);
        byte[] expected = SHA256.HashData(image);
        FileObjectIdentity? created = null;
        var targetNames = FileObjectIdentity.NamespaceKeys(target);
        bool issued = false;
        Exception? beforeIssueFailure = null;
        try
        {
            using (var output = create?.Invoke(staged) ??
                new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = FileObjectIdentity.Read(output.SafeFileHandle);
                lease.AddPreparedFile(FileKey(created.Value));
                output.Write(image);
                output.Flush(flushToDisk: true);
            }

            validate?.Invoke(staged);
            using var prepared = TryOpenImage(staged, maxBytes);
            if (prepared?.Identity != created || !(prepared?.Digest.AsSpan().SequenceEqual(expected) ?? false))
                throw new IOException("The prepared file changed before publication.");
            var before = Inspect(target, expected, originalDigest, maxBytes, preferOriginal: true);
            if (before != (originalDigest is null ? FileImageStatus.Missing : FileImageStatus.Original) ||
                !targetNames.AsSpan().SequenceEqual(FileObjectIdentity.NamespaceKeys(target)))
                return new(FilePublicationStatus.Conflict, before, FileImageStatus.Unavailable,
                    target, preservation, staged, null, operationId);
            token.ThrowIfCancellationRequested();
            prepared?.Dispose();

            Exception? error = null;
            issued = true;
            admitted.Observation.MarkIssued();
            try
            {
                if (issue is not null) issue(staged, target, preservation);
                else if (originalDigest is null) File.Move(staged, target, overwrite: false);
                else File.Replace(staged, target, preservation ?? throw new InvalidOperationException("A replacement requires preservation."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception;
            }

            var evidence = new Evidence(operationId, target, preservation, staged, expected, originalDigest?.ToArray(),
                maxBytes, error, lease.Names, [.. targetNames, FileKey(created!.Value)], created.Value,
                CandidateLength: image.LongLength);
            FilePublicationResult result = Observe(evidence);
            if (result.Status == FilePublicationStatus.Unchanged && result.Candidate == FileImageStatus.Candidate)
            {
                try
                {
                    FileObjectIdentity.DeleteIfSame(staged, created.Value);
                    result = result with { Candidate = Inspect(staged, evidence, isStage: true) };
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    result = result with { CleanupError = cleanup };
                }
            }
            if (result.Status == FilePublicationStatus.Verified)
                Retire(target, result.Candidate == FileImageStatus.Missing ? null : evidence with { Artifacts = lease.TakeArtifacts() });
            else if (result.Status != FilePublicationStatus.Unchanged || result.CleanupError is not null)
                Retain(evidence with { CleanupError = result.CleanupError }, lease);
            return result;
        }
        catch (Exception error) when (!issued)
        {
            beforeIssueFailure = error;
            throw;
        }
        finally
        {
            // An issued publication can leave a candidate for recovery. A
            // path alone does not establish ownership after it was exposed.
            if (created is { } identity && !issued)
            {
                try { FileObjectIdentity.DeleteIfSame(staged, identity); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    var evidence = new Evidence(operationId, target, preservation, staged, expected, originalDigest,
                        maxBytes, beforeIssueFailure, lease.Names, [.. targetNames, FileKey(identity)], identity,
                        BeforeIssueTarget: targetNames, CleanupError: error, CandidateLength: image.LongLength);
                    Retain(evidence, lease);
                    var result = Observe(evidence);
                    throw new FilePublicationException("The unissued prepared file could not be removed.", result);
                }
            }
        }
    }

    private static string FileKey(FileObjectIdentity identity) => $"F:{identity.Volume:X8}:{identity.Index:X16}";

    private static FilePublicationResult Observe(Evidence evidence, FileImageStatus? target = null, FileImageStatus? backup = null)
    {
        var installed = target ?? Inspect(evidence.Target, evidence, preferOriginal: evidence.BeforeIssueTarget is not null);
        string? preservation = evidence.Preservation;
        var preserved = backup ?? (preservation is null ? FileImageStatus.Missing :
            Inspect(preservation, evidence, preferOriginal: true));
        bool unchanged = installed == (evidence.OriginalDigest is null ? FileImageStatus.Missing : FileImageStatus.Original);
        FilePublicationStatus status;
        if (evidence.BeforeIssueTarget is { } basis)
        {
            status = unchanged && HasSameNamespace(evidence.Target, basis)
                ? FilePublicationStatus.Unchanged : FilePublicationStatus.Conflict;
        }
        else if (installed == FileImageStatus.Candidate)
        {
            status = evidence.OriginalDigest is null || preserved == FileImageStatus.Original
                ? FilePublicationStatus.Verified : FilePublicationStatus.UnverifiedPreservation;
        }
        else status = unchanged && (evidence.Error is not null || evidence.CleanupError is not null)
            ? FilePublicationStatus.Unchanged : FilePublicationStatus.Conflict;
        return new(status, installed, preserved, evidence.Target, preservation, evidence.Stage, evidence.Error, evidence.Id,
            Inspect(evidence.Stage, evidence, isStage: true), evidence.CleanupError);
    }

    private static bool HasSameNamespace(string path, string[] expected)
    {
        try { return expected.AsSpan().SequenceEqual(FileObjectIdentity.NamespaceKeys(path)); }
        catch (IOException) { return false; }
    }

    private sealed record Image(FileStream Stream, FileObjectIdentity Identity, byte[] Digest) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    private static Image? TryOpenImage(string path, int maxBytes)
    {
        FileStream? file = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("A publication image must be an ordinary file.");
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > maxBytes) throw new InvalidDataException("File image exceeds its size limit.");
            return new(file, FileObjectIdentity.Read(file.SafeFileHandle), SHA256.HashData(file));
        }
        catch (Exception error)
        {
            file?.Dispose();
            if (error is FileNotFoundException or DirectoryNotFoundException) return null;
            throw;
        }
    }

    internal static byte[]? ReadDigest(string path, int maxBytes)
    {
        using var image = TryOpenImage(path, maxBytes);
        return image?.Digest;
    }

    private static FileImageStatus Describe(Image? image, byte[] candidate, byte[]? original, bool preferOriginal,
        FileObjectIdentity? candidateIdentity = null, FileObjectIdentity? originalIdentity = null)
    {
        if (image is null) return FileImageStatus.Missing;
        ReadOnlySpan<byte> observed = image.Digest;
        bool isOriginal = (originalIdentity is null || image.Identity == originalIdentity) && observed.SequenceEqual(original);
        if (preferOriginal && isOriginal) return FileImageStatus.Original;
        if ((candidateIdentity is null || image.Identity == candidateIdentity) && observed.SequenceEqual(candidate))
            return FileImageStatus.Candidate;
        return isOriginal ? FileImageStatus.Original : FileImageStatus.Other;
    }

    private static FileImageStatus Inspect(string path, Evidence evidence, bool preferOriginal = false, bool isStage = false)
    {
        var (image, status) = AcquireObservation(path, evidence, preferOriginal, isStage);
        image?.Dispose();
        return status;
    }

    private static (Image? Image, FileImageStatus Status) AcquireObservation(string? path, Evidence evidence,
        bool preferOriginal = false, bool isStage = false)
    {
        if (path is null) return (null, FileImageStatus.Missing);
        try
        {
            var observed = TryOpenImage(path, evidence.Maximum);
            return (observed, Describe(observed, evidence.CandidateDigest, evidence.OriginalDigest, preferOriginal,
                isStage || evidence.RequireInstalledIdentity ? evidence.CandidateIdentity : null, evidence.OriginalIdentity));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, FileImageStatus.Unavailable);
        }
    }

    private static FileImageStatus Inspect(string path, byte[] candidate, byte[]? original, int maxBytes,
        bool preferOriginal = false)
    {
        try
        {
            using var observed = TryOpenImage(path, maxBytes);
            return Describe(observed, candidate, original, preferOriginal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FileImageStatus.Unavailable;
        }
    }
}
