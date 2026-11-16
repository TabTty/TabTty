using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    private const string Library = "libcrypto-3-x64.dll";
    private const DllImportSearchPath SearchPath = DllImportSearchPath.AssemblyDirectory |
        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;
    private static readonly Lazy<nint> Context = new(() =>
    {
        if (OPENSSL_init_crypto(0x00000080, 0) != 1 || OPENSSL_version_major() != 3 ||
            OPENSSL_version_minor() != 5 || OPENSSL_version_patch() < 9)
            throw new CryptographicException("A supported app-local OpenSSL 3.5 LTS runtime is required.");
        nint context = OSSL_LIB_CTX_new();
        if (context == 0) throw new CryptographicException("OpenSSL initialization failed.");
        return context;
    });

    internal static void Fill(Span<byte> bytes)
    {
        fixed (byte* destination = bytes)
            if (RAND_bytes_ex(Context.Value, destination, (nuint)bytes.Length, 256) != 1)
                throw new CryptographicException("OpenSSL random generation failed.");
    }

    internal static Guid NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        Fill(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    internal static void DeriveVaultKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Span<byte> output)
    {
        nint algorithm = EVP_KDF_fetch(Context.Value, "ARGON2ID", "provider=default");
        if (algorithm == 0) throw new CryptographicException("OpenSSL Argon2id is unavailable.");
        nint operation = 0;
        try
        {
            operation = EVP_KDF_CTX_new(algorithm);
            if (operation == 0) throw new CryptographicException("OpenSSL KDF initialization failed.");
            uint memory = 65536, iterations = 3, lanes = 4, threads = 1, version = 0x13, earlyClean = 1;
            fixed (byte* keys = "pass\0salt\0memcost\0iter\0lanes\0threads\0version\0early_clean\0"u8)
            fixed (byte* input = password)
            fixed (byte* saltPointer = salt)
            fixed (byte* result = output)
            {
                OsslParam* parameters = stackalloc OsslParam[9]
                {
                    OSSL_PARAM_construct_octet_string(keys, input, (nuint)password.Length),
                    OSSL_PARAM_construct_octet_string(keys + 5, saltPointer, (nuint)salt.Length),
                    OSSL_PARAM_construct_uint32(keys + 10, &memory),
                    OSSL_PARAM_construct_uint32(keys + 18, &iterations),
                    OSSL_PARAM_construct_uint32(keys + 23, &lanes),
                    OSSL_PARAM_construct_uint32(keys + 29, &threads),
                    OSSL_PARAM_construct_uint32(keys + 37, &version),
                    OSSL_PARAM_construct_uint32(keys + 45, &earlyClean),
                    default,
                };
                if (EVP_KDF_derive(operation, result, (nuint)output.Length, parameters) != 1)
                    throw new CryptographicException("OpenSSL Argon2id derivation failed.");
            }
        }
        finally
        {
            EVP_KDF_CTX_free(operation);
            EVP_KDF_free(algorithm);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OsslParam
    {
        public nint Key;
        public uint DataType;
        public nint Data;
        public nuint DataSize;
        public nuint ReturnSize;
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int OPENSSL_init_crypto(ulong options, nint settings);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint OPENSSL_version_major();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint OPENSSL_version_minor();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint OPENSSL_version_patch();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint OSSL_LIB_CTX_new();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int RAND_bytes_ex(nint context, byte* destination, nuint length, uint strength);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint EVP_KDF_fetch(nint context, string name, string properties);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint EVP_KDF_CTX_new(nint algorithm);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_KDF_CTX_free(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void EVP_KDF_free(nint algorithm);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern OsslParam OSSL_PARAM_construct_octet_string(byte* key, byte* data, nuint length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern OsslParam OSSL_PARAM_construct_uint32(byte* key, uint* data);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_KDF_derive(nint context, byte* output, nuint length, OsslParam* parameters);
}
