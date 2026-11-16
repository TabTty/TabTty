using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    internal sealed class Key : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal Key(nint handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle()
        {
            EVP_PKEY_free(handle);
            return true;
        }
    }

    public static Key GenerateX25519()
    {
        nint operation = EVP_PKEY_CTX_new_from_name(Context.Value, "X25519", "provider=default");
        Require(operation != 0);
        try
        {
            Require(EVP_PKEY_keygen_init(operation) == 1);
            int result = EVP_PKEY_generate(operation, out nint nativeKey);
            var key = new Key(nativeKey);
            if (result == 1 && !key.IsInvalid) return key;
            key.Dispose();
            throw Failure();
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static byte[] PublicX25519(Key key)
    {
        byte[] bytes = new byte[32];
        nuint length = 32;
        fixed (byte* output = bytes)
            Require(EVP_PKEY_get_raw_public_key(key, output, &length) == 1 && length == 32);
        return bytes;
    }

    public static void AgreeX25519(Key key, ReadOnlySpan<byte> peer, Span<byte> shared)
    {
        Require(peer.Length == 32 && shared.Length == 32);
        using Key publicKey = ImportPublic("X25519", peer);
        nint operation = EVP_PKEY_CTX_new_from_pkey(Context.Value, key, "provider=default");
        Require(operation != 0);
        try
        {
            nuint length = 32;
            fixed (byte* output = shared)
                Require(EVP_PKEY_derive_init(operation) == 1 && EVP_PKEY_derive_set_peer(operation, publicKey) == 1 &&
                    EVP_PKEY_derive(operation, output, &length) == 1 && length == 32);
            Span<byte> zero = stackalloc byte[32];
            zero.Clear();
            Require(!CryptographicOperations.FixedTimeEquals(shared, zero));
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static void VerifyEd25519(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> data)
    {
        Require(publicKey.Length == 32 && signature.Length == 64);
        using Key key = ImportPublic("ED25519", publicKey);
        nint operation = EVP_MD_CTX_new();
        Require(operation != 0);
        try
        {
            Require(EVP_DigestVerifyInit_ex(operation, 0, 0, Context.Value, "provider=default", key, 0) == 1);
            fixed (byte* signed = data)
            fixed (byte* proof = signature)
                Require(EVP_DigestVerify(operation, proof, (nuint)signature.Length, signed, (nuint)data.Length) == 1);
        }
        finally { EVP_MD_CTX_free(operation); }
    }

    public static void Sha256(ReadOnlySpan<byte> input, Span<byte> digest)
    {
        Require(digest.Length == 32);
        fixed (byte* source = input)
        fixed (byte* result = digest)
            Require(EVP_Q_digest(Context.Value, "SHA256", "provider=default", source, (nuint)input.Length,
                result, out nuint size) == 1 && size == 32);
    }

    public static void Gcm(bool encrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> input, Span<byte> output, Span<byte> tag)
    {
        Require((key.Length is 16 or 32) && nonce.Length == 12 && tag.Length == 16 && input.Length == output.Length);
        nint cipher = EVP_CIPHER_fetch(Context.Value, key.Length == 16 ? "AES-128-GCM" : "AES-256-GCM", "provider=default");
        Require(cipher != 0);
        nint operation = 0;
        try
        {
            operation = EVP_CIPHER_CTX_new();
            Require(operation != 0);
            fixed (byte* secret = key)
            fixed (byte* iv = nonce)
            fixed (byte* header = aad)
            fixed (byte* body = input)
            fixed (byte* result = output)
            fixed (byte* authentication = tag)
            {
                Require(EVP_CipherInit_ex2(operation, cipher, secret, iv, encrypt ? 1 : 0, 0) == 1);
                Require(EVP_CipherUpdate(operation, null, out _, header, aad.Length) == 1);
                Require(EVP_CipherUpdate(operation, result, out int written, body, input.Length) == 1 &&
                    written == output.Length);
                if (!encrypt) Require(EVP_CIPHER_CTX_ctrl(operation, 0x11, 16, authentication) == 1);
                byte* final = stackalloc byte[16];
                Require(EVP_CipherFinal_ex(operation, final, out int tail) == 1 && tail == 0);
                if (encrypt) Require(EVP_CIPHER_CTX_ctrl(operation, 0x10, 16, authentication) == 1);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
        finally
        {
            EVP_CIPHER_CTX_free(operation);
            EVP_CIPHER_free(cipher);
        }
    }

    public static byte[] DecryptCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> encrypted)
    {
        Require(key.Length == 32 && iv.Length == 16);
        nint cipher = EVP_CIPHER_fetch(Context.Value, "AES-256-CTR", "provider=default");
        Require(cipher != 0);
        nint operation = 0;
        byte[] output = new byte[encrypted.Length];
        try
        {
            operation = EVP_CIPHER_CTX_new();
            Require(operation != 0);
            fixed (byte* secret = key)
            fixed (byte* nonce = iv)
            fixed (byte* source = encrypted)
            fixed (byte* destination = output)
            {
                Require(EVP_CipherInit_ex2(operation, cipher, secret, nonce, 0, 0) == 1 &&
                    EVP_CipherUpdate(operation, destination, out int written, source, encrypted.Length) == 1 &&
                    written == output.Length);
                byte* final = stackalloc byte[16];
                Require(EVP_CipherFinal_ex(operation, final, out int tail) == 1 && tail == 0);
            }
            return output;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
        finally
        {
            EVP_CIPHER_CTX_free(operation);
            EVP_CIPHER_free(cipher);
        }
    }

    private static Key ImportPublic(string type, ReadOnlySpan<byte> publicKey)
    {
        fixed (byte* pointer = publicKey)
        {
            nint key = EVP_PKEY_new_raw_public_key_ex(Context.Value, type, "provider=default", pointer, (nuint)publicKey.Length);
            Require(key != 0);
            return new Key(key);
        }
    }

    private static void Require(bool success)
    {
        if (!success) throw Failure();
    }

    private static CryptographicException Failure() => new("OpenSSL SSH cryptographic operation failed.");

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_PKEY_CTX_new_from_name(nint library, string type, string properties);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_PKEY_CTX_new_from_pkey(nint library, Key key, string properties);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_PKEY_CTX_free(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_keygen_init(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_generate(nint context, out nint key);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_PKEY_free(nint key);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_get_raw_public_key(Key key, byte* destination, nuint* length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_PKEY_new_raw_public_key_ex(nint library, string type, string properties, byte* source, nuint length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_derive_init(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_derive_set_peer(nint context, Key peer);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_derive(nint context, byte* output, nuint* length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint EVP_MD_CTX_new();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_MD_CTX_free(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_DigestVerifyInit_ex(nint context, nint keyContext, nint digestName, nint library,
        string properties, Key key, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_DigestVerify(nint context, byte* signature, nuint signatureLength, byte* message, nuint messageLength);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_Q_digest(nint library, string name, string properties, byte* input, nuint length,
        byte* output, out nuint size);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_CIPHER_fetch(nint library, string name, string properties);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_CIPHER_free(nint cipher);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint EVP_CIPHER_CTX_new();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_CIPHER_CTX_free(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_CipherInit_ex2(nint context, nint cipher, byte* key, byte* nonce, int encrypt, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_CipherUpdate(nint context, byte* output, out int written, byte* input, int length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_CipherFinal_ex(nint context, byte* output, out int written);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_CIPHER_CTX_ctrl(nint context, int operation, int size, byte* pointer);
}
