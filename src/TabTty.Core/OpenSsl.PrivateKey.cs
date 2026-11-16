using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    public static Key ImportEd25519Seed(ReadOnlySpan<byte> seed)
    {
        Require(seed.Length == 32);
        fixed (byte* input = seed)
        {
            nint key = EVP_PKEY_new_raw_private_key_ex(Context.Value, "ED25519", "provider=default", input,
                (nuint)seed.Length);
            Require(key != 0);
            return new Key(key);
        }
    }

    public static Key ImportPkcs8(ReadOnlySpan<byte> der)
    {
        fixed (byte* input = der)
        {
            byte* cursor = input;
            nint info = d2i_PKCS8_PRIV_KEY_INFO(0, &cursor, der.Length);
            Require(info != 0);
            try
            {
                Require(cursor == input + der.Length);
                nint native = EVP_PKCS82PKEY_ex(info, Context.Value, "provider=default");
                Require(native != 0);
                return new Key(native);
            }
            finally { PKCS8_PRIV_KEY_INFO_free(info); }
        }
    }

    public static byte[] ExportPkcs8(Key key)
    {
        nint info = EVP_PKEY2PKCS8(key);
        Require(info != 0);
        try
        {
            int length = i2d_PKCS8_PRIV_KEY_INFO(info, null);
            Require(length is > 0 and <= 65536);
            byte[] der = new byte[length];
            try
            {
                fixed (byte* output = der)
                {
                    byte* cursor = output;
                    Require(i2d_PKCS8_PRIV_KEY_INFO(info, &cursor) == length && cursor == output + length);
                }
                return der;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(der);
                throw;
            }
        }
        finally { PKCS8_PRIV_KEY_INFO_free(info); }
    }

    public static Key DecryptPkcs8(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> passphrase)
    {
        fixed (byte* input = encoded)
        fixed (byte* password = passphrase)
        {
            byte* cursor = input;
            nint envelope = d2i_X509_SIG(0, &cursor, encoded.Length);
            Require(envelope != 0);
            try
            {
                Require(cursor == input + encoded.Length);
                nint info = PKCS8_decrypt_ex(envelope, password, passphrase.Length, Context.Value, "provider=default");
                Require(info != 0);
                try
                {
                    nint pointer = EVP_PKCS82PKEY_ex(info, Context.Value, "provider=default");
                    Require(pointer != 0);
                    return new Key(pointer);
                }
                finally { PKCS8_PRIV_KEY_INFO_free(info); }
            }
            finally { X509_SIG_free(envelope); }
        }
    }

    public static byte[] CheckedEd25519Public(Key key)
    {
        Require(EVP_PKEY_is_a(key, "ED25519") == 1);
        Span<byte> seed = stackalloc byte[32];
        try
        {
            nuint length = 32;
            fixed (byte* output = seed)
                Require(EVP_PKEY_get_raw_private_key(key, output, &length) == 1 && length == 32);
            using Key reconstructed = ImportEd25519Seed(seed);
            byte[] publicKey = ReadRawPublic(key);
            Require(publicKey.AsSpan().SequenceEqual(ReadRawPublic(reconstructed)));
            return publicKey;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private static byte[] ReadRawPublic(Key key)
    {
        byte[] publicKey = new byte[32];
        nuint length = 32;
        fixed (byte* output = publicKey)
            Require(EVP_PKEY_get_raw_public_key(key, output, &length) == 1 && length == 32);
        return publicKey;
    }

    public static byte[] SignEd25519(Key key, ReadOnlySpan<byte> message)
    {
        nint operation = EVP_MD_CTX_new();
        Require(operation != 0);
        try
        {
            Require(EVP_DigestSignInit_ex(operation, 0, 0, Context.Value, "provider=default", key, 0) == 1);
            byte[] signature = new byte[64];
            nuint size = 64;
            fixed (byte* input = message)
            fixed (byte* output = signature)
                Require(EVP_DigestSign(operation, output, &size, input, (nuint)message.Length) == 1 && size == 64);
            return signature;
        }
        finally { EVP_MD_CTX_free(operation); }
    }

    public static void CheckPrivateKey(Key key, string algorithm)
    {
        if (algorithm == "ssh-ed25519")
        {
            _ = CheckedEd25519Public(key);
            return;
        }
        Require(EVP_PKEY_is_a(key, algorithm == "ssh-rsa" ? "RSA" : "EC") == 1);
        nint operation = EVP_PKEY_CTX_new_from_pkey(Context.Value, key, "provider=default");
        Require(operation != 0);
        try
        {
            Require(EVP_PKEY_public_check(operation) == 1 && EVP_PKEY_private_check(operation) == 1 &&
                EVP_PKEY_pairwise_check(operation) == 1);
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static byte[] RsaNumber(Key key, string parameter)
    {
        Require(EVP_PKEY_get_bn_param(key, parameter, out nint value) == 1 && value != 0);
        try
        {
            int size = (BN_num_bits(value) + 7) / 8;
            Require(size is > 0 and <= 1024);
            byte[] bytes = new byte[size];
            fixed (byte* destination = bytes) Require(BN_bn2binpad(value, destination, size) == size);
            return bytes;
        }
        finally { BN_free(value); }
    }

    public static byte[] SignDigest(Key key, string hash, ReadOnlySpan<byte> data, bool rsa)
    {
        nint operation = EVP_MD_CTX_new();
        Require(operation != 0);
        try
        {
            Require(SignInit(operation, out nint keyContext, hash, Context.Value, "provider=default", key, 0) == 1);
            if (rsa) Require(EVP_PKEY_CTX_set_rsa_padding(keyContext, 1) == 1);
            nuint size = 1024;
            byte[] signature = new byte[1024];
            fixed (byte* input = data)
            fixed (byte* output = signature)
                Require(EVP_DigestSign(operation, output, &size, input, (nuint)data.Length) == 1 && size is > 0 and <= 1024);
            return signature.AsSpan(0, (int)size).ToArray();
        }
        finally { EVP_MD_CTX_free(operation); }
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_PKEY_new_raw_private_key_ex(nint library, string type, string properties, byte* key, nuint length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_PKEY_is_a(Key key, string name);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_get_raw_private_key(Key key, byte* output, nuint* size);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint d2i_PKCS8_PRIV_KEY_INFO(nint info, byte** input, int length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int i2d_PKCS8_PRIV_KEY_INFO(nint info, byte** output);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PKCS8_PRIV_KEY_INFO_free(nint info);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint EVP_PKEY2PKCS8(Key key);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_PKCS82PKEY_ex(nint info, nint library, string properties);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_DigestSignInit_ex(nint context, nint keyContext, nint digestName, nint library,
        string properties, Key key, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_DigestSign(nint context, byte* signature, nuint* signatureLength, byte* message, nuint messageLength);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_private_check(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_pairwise_check(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, EntryPoint = "EVP_DigestSignInit_ex", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int SignInit(nint context, out nint keyContext, string hash, nint library, string properties,
        Key key, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint d2i_X509_SIG(nint value, byte** input, int length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void X509_SIG_free(nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint PKCS8_decrypt_ex(nint value, byte* password, int passwordLength, nint library, string properties);
}
