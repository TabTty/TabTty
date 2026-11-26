using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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
    private static string prefix = "TabTty/MacroTrust/v1/";
    private static Action? beforeTestWrite;
    private static Action? beforeTestDelete;

    internal static void UseTestStore(Action? beforeWrite = null, Action? beforeDelete = null, Guid? storeId = null)
    {
        prefix = $"TabTty/Tests/{storeId ?? Guid.NewGuid():N}/MacroTrust/v1/";
        beforeTestWrite = beforeWrite;
        beforeTestDelete = beforeDelete;
    }
    private const uint Generic = 1, LocalMachine = 2;
    private const int NotFound = 1168, RecordSize = 98;
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
        if (!CredRead(prefix + key.Fingerprint, Generic, 0, out nint result))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            Entry entry = Decode(Marshal.PtrToStructure<Credential>(result), key.Fingerprint);
            if (entry.SourceDigest != key.SourceDigest || entry.ConstantsDigest != key.ConstantsDigest || entry.Decision is null)
                throw new InvalidDataException("Invalid macro trust decision.");
            return entry.Decision;
        }
        finally { CredFree(result); }
    }

    internal static unsafe void Save(MacroTrustKey key, MacroTrustDecision decision)
    {
        Span<byte> data = stackalloc byte[RecordSize];
        data[0] = RecordVersion;
        data[1] = (byte)decision;
        Convert.FromHexString(key.SourceDigest).CopyTo(data[2..34]);
        Convert.FromHexString(key.Fingerprint).CopyTo(data[34..66]);
        Convert.FromHexString(key.ConstantsDigest).CopyTo(data[66..]);
        fixed (byte* blob = data)
        {
            var credential = new Credential
            {
                Type = Generic,
                TargetName = prefix + key.Fingerprint,
                BlobSize = (uint)data.Length,
                Blob = (nint)blob,
                Persist = LocalMachine,
            };
            beforeTestWrite?.Invoke();
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        ExpireReviews(key.Fingerprint);
    }

    internal static List<Entry> List()
    {
        var entries = new List<Entry>();
        if (!CredEnumerate(prefix + "*", 0, out uint count, out nint results))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return entries;
            throw new Win32Exception(error);
        }
        try
        {
            for (int i = 0; i < count; i++)
            {
                var value = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(results, i * IntPtr.Size));
                if (value.Type != Generic || value.TargetName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                string fingerprint = value.TargetName[prefix.Length..].ToUpperInvariant();
                if (fingerprint.Length == 64 && fingerprint.All(char.IsAsciiHexDigit))
                    entries.Add(Decode(value, fingerprint));
            }
        }
        finally { CredFree(results); }
        return entries;
    }

    internal static void Forget(string fingerprint)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            throw new ArgumentException(nameof(fingerprint));
        beforeTestDelete?.Invoke();
        if (!CredDelete(prefix + fingerprint, Generic, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error);
        }
        ExpireReviews(fingerprint);
    }

    internal static void Reset()
    {
        // Invalidate pending answers even if a partial removal needs a retry.
        foreach (var review in pendingReviews) review.Expired = true;
        foreach (Entry entry in List()) Forget(entry.Fingerprint);
        if (List().Count != 0) throw new IOException("Macro trust decisions could not be cleared.");
    }

    private static Entry Decode(Credential value, string fingerprint)
    {
        if (value.Type == Generic && value.Persist == LocalMachine && value.BlobSize == RecordSize && value.Blob != 0 &&
            string.Equals(value.TargetName, prefix + fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            byte[] record = new byte[RecordSize];
            Marshal.Copy(value.Blob, record, 0, record.Length);
            var decision = (MacroTrustDecision)record[1];
            if (record[0] == RecordVersion && decision is MacroTrustDecision.Allow or MacroTrustDecision.Block &&
                Convert.ToHexString(record.AsSpan(34, 32)) == fingerprint)
                return new(fingerprint, Convert.ToHexString(record.AsSpan(2, 32)), decision,
                    Convert.ToHexString(record.AsSpan(66)));
        }
        return new(fingerprint, null, null);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        internal uint Flags, Type;
        internal string? TargetName, Comment;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        internal uint BlobSize;
        internal nint Blob;
        internal uint Persist, AttributeCount;
        internal nint Attributes;
        internal string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out nint credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(nint buffer);
}
