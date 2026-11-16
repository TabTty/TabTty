using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    internal sealed class Ctr : IDisposable
    {
        private nint operation;
        private nint cipher;

        public Ctr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, bool encrypt)
        {
            Require(key.Length is 16 or 32 && iv.Length == 16);
            try
            {
                cipher = EVP_CIPHER_fetch(Context.Value, key.Length == 16 ? "AES-128-CTR" : "AES-256-CTR", "provider=default");
                Require(cipher != 0);
                operation = EVP_CIPHER_CTX_new();
                Require(operation != 0);
                fixed (byte* secret = key)
                fixed (byte* nonce = iv)
                    Require(EVP_CipherInit_ex2(operation, cipher, secret, nonce, encrypt ? 1 : 0, 0) == 1);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
        {
            ObjectDisposedException.ThrowIf(operation == 0, this);
            Require(input.Length == output.Length);
            fixed (byte* source = input)
            fixed (byte* destination = output)
                Require(EVP_CipherUpdate(operation, destination, out int size, source, input.Length) == 1 && size == output.Length);
        }

        public void Dispose()
        {
            EVP_CIPHER_CTX_free(operation);
            EVP_CIPHER_free(cipher);
            operation = cipher = 0;
        }
    }

    public static byte[] Hmac(string algorithm, ReadOnlySpan<byte> key, uint sequence, ReadOnlySpan<byte> length,
        ReadOnlySpan<byte> body)
    {
        using var input = new SshWriter(8 + body.Length);
        input.Number(sequence);
        input.Raw(length);
        input.Raw(body);
        byte[] result = new byte[algorithm == "SHA512" ? 64 : 32];
        fixed (byte* secret = key)
        fixed (byte* message = input.Span)
        fixed (byte* destination = result)
            Require(EVP_Q_mac(Context.Value, "HMAC", "provider=default", algorithm, 0, secret, (nuint)key.Length,
                message, (nuint)input.Span.Length, destination, (nuint)result.Length, out nuint written) != 0 &&
                written == (nuint)result.Length);
        return result;
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_Q_mac(nint library, string name, string properties, string algorithm, nint parameters,
        byte* key, nuint keyLength, byte* data, nuint dataLength, byte* output, nuint outputSize, out nuint outputLength);
}
