using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabTty.App;

internal static class LocalState
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabTty");
    private static readonly string StatePath = Path.Combine(DirectoryPath, "state.json");
    private static bool temporaryDefaults;

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
        () => JsonSerializer.SerializeToUtf8Bytes(Defaults()));

    public static JsonObject Read() => ReadSnapshot().State;

    public static void Change(Action<JsonObject> update)
    {
        if (temporaryDefaults) throw new InvalidOperationException(App.Text("UseTemporaryState"));
        var (state, original) = ReadSnapshot();
        update(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException(App.Text("LocalStateInvalid"));
        JsonStoreRecovery.Save(StatePath, bytes, original);
    }

    private static JsonObject Defaults() => new()
    {
        ["schemaVersion"] = 1, ["requiredFeatures"] = new JsonArray(), ["registrations"] = new JsonArray(),
        ["lastWindow"] = null, ["selectedVaultPath"] = null,
    };

    private static (JsonObject State, byte[]? Bytes) ReadSnapshot(string? path = null)
    {
        if (path is null && temporaryDefaults) return (Defaults(), null);
        path ??= StatePath;
        if (!File.Exists(path)) return (Defaults(), null);
        byte[] bytes = ReadBytes(path);
        JsonObject state = JsonStoreRecovery.ParseObject(bytes);
        if (state["schemaVersion"]?.GetValue<int>() != 1 ||
            state["requiredFeatures"] is not JsonArray { Count: 0 } ||
            state["registrations"] is not JsonArray { Count: <= 10_000 } registrations ||
            !state.ContainsKey("lastWindow") || !state.ContainsKey("selectedVaultPath") ||
            (state["selectedVaultPath"] is not null && state["selectedVaultPath"]?.GetValue<string>() is not { Length: <= 32760 }))
            throw new InvalidDataException(App.Text("LocalStateInvalid"));
        var ids = new HashSet<Guid>();
        WindowPlacement.Validate(state["lastWindow"]);
        foreach (JsonNode? node in registrations)
        {
            if (node is not JsonObject item ||
                !Guid.TryParseExact(item["registrationId"]?.GetValue<string>(), "D", out Guid id) || id == Guid.Empty ||
                id.ToString("D") != item["registrationId"]!.GetValue<string>() || !ids.Add(id) ||
                item["path"]?.GetValue<string>() is not { Length: > 0 and <= 32760 } location || !Path.IsPathFullyQualified(location) ||
                item["displayPath"]?.GetValue<string>() is not { Length: > 0 and <= 32760 } ||
                item["favorite"]?.GetValue<bool>() is not { } || !item.ContainsKey("lastUsed") ||
                (item["lastUsed"] is not null && item["lastUsed"]?.GetValue<DateTimeOffset>() is not { Offset.Ticks: 0 }))
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
