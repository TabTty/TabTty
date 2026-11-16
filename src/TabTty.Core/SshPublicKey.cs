using System.Formats.Asn1;
using System.Numerics;
using System.Text;

namespace TabTty.Core;

public static class SshPublicKey
{
    internal static readonly string[] Signatures =
    [
        "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256",
    ];

    public static void Validate(string format, ReadOnlySpan<byte> blob)
    {
        var key = new SshReader(blob);
        if (!key.String().SequenceEqual(Encoding.ASCII.GetBytes(format)))
            throw new InvalidDataException("The SSH public-key format does not match its blob.");
        if (format == "ssh-ed25519")
        {
            if (key.String().Length != 32) throw new InvalidDataException("Invalid Ed25519 public key.");
        }
        else if (format.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
        {
            string curve = Encoding.ASCII.GetString(key.String());
            int size = curve switch { "nistp256" => 65, "nistp384" => 97, "nistp521" => 133, _ => 0 };
            ReadOnlySpan<byte> point = key.String();
            if (format != "ecdsa-sha2-" + curve || size == 0 || point.Length != size || point[0] != 4)
                throw new InvalidDataException("Invalid ECDSA public key.");
        }
        else if (format == "ssh-rsa")
        {
            ReadOnlySpan<byte> exponent = OpenSsl.PositiveInteger(key.String(), 1024);
            ReadOnlySpan<byte> modulus = OpenSsl.PositiveInteger(key.String(), 1024);
            int bits = modulus.Length * 8 - BitOperations.LeadingZeroCount((uint)modulus[0]) + 24;
            if (bits is < 2048 or > 8192 || (modulus[^1] & 1) == 0 || (exponent[^1] & 1) == 0 ||
                exponent.Length == 1 && exponent[0] < 3)
                throw new InvalidDataException("Invalid RSA public key size or exponent.");
        }
        else throw new InvalidDataException("Unsupported SSH public-key format.");
        key.End();
    }

    internal static string Verify(string negotiated, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> exchangeHash)
    {
        string format = negotiated.StartsWith("rsa-sha2-", StringComparison.Ordinal) ? "ssh-rsa" : negotiated;
        Validate(format, blob);
        var key = new SshReader(blob);
        key.String();
        var proof = new SshReader(signature);
        if (!proof.String().SequenceEqual(Encoding.ASCII.GetBytes(negotiated)))
            throw new InvalidDataException("The SSH host signature did not use its negotiated algorithm.");
        ReadOnlySpan<byte> bytes = proof.String();
        proof.End();
        if (format == "ssh-ed25519")
        {
            OpenSsl.VerifyEd25519(key.String(), bytes, exchangeHash);
        }
        else if (format == "ssh-rsa")
        {
            var exponent = OpenSsl.PositiveInteger(key.String(), 1024);
            var modulus = OpenSsl.PositiveInteger(key.String(), 1024);
            if (bytes.Length > modulus.Length) throw new InvalidDataException("Invalid RSA signature length.");
            byte[] padded = new byte[modulus.Length];
            bytes.CopyTo(padded.AsSpan(padded.Length - bytes.Length));
            OpenSsl.VerifyRsa(modulus, exponent, negotiated == "rsa-sha2-512" ? "SHA512" : "SHA256",
                exchangeHash, padded);
        }
        else
        {
            string curve = Encoding.ASCII.GetString(key.String());
            var point = key.String();
            var values = new SshReader(bytes);
            int limit = curve == "nistp521" ? 66 : curve == "nistp384" ? 48 : 32;
            var r = OpenSsl.PositiveInteger(values.String(), limit);
            var s = OpenSsl.PositiveInteger(values.String(), limit);
            values.End();
            var der = new AsnWriter(AsnEncodingRules.DER);
            der.PushSequence();
            der.WriteIntegerUnsigned(r);
            der.WriteIntegerUnsigned(s);
            der.PopSequence();
            OpenSsl.VerifyEc(curve == "nistp256" ? "prime256v1" : curve == "nistp384" ? "secp384r1" : "secp521r1",
                point, curve == "nistp256" ? "SHA256" : curve == "nistp384" ? "SHA384" : "SHA512",
                exchangeHash, der.Encode());
        }
        return format;
    }
}
