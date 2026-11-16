using System.Formats.Asn1;
using System.Numerics;

namespace TabTty.Core;

internal static class PrivateKeyEnvelope
{
    public static string InspectPkcs8(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length is 0 or > 65536) throw Invalid();
        try
        {
            var file = new AsnReader(encoded, AsnEncodingRules.DER);
            var info = file.ReadSequence();
            if (info.ReadInteger() != 0) throw Invalid();
            var algorithm = info.ReadSequence();
            string oid = algorithm.ReadObjectIdentifier();
            string? curve = null;
            switch (oid)
            {
                case "1.3.101.112":
                    break;
                case "1.2.840.113549.1.1.1":
                    algorithm.ReadNull();
                    break;
                case "1.2.840.10045.2.1":
                    curve = algorithm.ReadObjectIdentifier();
                    if (curve is not ("1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35")) throw Invalid();
                    break;
                default: throw Invalid();
            }
            algorithm.ThrowIfNotEmpty();
            if (!info.TryReadPrimitiveOctetString(out var privateKey)) throw Invalid();
            info.ThrowIfNotEmpty();
            file.ThrowIfNotEmpty();
            if (oid == "1.3.101.112")
            {
                var seed = new AsnReader(privateKey, AsnEncodingRules.DER);
                if (!seed.TryReadPrimitiveOctetString(out var bytes) || bytes.Length != 32) throw Invalid();
                seed.ThrowIfNotEmpty();
                return "ssh-ed25519";
            }
            if (curve is not null)
            {
                ValidateEc(privateKey, curve);
                return curve == "1.2.840.10045.3.1.7" ? "ecdsa-sha2-nistp256" :
                    curve == "1.3.132.0.34" ? "ecdsa-sha2-nistp384" : "ecdsa-sha2-nistp521";
            }
            ValidateRsa(privateKey);
            return "ssh-rsa";
        }
        catch (AsnContentException) { throw Invalid(); }
    }

    private static void ValidateRsa(ReadOnlyMemory<byte> privateKey)
    {
        var file = new AsnReader(privateKey, AsnEncodingRules.DER);
        var key = file.ReadSequence();
        if (key.ReadInteger() != 0) throw Invalid();
        ReadOnlySpan<byte> modulus = Positive(key.ReadIntegerBytes().Span);
        int bits = modulus.Length * 8 - BitOperations.LeadingZeroCount((uint)modulus[0]) + 24;
        if (bits is < 2048 or > 8192) throw Invalid();
        for (int index = 0; index < 7; index++) _ = Positive(key.ReadIntegerBytes().Span);
        key.ThrowIfNotEmpty();
        file.ThrowIfNotEmpty();
    }

    private static void ValidateEc(ReadOnlyMemory<byte> privateKey, string curve)
    {
        var file = new AsnReader(privateKey, AsnEncodingRules.DER);
        var key = file.ReadSequence();
        if (key.ReadInteger() != 1 || !key.TryReadPrimitiveOctetString(out var scalar) || scalar.Length is 0 or > 66)
            throw Invalid();
        if (key.HasData && key.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            var parameters = key.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            if (parameters.ReadObjectIdentifier() != curve) throw Invalid();
            parameters.ThrowIfNotEmpty();
        }
        if (key.HasData)
        {
            var publicKey = key.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true));
            if (!publicKey.TryReadPrimitiveBitString(out int unused, out var point) || unused != 0 ||
                point.Length is 0 or > 133) throw Invalid();
            publicKey.ThrowIfNotEmpty();
        }
        key.ThrowIfNotEmpty();
        file.ThrowIfNotEmpty();
    }

    private static ReadOnlySpan<byte> Positive(ReadOnlySpan<byte> integer)
    {
        if (integer.IsEmpty || (integer[0] & 0x80) != 0) throw Invalid();
        while (!integer.IsEmpty && integer[0] == 0) integer = integer[1..];
        return !integer.IsEmpty ? integer : throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid or unsupported private-key type, parameters, or size.");
}
