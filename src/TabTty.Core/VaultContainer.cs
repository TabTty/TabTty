using System.Security.Cryptography;
using System.Text;
using static System.Buffers.Binary.BinaryPrimitives;

namespace TabTty.Core;

internal static class VaultContainer
{
    public const int HeaderSize = 184;
    public const int TagSize = 16;
    public const int MaxPayload = 16 * 1024 * 1024;
    public const int MinimumMasterLength = 14;
    public const int MaximumMasterLength = 1024;
    public const int MaximumMasterInputLength = MaximumMasterLength * 2;
    public static readonly string MasterLengthMessage =
        $"The master password must contain {MinimumMasterLength} to {MaximumMasterLength} Unicode characters after NFC normalization.";
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] NormalizeMaster(ReadOnlySpan<char> input)
    {
        if (input.Length > MaximumMasterInputLength)
            throw new ArgumentOutOfRangeException(nameof(input), MasterLengthMessage);
        _ = StrictUtf8.GetByteCount(input);
        char[] buffer = new char[MaximumMasterInputLength];
        try
        {
            if (!input.TryNormalize(buffer, out int written, NormalizationForm.FormC))
                throw new ArgumentOutOfRangeException(nameof(input), MasterLengthMessage);
            ReadOnlySpan<char> normalized = buffer.AsSpan(0, written);
            int count = 0;
            foreach (Rune rune in normalized.EnumerateRunes())
            {
                if (Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029)
                    throw new ArgumentException("The master password contains a control character.");
                count++;
            }
            if (count is < MinimumMasterLength or > MaximumMasterLength)
                throw new ArgumentOutOfRangeException(nameof(input), MasterLengthMessage);
            byte[] password = new byte[StrictUtf8.GetByteCount(normalized)];
            StrictUtf8.GetBytes(normalized, password);
            return password;
        }
        finally { Array.Clear(buffer); }
    }

    public static Guid Validate(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize + TagSize ||
            !file[..8].SequenceEqual("TTTYKEY\0"u8) ||
            ReadUInt16BigEndian(file[8..]) != 1 || ReadUInt16BigEndian(file[10..]) != 0 ||
            ReadUInt32BigEndian(file[12..]) != HeaderSize ||
            ReadUInt64BigEndian(file[32..]) != 0x0001_0001_0001_0001 ||
            ReadUInt32BigEndian(file[40..]) != 65536 || ReadUInt32BigEndian(file[44..]) != 3 ||
            ReadUInt32BigEndian(file[48..]) != 4 ||
            ReadUInt32BigEndian(file[180..]) != 0)
            throw new InvalidDataException("Unsupported credential vault header.");
        ulong length = ReadUInt64BigEndian(file[172..]);
        if (length is 0 or > MaxPayload || length != (ulong)(file.Length - HeaderSize - TagSize))
            throw new InvalidDataException("Invalid credential vault size.");
        Guid id = new(file[16..32], bigEndian: true);
        if (id == Guid.Empty) throw new InvalidDataException("Invalid credential vault identity.");
        return id;
    }

    public static byte[] Wrap(Guid id, ReadOnlySpan<byte> password, ReadOnlySpan<byte> root)
    {
        byte[] header = new byte[HeaderSize];
        "TTTYKEY\0"u8.CopyTo(header);
        WriteUInt16BigEndian(header.AsSpan(8), 1);
        WriteUInt32BigEndian(header.AsSpan(12), HeaderSize);
        id.TryWriteBytes(header.AsSpan(16, 16), bigEndian: true, out _);
        for (int offset = 32; offset < 40; offset += 2)
            WriteUInt16BigEndian(header.AsSpan(offset), 1);
        WriteUInt32BigEndian(header.AsSpan(40), 65536);
        WriteUInt32BigEndian(header.AsSpan(44), 3);
        WriteUInt32BigEndian(header.AsSpan(48), 4);
        OpenSsl.Fill(header.AsSpan(52, 16));
        OpenSsl.Fill(header.AsSpan(68, 12));
        Span<byte> key = stackalloc byte[32];
        try
        {
            OpenSsl.DeriveVaultKey(password, header.AsSpan(52, 16), key);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Encrypt(header.AsSpan(68, 12), root, header.AsSpan(80, 32), header.AsSpan(112, TagSize),
                Join("TabTty/vault/v1/wrap\0"u8, header.AsSpan(0, 80)));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return header;
    }

    public static void Unwrap(ReadOnlySpan<byte> file, ReadOnlySpan<byte> password, Span<byte> root)
    {
        Span<byte> key = stackalloc byte[32];
        try
        {
            OpenSsl.DeriveVaultKey(password, file[52..68], key);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(file[68..80], file[80..112], file[112..128], root,
                Join("TabTty/vault/v1/wrap\0"u8, file[..80]));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> wrapper, ReadOnlySpan<byte> root, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaxPayload)
            throw new InvalidDataException("Credential vault payload exceeds its size limit.");
        byte[] file = new byte[HeaderSize + payload.Length + TagSize];
        Span<byte> header = file.AsSpan(0, HeaderSize);
        wrapper[..128].CopyTo(header);
        OpenSsl.Fill(header[128..160]);
        OpenSsl.Fill(header[160..172]);
        WriteUInt64BigEndian(header[172..], (ulong)payload.Length);
        Span<byte> key = stackalloc byte[32];
        try
        {
            DerivePayloadKey(header, root, key);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Encrypt(header[160..172], payload, file.AsSpan(HeaderSize, payload.Length), file.AsSpan(^TagSize),
                Join("TabTty/vault/v1/payload\0"u8, header));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return file;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> file, ReadOnlySpan<byte> root)
    {
        Span<byte> key = stackalloc byte[32];
        byte[] payload = new byte[file.Length - HeaderSize - TagSize];
        try
        {
            DerivePayloadKey(file[..HeaderSize], root, key);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(file[160..172], file[HeaderSize..^TagSize], file[^TagSize..], payload,
                Join("TabTty/vault/v1/payload\0"u8, file[..HeaderSize]));
            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void DerivePayloadKey(ReadOnlySpan<byte> header, ReadOnlySpan<byte> root, Span<byte> output)
    {
        ReadOnlySpan<byte> label = "TabTty/vault/v1/payload-key\0"u8;
        Span<byte> info = stackalloc byte[label.Length + 22];
        label.CopyTo(info);
        header[8..10].CopyTo(info[label.Length..]);
        header[34..38].CopyTo(info[(label.Length + 2)..]);
        header[16..32].CopyTo(info[(label.Length + 6)..]);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, root, output, header[128..160], info);
    }

    private static byte[] Join(ReadOnlySpan<byte> label, ReadOnlySpan<byte> header)
    {
        var aad = new byte[label.Length + header.Length];
        label.CopyTo(aad);
        header.CopyTo(aad.AsSpan(label.Length));
        return aad;
    }
}
