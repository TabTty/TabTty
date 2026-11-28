using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TabTty.Core;

namespace TabTty.App;

internal sealed class ProtectedTrustFile(string path, string category)
{
    internal const int MaximumBytes = 16 * 1024 * 1024 + 64 * 1024;
    private const uint Silent = 0x40;
    private const string Protection = "LOCAL=user";
    private readonly byte[] header = Encoding.ASCII.GetBytes($"TabTty/{category}/1\n");

    internal string Path => path;

    internal byte[]? Read(out byte[]? original, string? candidate = null)
    {
        if (candidate is null)
        {
            if (VerifiedFilePublication.IsPublicationActive(path)) throw new IOException(App.Text("FileOperationPending"));
            if (VerifiedFilePublication.IsUnresolved(path)) throw new IOException(App.Text("StoreSaveUnverified"));
        }
        original = JsonStoreRecovery.ReadBounded(candidate ?? path, "TrustStoreInvalid",
            allowMissing: candidate is null, maximumBytes: MaximumBytes);
        if (original is null) return null;

        nint descriptor = 0, data = 0, rule = 0;
        try
        {
            Check(NCryptUnprotectSecret(out descriptor, Silent, original, original.Length, 0, 0, out data, out int length));
            Check(NCryptGetProtectionDescriptorInfo(descriptor, 0, 1, out rule));
            if (!string.Equals(Marshal.PtrToStringUni(rule), Protection, StringComparison.OrdinalIgnoreCase) ||
                length < header.Length || length > MaximumBytes)
                throw Invalid();
            var plaintext = new byte[length];
            Marshal.Copy(data, plaintext, 0, length);
            if (!plaintext.AsSpan().StartsWith(header)) throw Invalid();
            return plaintext[header.Length..];
        }
        finally
        {
            if (rule != 0) LocalFree(rule);
            if (data != 0) LocalFree(data);
            if (descriptor != 0) NCryptCloseProtectionDescriptor(descriptor);
        }
    }

    internal byte[] Encode(byte[] payload)
    {
        if (payload.Length > 16 * 1024 * 1024) throw Invalid();
        nint descriptor = 0, data = 0;
        try
        {
            Check(NCryptCreateProtectionDescriptor(Protection, 0, out descriptor));
            byte[] input = [.. header, .. payload];
            Check(NCryptProtectSecret(descriptor, Silent, input, input.Length, 0, 0, out data, out int length));
            if (length is <= 0 or > MaximumBytes) throw Invalid();
            var result = new byte[length];
            Marshal.Copy(data, result, 0, length);
            return result;
        }
        finally
        {
            if (data != 0) LocalFree(data);
            if (descriptor != 0) NCryptCloseProtectionDescriptor(descriptor);
        }
    }

    internal FilePublicationOperation BeginSave(byte[] bytes, byte[]? original, Action<string> validate,
        Action<string, string, string?>? issue = null, CancellationToken token = default, bool recovery = false) =>
        Begin(bytes, original is null ? null : SHA256.HashData(original), validate, issue, token, recovery);

    private FilePublicationOperation Begin(byte[] bytes, byte[]? expected, Action<string> validate,
        Action<string, string, string?>? issue, CancellationToken token, bool recovery) =>
        VerifiedFilePublication.Begin(new(path, bytes, expected,
            expected is null ? null : path + ".previous-" + Guid.NewGuid().ToString("N"), MaximumBytes, Validate: validate, Recovery: recovery,
            CreateDirectory: true, Issue: issue, RemovePreservationOnVerified: true), token);

    internal void Save(byte[] bytes, byte[]? original, Action<string> validate, bool recovery = false) =>
        JsonStoreRecovery.RequireVerified(BeginSave(bytes, original, validate, recovery: recovery)
            .ObserveAsync().GetAwaiter().GetResult());

    internal void Clear(Action<string> validate)
    {
        if (VerifiedFilePublication.HasPendingPublication(path)) throw new IOException(App.Text("StoreSaveUnverified"));
        byte[]? expected = VerifiedFilePublication.ReadDigest(path, MaximumBytes);
        if (expected is not null)
            JsonStoreRecovery.RequireVerified(Begin(Encode("{}"u8.ToArray()), expected, validate, null, default, recovery: true)
                .ObserveAsync().GetAwaiter().GetResult());
    }

    private static IOException Invalid() => new(App.Text("TrustStoreInvalid"));

    private static void Check(int status)
    {
        if (status != 0) throw new IOException(App.Text("TrustStoreInvalid"), new System.ComponentModel.Win32Exception(status));
    }

    [DllImport("ncrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int NCryptCreateProtectionDescriptor(string descriptor, uint flags, out nint handle);
    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptCloseProtectionDescriptor(nint handle);
    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptGetProtectionDescriptorInfo(nint handle, nint allocator, uint type, out nint info);
    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptProtectSecret(nint descriptor, uint flags, byte[] bytes, int length,
        nint allocator, nint window, out nint result, out int size);
    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptUnprotectSecret(out nint descriptor, uint flags, byte[] bytes, int length,
        nint allocator, nint window, out nint result, out int size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint pointer);
}
