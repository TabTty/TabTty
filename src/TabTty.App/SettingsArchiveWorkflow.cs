using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using TabTty.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using StackPanel = System.Windows.Controls.StackPanel;
using static TabTty.App.App;
using static TabTty.App.SettingsArchive;

namespace TabTty.App;

internal sealed class SettingsArchiveWorkflow(App app, Window owner)
{
    private static string ZipFilter => $"{Text("ArchiveFiles")} (*.zip)|*.zip";
    private const int ProfileTargetLimit = MaximumArchiveBytes;

    internal void BackUp()
    {
        var dialog = new MainWindow.FormWindow(owner, Text("ArchiveBackup")) { Width = 560 };
        dialog.AddNotice("ArchiveVaultExcluded");
        dialog.AddStatusBar();
        dialog.AddAsyncButtons(async () =>
        {
            var picker = new SaveFileDialog { Filter = ZipFilter, DefaultExt = ".zip", AddExtension = true,
                FileName = "TabTty-backup.zip" };
            if (app.Modal(() => picker.ShowDialog(dialog)) != true) return false;
            string folder = app.Profiles.ActiveFolder;
            var settings = app.SettingsRecoveryStore();
            var highlights = app.Highlights.RecoveryStore();
            using var progress = dialog.BeginProgress("FileOperationPending");
            await Task.Run(() => Save(picker.FileName, Capture(folder, settings.Path, highlights.Path)));
            Report(dialog, "ArchiveBackup", picker.FileName);
            return true;
        }, acceptKey: "ArchiveBackup", cancelDefault: true);
        app.Modal(dialog.ShowDialog);
    }

    internal async Task VerifyAsync()
    {
        if (SelectZip(owner) is not { } path) return;
        var image = await Task.Run(() => Read(path));
        Report(owner, "ArchiveVerify", Text("ArchiveVerified") + "\n\n" + Describe(image));
    }

    internal async Task RestoreAsync()
    {
        if (SelectZip(owner) is not { } path) return;
        var image = await Task.Run(() => Read(path));
        var dialog = new MainWindow.FormWindow(owner, Text("ArchiveRestore")) { Width = 630 };
        dialog.AddPinnedField("ArchiveFiles", new DialogText(path));
        if (image.ExcludedRegistrations > 0)
            dialog.AddPinnedNotice(string.Format(Text("ArchiveSkippedRegistrations"), image.ExcludedRegistrations));
        var checks = new Dictionary<Item, CheckBox>();
        var choices = new StackPanel();
        foreach (Item item in Enum.GetValues<Item>())
        {
            bool available = item == Item.Profiles ? image.Profiles.Length > 0 : image.Store(item) is not null;
            if (item == Item.Registrations) available &= image.Profiles.Length > 0;
            var check = new CheckBox { Content = available ? Label(item) : $"{Label(item)} ({Text("ArchiveNoData")})",
                IsEnabled = available };
            checks[item] = check;
            choices.Children.Add(check);
        }
        dialog.AddDisplay("ArchiveRestoreItems", choices);
        bool profilesAvailable = checks[Item.Profiles].IsEnabled;
        checks[Item.Registrations].Checked += (_, _) =>
        {
            checks[Item.Profiles].IsChecked = true;
            checks[Item.Profiles].IsEnabled = false;
        };
        checks[Item.Registrations].Unchecked += (_, _) => checks[Item.Profiles].IsEnabled = profilesAvailable;
        dialog.AddStatusBar();
        var run = dialog.Form.AddAsyncButtons(() => ExecuteAsync(dialog, image,
            checks.Where(entry => entry.Value.IsChecked == true).Select(entry => entry.Key).ToArray()),
            acceptKey: "Run", cancelDefault: true);
        run.IsEnabled = false;
        foreach (var check in checks.Values)
        {
            check.Checked += (_, _) => run.IsEnabled = true;
            check.Unchecked += (_, _) => run.IsEnabled = checks.Values.Any(candidate => candidate.IsChecked == true);
        }
        app.Modal(dialog.ShowDialog);
    }

    private async Task<bool> ExecuteAsync(Window dialog, Image image, Item[] selected)
    {
        if (selected.Length == 0) return false;
        var settingsStore = app.SettingsRecoveryStore();
        var highlightStore = app.Highlights.RecoveryStore();
        var profileStore = ProfileState.RecoveryStore();
        JsonObject? settings = selected.Contains(Item.Settings)
            ? ValidateSettings(JsonStoreRecovery.ParseObject(image.Store(Item.Settings)!.Bytes)) : null;
        string? archivedFolder = settings?["profileSaveDirectory"]?.GetValue<string>();
        string folder = settings is null ? app.ProfileSaveDirectory : archivedFolder ?? DefaultProfileSaveDirectory;
        bool restoreProfiles = selected.Contains(Item.Profiles);
        bool alternative = false;
        if ((settings is not null || restoreProfiles) && !SamePath(folder, DefaultProfileSaveDirectory) && !DirectoryPresent(folder))
        {
            if (ChooseFolder(dialog, folder) is not { } chosen) return false;
            folder = chosen;
            alternative = true;
            if (settings is not null) settings["profileSaveDirectory"] = folder;
        }
        if (settings?["credentialVaultPath"]?.GetValue<string>() is { } vault && !SamePath(vault, DefaultVaultPath) &&
            !DirectoryPresent(Path.GetDirectoryName(vault)!))
        {
            if (ChooseFolder(dialog, Path.GetDirectoryName(vault)!) is not { } chosen) return false;
            settings["credentialVaultPath"] = Path.Combine(chosen, Path.GetFileName(vault));
        }
        if (settings is not null) _ = ValidateSettings(settings);
        if (restoreProfiles && string.IsNullOrWhiteSpace(folder)) throw new IOException(Text("ProfileSaveLocation"));

        var targets = await Task.Run(() => restoreProfiles ? image.Profiles.Select(content =>
        {
            string path = Path.Combine(folder, content.Name!);
            byte[]? prior = VerifiedFilePublication.ReadDigest(path, ProfileTargetLimit);
            byte[] next = SHA256.HashData(content.Bytes);
            return new ProfileTarget(path, content, prior, next);
        }).ToArray() : []);
        var stores = new Dictionary<Item, (JsonStoreRecovery.Store Store, byte[] Bytes, byte[]? Expected)>();
        var restoredCatalog = selected.Contains(Item.Registrations) ? ProfileState.ReadForArchiveRestore() : default;
        if (settings is not null)
            stores.Add(Item.Settings, (settingsStore, JsonSerializer.SerializeToUtf8Bytes(settings),
                VerifiedFilePublication.ReadDigest(settingsStore.Path, settingsStore.MaximumBytes)));
        if (selected.Contains(Item.Highlights))
            stores.Add(Item.Highlights, (highlightStore, image.Store(Item.Highlights)!.Bytes,
                VerifiedFilePublication.ReadDigest(highlightStore.Path, highlightStore.MaximumBytes)));
        string[] preview = selected.Select(item => item == Item.Profiles ? $"{Label(item)}: {folder}" :
            item == Item.Registrations ? $"{Label(item)}: {profileStore.Path}" :
            $"{Label(item)}: {stores[item].Store.Path}").ToArray();
        var collisions = targets.Where(target => target.Original is not null && !Same(target.Original, target.Digest))
            .Select(target => target.Path).ToArray();
        string plan = string.Join('\n', preview);
        if (collisions.Length > 0) plan += "\n\n" + Text("ArchiveRecyclePaths") + "\n" + string.Join('\n', collisions);
        var detail = new DialogField(Text("DialogDetails"), new DialogText(plan, 260));
        if (app.Modal(() => ThemedMessageBox.Show(dialog, Text("ArchiveRestoreConfirm"), Text("ArchiveRestore"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, acceptKey: "Run", context: detail))
            != MessageBoxResult.OK) return false;

        var recycled = new List<string>();
        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = new List<Item>();
        Item active = selected[0];
        try
        {
            foreach (Item item in selected.Order())
            {
                active = item;
                if (stores.TryGetValue(item, out var restore))
                    await JsonStoreRecovery.RestoreAsync(app, restore.Store, restore.Bytes, restore.Expected);
                else if (item == Item.Profiles)
                {
                    Directory.CreateDirectory(folder);
                    foreach (var target in targets)
                    {
                        await RestoreProfileAsync(dialog, target, recycled);
                        placed.Add(target.Content.Name!, await ProfileCatalog.NormalizeLocationAsync(target.Path, CancellationToken.None));
                    }
                    app.Profiles.ProfileFolderChanged();
                }
                else
                {
                    byte[] data = RebindRegistrations(restoredCatalog.Catalog!, image.Store(item)!.Bytes, placed);
                    await JsonStoreRecovery.RestoreAsync(app, profileStore, data, restoredCatalog.Expected);
                    app.Profiles.ProfileFolderChanged();
                }
                completed.Add(item);
            }
        }
        catch (Exception error) when (ProfileCatalog.IsFileProblem(error))
        {
            Report(dialog, "ArchiveRestore", Result() + "\n\n" + $"{Text("ArchiveFailed")}: {Label(active)}\n{error.Message}" +
                "\n\n" + Text("ArchiveNotRun") + ": " + string.Join(", ", selected.Except(completed).Except([active]).Select(Label)), error: true);
            return false;
        }
        Report(dialog, "ArchiveRestore", Result());
        return true;

        string Result()
        {
            var result = new List<string> { Text("ArchiveCompleted") + ": " + string.Join(", ", completed.Select(Label)) };
            if (placed.Count > 0) result.Add(Text("ArchiveProfileFiles") + ": " + placed.Count);
            if (completed.Contains(Item.Registrations))
                result.Add(string.Format(Text("ArchiveSkippedRegistrations"), image.ExcludedRegistrations));
            if (alternative && settings is null)
                result.Add(string.Format(Text("ArchiveAlternativeProfileFolder"), folder));
            if (recycled.Count > 0) result.Add(Text("ArchiveRecycled") + "\n" + string.Join('\n', recycled));
            return string.Join("\n\n", result);
        }
    }

    private sealed record ProfileTarget(string Path, Content Content, byte[]? Original, byte[] Digest);

    private static async Task RestoreProfileAsync(Window window, ProfileTarget target, List<string> recycled)
    {
        if (Same(target.Original, target.Digest))
        {
            CheckTarget();
            return;
        }
        string stage = StagePath(target.Path);
        bool owned = false;
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                owned = true;
                await stream.WriteAsync(target.Content.Bytes);
                stream.Flush(flushToDisk: true);
            }
            CheckTarget();
            if (target.Original is not null)
            {
                ProfileRecycling.Recycle(window, target.Path);
                recycled.Add(target.Path);
            }
            try
            {
                File.Move(stage, target.Path, overwrite: false);
                owned = false;
                if (!Same(target.Digest, VerifiedFilePublication.ReadDigest(target.Path, ProfileTargetLimit)))
                    throw new IOException(Text("StoreSaveUnverified"));
            }
            catch (Exception error) when (target.Original is not null && ProfileCatalog.IsFileProblem(error))
            {
                throw new IOException(Text("ArchivePlacementAfterRecycleFailed") + "\n" + target.Path, error);
            }
        }
        finally { if (owned) File.Delete(stage); }

        void CheckTarget()
        {
            if (!Same(target.Original, VerifiedFilePublication.ReadDigest(target.Path, ProfileTargetLimit)))
                throw new IOException(Text("StoreChanged") + "\n" + target.Path);
        }
    }

    private static byte[] RebindRegistrations(JsonObject catalog, byte[] bytes, Dictionary<string, string> placed)
    {
        var source = ProfileState.ParseCatalog(bytes)["registrations"]!.AsArray();
        var registrations = catalog["registrations"]!.AsArray();
        foreach (var item in source.OfType<JsonObject>())
        {
            string path = placed[Path.GetFileName(item["path"]!.GetValue<string>())];
            var existing = registrations.OfType<JsonObject>().SingleOrDefault(entry =>
                entry["path"]?.GetValue<string>() == path);
            if (existing is not null)
            {
                existing["favorite"] = item["favorite"]?.DeepClone();
                existing["lastUsed"] = item["lastUsed"]?.DeepClone();
                continue;
            }
            var copy = (JsonObject)item.DeepClone();
            if (registrations.Count >= MaximumProfiles) throw new IOException(Text("ProfileCatalogInvalid"));
            if (registrations.Any(entry => JsonNode.DeepEquals(entry?["registrationId"], copy["registrationId"])))
                copy["registrationId"] = Guid.NewGuid();
            copy["path"] = path;
            registrations.Add(copy);
        }
        return JsonSerializer.SerializeToUtf8Bytes(catalog);
    }

    private static Image Capture(string folder, string settingsPath, string highlightPath)
    {
        var contents = new List<Content>();
        long total = 0;
        AddStore(Item.Settings, settingsPath);
        AddStore(Item.Highlights, highlightPath);
        var captured = new HashSet<string>(StringComparer.Ordinal);
        if (DirectoryPresent(folder))
            foreach (string path in Directory.EnumerateFiles(folder).Where(path => Path.GetExtension(path)
                .Equals(".ttty", StringComparison.OrdinalIgnoreCase)))
            {
                if (captured.Count >= MaximumProfiles) throw new IOException(Text("ArchiveInvalid"));
                string location = ProfileCatalog.NormalizeLocationAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                AddStore(Item.Profiles, location, Path.GetFileName(path));
                captured.Add(location);
            }
        var catalog = ProfileState.ReadForArchive(out bool catalogExists);
        var registrations = catalog["registrations"]!.AsArray();
        var skipped = registrations.Where(record => !captured.Contains(record!["path"]!.GetValue<string>())).ToArray();
        foreach (var entry in skipped) registrations.Remove(entry);
        if (catalogExists) contents.Add(new(Item.Registrations, null, JsonSerializer.SerializeToUtf8Bytes(catalog)));
        return new(contents.ToArray(), skipped.Length);

        void AddStore(Item item, string path, string? name = null)
        {
            try
            {
                var bytes = JsonStoreRecovery.ReadBounded(path, "ArchiveInvalid", allowMissing: name is null, maximumBytes: Limit(item));
                if (bytes is null) return;
                if ((total += bytes.Length) > MaximumArchiveBytes) throw new IOException(Text("ArchiveInvalid"));
                contents.Add(new(item, name, bytes));
            }
            catch (Exception error) when (ProfileCatalog.IsFileProblem(error))
            { throw new IOException(Label(item) + "\n" + path + "\n" + error.Message, error); }
        }
    }

    private string? SelectZip(Window window)
    {
        var picker = new OpenFileDialog { Filter = ZipFilter, DefaultExt = ".zip" };
        return app.Modal(() => picker.ShowDialog(window)) == true ? picker.FileName : null;
    }

    private string? ChooseFolder(Window window, string path)
    {
        var detail = new DialogField(Text("ProfileListFilePath"), new DialogText(path));
        if (app.Modal(() => ThemedMessageBox.Show(window, Text("ArchiveMissingFolder"), Text("ArchiveRestore"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, acceptKey: "BrowsePath", context: detail))
            != MessageBoxResult.OK) return null;
        var picker = new OpenFolderDialog { Title = Text("ArchiveRestore") };
        return app.Modal(() => picker.ShowDialog(window)) == true ? picker.FolderName : null;
    }

    private static bool DirectoryPresent(string path)
    {
        if (path.Length == 0) return false;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException(Text("ArchiveMissingFolder"));
            return true;
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { return false; }
    }

    private static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first),
        Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    private static string Describe(Image image) => string.Join('\n', Enum.GetValues<Item>().Select(item =>
        $"{Label(item)}: {(item == Item.Profiles ? image.Profiles.Length.ToString() :
            image.Store(item) is null ? Text("ArchiveNoData") : Text("ArchiveIncluded"))}")
        .Append(string.Format(Text("ArchiveSkippedRegistrations"), image.ExcludedRegistrations)));

    private void Report(Window window, string title, string message, bool error = false) => app.Modal(() =>
        ThemedMessageBox.Show(window, "", Text(title), icon: error ? MessageBoxImage.Error : MessageBoxImage.Information,
            context: new DialogField(Text("OperationReportResult"), new DialogText(message, 270))));
}
