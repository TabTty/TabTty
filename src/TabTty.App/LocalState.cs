using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TabTty.Core;

namespace TabTty.App;

internal static class LocalState
{
    public static string DirectoryPath { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabTty");
    private static string StatePath => Path.Combine(DirectoryPath, "state.json");
    private static bool temporaryDefaults;
    private static byte[]? verifiedBytes;

    internal static void UseTestDirectory(string path) => DirectoryPath = path;

    public static bool Initialize()
    {
        try { _ = ReadSnapshot(); return true; }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception))
        {
            if (ThemedMessageBox.Show(App.Text("UseTemporaryState"), "TabTty",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) return false;
            temporaryDefaults = true;
            return true;
        }
    }

    public static JsonStoreRecovery.Store RecoveryStore() => new(App.Text("LocalStateStore"), StatePath,
        path => _ = ReadSnapshot(path), () => temporaryDefaults = false,
        () => JsonSerializer.SerializeToUtf8Bytes(Defaults()), SanitizeBackup: bytes =>
        {
            JsonObject state = Parse(bytes).State;
            state.Remove("profileTrust");
            return JsonSerializer.SerializeToUtf8Bytes(state);
        });

    public static JsonObject Read() => ReadSnapshot().State;

    public static JsonObject ReadForProfileTrust()
    {
        if (VerifiedFilePublication.HasPendingPublication(StatePath))
            throw new IOException(App.Text("StoreSaveUnverified"));
        return ReadSnapshot().State;
    }

    public static void Change(Action<JsonObject> update)
    {
        if (temporaryDefaults) throw new InvalidOperationException(App.Text("UseTemporaryState"));
        if (VerifiedFilePublication.HasPendingPublication(StatePath))
            throw new IOException(App.Text("StoreSaveUnverified"));
        var (state, original) = ReadSnapshot();
        update(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException(App.Text("LocalStateInvalid"));
        JsonStoreRecovery.Save(StatePath, bytes, original, path => _ = ReadSnapshot(path));
        Volatile.Write(ref verifiedBytes, bytes);
    }

    private static JsonObject Defaults() => new()
    {
        ["schemaVersion"] = 1, ["requiredFeatures"] = new JsonArray(), ["registrations"] = new JsonArray(),
        ["lastWindow"] = null, ["selectedVaultPath"] = null,
    };

    private static (JsonObject State, byte[]? Bytes) ReadSnapshot(string? path = null)
    {
        if (path is null && temporaryDefaults) return (Defaults(), null);
        bool live = path is null;
        path ??= StatePath;
        if (live && VerifiedFilePublication.HasPendingPublication(path))
            return Parse(Volatile.Read(ref verifiedBytes));
        byte[]? bytes = File.Exists(path) ? ReadBytes(path) : null;
        if (live && VerifiedFilePublication.HasPendingPublication(path))
            return Parse(Volatile.Read(ref verifiedBytes));
        var snapshot = Parse(bytes);
        if (live) Volatile.Write(ref verifiedBytes, bytes);
        return snapshot;
    }

    private static (JsonObject State, byte[]? Bytes) Parse(byte[]? bytes)
    {
        if (bytes is null) return (Defaults(), null);
        JsonObject state = JsonStoreRecovery.ParseObject(bytes);
        if (state["schemaVersion"]?.GetValue<int>() != 1 ||
            state["requiredFeatures"] is not JsonArray { Count: 0 } ||
            state["registrations"] is not JsonArray { Count: <= 10_000 } registrations ||
            !state.ContainsKey("lastWindow") || !state.ContainsKey("selectedVaultPath") ||
            (state["selectedVaultPath"] is not null && state["selectedVaultPath"]?.GetValue<string>() is not { Length: <= 32760 }))
            throw new InvalidDataException(App.Text("LocalStateInvalid"));
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state["selectedVaultPath"]?.GetValue<string>() is { Length: > 0 } legacyPath)
            try { _ = App.NormalizeVaultPath(legacyPath); }
            catch (ArgumentException error) { throw new InvalidDataException(App.Text("LocalStateInvalid"), error); }
        WindowPlacement.Validate(state["lastWindow"]);
        foreach (JsonNode? node in registrations)
        {
            if (node is not JsonObject item ||
                !Guid.TryParseExact(item["registrationId"]?.GetValue<string>(), "D", out Guid id) || id == Guid.Empty ||
                id.ToString("D") != item["registrationId"]!.GetValue<string>() || !ids.Add(id) ||
                item["path"]?.GetValue<string>() is not { } location || !OrdinaryWindowsPath.IsValid(location) || !Path.IsPathFullyQualified(location) ||
                item["displayPath"]?.GetValue<string>() is not { Length: > 0 and <= 32760 } ||
                item["favorite"]?.GetValue<bool>() is not { } || !item.ContainsKey("lastUsed") ||
                (item["lastUsed"] is not null && item["lastUsed"]?.GetValue<DateTimeOffset>() is not { Offset.Ticks: 0 }))
                throw new InvalidDataException(App.Text("LocalStateInvalid"));
            if (item["groupName"]?.GetValue<string>() is { } groupName &&
                groupName != ConnectionProfile.NormalizeGroupName(groupName))
                throw new InvalidDataException(App.Text("LocalStateInvalid"));
            if (item["name"]?.GetValue<string>() is { } name && (string.IsNullOrWhiteSpace(name) || name.Contains('\0') ||
                name.EnumerateRunes().Count() > 256 || !names.Add(name)))
                throw new InvalidDataException(App.Text("LocalStateInvalid"));
        }
        if (state.ContainsKey("profileTrust"))
        {
            if (state["profileTrust"] is not JsonObject trust)
                throw new InvalidDataException(App.Text("LocalStateInvalid"));
            foreach (var (location, version) in trust)
                if (!OrdinaryWindowsPath.IsValid(location) || !Path.IsPathFullyQualified(location) ||
                    version is not JsonValue value || !value.TryGetValue<string>(out string? stamp) ||
                    stamp is not { Length: 66 } || stamp[1] != ':' ||
                    !stamp.Skip(2).All(char.IsAsciiHexDigit))
                    throw new InvalidDataException(App.Text("LocalStateInvalid"));
        }
        return (state, bytes);
    }

    private static byte[] ReadBytes(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException(App.Text("LocalStateInvalid"));
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() >= 0) throw new InvalidDataException(App.Text("LocalStateInvalid"));
        return bytes;
    }
}
