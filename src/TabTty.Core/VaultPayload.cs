using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabTty.Core;

internal sealed class CredentialLookupException(MacroErrorCode code, string message) : KeyNotFoundException(message)
{
    internal MacroErrorCode Code { get; } = code;
}

internal sealed class CredentialVaultStaleException(string message) : IOException(message);

internal sealed class DuplicateCredentialNameException() : ArgumentException("A credential with this name already exists.");

public sealed record VaultEntryInfo(string Name, string Username, string Kind = "password",
    string? PublicType = null, string? Fingerprint = null);

public sealed class VaultSecret : IDisposable
{
    private byte[]? value;
    public VaultSecret(byte[] ownedBytes) => value = ownedBytes ?? throw new ArgumentNullException(nameof(ownedBytes));
    public ReadOnlyMemory<byte> Bytes => value ?? throw new ObjectDisposedException(nameof(VaultSecret));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref value, null) is { } bytes) CryptographicOperations.ZeroMemory(bytes);
    }
}

internal static class VaultPayload
{
    public const int MaxEntries = 1024;
    private const int MaxPassword = 16 * 1024;
    private const int MaxPrivateKey = 64 * 1024;

    public static byte[] Empty(Guid id) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        vaultId = id,
        entries = Array.Empty<object>(),
    });

    public static byte[] AddEntry(byte[] payload, Guid vaultId, VaultEntryInfo entry, ReadOnlySpan<byte> secret)
    {
        ValidateEntry(entry.Name, entry.Username);
        if (entry.Kind == "password") ValidatePassword(secret);
        var entries = Read(payload, vaultId, null, out _, out int insertion);
        if (entries.Any(candidate => candidate.Name == entry.Name)) throw new DuplicateCredentialNameException();
        if (entries.Count == MaxEntries) throw new InvalidDataException("The credential vault has reached its entry limit.");
        int capacity = checked(payload.Length + 4096 + 6 * (entry.Name.Length + entry.Username.Length) +
            Base64.GetMaxEncodedToUtf8Length(secret.Length));
        var buffer = new OwnedBuffer(capacity);
        try
        {
            buffer.Append(payload.AsSpan(0, insertion));
            if (entries.Count > 0) buffer.Append(","u8);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteEntry(writer, entry, secret);
            }
            buffer.Append(payload.AsSpan(insertion));
            return buffer.Written.ToArray();
        }
        finally { buffer.Clear(); }
    }

    public static byte[] UpdateEntry(byte[] payload, Guid vaultId, string originalName, string name, string username,
        ReadOnlySpan<byte> password, bool changePassword)
    {
        var entries = Read(payload, vaultId, originalName, out byte[]? oldSecret, out _);
        try
        {
            var entry = entries.SingleOrDefault(entry => entry.Name == originalName)
                ?? throw new KeyNotFoundException("The credential entry is missing.");
            ValidateEntry(name, username);
            if (name != originalName && entries.Any(candidate => candidate.Name == name))
                throw new DuplicateCredentialNameException();
            if (changePassword)
            {
                if (entry.Kind != "password") throw new ArgumentException("The selected entry is not a password.");
                ValidatePassword(password);
            }
            var (start, end) = Locate(payload, originalName);
            var buffer = new OwnedBuffer(payload.Length + 16384 + Base64.GetMaxEncodedToUtf8Length(password.Length));
            try
            {
                buffer.Append(payload.AsSpan(0, start));
                using (var writer = new Utf8JsonWriter(buffer))
                    WriteEntry(writer, entry with { Name = name, Username = username },
                        changePassword ? password : oldSecret!);
                buffer.Append(payload.AsSpan(end));
                return buffer.Written.ToArray();
            }
            finally { buffer.Clear(); }
        }
        finally
        {
            if (oldSecret is not null) CryptographicOperations.ZeroMemory(oldSecret);
        }
    }

    public static byte[] DeleteEntry(byte[] payload, Guid vaultId, string originalName)
    {
        if (!Read(payload, vaultId, null, out _, out _).Any(entry => entry.Name == originalName))
            throw new KeyNotFoundException("The credential entry is missing.");
        var (start, end) = Locate(payload, originalName);
        int before = start;
        while (before > 0 && payload[before - 1] is 9 or 10 or 13 or 32) before--;
        if (before > 0 && payload[before - 1] == ',') start = before - 1;
        else
        {
            int after = end;
            while (after < payload.Length && payload[after] is 9 or 10 or 13 or 32) after++;
            if (after < payload.Length && payload[after] == ',') end = after + 1;
        }
        byte[] result = new byte[payload.Length - end + start];
        payload.AsSpan(0, start).CopyTo(result);
        payload.AsSpan(end).CopyTo(result.AsSpan(start));
        return result;
    }

    private static (int Start, int End) Locate(ReadOnlySpan<byte> payload, string originalName)
    {
        var reader = new Utf8JsonReader(payload);
        int start = 0;
        bool selected = false;
        while (reader.Read())
        {
            if (reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.StartObject)
            {
                start = checked((int)reader.TokenStartIndex);
                selected = false;
            }
            else if (reader.CurrentDepth == 3 && reader.TokenType == JsonTokenType.PropertyName &&
                reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                selected = reader.GetString() == originalName;
            }
            else if (reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.EndObject && selected)
                return (start, checked((int)reader.BytesConsumed));
        }
        throw new KeyNotFoundException("The credential entry is missing.");
    }

    private static void WriteEntry(Utf8JsonWriter writer, VaultEntryInfo entry, ReadOnlySpan<byte> secret)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", entry.Kind);
        writer.WriteString("name", entry.Name);
        writer.WriteString("username", entry.Username);
        if (entry.Kind == "privateKey")
        {
            writer.WriteString("publicType", entry.PublicType);
            writer.WriteString("fingerprint", entry.Fingerprint);
            writer.WriteBase64String("privateKeyPkcs8", secret);
        }
        else writer.WriteBase64String("passwordUtf8", secret);
        writer.WriteEndObject();
    }

    public static IReadOnlyList<VaultEntryInfo> Read(ReadOnlySpan<byte> payload, Guid vaultId, string? wanted,
        out byte[]? selected, out int insertion)
    {
        selected = null;
        insertion = 0;
        try
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 8 });
            ReadToken(ref reader, JsonTokenType.StartObject);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<VaultEntryInfo>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = Property(ref reader, fields);
                switch (name)
                {
                    case "schemaVersion":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int version) || version != 1)
                            throw InvalidPayload();
                        break;
                    case "vaultId":
                        if (ReadId(ref reader) != vaultId) throw InvalidPayload();
                        break;
                    case "entries":
                        if (reader.TokenType != JsonTokenType.StartArray) throw InvalidPayload();
                        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                        {
                            if (entries.Count == MaxEntries) throw InvalidPayload();
                            VaultEntryInfo entry = ReadEntry(ref reader, wanted, ref selected);
                            if (!names.Add(entry.Name)) throw InvalidPayload();
                            entries.Add(entry);
                        }
                        if (reader.TokenType != JsonTokenType.EndArray) throw InvalidPayload();
                        insertion = checked((int)reader.TokenStartIndex);
                        break;
                    default:
                        throw InvalidPayload();
                }
            }
            if (fields.Count != 3 || reader.TokenType != JsonTokenType.EndObject || reader.Read())
                throw InvalidPayload();
            return entries;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException
            or InvalidDataException or CryptographicException)
        {
            if (selected is not null) CryptographicOperations.ZeroMemory(selected);
            selected = null;
            throw InvalidPayload();
        }
    }

    private static VaultEntryInfo ReadEntry(ref Utf8JsonReader reader, string? wanted, ref byte[]? selected)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string? name = null, username = null, kind = null, publicType = null, fingerprint = null;
        byte[]? secret = null;
        bool isKey = false;
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                switch (Property(ref reader, fields))
                {
                    case "kind": kind = ReadText(ref reader, 32); break;
                    case "name": name = ReadText(ref reader, 6144); break;
                    case "username":
                        username = ReadText(ref reader, 6144);
                        break;
                    case "passwordUtf8":
                        if (secret is not null) throw InvalidPayload();
                        secret = ReadBase64(ref reader, MaxPassword);
                        break;
                    case "privateKeyPkcs8":
                        if (secret is not null) throw InvalidPayload();
                        isKey = true;
                        secret = ReadBase64(ref reader, MaxPrivateKey);
                        break;
                    case "publicType": publicType = ReadText(ref reader, 128); break;
                    case "fingerprint": fingerprint = ReadText(ref reader, 128); break;
                    default: throw InvalidPayload();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || name is null || secret is null ||
                !fields.Contains("username") || kind != (isKey ? "privateKey" : "password") ||
                fields.Count != (isKey ? 6 : 4))
                throw InvalidPayload();
            ValidateEntry(name, username);
            if (isKey)
            {
                if (publicType is null || fingerprint is null) throw InvalidPayload();
                using SshPrivateKey key = SshPrivateKey.FromPkcs8(secret);
                if (key.Fingerprint != fingerprint || key.Algorithm != publicType) throw InvalidPayload();
            }
            else ValidatePassword(secret);
            if (name == wanted && selected is null)
            {
                selected = secret;
                secret = null;
            }
            return new VaultEntryInfo(name, username!, kind, publicType, fingerprint);
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] ReadBase64(ref Utf8JsonReader reader, int limit)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped ||
            reader.ValueSpan.Length > Base64.GetMaxEncodedToUtf8Length(limit))
            throw InvalidPayload();
        ReadOnlySpan<byte> encoded = reader.ValueSpan;
        if (!Base64.IsValid(encoded, out int decodedLength) || decodedLength > limit)
            throw InvalidPayload();
        byte[] secret = new byte[decodedLength];
        byte[] canonical = new byte[encoded.Length];
        try
        {
            if (Base64.DecodeFromUtf8(encoded, secret, out _, out int bytes) != OperationStatus.Done ||
                bytes != decodedLength || Base64.EncodeToUtf8(secret, canonical, out _, out int length) != OperationStatus.Done ||
                !encoded.SequenceEqual(canonical.AsSpan(0, length)))
                throw InvalidPayload();
            return secret;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(secret);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private static Guid ReadId(ref Utf8JsonReader reader)
    {
        string value = ReadText(ref reader, 36);
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty || value != id.ToString("D"))
            throw InvalidPayload();
        return id;
    }

    private static string ReadText(ref Utf8JsonReader reader, int maxEncoded)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > maxEncoded) throw InvalidPayload();
        return reader.GetString()!;
    }

    private static string Property(ref Utf8JsonReader reader, HashSet<string> fields)
    {
        if (reader.ValueSpan.Length > 96) throw InvalidPayload();
        string name = reader.GetString()!;
        if (!fields.Add(name) || !reader.Read()) throw InvalidPayload();
        return name;
    }

    private static void ReadToken(ref Utf8JsonReader reader, JsonTokenType expected)
    {
        if (!reader.Read() || reader.TokenType != expected) throw InvalidPayload();
    }

    private static void ValidateEntry(string name, string? username)
    {
        if (!IsValidName(name)) throw new ArgumentException("Invalid credential name.");
        if (!IsValidUsername(username)) throw new ArgumentException("Invalid credential username.");
    }

    internal static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > 256) return false;
        try { return VaultContainer.StrictUtf8.GetByteCount(name) > 0; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static bool IsValidUsername(string? username) => !string.IsNullOrWhiteSpace(username) &&
        !username.Any(char.IsControl) && VaultContainer.StrictUtf8.GetByteCount(username) <= 1024;

    private static void ValidatePassword(ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxPassword) throw new ArgumentException("Credential password exceeds its size limit.");
        _ = VaultContainer.StrictUtf8.GetCharCount(value);
    }

    private static InvalidDataException InvalidPayload() => new("Invalid or unsupported credential vault payload.");

    private sealed class OwnedBuffer(int capacity) : IBufferWriter<byte>
    {
        private readonly byte[] bytes = new byte[capacity];
        private int length;
        public ReadOnlySpan<byte> Written => bytes.AsSpan(0, length);
        public void Advance(int count) => length += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => GetSpace(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => GetSpace(sizeHint).Span;
        public void Append(ReadOnlySpan<byte> value)
        {
            value.CopyTo(GetSpan(value.Length));
            Advance(value.Length);
        }
        public void Clear() => CryptographicOperations.ZeroMemory(bytes);
        private Memory<byte> GetSpace(int size)
        {
            if (size > bytes.Length - length) throw InvalidPayload();
            return bytes.AsMemory(length);
        }
    }
}
