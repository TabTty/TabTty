using System.Security.Cryptography;

namespace TabTty.Core;

internal sealed class SshAgreement : IDisposable
{
    private readonly OpenSsl.Key key;
    private readonly OpenSsl.Key? kem;
    private readonly string? curve;
    private readonly int secretSize;
    private readonly bool finiteField;
    public string Hash { get; }
    public int HashSize { get; }
    public byte[] PublicKey { get; }

    public SshAgreement(string method)
    {
        (curve, secretSize, Hash, HashSize) = method switch
        {
            "curve25519-sha256" or "curve25519-sha256@libssh.org" or "mlkem768x25519-sha256" => (null, 32, "SHA256", 32),
            "ecdh-sha2-nistp256" => ("prime256v1", 32, "SHA256", 32),
            "ecdh-sha2-nistp384" => ("secp384r1", 48, "SHA384", 48),
            "ecdh-sha2-nistp521" => ("secp521r1", 66, "SHA512", 64),
            "diffie-hellman-group14-sha256" => ("modp_2048", 256, "SHA256", 32),
            "diffie-hellman-group16-sha512" => ("modp_4096", 512, "SHA512", 64),
            _ => throw new InvalidDataException("Unsupported SSH key exchange."),
        };
        finiteField = method.StartsWith("diffie-hellman-", StringComparison.Ordinal);
        key = curve is null ? OpenSsl.GenerateX25519() : OpenSsl.GenerateGroup(finiteField ? "DH" : "EC", curve);
        try
        {
            if (method == "mlkem768x25519-sha256")
            {
                kem = OpenSsl.GenerateMlKem768();
                PublicKey = new byte[1216];
                OpenSsl.EncodedPublic(kem, 1184).CopyTo(PublicKey, 0);
                OpenSsl.PublicX25519(key).CopyTo(PublicKey, 1184);
            }
            else if (finiteField)
            {
                using var integer = new SshWriter();
                integer.PositiveMpint(OpenSsl.DhPublic(key));
                PublicKey = integer.Span[4..].ToArray();
            }
            else PublicKey = curve is null ? OpenSsl.PublicX25519(key) : OpenSsl.GroupPublic(key);
        }
        catch
        {
            kem?.Dispose();
            key.Dispose();
            throw;
        }
    }

    public byte[] SharedSecret(ReadOnlySpan<byte> peer)
    {
        if (kem is not null) return HybridSecret(peer);
        byte[] secret = new byte[secretSize];
        try
        {
            if (finiteField) OpenSsl.AgreeDh(key, curve!, peer, secret);
            else if (curve is null) OpenSsl.AgreeX25519(key, peer, secret);
            else OpenSsl.AgreeEc(key, curve, peer, secret);
            using var encoded = new SshWriter(secret.Length + 5);
            encoded.PositiveMpint(secret);
            return encoded.Span.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private byte[] HybridSecret(ReadOnlySpan<byte> peer)
    {
        if (peer.Length != 1120) throw new InvalidDataException("Invalid SSH hybrid key-exchange reply length.");
        Span<byte> combined = stackalloc byte[64];
        byte[] digest = [];
        try
        {
            OpenSsl.DecapsulateMlKem768(kem!, peer[..1088], combined[..32]);
            OpenSsl.AgreeX25519(key, peer[1088..], combined[32..]);
            digest = OpenSsl.Hash("SHA256", combined, 32);
            using var encoded = new SshWriter(36);
            encoded.String(digest);
            return encoded.Span.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public void Dispose()
    {
        key.Dispose();
        kem?.Dispose();
    }
}
