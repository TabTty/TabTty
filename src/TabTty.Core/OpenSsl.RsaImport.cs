using System.Runtime.InteropServices;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    public static byte[] RsaReducedExponent(ReadOnlySpan<byte> exponent, ReadOnlySpan<byte> prime)
    {
        nint d = 0, modulus = 0, result = 0, context = 0;
        try
        {
            d = ReadSecretNumber(exponent);
            modulus = ReadSecretNumber(prime);
            Require(BN_sub_word(modulus, 1) == 1);
            result = BN_secure_new();
            context = BN_CTX_secure_new();
            Require(result != 0 && context != 0 && BN_nnmod(result, d, modulus, context) == 1);
            int size = (BN_num_bits(result) + 7) / 8;
            Require(size is > 0 and <= 1024);
            byte[] bytes = new byte[size];
            fixed (byte* output = bytes) Require(BN_bn2binpad(result, output, size) == size);
            return bytes;
        }
        finally
        {
            BN_clear_free(d);
            BN_clear_free(modulus);
            BN_clear_free(result);
            BN_CTX_free(context);
        }
    }

    private static nint ReadSecretNumber(ReadOnlySpan<byte> encoded)
    {
        Require(encoded.Length is > 0 and <= 1024);
        nint value = BN_secure_new();
        Require(value != 0);
        fixed (byte* input = encoded)
        {
            if (BN_bin2bn(input, encoded.Length, value) != 0) return value;
        }
        BN_clear_free(value);
        throw Failure();
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint BN_secure_new();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BN_clear_free(nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int BN_sub_word(nint value, nuint word);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint BN_CTX_secure_new();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BN_CTX_free(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int BN_nnmod(nint result, nint dividend, nint modulus, nint context);
}
