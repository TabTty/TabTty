using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using static TabTty.App.App;

namespace TabTty.App;

internal static class JsonStoreRecovery
{
    internal sealed record Store(string Name, string Path, Action<string> Validate, Action Reload, Func<byte[]> Defaults);

    public static void Show(App app, MainWindow owner, IReadOnlyList<Store> stores) => owner.OpenForm("RecoverStores", form =>
    {
        var choice = new ComboBox { ItemsSource = stores, DisplayMemberPath = nameof(Store.Name), SelectedIndex = 0 };
        form.AddField("RecoveryStore", choice);
        form.AddAsyncAction("RestoreStoreBackup", async () =>
        {
            if (choice.SelectedItem is not Store store) return;
            var picker = new OpenFileDialog { Filter = Text("RecoveryFileFilter"),
                InitialDirectory = LocalState.DirectoryPath, FileName = System.IO.Path.GetFileName(store.Path) + ".bak" };
            if (app.Modal(() => picker.ShowDialog(form.OwnerWindow)) != true || !Confirm(store)) return;
            await RunAsync(store, () => Restore(store.Path, picker.FileName, store.Validate));
        });
        form.AddAsyncAction("RecreateStore", async () =>
        {
            if (choice.SelectedItem is not Store store || !Confirm(store)) return;
            await RunAsync(store, () => Commit(store.Path, store.Defaults(), FileDigest(store.Path), recovery: true,
                store.Validate));
        });
        bool Confirm(Store store) => app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow,
            $"{Text("StoreRecoveryWarning")}\n\n{store.Name}\n{store.Path}", Text("RecoverStores"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes;

        async Task RunAsync(Store store, Action action)
        {
            await Task.Run(action);
            store.Reload();
        }
    });

    public static void Restore(string path, string backup, Action<string> validate)
    {
        byte[]? original = FileDigest(path);
        using var input = File.OpenRead(backup);
        if (input.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException(Text("StoreRecoveryInvalid"));
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() >= 0) throw new InvalidDataException(Text("StoreRecoveryInvalid"));
        Commit(path, bytes, original, recovery: true, validate);
    }

    public static void Save(string path, byte[] bytes, byte[]? original) =>
        Commit(path, bytes, original is null ? null : SHA256.HashData(original), recovery: false);

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

    private static byte[]? FileDigest(string path)
    {
        try { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Commit(string path, byte[] bytes, byte[]? expected, bool recovery, Action<string>? validate = null)
    {
        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string candidate = System.IO.Path.Combine(directory, "recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owns = false;
        try
        {
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owns = true;
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            validate?.Invoke(candidate);
            byte[]? actual = FileDigest(path);
            if (expected is null ? actual is not null : actual is null || !actual.AsSpan().SequenceEqual(expected))
                throw new IOException(Text("StoreChanged"));
            Exception? replacementError = null;
            try
            {
                if (expected is null) File.Move(candidate, path, overwrite: false);
                else File.Replace(candidate, path, recovery ? path + ".recovery-" + Guid.NewGuid().ToString("N") : path + ".bak");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { replacementError = exception; }
            try { actual = FileDigest(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                owns = false;
                throw new IOException(Text("StoreSaveUnverified"), exception);
            }
            if (actual is not null && actual.AsSpan().SequenceEqual(SHA256.HashData(bytes))) return;
            if (replacementError is not null && (expected is null ? actual is null :
                actual is not null && actual.AsSpan().SequenceEqual(expected))) throw new IOException(Text("StoreSaveFailed"), replacementError);
            owns = false;
            throw new IOException(Text("StoreSaveUnverified"), replacementError);
        }
        finally { if (owns && File.Exists(candidate)) File.Delete(candidate); }
    }
}
