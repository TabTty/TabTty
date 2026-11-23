using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class ProfileCatalog
{
    internal async Task<ConnectionProfile> ImportFileAsync(Window owner, string path, CancellationToken token)
    {
        string folder = app.ProfileSaveDirectory;
        long reset = trustReset;
        var profile = await LoadForUseAsync(owner, path, token);
        string location = await ProfileLocationAsync(profile, token);
        await registrationChanges.WaitAsync(token);
        try
        {
            var before = ReadTrust(location);
            if (before.Reset == reset && app.ProfileSaveDirectory == folder &&
                await ImportRevisionAsync(profile, location, folder, before, used: false, token) is { } imported)
                return imported;
            throw new IOException(Text("ProfileTrustWriteFailed"));
        }
        finally { registrationChanges.Release(); }
    }

    private static async Task<string> ProfileLocationAsync(ConnectionProfile profile, CancellationToken token)
    {
        if (profile.LocalOpeningLocation is { } local) return local;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await NormalizeLocationAsync(profile.OpenedPath, timeout.Token);
    }

    // Called with registrationChanges held. The source snapshot is the exact version shown for review.
    private async Task<ConnectionProfile?> ImportRevisionAsync(ConnectionProfile source, string location, string folder,
        TrustState before, bool used, CancellationToken token)
    {
        if (folder.Length == 0) throw new IOException(Text("ProfileSaveLocation"));
        if (!Path.GetExtension(location).Equals(".ttty", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Text("InvalidProfileFileName"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var importToken = timeout.Token;
        string normalizedFolder = await NormalizeLocationAsync(folder, importToken);
        if (ReadTrust(location) != before || app.ProfileSaveDirectory != folder) return null;
        bool inPlace = Path.GetDirectoryName(location) == normalizedFolder;
        if (inPlace && before.Registration is not null)
        {
            SelectImportedProfile(location, used);
            return source;
        }

        byte[] bytes = source.SnapshotBytes;
        byte[] digest = SHA256.HashData(bytes);
        string destination = location;
        FileObjectIdentity? owned = null;
        try
        {
            if (!inPlace)
            {
                string stem = Path.GetFileNameWithoutExtension(location);
                string extension = Path.GetExtension(location);
                for (int number = 1; ; number++)
                {
                    importToken.ThrowIfCancellationRequested();
                    string suffix = number == 1 ? "" : " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
                    string filename = stem + suffix + extension;
                    destination = Path.Combine(normalizedFolder, filename);
                    if (filename.Length > 255 || !OrdinaryWindowsPath.IsValid(destination))
                        throw new IOException(Text("InvalidProfileFileName"));
                    if (FindRegistration(LocalState.ReadForProfileTrust(), destination) is not null ||
                        await Task.Run(() => Path.Exists(destination), importToken).WaitAsync(importToken))
                        continue;
                    FileObjectIdentity? prepared = null;
                    var publication = VerifiedFilePublication.Begin(new(destination, bytes, null, null, 1024 * 1024,
                        Create: stage =>
                        {
                            var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            try { prepared = FileObjectIdentity.Read(stream.SafeFileHandle); return stream; }
                            catch { stream.Dispose(); throw; }
                        }, CreateDirectory: true, ExclusiveCreate: true), importToken);
                    var outcome = await publication.ObserveAsync();
                    if (outcome.Status == FilePublicationStatus.Verified)
                    {
                        owned = prepared;
                        break;
                    }
                    if (outcome.Status == FilePublicationStatus.Occupied) continue;
                    throw new FilePublicationException(Text("ProfileImportRecovery") + "\n\n" + destination, outcome);
                }
            }

            string registeredPath = await NormalizeLocationAsync(destination, importToken);
            var imported = await ConnectionProfile.LoadAsync(registeredPath, importToken);
            var installed = await imported.CheckSavedRevisionAsync(registeredPath, importToken);
            if (imported.TrustVersion != source.TrustVersion || installed is null ||
                owned is { } identity && installed != identity)
                throw new IOException(Text("ProfileTrustVersionChanged"));
            if (ReadTrust(location) != before || app.ProfileSaveDirectory != folder)
            {
                await RemoveImportCopyAsync();
                return null;
            }
            importToken.ThrowIfCancellationRequested();
            CommitRegistration(registeredPath, registeredPath, imported, used, imported.TrustVersion);
            owned = null;
            configuredFolder = null;
            SelectImportedProfile(registeredPath, used);
            return imported;
        }
        catch (FilePublicationException error) when (owned is not null && error.Result.Status != FilePublicationStatus.Unchanged)
        {
            // An uncertain state write can already reference this copy.
            throw new IOException(Text("ProfileImportRecovery") + "\n\n" + destination, error);
        }
        catch (Exception error) when (IsFileProblem(error) || error is OperationCanceledException)
        {
            try { await RemoveImportCopyAsync(); }
            catch (Exception cleanup) when (IsFileProblem(cleanup))
            {
                throw new IOException(Text("ProfileImportRecovery") + "\n\n" + destination, new AggregateException(error, cleanup));
            }
            throw;
        }

        async Task RemoveImportCopyAsync()
        {
            if (owned is not { } identity) return;
            if (!await Task.Run(() => FileObjectIdentity.DeleteIfSame(destination, identity,
                    expectedDigest: digest, maximumBytes: bytes.Length)))
                throw new IOException(Text("ProfileImportRecovery") + "\n\n" + destination);
            owned = null;
        }
    }

    private void SelectImportedProfile(string location, bool connecting)
    {
        if (!connecting) lastCreatedProfile = (lastCreatedProfile.Sequence + 1, location);
        ProfileSaved?.Invoke();
    }
}
