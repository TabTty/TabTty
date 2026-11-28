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
        Func<string, Adoption>? PrepareAdoption = null, Func<byte[], byte[]>? SanitizeBackup = null,
        SemaphoreSlim? WriteGate = null, int MaximumBytes = MaximumStoreBytes, bool KeepPrevious = true);

    internal sealed record Adoption(IDisposable? Reservation, Action Apply);

    internal static Task<string[]> RecheckAsync(App app, Store store) =>
        ApplyAsync(app, store, validate =>
        {
            var outcome = VerifiedFilePublication.Reconcile(store.Path, validate);
            if (outcome is null) return VerifiedFilePublication.ReinspectRetainedStages(store.Path);
            RequireVerified(outcome);
            return VerifiedFilePublication.GetRetainedStagePaths(store.Path);
        });

    internal static Task<string[]> RestoreAsync(App app, Store store, byte[] bytes, byte[]? expected) =>
        ApplyAsync(app, store, validate =>
        {
            Commit(store.Path, bytes, expected, recovery: true, validate, store.MaximumBytes, store.KeepPrevious);
            return VerifiedFilePublication.GetRetainedStagePaths(store.Path);
        });

    private static async Task<string[]> ApplyAsync(App app, Store store, Func<Action<string>, string[]> operation)
    {
        Adoption? adoption = null;
        if (store.WriteGate is { } gate) await gate.WaitAsync();
        try
        {
            string[] stages = await Task.Run(() => operation(candidate =>
            {
                if (store.PrepareAdoption is { } prepare)
                    adoption = app.Dispatcher.Invoke(() => prepare(candidate));
                else store.Validate(candidate);
            }));
            if (adoption is null) store.Reload();
            else adoption.Apply();
            return stages;
        }
        finally
        {
            adoption?.Reservation?.Dispose();
            store.WriteGate?.Release();
        }
    }

    internal static bool ShowRetainedStages(FormView form, string[] stages)
    {
        if (stages.Length == 0) return false;
        string paths = string.Join("\n", stages);
        var fields = RetainedStageFields(paths);
        form.Reports.Report(("retained-stages", paths), ReportField.Describe(fields), fields);
        return true;
    }

    internal static ReportField[] RetainedStageFields(string paths) =>
        [new(Text("OperationReportResult"), Text("PublicationRetainedStages")), new(Text("RetainedFilePaths"), paths, Framed: true)];

    public static void Restore(string path, string backup, Action<string> validate) =>
        Restore(path, backup, FileDigest(path), validate);

    private static void Restore(string path, string backup, byte[]? original, Action<string> validate,
        Func<byte[], byte[]>? sanitize = null)
    {
        byte[] bytes = ReadBounded(backup, "StoreRecoveryInvalid")!;
        Commit(path, sanitize?.Invoke(bytes) ?? bytes, original, recovery: true, validate);
    }

    internal static byte[]? ReadBounded(string path, string invalidMessage, bool allowMissing = false,
        int maximumBytes = MaximumStoreBytes)
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
            if (length <= 0 || length > maximumBytes) throw new InvalidDataException(Text(invalidMessage));
            byte[] bytes = new byte[(int)length];
            file.ReadExactly(bytes);
            if (file.ReadByte() >= 0) throw new InvalidDataException(Text(invalidMessage));
            return bytes;
        }
    }

    public static void Save(string path, byte[] bytes, byte[]? original, Action<string> validate, bool keepPrevious = true) =>
        RequireVerified(BeginSave(path, bytes, original, validate, keepPrevious: keepPrevious)
            .ObserveAsync().GetAwaiter().GetResult());

    internal static FilePublicationOperation BeginSave(string path, byte[] bytes, byte[]? original, Action<string> validate,
        Action<string, string, string?>? issue = null, CancellationToken beforeIssue = default, bool keepPrevious = true) =>
        Begin(path, bytes, original is null ? null : SHA256.HashData(original), recovery: false, validate, issue,
            beforeIssue, keepPrevious: keepPrevious);

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

    internal const int MaximumStoreBytes = 16 * 1024 * 1024;

    private static byte[]? FileDigest(string path) => VerifiedFilePublication.ReadDigest(path, MaximumStoreBytes);

    private static void Commit(string path, byte[] bytes, byte[]? expected, bool recovery, Action<string>? validate = null,
        int maximumBytes = MaximumStoreBytes, bool keepPrevious = true) =>
        RequireVerified(Begin(path, bytes, expected, recovery, validate, maximumBytes: maximumBytes,
            keepPrevious: keepPrevious).ObserveAsync().GetAwaiter().GetResult());

    private static FilePublicationOperation Begin(string path, byte[] bytes, byte[]? expected, bool recovery,
        Action<string>? validate, Action<string, string, string?>? issue = null, CancellationToken beforeIssue = default,
        int maximumBytes = MaximumStoreBytes, bool keepPrevious = true)
    {
        string? preservation = expected is null ? null :
            recovery || !keepPrevious ? path + ".recovery-" + Guid.NewGuid().ToString("N") : path + ".bak";
        return VerifiedFilePublication.Begin(new(path, bytes, expected, preservation, maximumBytes,
            Validate: validate, Recovery: recovery, CreateDirectory: true, Issue: issue,
            RemovePreservationOnVerified: !keepPrevious), beforeIssue);
    }

    internal static void RequireVerified(FilePublicationResult result)
    {
        if (result.Status != FilePublicationStatus.Verified)
            throw new FilePublicationException(DescribeFailure(result), result);
    }

    internal static string DescribeFailure(FilePublicationResult result) => ReportField.Describe(FailureFields(result));

    internal static ReportField[] FailureFields(FilePublicationResult result)
    {
        string key = result.Status switch
        {
            FilePublicationStatus.Unchanged => "StoreSaveFailed",
            FilePublicationStatus.Conflict when result.Error is null && result.Candidate == FileImageStatus.Unavailable
                => "StoreChanged",
            _ => "StoreSaveUnverified",
        };
        var fields = new List<ReportField> { new(Text("OperationReportResult"), Text(key)) };
        AddFile("PublicationTargetPath", "PublicationTargetState", result.TargetPath, result.Target);
        if (result.PreservationPath is { } preservation)
            AddFile("PublicationPreservedPath", "PublicationPreservedState", preservation, result.Preservation);
        if (result.Candidate is not (FileImageStatus.Missing or FileImageStatus.Unavailable))
            AddFile("PublicationPreparedPath", "PublicationPreparedState", result.CandidatePath, result.Candidate);
        if (result.CleanupError is not null)
            fields.Add(new(Text("PublicationCleanupResult"), Text("PublicationCleanupFailed")));
        return fields.ToArray();

        void AddFile(string pathKey, string stateKey, string path, FileImageStatus state)
        {
            fields.Add(new(Text(pathKey), path));
            fields.Add(new(Text(stateKey), Text($"PublishedImage{state}")));
        }
    }
}
