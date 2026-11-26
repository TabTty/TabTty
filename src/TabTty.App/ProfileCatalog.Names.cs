using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class ProfileCatalog
{
    private string? configuredFolder;
    private string activeFolder = "";

    internal string ActiveFolder
    {
        get
        {
            string configured = app.ProfileSaveDirectory;
            if (configuredFolder != configured)
            {
                activeFolder = configured.Length == 0 ? "" : ProfileLocation.Normalize(configured);
                configuredFolder = configured;
            }
            return activeFolder;
        }
    }

    internal void ProfileFolderChanged()
    {
        CatalogFolderChanged?.Invoke();
    }

    internal static void ValidateProfileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 250 || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || OrdinaryWindowsPath.IsReservedFileName(name))
            throw new ArgumentException(Text("InvalidProfileFileName"));
    }

    private static string ProfilePathFor(string directory, string name)
    {
        if (directory.Length == 0) throw new IOException(Text("ProfileSaveLocation"));
        ValidateProfileName(name);
        string path = Path.Combine(directory, name + ".ttty");
        if (!OrdinaryWindowsPath.IsValid(path)) throw new ArgumentException(Text("InvalidProfileFileName"));
        return path;
    }

    private async Task<ProfileEdit> PrepareEditAsync(string path, ConnectionProfile original, string name, bool fromExplorer)
    {
        TrustState trust = ReadTrust(path);
        string? destination = null;
        if (original.DisplayName != name)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string target = ProfilePathFor(Path.GetDirectoryName(path)!, name);
            string existing = await NormalizeLocationAsync(target, timeout.Token);
            if (existing != path && (FindRegistration(LocalState.ReadForProfileTrust(), existing) is not null ||
                await Task.Run(() => Path.Exists(target), timeout.Token).WaitAsync(timeout.Token)))
                throw new IOException(Text("ProfileFileInUse"));
            destination = target;
        }
        return new(destination, trust, fromExplorer ? null : original.TrustVersion);
    }

    private async Task<string> FinishEditAsync(string path, ConnectionProfile saved, ProfileEdit edit)
    {
        string destination = edit.Destination ?? path;
        bool moved = destination != path;
        TrustState originalLocation = ReadTrust(path);
        bool registrationAlreadyMoved = moved && originalLocation.Registration is null &&
            edit.Trust.Registration is { } originalId && ReadTrust(destination).Registration == originalId;
        if (originalLocation.Registration != edit.Trust.Registration && !registrationAlreadyMoved)
            throw new IOException(Text("ProfileRenameStateChanged"));
        if (moved && !saved.IsSavedAt(destination))
            await Task.Run(() => saved.MoveSavedRevision(destination));
        try
        {
            TrustState current = ReadTrust(path);
            string? version = edit.PreviousVersion is not null && edit.Trust.Version == edit.PreviousVersion && current == edit.Trust
                ? saved.TrustVersion : null;
            JsonObject snapshot = LocalState.ReadForProfileTrust();
            JsonObject? existing = FindRegistration(snapshot, path);
            bool updateRegistration = existing is not null && (moved ||
                existing["groupName"]?.GetValue<string>() != saved.GroupName);
            bool registrationMoved = moved && existing is null && edit.Trust.Registration is { } id &&
                FindRegistration(snapshot, destination)?["registrationId"]?.GetValue<Guid>() == id;
            bool removeOldTrust = moved && (current.Version is not null || snapshot[TrustKey]?[destination] is not null);
            if (!registrationMoved && (removeOldTrust || updateRegistration || version is not null))
            {
                LocalState.Change(state =>
                {
                    var registration = FindRegistration(state, path);
                    if (moved)
                    {
                        if (registration?["registrationId"]?.GetValue<Guid>() != edit.Trust.Registration ||
                            FindRegistration(state, destination) is not null)
                            throw new IOException(Text("ProfileRenameStateChanged"));
                        if (registration is not null)
                        {
                            registration["path"] = destination;
                        }
                        if (state[TrustKey] is JsonObject trust)
                        {
                            trust.Remove(path);
                            trust.Remove(destination);
                        }
                    }
                    if (updateRegistration && registration is not null)
                    {
                        StoreRegistrationGroup(registration, saved.GroupName);
                    }
                    if (version is not null) StoreVersion(state, destination, version);
                });
            }
        }
        catch (FilePublicationException error) when (moved && error.Result.Status != FilePublicationStatus.Unchanged)
        {
            // Keep the file at the new path while the registration store's outcome is unresolved.
            throw new IOException(string.Format(Text("ProfileRenameRecovery"), destination), error);
        }
        catch (Exception error) when (moved && IsFileProblem(error))
        {
            try { await Task.Run(() => saved.MoveSavedRevision(path)); }
            catch (Exception rollback) when (IsFileProblem(rollback))
            { throw new IOException(string.Format(Text("ProfileRenameRecovery"), destination), new AggregateException(error, rollback)); }
            throw;
        }
        if (moved)
        {
            metadata.Remove(path);
            removals[path] = removals.GetValueOrDefault(path) + 1;
            removals[destination] = removals.GetValueOrDefault(destination) + 1;
            foreach (var tab in app.Windows.OfType<MainWindow>().SelectMany(window => window.TerminalTabs))
                tab.UpdateProfilePath(path, destination);
        }
        RememberMetadata(destination, saved);
        return destination;
    }

    private sealed record ProfileEdit(string? Destination, TrustState Trust, string? PreviousVersion);
}
