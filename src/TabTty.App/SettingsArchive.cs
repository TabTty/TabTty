using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal static class SettingsArchive
{
    internal enum Item { Settings, Highlights, Profiles, Registrations }

    internal sealed record Content(Item Item, string? Name, byte[] Bytes);
    internal sealed record Image(Content[] Contents, int ExcludedRegistrations)
    {
        internal Content? Store(Item item) => Contents.SingleOrDefault(content => content.Item == item);
        internal Content[] Profiles => Contents.Where(content => content.Item == Item.Profiles).ToArray();
    }

    private sealed record Entry(Item Item, string? Name, int Length, string Digest);
    private sealed record Manifest(int Version, Entry[] Entries, int ExcludedRegistrations);
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16, AllowDuplicateProperties = false,
    };
    internal const int MaximumProfiles = 10_000;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    internal const int MaximumArchiveBytes = 256 * 1024 * 1024;

    internal static string Label(Item item) => Text(item switch
    {
        Item.Settings => "Settings",
        Item.Highlights => "HighlightSets",
        Item.Profiles => "ArchiveProfileFiles",
        _ => "ArchiveProfileRegistrations",
    });

    internal static void Save(string path, Image image)
    {
        Validate(image);
        byte[]? original = VerifiedFilePublication.ReadDigest(path, MaximumArchiveBytes + MaximumManifestBytes);
        string stage = StagePath(path);
        bool owned = false;
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                owned = true;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = new Manifest(1, image.Contents.Select(content => new Entry(content.Item,
                        content.Name, content.Bytes.Length, Digest(content.Bytes))).ToArray(), image.ExcludedRegistrations);
                    Write(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, json));
                    for (int index = 0; index < image.Contents.Length; index++)
                        Write(archive, EntryName(index), image.Contents[index].Bytes);
                }
                stream.Flush(flushToDisk: true);
            }
            _ = Read(stage);
            byte[]? prepared = VerifiedFilePublication.ReadDigest(stage, MaximumArchiveBytes + MaximumManifestBytes);
            byte[]? observed = VerifiedFilePublication.ReadDigest(path, MaximumArchiveBytes + MaximumManifestBytes);
            if (!Same(original, observed)) throw new IOException(Text("StoreChanged"));
            if (original is null) File.Move(stage, path, overwrite: false);
            else File.Replace(stage, path, null);
            owned = false;
            if (!Same(prepared, VerifiedFilePublication.ReadDigest(path, MaximumArchiveBytes + MaximumManifestBytes)))
                throw new IOException(Text("StoreSaveUnverified"));
        }
        finally { if (owned) File.Delete(stage); }
    }

    internal static Image Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumArchiveBytes + MaximumManifestBytes) throw Invalid();
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > MaximumProfiles + 4) throw Invalid();
        var names = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if (!names.TryAdd(entry.FullName, entry)) throw Invalid();
        if (!names.Remove("manifest.json", out var manifestEntry)) throw Invalid();
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(manifestEntry, MaximumManifestBytes), json);
        if (manifest is not { Version: 1, Entries: not null, ExcludedRegistrations: >= 0 and <= MaximumProfiles } ||
            manifest.Entries.Length != names.Count) throw Invalid();
        var contents = new Content[manifest.Entries.Length];
        long total = 0;
        for (int index = 0; index < contents.Length; index++)
        {
            if (manifest.Entries[index] is not { } entry || !Enum.IsDefined(entry.Item) ||
                !names.Remove(EntryName(index), out var zipped) || zipped.Length != entry.Length ||
                (total += entry.Length) > MaximumArchiveBytes) throw Invalid();
            byte[] bytes = ReadEntry(zipped, Limit(entry.Item));
            if (Digest(bytes) != entry.Digest) throw Invalid();
            contents[index] = new(entry.Item, entry.Name, bytes);
        }
        var image = new Image(contents, manifest.ExcludedRegistrations);
        Validate(image);
        return image;
    }

    internal static int Limit(Item item) => item switch
    {
        Item.Settings => 64 * 1024,
        Item.Profiles => 1024 * 1024,
        _ => JsonStoreRecovery.MaximumStoreBytes,
    };

    private static void Validate(Image image)
    {
        if (image.Contents.Length > MaximumProfiles + 3 ||
            image.Contents.Sum(content => (long)content.Bytes.Length) > MaximumArchiveBytes) throw Invalid();
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stores = new HashSet<Item>();
        foreach (var entry in image.Contents)
        {
            if (entry.Bytes.Length == 0 || entry.Bytes.Length > Limit(entry.Item)) throw Invalid();
            if (entry.Item == Item.Profiles)
            {
                if (entry.Name is not { Length: > 0 and <= 255 } name || Path.GetFileName(name) != name ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !Path.GetExtension(name).Equals(".ttty", StringComparison.OrdinalIgnoreCase) ||
                    OrdinaryWindowsPath.IsReservedFileName(name) ||
                    !OrdinaryWindowsPath.IsValid(Path.Combine(@"C:\TabTty", name)) || !profiles.Add(name)) throw Invalid();
                try { _ = ConnectionProfile.ParseProfile(name, entry.Bytes); }
                catch (Exception error) when (ProfileCatalog.IsFileProblem(error))
                { throw new InvalidDataException(Label(entry.Item) + "\n" + name + "\n" + error.Message, error); }
            }
            else if (entry.Name is not null || !stores.Add(entry.Item)) throw Invalid();
            else if (entry.Item == Item.Settings) _ = ValidateSettings(JsonStoreRecovery.ParseObject(entry.Bytes));
            else if (entry.Item == Item.Highlights) HighlightCatalog.ValidateArchive(entry.Bytes);
        }
        if (image.Store(Item.Registrations) is not { } registration) return;
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ProfileState.ParseCatalog(registration.Bytes)["registrations"]!.AsArray())
        {
            string name = Path.GetFileName(item!["path"]!.GetValue<string>());
            if (!profiles.Contains(name) || !mapped.Add(name)) throw Invalid();
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length <= 0 || entry.Length > maximum) throw Invalid();
        using var stream = entry.Open();
        var data = new byte[entry.Length];
        stream.ReadExactly(data);
        if (stream.ReadByte() != -1) throw Invalid();
        return data;
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
    }

    internal static bool Same(byte[]? first, byte[]? second) => first is null ? second is null :
        second is not null && first.AsSpan().SequenceEqual(second);
    internal static string StagePath(string path) => Path.Combine(Path.GetDirectoryName(path)!, $".tabtty-{Guid.NewGuid():N}.tmp");
    private static string EntryName(int index) => $"entries/{index:D5}";
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidDataException Invalid() => new(Text("ArchiveInvalid"));
}
