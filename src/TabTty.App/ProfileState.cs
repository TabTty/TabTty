using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal static class ProfileState
{
    private const string TrustKey = "profileTrust";
    private const int PendingLimit = 46 * 1024 * 1024;
    private static readonly object gate = new();
    private static string CatalogPath => Path.Combine(LocalState.DirectoryPath, "profile-catalog.json");
    private static string PendingPath => CatalogPath + ".pending";
    private static ProtectedTrustFile Trust => new(Path.Combine(LocalState.DirectoryPath, "profile-trust.dat"), "profile-trust");
    private static string? temporaryCatalog;

    private sealed record Pair(byte[]? OldCatalog, byte[]? OldTrust, string NewCatalog, string NewTrust);

    internal static bool Initialize()
    {
        try { _ = Read(); return true; }
        catch (Exception error) when (ProfileCatalog.IsFileProblem(error))
        {
            if (ThemedMessageBox.Show(Text("UseTemporaryCatalog"), "TabTty", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No, acceptKey: "UseTemporaryDefaults") != MessageBoxResult.Yes)
                return false;
            temporaryCatalog = CatalogPath;
            return true;
        }
    }

    internal static JsonStoreRecovery.Store RecoveryStore() => new(Text("ProfileCatalog"), CatalogPath,
        ValidateCatalogFile, () => temporaryCatalog = null, () => JsonSerializer.SerializeToUtf8Bytes(Defaults()));

    internal static JsonStoreRecovery.Store TrustRecoveryStore() => new(Text("ManageProfileTrust"), Trust.Path,
        ValidateTrustFile, () => { }, () => Trust.Encode("{}"u8.ToArray()),
        MaximumBytes: ProtectedTrustFile.MaximumBytes, KeepPrevious: false);

    internal static JsonObject Read()
    {
        lock (gate)
        {
            if (temporaryCatalog == CatalogPath) return Defaults();
            ResolvePair();
            return ReadCatalog(out _);
        }
    }

    internal static JsonObject ReadForProfileTrust()
    {
        lock (gate)
        {
            ResolvePair();
            var catalog = Read();
            catalog[TrustKey] = ReadTrust(out _);
            return catalog;
        }
    }

    internal static JsonObject ReadForArchive(out bool exists)
    {
        lock (gate)
        {
            ResolvePair();
            var catalog = ReadCatalog(out byte[]? bytes);
            exists = bytes is not null;
            return catalog;
        }
    }

    internal static (JsonObject Catalog, byte[]? Expected) ReadForArchiveRestore()
    {
        lock (gate)
        {
            ResolvePair();
            var expected = VerifiedFilePublication.ReadDigest(CatalogPath, JsonStoreRecovery.MaximumStoreBytes);
            if (expected is null) return (Defaults(), null);
            try
            {
                var bytes = JsonStoreRecovery.ReadBounded(CatalogPath, "ProfileCatalogInvalid");
                if (!SHA256.HashData(bytes!).AsSpan().SequenceEqual(expected)) throw new IOException(Text("StoreChanged"));
                return (ParseCatalog(bytes), expected);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or InvalidOperationException)
            { return (Defaults(), expected); }
        }
    }

    internal static void Change(Action<JsonObject> update)
    {
        lock (gate)
        {
            EnsureWritable();
            var catalog = ReadCatalog(out byte[]? oldCatalog);
            JsonObject oldRecords = ReadTrust(out byte[]? oldTrust);
            catalog[TrustKey] = oldRecords.DeepClone();
            update(catalog);
            var records = catalog[TrustKey] as JsonObject ?? new JsonObject();
            catalog.Remove(TrustKey);
            byte[] nextCatalog = JsonSerializer.SerializeToUtf8Bytes(catalog);
            bool catalogChanged = !JsonNode.DeepEquals(catalog, ParseCatalog(oldCatalog));
            bool trustChanged = !JsonNode.DeepEquals(records, oldRecords);
            if (!trustChanged)
            {
                if (catalogChanged) SaveCatalog(nextCatalog, oldCatalog);
                return;
            }

            var trust = Trust;
            byte[] nextTrust = trust.Encode(JsonSerializer.SerializeToUtf8Bytes(records));
            if (!catalogChanged)
            {
                trust.Save(nextTrust, oldTrust, ValidateTrustFile);
                return;
            }

            var pair = new Pair(oldCatalog, oldTrust, Digest(nextCatalog)!, Digest(nextTrust)!);
            Directory.CreateDirectory(LocalState.DirectoryPath);
            using (var intent = new FileStream(PendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(intent, pair);
                intent.Flush(flushToDisk: true);
            }
            try
            {
                SaveCatalog(nextCatalog, oldCatalog);
                trust.Save(nextTrust, oldTrust, ValidateTrustFile);
                File.Delete(PendingPath);
            }
            catch (Exception failure) when (ProfileCatalog.IsFileProblem(failure))
            {
                try { ResolvePair(rollback: true); }
                catch (Exception rollback) when (ProfileCatalog.IsFileProblem(rollback))
                {
                    throw new IOException(Text("ProfileRegistrationRollbackFailed") + "\n\n" +
                        failure.Message + "\n\n" + rollback.Message, new AggregateException(failure, rollback));
                }
                throw;
            }
        }
    }

    internal static bool TryChange(Func<JsonObject, bool> update)
    {
        lock (gate)
        {
            EnsureWritable();
            var catalog = ReadCatalog(out byte[]? original);
            if (!update(catalog)) return false;
            SaveCatalog(JsonSerializer.SerializeToUtf8Bytes(catalog), original);
            return true;
        }
    }

    internal static bool RemoveTrust(string location)
    {
        lock (gate)
        {
            ResolvePair();
            var records = ReadTrust(out byte[]? original);
            if (!records.Remove(location)) return false;
            Trust.Save(Trust.Encode(JsonSerializer.SerializeToUtf8Bytes(records)), original, ValidateTrustFile);
            return true;
        }
    }

    internal static void ResetTrust()
    {
        lock (gate)
        {
            ResolvePair();
            Trust.Clear(ValidateTrustFile);
        }
    }

    internal static KeyValuePair<string, string>[] TrustedLocations() => ReadForProfileTrust()[TrustKey]!.AsObject()
        .Select(entry => KeyValuePair.Create(entry.Key, entry.Value!.GetValue<string>()))
        .OrderBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static JsonObject ParseCatalog(byte[]? bytes)
    {
        if (bytes is null) return Defaults();
        var catalog = JsonStoreRecovery.ParseObject(bytes);
        if (catalog["schemaVersion"]?.GetValue<int>() != 1 ||
            catalog["requiredFeatures"] is not JsonArray { Count: 0 } ||
            catalog["registrations"] is not JsonArray { Count: <= 10_000 } registrations || catalog.ContainsKey(TrustKey))
            throw InvalidCatalog();
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? entry in registrations)
        {
            if (entry is not JsonObject item ||
                !Guid.TryParseExact(item["registrationId"]?.GetValue<string>(), "D", out Guid id) || id == Guid.Empty ||
                id.ToString("D") != item["registrationId"]!.GetValue<string>() || !ids.Add(id) ||
                item["path"]?.GetValue<string>() is not { } location || !OrdinaryWindowsPath.IsValid(location) ||
                !Path.IsPathFullyQualified(location) || !paths.Add(location) || item["favorite"]?.GetValue<bool>() is not { } ||
                !item.ContainsKey("lastUsed") || (item["lastUsed"] is not null &&
                    item["lastUsed"]?.GetValue<DateTimeOffset>() is not { Offset.Ticks: 0 }) ||
                (item["groupName"]?.GetValue<string>() is { } group && group != ConnectionProfile.NormalizeGroupName(group)))
                throw InvalidCatalog();
        }
        return catalog;
    }

    private static JsonObject Defaults() => new() { ["schemaVersion"] = 1, ["requiredFeatures"] = new JsonArray(),
        ["registrations"] = new JsonArray() };

    private static InvalidDataException InvalidCatalog() => new(Text("ProfileCatalogInvalid"));

    private static void EnsureWritable()
    {
        if (temporaryCatalog == CatalogPath) throw new IOException(Text("UseTemporaryCatalog"));
        ResolvePair();
    }

    private static JsonObject ReadCatalog(out byte[]? bytes)
    {
        if (VerifiedFilePublication.HasPendingPublication(CatalogPath)) throw new IOException(Text("StoreSaveUnverified"));
        bytes = JsonStoreRecovery.ReadBounded(CatalogPath, "ProfileCatalogInvalid", allowMissing: true);
        return ParseCatalog(bytes);
    }

    private static void ValidateCatalogFile(string path) =>
        _ = ParseCatalog(JsonStoreRecovery.ReadBounded(path, "ProfileCatalogInvalid"));

    private static void SaveCatalog(byte[] bytes, byte[]? original) =>
        JsonStoreRecovery.Save(CatalogPath, bytes, original, ValidateCatalogFile);

    private static void ValidateTrustFile(string path) => _ = ReadTrust(out _, path);

    private static JsonObject ReadTrust(out byte[]? original, string? candidate = null)
    {
        byte[]? bytes = Trust.Read(out original, candidate);
        if (bytes is null) return new();
        var records = JsonStoreRecovery.ParseObject(bytes);
        foreach (var (location, value) in records)
            if (!OrdinaryWindowsPath.IsValid(location) || !Path.IsPathFullyQualified(location) ||
                value is not JsonValue field || !field.TryGetValue<string>(out var version) ||
                version is not { Length: 66 } || version[0] is not ('1' or '2') || version[1] != ':' ||
                !version.Skip(2).All(char.IsAsciiHexDigit))
                throw new InvalidDataException(Text("TrustStoreInvalid"));
        return records;
    }

    private static string? Digest(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    private static void ResolvePair(bool rollback = false)
    {
        byte[]? pending = JsonStoreRecovery.ReadBounded(PendingPath, "StoreSaveUnverified", allowMissing: true,
            maximumBytes: PendingLimit);
        if (pending is null) return;
        var pair = JsonSerializer.Deserialize<Pair>(pending) ?? throw InvalidCatalog();
        var trust = Trust;
        if (VerifiedFilePublication.HasPendingPublication(CatalogPath))
            _ = VerifiedFilePublication.Reconcile(CatalogPath, ValidateCatalogFile);
        if (VerifiedFilePublication.HasPendingPublication(trust.Path))
            _ = VerifiedFilePublication.Reconcile(trust.Path, ValidateTrustFile);
        var catalog = JsonStoreRecovery.ReadBounded(CatalogPath, "ProfileCatalogInvalid", allowMissing: true);
        var records = JsonStoreRecovery.ReadBounded(trust.Path, "TrustStoreInvalid", allowMissing: true,
            maximumBytes: ProtectedTrustFile.MaximumBytes);
        if (rollback || Digest(catalog) != pair.NewCatalog || Digest(records) != pair.NewTrust)
        {
            Undo(trust.Path, records, pair.OldTrust, pair.NewTrust, (bytes, original) =>
                trust.Save(bytes, original, ValidateTrustFile));
            Undo(CatalogPath, catalog, pair.OldCatalog, pair.NewCatalog, SaveCatalog);
        }
        File.Delete(PendingPath);
    }

    private static void Undo(string path, byte[]? current, byte[]? prior, string planned, Action<byte[], byte[]?> save)
    {
        string? actual = Digest(current);
        if (actual == Digest(prior)) return;
        if (actual != planned || VerifiedFilePublication.HasPendingPublication(path))
            throw new IOException(Text("StoreChanged") + "\n" + path);
        if (prior is not null) save(prior, current);
        else File.Delete(path);
    }
}
