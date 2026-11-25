using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal static class JsonStoreRecovery
{
    internal sealed record Store(string Name, string Path, Action<string> Validate, Action Reload, Func<byte[]> Defaults,
        Func<string, Adoption>? PrepareAdoption = null, Func<byte[], byte[]>? SanitizeBackup = null);

    internal sealed record Adoption(IDisposable? Reservation, Action Apply);

    public static void Show(App app, MainWindow owner, IReadOnlyList<Store> stores) => owner.OpenForm("RecoverStores", form =>
    {
        form.AddStatusBar();
        var choice = new ComboBox { ItemsSource = stores, DisplayMemberPath = nameof(Store.Name), SelectedIndex = 0 };
        form.AddField("RecoveryStore", choice);
        form.AddAsyncAction("RecheckFilePublication", async () =>
        {
            if (choice.SelectedItem is not Store store) return;
            Adoption? adoption = null;
            try
            {
                using var progress = form.BeginProgress("FileOperationPending");
                var outcome = await Task.Run(() => VerifiedFilePublication.Reconcile(store.Path, candidate =>
                {
                    if (store.PrepareAdoption is { } prepare)
                        adoption = app.Dispatcher.Invoke(() => prepare(candidate));
                    else store.Validate(candidate);
                }));
                if (outcome is null)
                {
                    var stages = await Task.Run(() => VerifiedFilePublication.ReinspectRetainedStages(store.Path));
                    if (!ShowRetainedStages(form, stages))
                        form.SetStatusResource("NoFilePublicationToRecheck");
                    return;
                }
                if (outcome.Status != FilePublicationStatus.Verified)
                    throw new FilePublicationException(Text("StoreSaveUnverified"), outcome);
                if (adoption is null) store.Reload();
                else adoption.Apply();
                ShowRetainedStages(form, VerifiedFilePublication.GetRetainedStagePaths(store.Path));
            }
            finally { adoption?.Reservation?.Dispose(); }
        });
        form.AddAsyncAction("RestoreStoreBackup", async () =>
        {
            if (choice.SelectedItem is not Store store) return;
            var picker = new OpenFileDialog { Filter = Text("RecoveryFileFilter"),
                InitialDirectory = LocalState.DirectoryPath, FileName = System.IO.Path.GetFileName(store.Path) + ".bak" };
            if (app.Modal(() => picker.ShowDialog(form.OwnerWindow)) != true) return;
            byte[]? original = await Task.Run(() => FileDigest(store.Path));
            if (!form.IsLoaded || !Confirm(store)) return;
            await RunAsync(store, validate => Restore(store.Path, picker.FileName, original, validate, store.SanitizeBackup));
        });
        form.AddAsyncAction("RecreateStore", async () =>
        {
            if (choice.SelectedItem is not Store store) return;
            byte[]? original = await Task.Run(() => FileDigest(store.Path));
            if (!form.IsLoaded || !Confirm(store)) return;
            await RunAsync(store, validate => Commit(store.Path, store.Defaults(), original, recovery: true,
                validate));
        });
        bool Confirm(Store store) => app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow,
            $"{Text("StoreRecoveryWarning")}\n\n{store.Name}\n{store.Path}", Text("RecoverStores"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes;

        async Task RunAsync(Store store, Action<Action<string>> action)
        {
            Adoption? adoption = null;
            try
            {
                using var progress = form.BeginProgress("FileOperationPending");
                await Task.Run(() => action(candidate =>
                {
                    if (store.PrepareAdoption is { } prepare)
                        adoption = app.Dispatcher.Invoke(() => prepare(candidate));
                    else store.Validate(candidate);
                }));
                if (adoption is null) store.Reload();
                else adoption.Apply();
                ShowRetainedStages(form, VerifiedFilePublication.GetRetainedStagePaths(store.Path));
            }
            finally { adoption?.Reservation?.Dispose(); }
        }
    });

    internal static bool ShowRetainedStages(FormView form, string[] stages)
    {
        if (stages.Length == 0) return false;
        string paths = string.Join("\n", stages);
        form.Reports.Report(("retained-stages", paths), Text("PublicationRetainedStages") + "\n\n" + paths);
        return true;
    }

    public static void Restore(string path, string backup, Action<string> validate) =>
        Restore(path, backup, FileDigest(path), validate);

    private static void Restore(string path, string backup, byte[]? original, Action<string> validate,
        Func<byte[], byte[]>? sanitize = null)
    {
        byte[] bytes = ReadBounded(backup, "StoreRecoveryInvalid")!;
        Commit(path, sanitize?.Invoke(bytes) ?? bytes, original, recovery: true, validate);
    }

    internal static byte[]? ReadBounded(string path, string invalidMessage, bool allowMissing = false)
    {
        FileStream file;
        try { file = File.OpenRead(path); }
        catch (Exception error) when (allowMissing && error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        using (file)
        {
            long length = file.Length;
            if (length is <= 0 or > MaximumStoreBytes) throw new InvalidDataException(Text(invalidMessage));
            byte[] bytes = new byte[(int)length];
            file.ReadExactly(bytes);
            if (file.ReadByte() >= 0) throw new InvalidDataException(Text(invalidMessage));
            return bytes;
        }
    }

    public static void Save(string path, byte[] bytes, byte[]? original, Action<string> validate) =>
        RequireVerified(BeginSave(path, bytes, original, validate).ObserveAsync().GetAwaiter().GetResult());

    internal static FilePublicationOperation BeginSave(string path, byte[] bytes, byte[]? original, Action<string> validate,
        Action<string, string, string?>? issue = null, CancellationToken beforeIssue = default) =>
        Begin(path, bytes, original is null ? null : SHA256.HashData(original), recovery: false, validate, issue, beforeIssue);

    public static JsonObject ParseObject(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[3..];
        _ = new UTF8Encoding(false, true).GetCharCount(bytes);
        return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
            { MaxDepth = 16, AllowDuplicateProperties = false }) as JsonObject ??
            throw new InvalidDataException(Text("StoreRecoveryInvalid"));
    }

    public static void PreserveUnknown(JsonNode updated, JsonNode prior)
    {
        if (updated is JsonObject current && prior is JsonObject old)
        {
            foreach (var (name, value) in old)
            {
                if (!current.ContainsKey(name)) current[name] = value?.DeepClone();
                else if (current[name] is { } next && value is not null) PreserveUnknown(next, value);
            }
        }
        else if (updated is JsonArray currentItems && prior is JsonArray oldItems)
        {
            foreach (JsonObject item in currentItems.OfType<JsonObject>())
            {
                string? identity = item.ContainsKey("setId") ? "setId" : item.ContainsKey("ruleId") ? "ruleId" : null;
                if (identity is null) continue;
                JsonObject? previous = oldItems.OfType<JsonObject>().FirstOrDefault(candidate =>
                    JsonNode.DeepEquals(item[identity], candidate[identity]));
                if (previous is not null) PreserveUnknown(item, previous);
            }
        }
    }

    private const int MaximumStoreBytes = 16 * 1024 * 1024;

    private static byte[]? FileDigest(string path) => VerifiedFilePublication.ReadDigest(path, MaximumStoreBytes);

    private static void Commit(string path, byte[] bytes, byte[]? expected, bool recovery, Action<string>? validate = null)
        => RequireVerified(Begin(path, bytes, expected, recovery, validate).ObserveAsync().GetAwaiter().GetResult());

    private static FilePublicationOperation Begin(string path, byte[] bytes, byte[]? expected, bool recovery,
        Action<string>? validate, Action<string, string, string?>? issue = null, CancellationToken beforeIssue = default)
    {
        string? preservation = expected is null ? null :
            recovery ? path + ".recovery-" + Guid.NewGuid().ToString("N") : path + ".bak";
        return VerifiedFilePublication.Begin(new(path, bytes, expected, preservation, MaximumStoreBytes,
            Validate: validate, Recovery: recovery, CreateDirectory: true, Issue: issue), beforeIssue);
    }

    internal static void RequireVerified(FilePublicationResult result)
    {
        if (result.Status != FilePublicationStatus.Verified)
            throw new FilePublicationException(DescribeFailure(result), result);
    }

    internal static string DescribeFailure(FilePublicationResult result)
    {
        string key = result.Status switch
        {
            FilePublicationStatus.Unchanged => "StoreSaveFailed",
            FilePublicationStatus.Conflict when result.Error is null && result.Candidate == FileImageStatus.Unavailable
                => "StoreChanged",
            _ => "StoreSaveUnverified",
        };
        var lines = new List<string> { Text(key), Describe(result.TargetPath, result.Target) };
        if (result.PreservationPath is { } preservation) lines.Add(Describe(preservation, result.Preservation));
        if (result.Candidate is not (FileImageStatus.Missing or FileImageStatus.Unavailable))
            lines.Add(Describe(result.CandidatePath, result.Candidate));
        if (result.CleanupError is not null) lines.Add(Text("PublicationCleanupFailed"));
        return string.Join("\n\n", lines);

        static string Describe(string path, FileImageStatus state) => $"{Text($"PublishedImage{state}")}\n{path}";
    }
}
