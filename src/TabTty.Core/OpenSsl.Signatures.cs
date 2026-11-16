using System.Runtime.InteropServices;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    public static void VerifyEc(string group, ReadOnlySpan<byte> point, string hash, ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature)
    {
        using Key key = ImportEcPublic(group, point);
        VerifyDigest(key, hash, data, signature, rsa: false);
    }

    public static void VerifyRsa(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> exponent, string hash,
        ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using Key key = ImportRsaPublic(modulus, exponent);
        VerifyDigest(key, hash, data, signature, rsa: true);
    }

    private static Key ImportRsaPublic(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> exponent)
    {
        nint n, e;
        fixed (byte* value = modulus) n = BN_bin2bn(value, modulus.Length, 0);
        Require(n != 0);
        try
        {
            fixed (byte* value = exponent) e = BN_bin2bn(value, exponent.Length, 0);
            Require(e != 0);
            try
            {
                nint builder = OSSL_PARAM_BLD_new();
                Require(builder != 0);
                try
                {
                    fixed (byte* names = "n\0e\0"u8)
                    {
                        Require(OSSL_PARAM_BLD_push_BN(builder, names, n) == 1 &&
                            OSSL_PARAM_BLD_push_BN(builder, names + 2, e) == 1);
                        return BuildPublicKey("RSA", builder);
                    }
                }
                finally { OSSL_PARAM_BLD_free(builder); }
            }
            finally { BN_free(e); }
        }
        finally { BN_free(n); }
    }

    private static void VerifyDigest(Key key, string hash, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, bool rsa)
    {
        nint operation = EVP_MD_CTX_new();
        Require(operation != 0);
        try
        {
            Require(VerifyInit(operation, out nint keyContext, hash, Context.Value, "provider=default", key, 0) == 1);
            if (rsa) Require(EVP_PKEY_CTX_set_rsa_padding(keyContext, 1) == 1);
            fixed (byte* signed = data)
            fixed (byte* proof = signature)
                Require(EVP_DigestVerify(operation, proof, (nuint)signature.Length, signed, (nuint)data.Length) == 1);
        }
        finally { EVP_MD_CTX_free(operation); }
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, EntryPoint = "EVP_DigestVerifyInit_ex", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int VerifyInit(nint context, out nint keyContext, string digestName, nint library,
        string properties, Key key, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_CTX_set_rsa_padding(nint context, int padding);
}
