using TabTty.Core;

internal static partial class ArchitectureOwnershipContracts
{
    private static async Task VaultResultAndMetadataAdmissionPrecedeMutationAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        const string master = "original publication test password";
        string path = Path.Combine(directory.Path, "capacity.tttykey");
        var token = deadline.Token;
        using var vault = await CredentialVault.CreateAsync(path, master.AsMemory(), token);
        await vault.AddPasswordAsync("original", "original secret"u8.ToArray(), token);
        var original = vault.ReadCommittedMetadata();
        byte[] saved = File.ReadAllBytes(path);
        int issued = 0;
        using (VaultPublicationCapacity.Results.Reserve(VaultPublicationCapacity.ResultLimit))
        {
            try
            {
                await vault.ChangeMasterPasswordAsync("replacement publication password".AsMemory(), (_, _, _) => issued++, token);
                throw new InvalidOperationException("A master change bypassed result admission.");
            }
            catch (ResourceLimitException) { }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using (VaultPublicationCapacity.Metadata.Reserve(
            VaultPublicationCapacity.MetadataLimit - VaultPublicationCapacity.Metadata.Used))
        {
            try
            {
                await vault.AddPasswordAsync("refused", "unused secret"u8.ToArray(), token);
                throw new InvalidOperationException("An entry save bypassed metadata admission.");
            }
            catch (ResourceLimitException) { }
        }
        if (issued != 0 || !ReferenceEquals(vault.ReadCommittedMetadata(), original) ||
            !saved.AsSpan().SequenceEqual(File.ReadAllBytes(path)))
            throw new InvalidOperationException("Capacity refusal changed the vault generation.");
        string name = await vault.AddPasswordAsync("after release", "accepted secret"u8.ToArray(), token);
        var published = vault.ReadCommittedMetadata();
        if (published.Revision != original.Revision + 1 || !published.Entries.Any(entry => entry.Name == name) ||
            original.Entries.Count != 1)
            throw new InvalidOperationException("A later save did not publish immutable committed metadata.");
    }

    private static async Task VaultRestoreRetainsIssuedReplacementAsync(bool unlocked)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var release = new ManualResetEventSlim();
        var token = deadline.Token;
        string target = Path.Combine(directory.Path, "restore.tttykey");
        string backup = Path.Combine(directory.Path, "candidate.tttykey");
        const string originalMaster = "initial credential vault password";
        const string nextMaster = "rotated credential vault password";
        string entry;
        Guid vaultId;
        using (var created = await CredentialVault.CreateAsync(target, originalMaster.AsMemory(), token))
        {
            vaultId = created.Id;
            entry = await created.AddPasswordAsync("original entry", "initial secret"u8.ToArray(), token);
            await created.BackupAsync(backup, token);
            await created.UpdateEntryAsync(entry, "later entry", "later secret"u8.ToArray(), token);
            await created.ChangeMasterPasswordAsync(nextMaster.AsMemory(), token);
        }
        byte[] original = File.ReadAllBytes(target), candidate = File.ReadAllBytes(backup);
        using var owner = unlocked ? await CredentialVault.UnlockAsync(target, nextMaster.AsMemory(), token) : null;
        var before = owner?.ReadCommittedMetadata();
        long charged = VaultPublicationCapacity.Results.Used;
        var basis = CredentialVault.PrepareRestore(target, backup, owner);
        var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int mutations = 0;
        var restoration = CredentialVault.RestoreFileAsync(basis, originalMaster.AsMemory(), owner, stop.Token,
            (stage, path, preservation) =>
            {
                mutations++;
                File.Replace(stage, path, preservation!);
                issued.SetResult(preservation!);
                release.Wait(token);
                throw new IOException("The fixture reported an error after actual replacement.");
            });
        string preserved;
        try
        {
            preserved = await issued.Task.WaitAsync(token);
            stop.Cancel();
            if (restoration.IsCompleted || VaultPublicationCapacity.Results.Used != charged + 1 ||
                !ReferenceEquals(before, owner?.ReadCommittedMetadata()) ||
                !candidate.AsSpan().SequenceEqual(File.ReadAllBytes(target)) ||
                !original.AsSpan().SequenceEqual(File.ReadAllBytes(preserved)))
                throw new InvalidOperationException("An issued restore released ownership or adopted too early.");
            File.Delete(preserved);
        }
        finally { release.Set(); }
        try
        {
            using var unexpected = await restoration.WaitAsync(token);
            throw new InvalidOperationException("A restore adopted an image without preserved original bytes.");
        }
        catch (FilePublicationException error) when (error.Result.Status == FilePublicationStatus.UnverifiedPreservation &&
            error.Result.Target == FileImageStatus.Candidate && error.Result.Preservation == FileImageStatus.Missing) { }
        if (!VerifiedFilePublication.IsUnresolved(target) || !ReferenceEquals(before, owner?.ReadCommittedMetadata()) ||
            VaultPublicationCapacity.Results.Used != charged + (unlocked ? 1 : 0))
            throw new InvalidOperationException("The partial restore lost its evidence or retained-root charge.");
        File.WriteAllBytes(preserved, original);
        using var opened = unlocked ? null :
            await CredentialVault.ReconcileLockedPublicationAsync(target, originalMaster.AsMemory(), token);
        if (owner is not null && !await owner.ReconcilePublicationAsync())
            throw new InvalidOperationException("The exact unlocked restore candidate could not be adopted.");
        var restored = owner ?? opened ?? throw new InvalidOperationException("Locked reinspection did not authenticate its candidate.");
        using var secret = await restored.GetPasswordAsync(entry, token);
        using var prior = await CredentialVault.UnlockAsync(preserved, nextMaster.AsMemory(), token);
        using var priorSecret = await prior.GetPasswordAsync("later entry", token);
        if (restored.Id != vaultId || prior.Id != vaultId || !secret.Bytes.Span.SequenceEqual("initial secret"u8) || !priorSecret.Bytes.Span.SequenceEqual("later secret"u8) ||
            restored.ReadCommittedMetadata().Entries.Single().Name != "original entry" || mutations != 1 ||
            VaultPublicationCapacity.Results.Used != charged || VerifiedFilePublication.IsUnresolved(target))
            throw new InvalidOperationException("Restore reinspection republished or adopted the wrong root or metadata.");
    }

    private static async Task VaultMasterInputAndRootFailuresPreserveTheGenerationAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        string path = Path.Combine(directory.Path, "master-root.tttykey");
        using var vault = await CredentialVault.CreateAsync(path, "original master fixture".AsMemory(), token);
        string entry = await vault.AddPasswordAsync("entry", "entry secret fixture"u8.ToArray(), token);
        byte[] saved = File.ReadAllBytes(path), preserved = File.ReadAllBytes(path + ".bak");
        var revision = vault.ReadCommittedMetadata();
        long slots = VaultPublicationCapacity.Results.Used;
        try
        {
            await vault.ChangeMasterPasswordAsync(new string('x', VaultContainer.MaximumMasterInputLength + 1).AsMemory(), token);
            throw new InvalidOperationException("An oversized master input was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }

        byte[]? failedRoot = null;
        var rngFailure = new System.Security.Cryptography.CryptographicException("Controlled RNG failure.");
        try
        {
            await vault.ChangeMasterPasswordAsync("replacement master fixture".AsMemory(), MustNotIssue, token,
                fillRoot: bytes =>
                {
                    failedRoot = bytes;
                    bytes.AsSpan().Fill(0x51);
                    throw rngFailure;
                });
            throw new InvalidOperationException("A failed RNG was reported as successful.");
        }
        catch (System.Security.Cryptography.CryptographicException error) when (ReferenceEquals(error, rngFailure)) { }
        if (failedRoot is null || failedRoot.Any(value => value != 0))
            throw new InvalidOperationException("The partially initialized replacement root was not cleared.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cancelled = vault.ChangeMasterPasswordAsync("cancelled master fixture".AsMemory(), MustNotIssue, stop.Token,
            fillRoot: bytes =>
            {
                bytes.AsSpan().Fill(0x3e);
                entered.SetResult(bytes);
                release.Wait(token);
            });
        byte[] heldRoot;
        try
        {
            heldRoot = await entered.Task.WaitAsync(token);
            using var queueStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var queued = Enumerable.Range(0, 31).Select(_ =>
                CredentialVault.UnlockAsync(path, "original master fixture".AsMemory(), queueStop.Token)).ToArray();
            try
            {
                using var excess = await CredentialVault.UnlockAsync(path, "original master fixture".AsMemory(), token);
                throw new InvalidOperationException("A thirty-third derivation bypassed bounded admission.");
            }
            catch (ResourceLimitException) { }
            finally { queueStop.Cancel(); }
            foreach (var request in queued)
            {
                try
                {
                    using var unexpected = await request;
                    throw new InvalidOperationException("A cancelled queued unlock ran while the original worker was held.");
                }
                catch (OperationCanceledException) when (queueStop.IsCancellationRequested) { }
            }
            stop.Cancel();
            if (cancelled.IsCompleted || heldRoot.Any(value => value != 0x3e))
                throw new InvalidOperationException("Cancellation cleared a buffer while root generation still owned it.");
        }
        finally { release.Set(); }
        try
        {
            await cancelled.WaitAsync(token);
            throw new InvalidOperationException("A cancelled root generation continued to publication.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested) { }
        if (heldRoot.Any(value => value != 0) || !ReferenceEquals(vault.ReadCommittedMetadata(), revision) ||
            VaultPublicationCapacity.Results.Used != slots || VerifiedFilePublication.IsUnresolved(path) ||
            !File.ReadAllBytes(path).AsSpan().SequenceEqual(saved) ||
            !File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(preserved))
            throw new InvalidOperationException("A failed or cancelled root changed the effective revision or file evidence.");
        using var credential = await vault.GetPasswordAsync(entry, token);
        if (!credential.Bytes.Span.SequenceEqual("entry secret fixture"u8))
            throw new InvalidOperationException("The previous root no longer resolved its credential.");

        static void MustNotIssue(string _, string __, string? ___) =>
            throw new InvalidOperationException("An unaccepted master change reached file mutation.");
    }

    private static async Task UnissuedVaultCleanupRetiresWithoutAdoptingAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        const string master = "original publication test password";
        string path = Path.Combine(directory.Path, "preissue.tttykey");
        using var vault = await CredentialVault.CreateAsync(path, master.AsMemory(), token);
        string entry = await vault.AddPasswordAsync("original", "original secret"u8.ToArray(), token);
        byte[] prior = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        var revision = vault.ReadCommittedMetadata();
        long capacity = VaultPublicationCapacity.Results.Used;
        FileStream? stage = null;
        FilePublicationResult pending;
        try
        {
            await vault.ChangeMasterPasswordAsync("unused replacement password".AsMemory(),
                (_, _, _) => throw new InvalidOperationException("A failed stage reached mutation."), token,
                validateStaged: name =>
                {
                    stage = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Controlled pre-issue vault failure.");
                });
            throw new InvalidOperationException("The pre-issue failure did not propagate.");
        }
        catch (FilePublicationException error) { pending = error.Result; }
        finally { stage?.Dispose(); }
        if (pending is not { Status: FilePublicationStatus.Unchanged, CleanupError: not null } ||
            !vault.HasUnverifiedPublication || VaultPublicationCapacity.Results.Used != capacity + 1 ||
            !ReferenceEquals(vault.ReadCommittedMetadata(), revision) ||
            !File.ReadAllBytes(path).AsSpan().SequenceEqual(prior) ||
            !File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(backup))
            throw new InvalidOperationException("A pre-issue vault failure changed the original or lost result ownership.");

        File.Delete(pending.CandidatePath);
        var inspected = await vault.ReconcilePublicationResultAsync();
        if (inspected is not { Status: FilePublicationStatus.Unchanged, EvidenceRetired: true } ||
            vault.HasUnverifiedPublication || VaultPublicationCapacity.Results.Used != capacity ||
            !ReferenceEquals(vault.ReadCommittedMetadata(), revision))
            throw new InvalidOperationException("Vault cleanup retirement adopted an unissued root or retained its result slot.");
        using var unlocked = await CredentialVault.UnlockAsync(path, master.AsMemory(), token);
        using var current = await vault.GetPasswordAsync(entry, token);
        using var persisted = await unlocked.GetPasswordAsync(entry, token);
        if (!current.Bytes.Span.SequenceEqual("original secret"u8) || !persisted.Bytes.Span.SequenceEqual(current.Bytes.Span))
            throw new InvalidOperationException("Proof of cleanup changed the effective or persisted credential.");

        const string replacement = "verified replacement after the observed error";
        int issues = 0;
        await vault.ChangeMasterPasswordAsync(replacement.AsMemory(), (candidate, target, preservation) =>
        {
            issues++;
            File.Replace(candidate, target, preservation);
            throw new IOException("Controlled error reported after the verified replacement.");
        }, token);
        using var updated = await CredentialVault.UnlockAsync(path, replacement.AsMemory(), token);
        using var unchangedBackup = await CredentialVault.UnlockAsync(path + ".bak", master.AsMemory(), token);
        using var updatedCredential = await updated.GetPasswordAsync(entry, token);
        using var backupCredential = await unchangedBackup.GetPasswordAsync(entry, token);
        if (issues != 1 || vault.Revision != revision.Revision + 1 || vault.HasUnverifiedPublication ||
            VaultPublicationCapacity.Results.Used != capacity ||
            !updatedCredential.Bytes.Span.SequenceEqual("original secret"u8) ||
            !backupCredential.Bytes.Span.SequenceEqual(updatedCredential.Bytes.Span))
            throw new InvalidOperationException("A reported file error hid a fully verified master change or repeated it.");
    }

    private static async Task VaultInitializationPreservesObservedFileIdentitiesAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        const string master = "explicit initialization test password";
        string path = Path.Combine(directory.Path, "empty.tttykey");
        File.WriteAllBytes(path, []);
        var original = CredentialVault.InspectFile(path)!;

        FileStream? holdStage = null;
        FilePublicationResult stagingFailure;
        try
        {
            using var rejected = await CredentialVault.InitializeEmptyAsync(path, master.AsMemory(), original, token,
                (_, _, _) => throw new InvalidOperationException("A failed stage parked the original."),
                validateStage: stage =>
                {
                    holdStage = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Controlled initialization preparation failure.");
                });
            throw new InvalidOperationException("The preparation failure was lost.");
        }
        catch (FilePublicationException error) { stagingFailure = error.Result; }
        finally { holdStage?.Dispose(); }
        if (stagingFailure is not { Status: FilePublicationStatus.Unchanged, CleanupError: not null } ||
            CredentialVault.InspectFile(path) != original || !VerifiedFilePublication.IsUnresolved(path) ||
            File.Exists(stagingFailure.PreservationPath))
            throw new InvalidOperationException("An unissued initialization lost its original or its artifact ownership.");
        File.Delete(stagingFailure.CandidatePath);
        if (VerifiedFilePublication.Reconcile(path, _ => throw new InvalidOperationException("Unissued vault was adopted.")) is not
            { Status: FilePublicationStatus.Unchanged, EvidenceRetired: true } || VerifiedFilePublication.IsUnresolved(path))
            throw new InvalidOperationException("Proven stage retirement left an unissued initialization unresolved.");

        await CompensatedAsync(VaultInitializationStep.ParkOriginal);
        await CompensatedAsync(VaultInitializationStep.InstallCandidate);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            try
            {
                using var interrupted = await CredentialVault.InitializeEmptyAsync(path, master.AsMemory(), original,
                    stop.Token, (step, source, target) =>
                    {
                        File.Move(source, target);
                        if (step == VaultInitializationStep.ParkOriginal) stop.Cancel();
                    });
                throw new InvalidOperationException("Cancellation after parking still installed a vault.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            if (CredentialVault.InspectFile(path) != original || VerifiedFilePublication.IsUnresolved(path))
                throw new InvalidOperationException("Cancellation did not restore the exact empty file.");
        }

        FilePublicationResult evidence;
        try
        {
            using var unusable = await CredentialVault.InitializeEmptyAsync(path, master.AsMemory(), original, token,
                (step, source, target) =>
                {
                    if (step != VaultInitializationStep.ParkOriginal) throw new IOException("Injected move failure.");
                    File.Move(source, target);
                });
            throw new InvalidOperationException("An initialization with failed compensation returned success.");
        }
        catch (FilePublicationException error)
        {
            evidence = error.Result;
        }
        string parked = evidence.PreservationPath!;
        string moved = Path.Combine(directory.Path, "authentic-original.tttykey");
        if (evidence.Target != FileImageStatus.Missing || evidence.Preservation != FileImageStatus.Original ||
            evidence.Candidate != FileImageStatus.Candidate || !VerifiedFilePublication.IsUnresolved(path))
            throw new InvalidOperationException("Initialization did not retain all effects after failed compensation.");

        File.Move(evidence.CandidatePath, path);
        File.Move(parked, moved);
        File.WriteAllBytes(parked, []);
        if (VerifiedFilePublication.Reconcile(path) is not { Preservation: FileImageStatus.Other })
            throw new InvalidOperationException("An empty file with another identity was credited as preservation.");
        File.Delete(parked);
        File.Move(moved, parked);
        using var restored = await CredentialVault.ReconcileLockedPublicationAsync(path, master.AsMemory(), token);
        if (restored is null || CredentialVault.InspectFile(parked) != original || VerifiedFilePublication.IsUnresolved(path))
            throw new InvalidOperationException("Read-only reinspection did not adopt the exact installed candidate.");

        string cleanupTarget = Path.Combine(directory.Path, "cleanup.tttykey");
        File.WriteAllBytes(cleanupTarget, []);
        FileStream? denyDelete = null;
        string? parkedForCleanup = null;
        try
        {
            using var completed = await CredentialVault.InitializeEmptyAsync(cleanupTarget, master.AsMemory(),
                CredentialVault.InspectFile(cleanupTarget)!, token, (step, source, target) =>
                {
                    File.Move(source, target);
                    if (step == VaultInitializationStep.ParkOriginal)
                    {
                        parkedForCleanup = target;
                        denyDelete = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    if (step == VaultInitializationStep.InstallCandidate)
                        throw new IOException("An error was reported after the candidate was installed.");
                });
            if (completed.InitializationResult is not { Status: FilePublicationStatus.Verified, CleanupError: not null } ||
                VerifiedFilePublication.IsUnresolved(cleanupTarget) || (await completed.ListEntriesAsync(token)).Count != 0)
                throw new InvalidOperationException("Verified initialization was lost because cleanup failed.");
            if (parkedForCleanup is null ||
                !VerifiedFilePublication.ReinspectRetainedStages(cleanupTarget).SequenceEqual([parkedForCleanup]))
                throw new InvalidOperationException("Verified initialization released an undeleted parked file.");
        }
        finally { denyDelete?.Dispose(); }
        File.Delete(parkedForCleanup!);
        if (VerifiedFilePublication.ReinspectRetainedStages(cleanupTarget).Length != 0)
            throw new InvalidOperationException("Proven removal did not retire the parked artifact.");

        async Task CompensatedAsync(VaultInitializationStep failingStep)
        {
            try
            {
                using var initialized = await CredentialVault.InitializeEmptyAsync(path, master.AsMemory(), original, token,
                    (step, source, target) =>
                    {
                        if (step == failingStep) throw new IOException("Injected move refusal.");
                        File.Move(source, target);
                    });
                throw new InvalidOperationException("A failed initialization returned an unlocked owner.");
            }
            catch (FilePublicationException error) when (error.Result is
                { Status: FilePublicationStatus.Unchanged, Target: FileImageStatus.Original, Candidate: FileImageStatus.Missing }) { }
            if (CredentialVault.InspectFile(path) != original || VerifiedFilePublication.IsUnresolved(path))
                throw new InvalidOperationException("A failed move left the original changed or installed a candidate.");
        }
    }

    private static async Task VaultRestoreRefusesChangedSourcesAndRescuesOnlyAuthenticatedBytesAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        const string master = "fixture recovery compatibility password";
        string target = Path.Combine(directory.Path, "target.tttykey");
        string backup = Path.Combine(directory.Path, "backup.tttykey");
        string different = Path.Combine(directory.Path, "different.tttykey");
        string moved = Path.Combine(directory.Path, "reviewed.tttykey");
        string rescue = Path.Combine(directory.Path, "rescue.tttykey");
        using var owner = await CredentialVault.CreateAsync(target, master.AsMemory(), token);
        string entry = await owner.AddPasswordAsync("rescue entry", "authentic source"u8.ToArray(), token);
        await owner.BackupAsync(backup, token);
        using (await CredentialVault.CreateAsync(different, master.AsMemory(), token)) { }
        byte[] authentic = File.ReadAllBytes(target);

        foreach (CredentialVault? context in new[] { null, owner })
        {
            var mismatch = CredentialVault.PrepareRestore(target, different, context);
            try
            {
                using var unexpected = await CredentialVault.RestoreFileAsync(mismatch, master.AsMemory(), context, token);
                throw new InvalidOperationException("An authenticated backup with another vault ID was accepted.");
            }
            catch (InvalidDataException) { }
        }
        var basis = CredentialVault.PrepareRestore(target, backup, owner);
        File.Move(backup, moved);
        File.Copy(moved, backup);
        try
        {
            using var unexpected = await CredentialVault.RestoreFileAsync(basis, master.AsMemory(), owner, token);
            throw new InvalidOperationException("Restore accepted a different file with the reviewed bytes.");
        }
        catch (IOException) { }
        if (!authentic.AsSpan().SequenceEqual(File.ReadAllBytes(target)) ||
            Directory.GetFiles(directory.Path, "*.recovery-*.tttykey").Length != 0)
            throw new InvalidOperationException("Failed restore checks had physical effects.");

        byte[] damaged = "unverified bytes from an external writer"u8.ToArray();
        File.WriteAllBytes(target, damaged);
        try
        {
            using var unexpected = await owner.GetPasswordAsync(entry, token);
            throw new InvalidOperationException("The externally changed vault delivered a credential.");
        }
        catch (CredentialVaultStaleException) { }
        await owner.BackupRetainedGenerationAsync(rescue, token);
        try
        {
            await owner.BackupRetainedGenerationAsync(rescue, token);
            throw new InvalidOperationException("Rescue overwrote an existing destination.");
        }
        catch (IOException) { }
        if (!authentic.AsSpan().SequenceEqual(File.ReadAllBytes(rescue)) ||
            !damaged.AsSpan().SequenceEqual(File.ReadAllBytes(target)) || !owner.IsStale)
            throw new InvalidOperationException("Rescue copied external bytes or removed the independent stale condition.");

        string future = Path.Combine(directory.Path, "future.tttykey");
        byte[] futureBytes = authentic.ToArray();
        futureBytes[9] = 2;
        File.WriteAllBytes(future, futureBytes);
        try
        {
            using var unexpected = await CredentialVault.RecoverAsync(future, rescue, master.AsMemory(), token);
            throw new InvalidOperationException("Recovery overwrote a newer format.");
        }
        catch (InvalidDataException error) when (error.Message == "A newer credential vault format cannot be replaced.") { }
        if (!File.ReadAllBytes(future).AsSpan().SequenceEqual(futureBytes) ||
            Directory.EnumerateFiles(directory.Path, "*.recovery-*.tttykey").Any())
            throw new InvalidOperationException("Future-format refusal changed the destination or created preservation.");

        string damagedCopy = Path.Combine(directory.Path, "damaged.tttykey");
        File.WriteAllBytes(damagedCopy, damaged);
        using var restored = await CredentialVault.RecoverAsync(damagedCopy, rescue, master.AsMemory(), token);
        string preservedDamage = Directory.EnumerateFiles(directory.Path, "damaged.tttykey.recovery-*.tttykey").Single();
        using var recovered = await restored.GetPasswordAsync(entry, token);
        if (!File.ReadAllBytes(damagedCopy).AsSpan().SequenceEqual(authentic) ||
            !File.ReadAllBytes(preservedDamage).AsSpan().SequenceEqual(damaged) ||
            !recovered.Bytes.Span.SequenceEqual("authentic source"u8) || !owner.IsStale)
            throw new InvalidOperationException("The locked recovery failed to preserve damaged bytes or authenticated the wrong generation.");
    }
}
