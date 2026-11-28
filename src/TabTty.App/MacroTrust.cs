using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TabTty.Core;

namespace TabTty.App;

internal enum MacroTrustDecision : byte { Allow = 1, Block = 2 }
internal enum MacroReviewResult { Run, Block, Expired }

internal readonly record struct MacroTrustKey(string SourceDigest, string Fingerprint, string ConstantsDigest)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly string EmptyConstantsDigest = ConstantsVersion([]);

    internal static string ConstantsVersion(IReadOnlyList<MacroConstantsBinding> constants)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("TabTty/MacroConstants/v1"u8);
        AppendLength(hash, constants.Count);
        foreach (var binding in constants) hash.AppendData(Convert.FromHexString(binding.File.Digest));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static MacroTrustKey Create(string sourceDigest, IReadOnlyList<string> arguments, string? constantsDigest = null)
    {
        MacroLimits.ValidateArguments(arguments);
        constantsDigest ??= EmptyConstantsDigest;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("TabTty/MacroTrust/v1"u8);
        hash.AppendData(Convert.FromHexString(sourceDigest));
        hash.AppendData(Convert.FromHexString(constantsDigest));
        AppendLength(hash, arguments.Count);
        foreach (string argument in arguments)
        {
            byte[] encoded = Utf8.GetBytes(argument);
            AppendLength(hash, encoded.Length);
            hash.AppendData(encoded);
        }
        return new(sourceDigest, Convert.ToHexString(hash.GetHashAndReset()), constantsDigest);
    }

    private static void AppendLength(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)value));
        hash.AppendData(bytes);
    }
}

// Used on the application dispatcher. Reads remain live so a store error or an
// out-of-process removal cannot be hidden by an in-memory decision.
internal static class MacroTrust
{
    private static Guid? testStoreId;
    private static string? testDirectory;
    private static ProtectedTrustFile Store => new(Path.Combine(testDirectory ?? LocalState.DirectoryPath,
        testStoreId is { } id ? $"macro-trust-{id:N}.dat" : "macro-trust.dat"), "macro-trust");
    private static Action? beforeTestWrite;
    private static Action? beforeTestDelete;

    internal static void UseTestStore(Action? beforeWrite = null, Action? beforeDelete = null, Guid? storeId = null,
        string? directory = null)
    {
        testStoreId = storeId ?? Guid.NewGuid();
        testDirectory = directory;
        beforeTestWrite = beforeWrite;
        beforeTestDelete = beforeDelete;
    }
    private const int RecordSize = 98;
    private const byte RecordVersion = 1;
    private static readonly HashSet<PendingReview> pendingReviews = [];

    internal sealed class PendingReview(MacroTrustKey key, MacroTrustDecision? remembered, bool elevated) : IDisposable
    {
        internal MacroTrustKey Key { get; } = key;
        internal bool Expired { get; set; }
        internal bool CanRemember(MacroTrustDecision decision) => remembered is null &&
            (decision == MacroTrustDecision.Block || decision == MacroTrustDecision.Allow && !elevated);

        internal MacroReviewResult Confirm(MacroTrustDecision? choice)
        {
            if (Expired || Read(Key) != remembered) return MacroReviewResult.Expired;
            if (choice is not { } decision) return MacroReviewResult.Run;
            if (!CanRemember(decision)) return MacroReviewResult.Expired;
            Save(Key, decision);
            return decision == MacroTrustDecision.Allow ? MacroReviewResult.Run : MacroReviewResult.Block;
        }

        public void Dispose() => pendingReviews.Remove(this);
    }

    internal static (bool RunImmediately, PendingReview? Review) Evaluate(MacroTrustKey key, bool elevated)
    {
        var remembered = Read(key);
        if (remembered == MacroTrustDecision.Block) return (false, null);
        if (remembered == MacroTrustDecision.Allow && !elevated) return (true, null);
        var review = new PendingReview(key, remembered, elevated);
        pendingReviews.Add(review);
        return (false, review);
    }

    private static void ExpireReviews(string fingerprint)
    {
        foreach (var review in pendingReviews)
            if (string.Equals(review.Key.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                review.Expired = true;
    }

    internal readonly record struct Entry(string Fingerprint, string? SourceDigest, MacroTrustDecision? Decision,
        string? ConstantsDigest = null);

    internal static MacroTrustDecision? Read(MacroTrustKey key)
    {
        var entries = ReadEntries(out _);
        if (!entries.TryGetValue(key.Fingerprint, out var entry)) return null;
        if (entry.SourceDigest != key.SourceDigest || entry.ConstantsDigest != key.ConstantsDigest)
            throw new IOException(App.Text("MacroTrustUnavailable"));
        return entry.Decision;
    }

    internal static void Save(MacroTrustKey key, MacroTrustDecision decision)
    {
        var entries = ReadEntries(out byte[]? original);
        beforeTestWrite?.Invoke();
        entries[key.Fingerprint] = new(key.Fingerprint, key.SourceDigest, decision, key.ConstantsDigest);
        WriteEntries(entries, original);
    }

    internal static List<Entry> List() => [.. ReadEntries(out _).Values];

    internal static JsonStoreRecovery.Store RecoveryStore() => new(App.Text("MacroManageTrust"), Store.Path,
        path => _ = ReadEntries(out _, path), () => { }, () => Store.Encode("{}"u8.ToArray()),
        MaximumBytes: ProtectedTrustFile.MaximumBytes, KeepPrevious: false);

    internal static void Forget(string fingerprint)
    {
        ExpireReviews(fingerprint);
        var entries = ReadEntries(out byte[]? original);
        if (!entries.Remove(fingerprint)) return;
        beforeTestDelete?.Invoke();
        WriteEntries(entries, original);
    }

    internal static void Reset()
    {
        foreach (var review in pendingReviews) review.Expired = true;
        beforeTestDelete?.Invoke();
        Store.Clear(path => _ = ReadEntries(out _, path));
    }

    private static Dictionary<string, Entry> ReadEntries(out byte[]? original, string? candidate = null)
    {
        byte[]? data = Store.Read(out original, candidate);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (data is null) return entries;
        var document = JsonStoreRecovery.ParseObject(data);
        foreach (var (fingerprint, value) in document)
        {
            if (value is not System.Text.Json.Nodes.JsonValue field ||
                !field.TryGetValue<string>(out string? text) || text is not { Length: 132 })
                throw new IOException(App.Text("MacroTrustUnavailable"));
            byte[] record = Convert.FromBase64String(text);
            if (record.Length != RecordSize || record[0] != RecordVersion ||
                (MacroTrustDecision)record[1] is not (MacroTrustDecision.Allow or MacroTrustDecision.Block) ||
                Convert.ToHexString(record.AsSpan(34, 32)) != fingerprint)
                throw new IOException(App.Text("MacroTrustUnavailable"));
            entries.Add(fingerprint, new(fingerprint, Convert.ToHexString(record.AsSpan(2, 32)),
                (MacroTrustDecision)record[1], Convert.ToHexString(record.AsSpan(66))));
        }
        return entries;
    }

    private static void WriteEntries(Dictionary<string, Entry> entries, byte[]? original)
    {
        var document = new System.Text.Json.Nodes.JsonObject();
        var record = new byte[RecordSize];
        foreach (var entry in entries.Values)
        {
            record[0] = RecordVersion;
            record[1] = (byte)entry.Decision!.Value;
            Convert.FromHexString(entry.SourceDigest!).CopyTo(record, 2);
            Convert.FromHexString(entry.Fingerprint).CopyTo(record, 34);
            Convert.FromHexString(entry.ConstantsDigest!).CopyTo(record, 66);
            document[entry.Fingerprint] = Convert.ToBase64String(record);
        }
        var store = Store;
        store.Save(store.Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document)), original,
            path => _ = ReadEntries(out _, path));
    }
}
