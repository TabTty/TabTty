using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    public static byte[] DecryptLegacyPem(string name, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> encrypted)
    {
        int keyLength = name switch { "AES-128-CBC" => 16, "AES-192-CBC" => 24, "AES-256-CBC" => 32, _ => 0 };
        Require(keyLength != 0 && iv.Length == 16 && encrypted.Length % 16 == 0);
        nint cipher = EVP_CIPHER_fetch(Context.Value, name, "provider=default");
        Require(cipher != 0);
        nint digest = 0, operation = 0;
        byte[] output = new byte[encrypted.Length + 16];
        Span<byte> key = stackalloc byte[32];
        try
        {
            digest = EVP_MD_fetch(Context.Value, "MD5", "provider=default");
            operation = EVP_CIPHER_CTX_new();
            Require(digest != 0 && operation != 0);
            fixed (byte* nonce = iv)
            fixed (byte* pass = password)
            fixed (byte* secret = key)
            fixed (byte* source = encrypted)
            fixed (byte* destination = output)
            {
                Require(EVP_BytesToKey(cipher, digest, nonce, pass, password.Length, 1, secret, null) == keyLength);
                Require(EVP_CipherInit_ex2(operation, cipher, secret, nonce, 0, 0) == 1);
                Require(EVP_CipherUpdate(operation, destination, out int size, source, encrypted.Length) == 1);
                Require(size <= encrypted.Length);
                Require(EVP_CipherFinal_ex(operation, destination + size, out int tail) == 1 && tail is >= 0 and <= 16);
                return output.AsSpan(0, size + tail).ToArray();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(output);
            EVP_CIPHER_CTX_free(operation);
            EVP_MD_free(digest);
            EVP_CIPHER_free(cipher);
        }
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_MD_fetch(nint library, string name, string properties);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_MD_free(nint digest);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_BytesToKey(nint cipher, nint digest, byte* salt, byte* password, int passwordLength,
        int iterations, byte* key, byte* iv);
}
