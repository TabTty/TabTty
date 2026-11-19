using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TabTty.Core;

internal static partial class ArchitectureOwnershipContracts
{
    private static async Task PublicationRejectsPhysicalAliasesAndChangedCandidatesAsync()
    {
        using var fixture = new PublicationDirectory();
        string target = fixture.File("profile");
        string alias = fixture.File("alias");
        byte[] original = "original"u8.ToArray(), candidate = "candidate"u8.ToArray();
        File.WriteAllBytes(target, original);
        if (!CreateHardLink(alias, target, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using var held = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new FilePublicationRequest(target, candidate, SHA256.HashData(original), target + ".bak", 1024,
            Issue: (stage, path, backup) =>
            {
                entered.SetResult();
                held.Wait(deadline.Token);
                File.Replace(stage, path, backup);
                throw new IOException("Controlled error after successful replacement.");
            });
        var owner = VerifiedFilePublication.Begin(request);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            var competing = VerifiedFilePublication.Begin(request with
            {
                Target = alias, Preservation = alias + ".bak",
                Issue = (_, _, _) => throw new InvalidOperationException("The competing alias reached mutation."),
            });
            try
            {
                await competing.ObserveAsync(deadline.Token);
                throw new InvalidOperationException("A hard-link alias bypassed the existing target reservation.");
            }
            catch (IOException error) when (error.Message.Contains("aliases an active", StringComparison.Ordinal)) { }
        }
        finally
        {
            held.Set();
            await owner.ActualRelease;
        }
        if ((await owner.ObserveAsync()).Status != FilePublicationStatus.Verified ||
            !File.ReadAllBytes(target + ".bak").AsSpan().SequenceEqual(original) ||
            !File.ReadAllBytes(target).AsSpan().SequenceEqual(candidate) || File.Exists(alias + ".bak"))
            throw new InvalidOperationException("A late replacement error hid the verified publish or touched another backup.");

        string? reused = null;
        string moved = fixture.File("moved");
        try
        {
            VerifiedFilePublication.Publish(target, original, SHA256.HashData(candidate), target + ".bak", 1024, default,
                validate: stage =>
                {
                    reused = stage;
                    File.Move(stage, moved);
                    File.WriteAllBytes(stage, original);
                },
                issue: (_, _, _) => throw new InvalidOperationException("The replacement candidate reached mutation."));
            throw new InvalidOperationException("The candidate was accepted under a reused name.");
        }
        catch (IOException error) when (error.Message == "The prepared file changed before publication.") { }
        if (reused is null || !File.ReadAllBytes(reused).AsSpan().SequenceEqual(original) ||
            !File.ReadAllBytes(moved).AsSpan().SequenceEqual(original) ||
            !File.ReadAllBytes(target).AsSpan().SequenceEqual(candidate))
            throw new InvalidOperationException("Cleanup deleted a file that reused the prepared name or changed the target.");
    }

    private static void PublicationRecoveryRetainsEarlierEvidence()
    {
        using var fixture = new PublicationDirectory();
        string target = fixture.File("profile");
        string backup = target + ".bak";
        byte[] original = "original"u8.ToArray(), candidate = "candidate"u8.ToArray();
        File.WriteAllBytes(target, original);
        FileStream? inaccessible = null;
        try
        {
            var first = VerifiedFilePublication.Publish(target, candidate, SHA256.HashData(original), backup,
                1024, default, issue: (stage, path, preserved) =>
                {
                    File.Replace(stage, path, preserved);
                    inaccessible = new FileStream(preserved!, FileMode.Open, FileAccess.Read, FileShare.None);
                });
            if (first is not { Status: FilePublicationStatus.UnverifiedPreservation, Preservation: FileImageStatus.Unavailable })
                throw new InvalidOperationException("An unreadable backup did not retain the original publication.");

            FilePublicationResult failed;
            string recoveryCopy = fixture.File("recovery");
            FileStream? preventCleanup = null;
            try
            {
                VerifiedFilePublication.Publish(target, "recovery"u8.ToArray(), SHA256.HashData(candidate), recoveryCopy,
                    1024, default, recovery: true,
                    validate: stage =>
                    {
                        preventCleanup = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read);
                        throw new IOException("Controlled failure before replacement.");
                    },
                    issue: (_, _, _) => throw new InvalidOperationException("Failed recovery reached mutation."));
                throw new InvalidOperationException("An unissued recovery with failed cleanup was not reported.");
            }
            catch (FilePublicationException error) { failed = error.Result; }
            finally { preventCleanup?.Dispose(); }

            var held = VerifiedFilePublication.Reconcile(target, _ => throw new InvalidOperationException("Recovery was adopted."));
            if (held?.OperationId != failed.OperationId || held.Status != FilePublicationStatus.Unchanged ||
                held.CleanupError is null || held.EvidenceRetired || !File.Exists(failed.CandidatePath) || File.Exists(recoveryCopy))
                throw new InvalidOperationException("An unissued recovery lost its cleanup evidence.");
            File.Delete(failed.CandidatePath);
            var cleared = VerifiedFilePublication.Reconcile(target, _ => throw new InvalidOperationException("Recovery was adopted."));
            if (cleared is not { Status: FilePublicationStatus.Unchanged, EvidenceRetired: true } ||
                VerifiedFilePublication.Reconcile(target)?.OperationId != first.OperationId)
                throw new InvalidOperationException("Retiring a failed recovery erased the earlier publication restriction.");

            inaccessible?.Dispose();
            inaccessible = null;
            var settled = VerifiedFilePublication.Reconcile(target, path =>
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(candidate))
                    throw new InvalidOperationException("The original candidate changed before adoption.");
            }, first.OperationId);
            if (settled is not { Status: FilePublicationStatus.Verified, EvidenceRetired: true } ||
                VerifiedFilePublication.IsUnresolved(target) || !File.ReadAllBytes(backup).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("The original recovery evidence was not retired after the backup became readable.");
        }
        finally { inaccessible?.Dispose(); }
    }

    private static void PublicationRecoveryHandsOffSurvivingStage()
    {
        using var fixture = new PublicationDirectory();
        string target = fixture.File("profile");
        string restored = fixture.File("recovery-original");
        byte[] original = "original"u8.ToArray(), failedBytes = "failed candidate"u8.ToArray(), recovered = "recovered"u8.ToArray();
        byte[] digest = SHA256.HashData(original);
        File.WriteAllBytes(target, original);
        FileStream? held = null;
        FilePublicationResult failed;
        try
        {
            try
            {
                VerifiedFilePublication.Publish(target, failedBytes, digest, target + ".bak", 1024, default,
                    validate: stage =>
                    {
                        held = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read);
                        throw new IOException("Controlled preparation failure.");
                    });
                throw new InvalidOperationException("The failed preparation reached publication.");
            }
            catch (FilePublicationException error) { failed = error.Result; }
            if (failed.CleanupError is null || !VerifiedFilePublication.IsUnresolved(target))
                throw new InvalidOperationException("The failed preparation did not retain its candidate.");

            var recovery = VerifiedFilePublication.Publish(target, recovered, digest, restored, 1024, default, recovery: true);
            if (recovery.Status != FilePublicationStatus.Verified || VerifiedFilePublication.IsUnresolved(target) ||
                !VerifiedFilePublication.ReinspectRetainedStages(target).SequenceEqual([failed.CandidatePath]) ||
                !File.ReadAllBytes(failed.CandidatePath).AsSpan().SequenceEqual(failedBytes) ||
                !File.ReadAllBytes(target).AsSpan().SequenceEqual(recovered) ||
                !File.ReadAllBytes(restored).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Verified recovery lost its superseded stage or the exact restored contents.");

            string alias = fixture.File("stage-alias");
            if (!CreateHardLink(alias, failed.CandidatePath, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                VerifiedFilePublication.Publish(alias, recovered, SHA256.HashData(failedBytes), alias + ".bak", 1024, default);
                throw new InvalidOperationException("A later publication reused an alias of the retained stage.");
            }
            catch (IOException error) when (error.Message.Contains("aliases an active", StringComparison.Ordinal)) { }
            held?.Dispose();
            held = null;
            File.Delete(alias);
        }
        finally { held?.Dispose(); }

        string probe = fixture.File("capacity-probe");
        File.WriteAllBytes(probe, original);
        var request = new FilePublicationRequest(probe, recovered, digest, probe + ".bak",
            256 * 1024 * 1024 - recovered.Length);
        try
        {
            VerifiedFilePublication.Begin(request);
            throw new InvalidOperationException("The retained stage no longer charged its artifact bytes.");
        }
        catch (ResourceLimitException) { }
        File.Delete(failed.CandidatePath);
        File.WriteAllBytes(failed.CandidatePath, failedBytes);
        if (VerifiedFilePublication.ReinspectRetainedStages(target).Length != 0 ||
            !File.ReadAllBytes(failed.CandidatePath).AsSpan().SequenceEqual(failedBytes))
            throw new InvalidOperationException("Reinspection deleted a replacement with identical stage bytes.");
        if (VerifiedFilePublication.Begin(request).ObserveAsync().GetAwaiter().GetResult().Status != FilePublicationStatus.Verified)
            throw new InvalidOperationException("Reinspection of the retired stage did not return its capacity.");

        string removedDirectory = Directory.CreateDirectory(fixture.File("removed-parent")).FullName;
        string created = Path.Combine(removedDirectory, "profile");
        var copied = VerifiedFilePublication.Publish(created, recovered, null, null, 1024, default,
            issue: (stage, path, _) => File.Copy(stage, path));
        if (copied.Status != FilePublicationStatus.Verified ||
            !VerifiedFilePublication.GetRetainedStagePaths(created).SequenceEqual([copied.CandidatePath]))
            throw new InvalidOperationException("A verified publication discarded a surviving stage.");
        File.Delete(copied.CandidatePath);
        File.Delete(created);
        Directory.Delete(removedDirectory);
        if (VerifiedFilePublication.ReinspectRetainedStages(created).Length != 0)
            throw new InvalidOperationException("Confirmed removal of the parent did not retire the stage.");
    }

    private static void PublicationRetainedCapacityCanBeReconciled(int count, int maxBytes)
    {
        using var fixture = new PublicationDirectory();
        byte[] original = "original"u8.ToArray(), candidate = "candidate"u8.ToArray();
        byte[] digest = SHA256.HashData(original);
        var held = new List<FilePublicationResult>();
        foreach (int index in Enumerable.Range(0, count))
        {
            string target = fixture.File($"profile-{index}");
            File.WriteAllBytes(target, original);
            var result = VerifiedFilePublication.Publish(target, candidate, digest, target + ".bak",
                maxBytes, default, issue: (stage, path, _) => File.Move(stage, path, overwrite: true));
            if (result is not { Status: FilePublicationStatus.UnverifiedPreservation, Preservation: FileImageStatus.Missing })
                throw new InvalidOperationException("The publication did not retain a record with missing preservation.");
            held.Add(result);
        }

        string rejected = fixture.File("rejected");
        File.WriteAllBytes(rejected, original);
        try
        {
            VerifiedFilePublication.Begin(new(rejected, candidate, digest, rejected + ".bak", maxBytes));
            throw new InvalidOperationException("Full retained publication capacity admitted a new replacement.");
        }
        catch (ResourceLimitException) when (count == 15) { }
        catch (IOException error) when (count == 64 && error.Message.Contains("operation is pending", StringComparison.Ordinal)) { }
        if (!File.ReadAllBytes(rejected).AsSpan().SequenceEqual(original) || File.Exists(rejected + ".bak") ||
            Directory.EnumerateFiles(fixture.Path, ".tabtty-*.tmp").Any())
            throw new InvalidOperationException("A rejected publication touched its original or created an artifact.");

        foreach (var record in held)
        {
            File.WriteAllBytes(record.PreservationPath!, original);
            var reconciled = VerifiedFilePublication.Reconcile(record.TargetPath, path =>
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(candidate))
                    throw new InvalidOperationException("The retained candidate changed.");
            }, record.OperationId);
            if (reconciled is not { Status: FilePublicationStatus.Verified, EvidenceRetired: true })
                throw new InvalidOperationException("Full record capacity prevented safe reinspection.");
        }
        if (VerifiedFilePublication.Publish(rejected, candidate, digest, rejected + ".bak", maxBytes, default).Status !=
            FilePublicationStatus.Verified || !File.ReadAllBytes(rejected + ".bak").AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Retired records did not return their admission capacity or preserve the original.");
    }

    private sealed class PublicationDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("TabTtyPublication-").FullName;
        internal string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
