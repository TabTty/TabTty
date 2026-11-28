using System.IO;
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

    internal static void UseTestDirectory(string path)
    {
        DirectoryPath = path;
        temporaryDefaults = false;
        verifiedBytes = null;
    }

    public static bool Initialize()
    {
        try { _ = ReadSnapshot(); return true; }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception))
        {
            if (ThemedMessageBox.Show(App.Text("UseTemporaryState"), "TabTty",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No, acceptKey: "UseTemporaryDefaults") != System.Windows.MessageBoxResult.Yes) return false;
            temporaryDefaults = true;
            return true;
        }
    }

    public static JsonStoreRecovery.Store RecoveryStore() => new(App.Text("LocalStateStore"), StatePath,
        path => _ = ReadSnapshot(path), () => temporaryDefaults = false,
        () => JsonSerializer.SerializeToUtf8Bytes(Defaults()), KeepPrevious: false);

    public static JsonObject Read() => ReadSnapshot().State;

    internal static bool IsTemporary => temporaryDefaults;

    public static JsonObject ReadForRecording()
    {
        if (VerifiedFilePublication.HasPendingPublication(StatePath))
            throw new IOException(App.Text("StoreSaveUnverified"));
        return ReadSnapshot().State;
    }

    public static void Change(Action<JsonObject> update) => TryChange(state =>
    {
        update(state);
        return true;
    });

    public static bool TryChange(Func<JsonObject, bool> update)
    {
        if (temporaryDefaults) throw new InvalidOperationException(App.Text("UseTemporaryState"));
        if (VerifiedFilePublication.HasPendingPublication(StatePath))
            throw new IOException(App.Text("StoreSaveUnverified"));
        var (state, original) = ReadSnapshot();
        if (!update(state)) return false;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException(App.Text("LocalStateInvalid"));
        JsonStoreRecovery.Save(StatePath, bytes, original, path => _ = ReadSnapshot(path), keepPrevious: false);
        Volatile.Write(ref verifiedBytes, bytes);
        return true;
    }

    private static JsonObject Defaults() => new()
    {
        ["schemaVersion"] = 1, ["requiredFeatures"] = new JsonArray(), ["lastWindow"] = null,
    };

    private static (JsonObject State, byte[]? Bytes) ReadSnapshot(string? path = null)
    {
        if (path is null && temporaryDefaults) return (Defaults(), null);
        bool live = path is null;
        path ??= StatePath;
        if (live && VerifiedFilePublication.HasPendingPublication(path))
            return Parse(Volatile.Read(ref verifiedBytes));
        byte[]? bytes = JsonStoreRecovery.ReadBounded(path, "LocalStateInvalid", allowMissing: live);
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
            state["requiredFeatures"] is not JsonArray { Count: 0 } || !state.ContainsKey("lastWindow"))
            throw new InvalidDataException(App.Text("LocalStateInvalid"));
        WindowPlacement.Validate(state["lastWindow"]);
        state.Remove("registrations");
        state.Remove("profileTrust");
        state.Remove("selectedVaultPath");
        if (state.ContainsKey("recordingMappings"))
        {
            if (state["recordingMappings"] is not JsonObject mappings)
                throw new InvalidDataException(App.Text("LocalStateInvalid"));
            foreach (var (key, value) in mappings)
                if (key.Length != 64 || !key.All(char.IsAsciiHexDigit) || value is not JsonValue entry ||
                    !entry.TryGetValue<string>(out string? hash) || hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException(App.Text("LocalStateInvalid"));
        }
        return (state, bytes);
    }

}
