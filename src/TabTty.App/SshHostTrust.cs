using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class SshHostTrust(App app, string pathName, Action<string, string, string?>? issue)
{
    public JsonStoreRecovery.Store RecoveryStore() => new(Text("HostKeys"), pathName,
        path => _ = Read(path), () => { },
        () => "{\"schemaVersion\":1,\"requiredFeatures\":[],\"destinations\":[]}"u8.ToArray());

    public IReadOnlyList<string> ApprovedFormats(SshDestination target)
    {
        var (document, _) = Read();
        var destination = document["destinations"]!.AsArray().OfType<JsonObject>().SingleOrDefault(record =>
            record["kind"]!.GetValue<string>() == target.Kind && record["host"]!.GetValue<string>() == target.Host &&
            record["scopeId"]?.GetValue<long>() == target.ScopeId && record["port"]!.GetValue<int>() == target.Port);
        return destination?["keys"]!.AsArray().OfType<JsonObject>()
            .Select(key => key["format"]!.GetValue<string>()).ToArray() ?? [];
    }

    public async Task<bool> ApproveAsync(Window owner, SshHostKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (document, original) = Read();
        var destinations = document["destinations"]!.AsArray();
        var destination = destinations.OfType<JsonObject>().SingleOrDefault(record =>
            record["kind"]!.GetValue<string>() == key.Destination.Kind &&
            record["host"]!.GetValue<string>() == key.Destination.Host &&
            record["scopeId"]?.GetValue<long>() == key.Destination.ScopeId &&
            record["port"]!.GetValue<int>() == key.Destination.Port);
        var keys = destination?["keys"]!.AsArray();
        var previous = keys?.OfType<JsonObject>().SingleOrDefault(record => record["format"]!.GetValue<string>() == key.Format);
        string? priorFingerprint = previous?["fingerprint"]!.GetValue<string>();
        if (priorFingerprint == key.Fingerprint && previous!["publicBlob"]!.GetValue<string>() ==
            Convert.ToBase64String(key.PublicBlob.Span))
            return true;
        var (title, accept) = (destination, previous) switch
        {
            (null, _) => ("NewHostKey", "TrustHostKey"),
            (_, null) => ("AdditionalHostKey", "AddHostKey"),
            _ => ("ChangedHostKey", "ReplaceHostKey"),
        };
        var form = new MainWindow.FormWindow(owner, Text(title));
        form.AddField("SshDestination", ReadOnly(key.Destination.ToString()));
        form.AddField("HostKeyType", ReadOnly(key.Format));
        if (priorFingerprint is not null)
            form.AddField("PreviousFingerprint", ReadOnly(priorFingerprint));
        else if (keys is not null)
            form.AddField("ApprovedHostKeys", ReadOnly(string.Join(Environment.NewLine,
                keys.OfType<JsonObject>().Select(item => $"{item["format"]}   {item["fingerprint"]}"))));
        form.AddField("PresentedFingerprint", ReadOnly(key.Fingerprint));
        form.AddNotice("VerifyHostFingerprint");
        form.AddButtons(() => { }, acceptKey: accept, cancelDefault: true);
        if (ShowCancellable(app, form, token) != true) return false;
        token.ThrowIfCancellationRequested();

        if (destination is null)
        {
            if (destinations.Count >= 10_000) throw new InvalidDataException("The SSH host-key store is full.");
            keys = new JsonArray();
            destination = new JsonObject
            {
                ["kind"] = key.Destination.Kind, ["host"] = key.Destination.Host,
                ["scopeId"] = key.Destination.ScopeId, ["port"] = key.Destination.Port, ["keys"] = keys,
            };
            destinations.Add(destination);
        }
        var entry = new JsonObject
        {
            ["format"] = key.Format, ["publicBlob"] = Convert.ToBase64String(key.PublicBlob.Span),
            ["fingerprint"] = key.Fingerprint, ["approvedAt"] = DateTimeOffset.UtcNow,
        };
        if (previous is null) keys!.Add(entry);
        else
        {
            JsonStoreRecovery.PreserveUnknown(entry, previous);
            keys![keys.IndexOf(previous)] = entry;
        }
        await SaveAsync(document, original, token);
        token.ThrowIfCancellationRequested();
        return true;
    }

    public void Show(MainWindow owner) => owner.OpenForm("HostKeys", form =>
    {
        form.ShowFileOperationStatus();
        var (document, original) = Read();
        var destinations = document["destinations"]!.AsArray();
        var items = destinations.OfType<JsonObject>().SelectMany(destination => destination["keys"]!.AsArray()
            .OfType<JsonObject>().Select(key => new HostRow(destination, key))).ToList();
        var list = new ListBox { MinHeight = 140, MaxHeight = 320, ItemsSource = items };
        form.AddField("HostKeys", list);
        form.AddAsyncButtons(async () =>
        {
            if (list.SelectedItem is not HostRow row) throw new ArgumentException(Text("SelectHostKey"));
            if (app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, Text("ConfirmDeleteHostKey"), Text("HostKeys"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes)
                return false;
            var keys = row.Destination["keys"]!.AsArray();
            int index = keys.IndexOf(row.Key);
            keys.RemoveAt(index);
            if (keys.Count == 0) destinations.Remove(row.Destination);
            try { await SaveAsync(document, original); }
            catch
            {
                if (row.Destination.Parent is null) destinations.Add(row.Destination);
                keys.Insert(index, row.Key);
                throw;
            }
            return true;
        }, acceptKey: "DeleteHostKey", cancelDefault: true);
    });

    internal static bool? ShowCancellable(App app, Window dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cancellation = token.Register(() =>
            dialog.Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        return app.Modal(dialog.ShowDialog);
    }

    private static TextBox ReadOnly(string value) => new() { Text = value, IsReadOnly = true };

    private (JsonObject Document, byte[]? Original) Read(string? path = null)
    {
        bool live = path is null;
        path ??= pathName;
        if (live && VerifiedFilePublication.IsPublicationActive(path)) throw new IOException(Text("FileOperationPending"));
        if (live && VerifiedFilePublication.IsUnresolved(path)) throw new IOException(Text("StoreSaveUnverified"));
        byte[]? bytes = JsonStoreRecovery.ReadBounded(path, "HostStoreInvalid", allowMissing: live);
        if (bytes is null)
            return (new JsonObject
            {
                ["schemaVersion"] = 1, ["requiredFeatures"] = new JsonArray(), ["destinations"] = new JsonArray(),
            }, null);
        JsonObject document = JsonStoreRecovery.ParseObject(bytes);
        if (document["schemaVersion"]?.GetValue<int>() != 1 ||
            document["requiredFeatures"] is not JsonArray { Count: 0 } ||
            document["destinations"] is not JsonArray { Count: <= 10_000 } destinations)
            throw InvalidStore();
        var unique = new HashSet<SshDestination>();
        foreach (var node in destinations)
        {
            if (node is not JsonObject record || record["kind"]?.GetValue<string>() is not { } kind ||
                record["host"]?.GetValue<string>() is not { } host || record["port"]?.GetValue<int>() is not { } port ||
                record["keys"] is not JsonArray { Count: > 0 } keys)
                throw InvalidStore();
            long? scope = record["scopeId"]?.GetValue<long>();
            var saved = new SshDestination(kind, host, scope, port);
            if (SshDestination.Parse(saved.NetworkHost, port) != saved || !unique.Add(saved))
                throw InvalidStore();
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in keys)
            {
                if (entry is not JsonObject item || item["format"]?.GetValue<string>() is not { } format ||
                    !formats.Add(format) || item["publicBlob"]?.GetValue<string>() is not { Length: <= 65536 } encoded ||
                    item["fingerprint"]?.GetValue<string>() is not { } expected ||
                    item["approvedAt"]?.GetValue<DateTimeOffset>() is not { Offset.Ticks: 0 })
                    throw InvalidStore();
                byte[] blob = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(blob) != encoded ||
                    "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=') != expected)
                    throw InvalidStore();
                SshPublicKey.Validate(format, blob);
            }
        }
        return (document, bytes);
    }

    private async Task SaveAsync(JsonObject document, byte[]? original, CancellationToken token = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > 16 * 1024 * 1024) throw InvalidStore();
        var operation = JsonStoreRecovery.BeginSave(pathName, bytes, original, path => _ = Read(path), issue, token);
        JsonStoreRecovery.RequireVerified(await operation.ObserveAsync());
    }

    private static InvalidDataException InvalidStore() => new(Text("HostStoreInvalid"));

    private sealed record HostRow(JsonObject Destination, JsonObject Key)
    {
        public override string ToString()
        {
            var destination = new SshDestination(Destination["kind"]!.GetValue<string>(),
                Destination["host"]!.GetValue<string>(), Destination["scopeId"]?.GetValue<long>(),
                Destination["port"]!.GetValue<int>());
            return $"{destination}   {Key["format"]!.GetValue<string>()}   {Key["fingerprint"]!.GetValue<string>()}";
        }
    }
}
