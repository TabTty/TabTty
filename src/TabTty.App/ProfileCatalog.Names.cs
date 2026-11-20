using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class ProfileCatalog
{
    private static string AllocateName(string requested, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(requested)) requested = Text("ProfileListName");
        if (names.Add(requested)) return requested;
        for (int i = 1; ; i++)
        {
            string suffix = " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
            string name = string.Concat(requested.EnumerateRunes().Take(256 - suffix.Length)) + suffix;
            if (names.Add(name)) return name;
        }
    }

    private string GetRegistrationName(JsonObject state, string? location, string requested, bool allowSuffix)
    {
        var names = ReadRegistrations(state).Where(item => item.Path != location).Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowSuffix) return AllocateName(requested, names);
        if (names.Contains(requested)) throw new IOException(Text("ProfileNameInUse"));
        return requested;
    }

    private async Task ReadLegacyNamesAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var missing = LocalState.ReadForProfileTrust()["registrations"]!.AsArray().OfType<JsonObject>()
            .Where(item => item["name"] is null).Select(item => item["path"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        if (missing.Count != 0)
            await ReadRegistrationDetailsAsync(ReadRegistrations().Where(item => missing.Contains(item.Path)).ToArray(),
                timeout.Token, legacyOnly: true);
        token.ThrowIfCancellationRequested();
    }

    private async Task<bool> IsInProfileDirectoryAsync(string location, CancellationToken token)
    {
        string directory = app.ProfileSaveDirectory;
        if (directory.Length == 0) return false;
        string normalized = await NormalizeLocationAsync(directory, token);
        return Path.GetDirectoryName(location) == normalized;
    }

    private static string ProfilePathFor(string directory, string name)
    {
        if (directory.Length == 0) throw new IOException(Text("ProfileSaveLocation"));
        if (name.Length > 250 || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || OrdinaryWindowsPath.IsReservedFileName(name))
            throw new ArgumentException(Text("InvalidProfileFileName"));
        string path = Path.Combine(directory, name + ".ttty");
        if (!OrdinaryWindowsPath.IsValid(path)) throw new ArgumentException(Text("InvalidProfileFileName"));
        return path;
    }

    private async Task<ProfileEdit> PrepareEditAsync(string path, ConnectionProfile original, ConnectionProfile draft,
        bool fromExplorer)
    {
        TrustState trust = ReadTrust(path);
        string? name = null;
        string? destination = null;
        if (trust.Registration is not null && original.DisplayName != draft.DisplayName)
        {
            name = GetRegistrationName(LocalState.ReadForProfileTrust(), path, draft.DisplayName, allowSuffix: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (await IsInProfileDirectoryAsync(path, timeout.Token))
            {
                string target = ProfilePathFor(Path.GetDirectoryName(path)!, name);
                if (target != path)
                {
                    string existing = await NormalizeLocationAsync(target, timeout.Token);
                    if (existing != path && (FindRegistration(LocalState.ReadForProfileTrust(), existing) is not null ||
                        await Task.Run(() => Path.Exists(target), timeout.Token).WaitAsync(timeout.Token)))
                        throw new IOException(Text("ProfileFileInUse"));
                    destination = target;
                }
            }
        }
        return new(name, destination, trust, fromExplorer ? null : VersionFor(path, original, null));
    }

    private async Task<string> FinishEditAsync(string path, ConnectionProfile saved, ProfileEdit edit)
    {
        string destination = edit.Destination ?? path;
        bool moved = destination != path;
        if (moved && !saved.IsSavedAt(destination))
            await Task.Run(() => saved.MoveSavedRevision(destination));
        try
        {
            TrustState current = ReadTrust(path);
            string? version = edit.PreviousVersion is not null && edit.Trust.Version == edit.PreviousVersion && current == edit.Trust
                ? VersionFor(destination, saved, null) : null;
            JsonObject snapshot = LocalState.ReadForProfileTrust();
            JsonObject? existing = FindRegistration(snapshot, path);
            bool updateRegistration = existing is not null && (edit.Name is not null ||
                existing["groupName"]?.GetValue<string>() != saved.GroupName);
            bool registrationMoved = moved && existing is null && edit.Trust.Registration is { } id &&
                FindRegistration(snapshot, destination)?["registrationId"]?.GetValue<Guid>() == id;
            if (!registrationMoved && (moved || updateRegistration || version is not null))
            {
                LocalState.Change(state =>
                {
                    var registration = FindRegistration(state, path);
                    if (moved)
                    {
                        if (registration?["registrationId"]?.GetValue<Guid>() != edit.Trust.Registration ||
                            registration is null || FindRegistration(state, destination) is not null)
                            throw new IOException(Text("ProfileRenameStateChanged"));
                        registration!["path"] = destination;
                        registration["displayPath"] = destination;
                        if (state[TrustKey] is JsonObject trust)
                        {
                            trust.Remove(path);
                            trust.Remove(destination);
                        }
                    }
                    if (updateRegistration && registration is not null)
                    {
                        if (edit.Name is not null) registration["name"] = edit.Name;
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

    private sealed record ProfileEdit(string? Name, string? Destination, TrustState Trust, string? PreviousVersion);
}
