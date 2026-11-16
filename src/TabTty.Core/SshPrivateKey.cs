using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Formats.Asn1;

namespace TabTty.Core;

internal sealed class SshPrivateKey : IDisposable
{
    private readonly OpenSsl.Key native;
    public string Algorithm { get; }
    public byte[] PublicBlob { get; }
    public string Fingerprint { get; }

    private SshPrivateKey(OpenSsl.Key key, string algorithm)
    {
        native = key;
        Algorithm = algorithm;
        OpenSsl.CheckPrivateKey(native, algorithm);
        using var blob = new SshWriter();
        blob.Text(Algorithm);
        if (algorithm == "ssh-ed25519") blob.String(OpenSsl.CheckedEd25519Public(native));
        else if (algorithm == "ssh-rsa")
        {
            blob.PositiveMpint(OpenSsl.RsaNumber(native, "e"));
            blob.PositiveMpint(OpenSsl.RsaNumber(native, "n"));
        }
        else
        {
            blob.Text(algorithm["ecdsa-sha2-".Length..]);
            blob.String(OpenSsl.GroupPublic(native));
        }
        PublicBlob = blob.Span.ToArray();
        Span<byte> digest = stackalloc byte[32];
        OpenSsl.Sha256(PublicBlob, digest);
        Fingerprint = "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }

    public static SshPrivateKey Import(ReadOnlySpan<byte> source, ReadOnlyMemory<byte>? passphrase = null)
    {
        if (source.IsEmpty || source.Length > 1024 * 1024) throw InvalidKey();
        if (source[0] == 0x30) return FromDer(source, passphrase);
        source = Trim(source);
        if (source.StartsWith("-----BEGIN RSA PRIVATE KEY-----"u8))
            return FromTraditionalPem(source, rsa: true, passphrase);
        if (source.StartsWith("-----BEGIN EC PRIVATE KEY-----"u8))
            return FromTraditionalPem(source, rsa: false, passphrase);
        bool ssh = source.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----"u8);
        bool encrypted = source.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----"u8);
        var begin = ssh ? "-----BEGIN OPENSSH PRIVATE KEY-----"u8 :
            encrypted ? "-----BEGIN ENCRYPTED PRIVATE KEY-----"u8 : "-----BEGIN PRIVATE KEY-----"u8;
        var end = ssh ? "-----END OPENSSH PRIVATE KEY-----"u8 :
            encrypted ? "-----END ENCRYPTED PRIVATE KEY-----"u8 : "-----END PRIVATE KEY-----"u8;
        if (!source.StartsWith(begin) || !source.EndsWith(end) || source.Length <= begin.Length + end.Length)
            throw new InvalidDataException("Unsupported private-key file format.");
        ReadOnlySpan<byte> contents = source[begin.Length..^end.Length];
        if (contents[0] is not (10 or 13) || contents[^1] is not (10 or 13)) throw InvalidKey();
        byte[] decoded = new byte[contents.Length];
        try
        {
            int length = 0;
            foreach (byte value in contents)
                if (!IsSpace(value)) decoded[length++] = value;
            if (length > Base64.GetMaxEncodedToUtf8Length(256 * 1024) ||
                Base64.DecodeFromUtf8InPlace(decoded.AsSpan(0, length), out int written) != OperationStatus.Done ||
                written > 256 * 1024)
                throw InvalidKey();
            return ssh ? FromOpenSsh(decoded.AsSpan(0, written), passphrase) : FromDer(decoded.AsSpan(0, written), passphrase);
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    private static SshPrivateKey FromTraditionalPem(ReadOnlySpan<byte> source, bool rsa, ReadOnlyMemory<byte>? passphrase)
    {
        var begin = rsa ? "-----BEGIN RSA PRIVATE KEY-----"u8 : "-----BEGIN EC PRIVATE KEY-----"u8;
        var end = rsa ? "-----END RSA PRIVATE KEY-----"u8 : "-----END EC PRIVATE KEY-----"u8;
        if (!source.EndsWith(end) || source.Length <= begin.Length + end.Length) throw InvalidKey();
        ReadOnlySpan<byte> body = Trim(source[begin.Length..^end.Length]);
        string? cipher = null;
        byte[] iv = [];
        if (body.StartsWith("Proc-Type:"u8))
        {
            if (!ReadPemLine(ref body).SequenceEqual("Proc-Type: 4,ENCRYPTED"u8)) throw InvalidKey();
            var line = ReadPemLine(ref body);
            if (!line.StartsWith("DEK-Info: "u8)) throw InvalidKey();
            line = line[10..];
            int comma = line.IndexOf((byte)',');
            if (comma < 0) throw InvalidKey();
            cipher = System.Text.Encoding.ASCII.GetString(line[..comma]);
            if (cipher is not ("AES-128-CBC" or "AES-192-CBC" or "AES-256-CBC") || line[(comma + 1)..].Length != 32)
                throw new InvalidDataException("Unsupported traditional PEM encryption.");
            iv = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(line[(comma + 1)..]));
            if (!ReadPemLine(ref body).IsEmpty) throw InvalidKey();
        }
        byte[] decoded = new byte[body.Length];
        byte[] plaintext = [], pkcs8 = [];
        try
        {
            int length = 0;
            foreach (byte value in body)
                if (!IsSpace(value)) decoded[length++] = value;
            if (length > Base64.GetMaxEncodedToUtf8Length(256 * 1024) ||
                Base64.DecodeFromUtf8InPlace(decoded.AsSpan(0, length), out int written) != OperationStatus.Done ||
                written > 256 * 1024) throw InvalidKey();
            if (cipher is not null)
            {
                if (passphrase is not { } password) throw new PrivateKeyPassphraseRequiredException();
                if (password.Length > 16 * 1024 || password.Span.Contains((byte)0)) throw InvalidKey();
                _ = VaultContainer.StrictUtf8.GetCharCount(password.Span);
                plaintext = OpenSsl.DecryptLegacyPem(cipher, iv, password.Span, decoded.AsSpan(0, written));
            }
            else plaintext = decoded.AsSpan(0, written).ToArray();
            if (rsa) pkcs8 = WrapPrivateKey(plaintext, "1.2.840.113549.1.1.1", null);
            else
            {
                var file = new AsnReader(plaintext, AsnEncodingRules.DER);
                var key = file.ReadSequence();
                if (key.ReadInteger() != 1) throw InvalidKey();
                key.ReadOctetString();
                var parameters = key.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                string curve = parameters.ReadObjectIdentifier();
                parameters.ThrowIfNotEmpty();
                pkcs8 = WrapPrivateKey(plaintext, "1.2.840.10045.2.1", curve);
            }
            return FromPkcs8(pkcs8);
        }
        catch (AsnContentException) { throw InvalidKey(); }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static ReadOnlySpan<byte> ReadPemLine(ref ReadOnlySpan<byte> body)
    {
        int end = body.IndexOf((byte)'\n');
        if (end < 0 || end > 1024) throw InvalidKey();
        var line = body[..end];
        body = body[(end + 1)..];
        return !line.IsEmpty && line[^1] == '\r' ? line[..^1] : line;
    }

    private static SshPrivateKey FromDer(ReadOnlySpan<byte> source, ReadOnlyMemory<byte>? passphrase)
    {
        if (source.Length > 256 * 1024) throw InvalidKey();
        byte[] copy = source.ToArray();
        try
        {
            var reader = new AsnReader(copy, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            if (sequence.PeekTag().TagClass == TagClass.Universal &&
                sequence.PeekTag().TagValue == (int)UniversalTagNumber.Integer)
                return FromPkcs8(source);
            EncryptedPrivateKeyEnvelope.Validate(copy);
            if (passphrase is not { } password) throw new PrivateKeyPassphraseRequiredException();
            if (password.Length > 16 * 1024 || password.Span.Contains((byte)0))
                throw new ArgumentException("Invalid private-key passphrase.");
            _ = VaultContainer.StrictUtf8.GetCharCount(password.Span);
            using OpenSsl.Key key = OpenSsl.DecryptPkcs8(source, password.Span);
            byte[] der = OpenSsl.ExportPkcs8(key);
            try { return FromPkcs8(der); }
            finally { CryptographicOperations.ZeroMemory(der); }
        }
        catch (AsnContentException) { throw InvalidKey(); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    public static SshPrivateKey FromPkcs8(ReadOnlySpan<byte> der)
    {
        byte[] owned = der.ToArray();
        try
        {
            string algorithm = PrivateKeyEnvelope.InspectPkcs8(owned);
            return Adopt(OpenSsl.ImportPkcs8(der), algorithm);
        }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }

    private static SshPrivateKey FromOpenSsh(ReadOnlySpan<byte> bytes, ReadOnlyMemory<byte>? passphrase)
    {
        var reader = new SshReader(bytes);
        if (!reader.Take(15).SequenceEqual("openssh-key-v1\0"u8)) throw InvalidKey();
        var cipher = reader.String();
        var kdf = reader.String();
        var options = reader.String();
        if (reader.Number() != 1) throw InvalidKey();
        ReadOnlySpan<byte> blob = reader.String();
        var data = reader.String();
        reader.End();
        var publicInfo = new SshReader(blob);
        string type = System.Text.Encoding.ASCII.GetString(publicInfo.String());
        SshPublicKey.Validate(type, blob);
        if (cipher.SequenceEqual("none"u8) && kdf.SequenceEqual("none"u8) && options.IsEmpty)
        {
            if (data.Length % 8 != 0) throw InvalidKey();
            return FromOpenSshPrivate(data, blob, 8);
        }
        if (!cipher.SequenceEqual("aes256-ctr"u8) || !kdf.SequenceEqual("bcrypt"u8) || data.Length % 16 != 0)
            throw new InvalidDataException("Unsupported OpenSSH key encryption.");
        var derivation = new SshReader(options);
        var salt = derivation.String();
        uint rounds = derivation.Number();
        derivation.End();
        if (salt.Length is < 1 or > 64 || rounds is < 1 or > 1024) throw InvalidKey();
        if (passphrase is not { } password) throw new PrivateKeyPassphraseRequiredException();
        if (password.Span.Contains((byte)0)) throw InvalidKey();
        _ = VaultContainer.StrictUtf8.GetCharCount(password.Span);
        Span<byte> derived = stackalloc byte[48];
        byte[] plaintext = [];
        try
        {
            OpenSshBcrypt.Derive(password.Span, salt, rounds, derived);
            plaintext = OpenSsl.DecryptCtr(derived[..32], derived[32..], data);
            return FromOpenSshPrivate(plaintext, blob, 16);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static SshPrivateKey FromOpenSshPrivate(ReadOnlySpan<byte> data, ReadOnlySpan<byte> blob, int blockSize)
    {
        var key = new SshReader(data);
        if (key.Number() != key.Number()) throw InvalidKey();
        string type = System.Text.Encoding.ASCII.GetString(key.String());
        SshPrivateKey result;
        if (type == "ssh-ed25519")
        {
            var publicKey = key.String();
            var privateKey = key.String();
            if (publicKey.Length != 32 || privateKey.Length != 64 || !publicKey.SequenceEqual(privateKey[32..]))
                throw InvalidKey();
            result = Adopt(OpenSsl.ImportEd25519Seed(privateKey[..32]), "ssh-ed25519");
            if (!result.PublicBlob.AsSpan(^32).SequenceEqual(publicKey))
            {
                result.Dispose();
                throw InvalidKey();
            }
        }
        else
        {
            byte[] der = type == "ssh-rsa" ? OpenSshRsaDer(ref key) :
                type.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) ? OpenSshEcDer(ref key, type) : throw InvalidKey();
            try { result = FromPkcs8(der); }
            finally { CryptographicOperations.ZeroMemory(der); }
        }
        try
        {
            key.String();
            if (key.Length >= blockSize || !result.PublicBlob.AsSpan().SequenceEqual(blob)) throw InvalidKey();
            for (int index = 1; key.Length != 0; index++)
                if (key.Byte() != index) throw InvalidKey();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static byte[] OpenSshRsaDer(ref SshReader source)
    {
        var n = OpenSsl.PositiveInteger(source.String(), 1024);
        var e = OpenSsl.PositiveInteger(source.String(), 1024);
        var d = OpenSsl.PositiveInteger(source.String(), 1024);
        var inverse = OpenSsl.PositiveInteger(source.String(), 1024);
        var p = OpenSsl.PositiveInteger(source.String(), 1024);
        var q = OpenSsl.PositiveInteger(source.String(), 1024);
        if (n.Length < 256) throw InvalidKey();
        byte[] dp = [], dq = [], privateKey = [];
        var rsa = new AsnWriter(AsnEncodingRules.DER);
        try
        {
            dp = OpenSsl.RsaReducedExponent(d, p);
            dq = OpenSsl.RsaReducedExponent(d, q);
            rsa.PushSequence();
            rsa.WriteInteger(0);
            rsa.WriteIntegerUnsigned(n);
            rsa.WriteIntegerUnsigned(e);
            rsa.WriteIntegerUnsigned(d);
            rsa.WriteIntegerUnsigned(p);
            rsa.WriteIntegerUnsigned(q);
            rsa.WriteIntegerUnsigned(dp);
            rsa.WriteIntegerUnsigned(dq);
            rsa.WriteIntegerUnsigned(inverse);
            rsa.PopSequence();
            privateKey = rsa.Encode();
            return WrapPrivateKey(privateKey, "1.2.840.113549.1.1.1", null);
        }
        finally
        {
            rsa.Reset();
            CryptographicOperations.ZeroMemory(dp);
            CryptographicOperations.ZeroMemory(dq);
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static byte[] OpenSshEcDer(ref SshReader source, string type)
    {
        string curve = System.Text.Encoding.ASCII.GetString(source.String());
        var point = source.String();
        (int size, string oid) = curve switch
        {
            "nistp256" => (32, "1.2.840.10045.3.1.7"),
            "nistp384" => (48, "1.3.132.0.34"),
            "nistp521" => (66, "1.3.132.0.35"),
            _ => throw InvalidKey(),
        };
        if (type != "ecdsa-sha2-" + curve || point.Length != size * 2 + 1 || point[0] != 4) throw InvalidKey();
        var d = OpenSsl.PositiveInteger(source.String(), size);
        byte[] scalar = new byte[size], privateKey = [];
        var ec = new AsnWriter(AsnEncodingRules.DER);
        try
        {
            d.CopyTo(scalar.AsSpan(size - d.Length));
            ec.PushSequence();
            ec.WriteInteger(1);
            ec.WriteOctetString(scalar);
            var publicTag = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
            ec.PushSequence(publicTag);
            ec.WriteBitString(point);
            ec.PopSequence(publicTag);
            ec.PopSequence();
            privateKey = ec.Encode();
            return WrapPrivateKey(privateKey, "1.2.840.10045.2.1", oid);
        }
        finally
        {
            ec.Reset();
            CryptographicOperations.ZeroMemory(scalar);
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static byte[] WrapPrivateKey(ReadOnlySpan<byte> key, string algorithm, string? curve)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        try
        {
            writer.PushSequence();
            writer.WriteInteger(0);
            writer.PushSequence();
            writer.WriteObjectIdentifier(algorithm);
            if (curve is null) writer.WriteNull();
            else writer.WriteObjectIdentifier(curve);
            writer.PopSequence();
            writer.WriteOctetString(key);
            writer.PopSequence();
            return writer.Encode();
        }
        finally { writer.Reset(); }
    }

    private static SshPrivateKey Adopt(OpenSsl.Key key, string algorithm)
    {
        try { return new SshPrivateKey(key, algorithm); }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public byte[] ExportPkcs8() => OpenSsl.ExportPkcs8(native);
    public byte[] Sign(string signatureAlgorithm, ReadOnlySpan<byte> input)
    {
        if (Algorithm == "ssh-ed25519" && signatureAlgorithm == Algorithm) return OpenSsl.SignEd25519(native, input);
        if (Algorithm == "ssh-rsa" && signatureAlgorithm is "rsa-sha2-512" or "rsa-sha2-256")
            return OpenSsl.SignDigest(native, signatureAlgorithm == "rsa-sha2-512" ? "SHA512" : "SHA256", input, rsa: true);
        if (Algorithm != signatureAlgorithm || !Algorithm.StartsWith("ecdsa-", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported SSH user signature.");
        byte[] der = OpenSsl.SignDigest(native, Algorithm.EndsWith("256", StringComparison.Ordinal) ? "SHA256" :
            Algorithm.EndsWith("384", StringComparison.Ordinal) ? "SHA384" : "SHA512", input, rsa: false);
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        using var signature = new SshWriter();
        signature.PositiveMpint(sequence.ReadIntegerBytes().Span);
        signature.PositiveMpint(sequence.ReadIntegerBytes().Span);
        sequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();
        return signature.Span.ToArray();
    }
    public void Dispose() => native.Dispose();

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && IsSpace(value[0])) value = value[1..];
        while (!value.IsEmpty && IsSpace(value[^1])) value = value[..^1];
        return value;
    }
    private static bool IsSpace(byte value) => value is 9 or 10 or 13 or 32;
    private static InvalidDataException InvalidKey() => new("Invalid SSH private-key file.");
}
