using System.Formats.Asn1;

namespace TabTty.Core;

public sealed class PrivateKeyPassphraseRequiredException() : IOException("The source private key requires a passphrase.");

internal static class EncryptedPrivateKeyEnvelope
{
    public static void Validate(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length is 0 or > 256 * 1024) throw Invalid();
        try
        {
            var file = new AsnReader(encoded, AsnEncodingRules.DER);
            var info = file.ReadSequence();
            var algorithm = info.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.2.840.113549.1.5.13") throw Invalid();
            var parameters = algorithm.ReadSequence();
            var kdf = parameters.ReadSequence();
            if (kdf.ReadObjectIdentifier() != "1.2.840.113549.1.5.12") throw Invalid();
            var derivation = kdf.ReadSequence();
            if (!derivation.TryReadPrimitiveOctetString(out var salt) || salt.Length is < 1 or > 64 ||
                !derivation.TryReadInt32(out int iterations) || iterations is < 1 or > 10_000_000) throw Invalid();
            int? keyLength = null;
            if (derivation.HasData && derivation.PeekTag().TagClass == TagClass.Universal &&
                derivation.PeekTag().TagValue == (int)UniversalTagNumber.Integer)
            {
                if (!derivation.TryReadInt32(out int length)) throw Invalid();
                keyLength = length;
            }
            if (derivation.HasData)
            {
                var prf = derivation.ReadSequence();
                if (prf.ReadObjectIdentifier() is not ("1.2.840.113549.2.7" or "1.2.840.113549.2.9" or
                    "1.2.840.113549.2.10" or "1.2.840.113549.2.11")) throw Invalid();
                if (prf.HasData) prf.ReadNull();
                prf.ThrowIfNotEmpty();
            }
            derivation.ThrowIfNotEmpty();
            kdf.ThrowIfNotEmpty();
            var encryption = parameters.ReadSequence();
            int keySize = encryption.ReadObjectIdentifier() switch
            {
                "2.16.840.1.101.3.4.1.2" => 16,
                "2.16.840.1.101.3.4.1.22" => 24,
                "2.16.840.1.101.3.4.1.42" => 32,
                _ => throw Invalid(),
            };
            if ((keyLength is { } expected && expected != keySize) ||
                !encryption.TryReadPrimitiveOctetString(out var nonce) || nonce.Length != 16)
                throw Invalid();
            encryption.ThrowIfNotEmpty();
            parameters.ThrowIfNotEmpty();
            algorithm.ThrowIfNotEmpty();
            if (!info.TryReadPrimitiveOctetString(out var ciphertext) ||
                ciphertext.Length is 0 or > 256 * 1024 || ciphertext.Length % 16 != 0) throw Invalid();
            info.ThrowIfNotEmpty();
            file.ThrowIfNotEmpty();
        }
        catch (AsnContentException) { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("Invalid or unsupported encrypted PKCS#8 parameters.");
}
