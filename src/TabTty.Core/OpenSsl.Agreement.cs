using System.Runtime.InteropServices;
using System.Text;

namespace TabTty.Core;

internal static unsafe partial class OpenSsl
{
    public static Key GenerateGroup(string type, string group)
    {
        nint operation = EVP_PKEY_CTX_new_from_name(Context.Value, type, "provider=default");
        Require(operation != 0);
        try
        {
            Require(EVP_PKEY_keygen_init(operation) == 1 && EVP_PKEY_CTX_set_group_name(operation, group) == 1);
            int status = EVP_PKEY_generate(operation, out nint pointer);
            var key = new Key(pointer);
            if (status == 1 && !key.IsInvalid) return key;
            key.Dispose();
            throw Failure();
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static byte[] GroupPublic(Key key)
        => EncodedPublic(key, 0);

    public static byte[] EncodedPublic(Key key, int expectedSize)
    {
        Require(EVP_PKEY_get_octet_string_param(key, "encoded-pub-key", null, 0, out nuint length) == 1 &&
            length is > 0 and <= 1184 && (expectedSize == 0 ? length <= 133 : length == (nuint)expectedSize));
        byte[] output = new byte[(int)length];
        fixed (byte* value = output)
            Require(EVP_PKEY_get_octet_string_param(key, "encoded-pub-key", value, length, out nuint written) == 1 &&
                written == length);
        return output;
    }

    public static Key GenerateMlKem768()
    {
        nint operation = EVP_PKEY_CTX_new_from_name(Context.Value, "ML-KEM-768", "provider=default");
        Require(operation != 0);
        try
        {
            Require(EVP_PKEY_keygen_init(operation) == 1);
            int status = EVP_PKEY_generate(operation, out nint pointer);
            var key = new Key(pointer);
            if (status == 1 && !key.IsInvalid) return key;
            key.Dispose();
            throw Failure();
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static void DecapsulateMlKem768(Key key, ReadOnlySpan<byte> ciphertext, Span<byte> secret)
    {
        Require(ciphertext.Length == 1088 && secret.Length == 32);
        nint operation = EVP_PKEY_CTX_new_from_pkey(Context.Value, key, "provider=default");
        Require(operation != 0);
        try
        {
            Require(EVP_PKEY_decapsulate_init(operation, 0) == 1);
            nuint length = 32;
            fixed (byte* source = ciphertext)
            fixed (byte* destination = secret)
                Require(EVP_PKEY_decapsulate(operation, destination, &length, source, (nuint)ciphertext.Length) == 1 &&
                    length == 32);
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static byte[] DhPublic(Key key)
    {
        Require(EVP_PKEY_get_bn_param(key, "pub", out nint value) == 1 && value != 0);
        try
        {
            int size = (BN_num_bits(value) + 7) / 8;
            Require(size is > 0 and <= 512);
            byte[] output = new byte[size];
            fixed (byte* destination = output) Require(BN_bn2binpad(value, destination, size) == size);
            return output;
        }
        finally { BN_free(value); }
    }

    public static void AgreeDh(Key privateKey, string group, ReadOnlySpan<byte> peer, Span<byte> output)
    {
        peer = PositiveInteger(peer, output.Length);
        nint number;
        fixed (byte* bytes = peer) number = BN_bin2bn(bytes, peer.Length, 0);
        Require(number != 0);
        nint builder = 0;
        try
        {
            builder = OSSL_PARAM_BLD_new();
            Require(builder != 0);
            byte[] groupName = Encoding.ASCII.GetBytes(group + "\0");
            fixed (byte* names = "group\0pub\0"u8)
            fixed (byte* groupPointer = groupName)
            {
                Require(OSSL_PARAM_BLD_push_utf8_string(builder, names, groupPointer, 0) == 1 &&
                    OSSL_PARAM_BLD_push_BN(builder, names + 6, number) == 1);
                using Key publicKey = BuildPublicKey("DH", builder);
                DeriveShared(privateKey, publicKey, output, allowUnpadded: true);
            }
        }
        finally
        {
            OSSL_PARAM_BLD_free(builder);
            BN_free(number);
        }
    }

    internal static ReadOnlySpan<byte> PositiveInteger(ReadOnlySpan<byte> encoded, int maximum)
    {
        if (encoded.IsEmpty || encoded[0] >= 0x80 || encoded.Length - (encoded[0] == 0 ? 1 : 0) > maximum ||
            (encoded[0] == 0 && (encoded.Length == 1 || encoded[1] < 0x80)))
            throw new InvalidDataException("Invalid positive SSH mpint.");
        return encoded[0] == 0 ? encoded[1..] : encoded;
    }

    public static void AgreeEc(Key privateKey, string group, ReadOnlySpan<byte> peer, Span<byte> output)
    {
        int publicLength = group switch { "prime256v1" => 65, "secp384r1" => 97, "secp521r1" => 133, _ => 0 };
        Require(peer.Length == publicLength && peer[0] == 4);
        using Key publicKey = ImportEcPublic(group, peer);
        DeriveShared(privateKey, publicKey, output);
    }

    private static Key ImportEcPublic(string group, ReadOnlySpan<byte> peer)
    {
        nint builder = OSSL_PARAM_BLD_new();
        Require(builder != 0);
        try
        {
            byte[] groupName = Encoding.ASCII.GetBytes(group + "\0");
            fixed (byte* names = "group\0pub\0"u8)
            fixed (byte* groupPointer = groupName)
            fixed (byte* value = peer)
            {
                Require(OSSL_PARAM_BLD_push_utf8_string(builder, names, groupPointer, 0) == 1 &&
                    OSSL_PARAM_BLD_push_octet_string(builder, names + 6, value, (nuint)peer.Length) == 1);
                return BuildPublicKey("EC", builder);
            }
        }
        finally { OSSL_PARAM_BLD_free(builder); }
    }

    private static Key BuildPublicKey(string type, nint builder)
    {
        nint parameters = OSSL_PARAM_BLD_to_param(builder);
        Require(parameters != 0);
        nint operation = 0;
        try
        {
            operation = EVP_PKEY_CTX_new_from_name(Context.Value, type, "provider=default");
            Require(operation != 0 && EVP_PKEY_fromdata_init(operation) == 1);
            int status = EVP_PKEY_fromdata(operation, out nint pointer, 0x86, parameters);
            var key = new Key(pointer);
            try
            {
                Require(status == 1 && !key.IsInvalid);
                nint check = EVP_PKEY_CTX_new_from_pkey(Context.Value, key, "provider=default");
                Require(check != 0);
                try { Require(EVP_PKEY_public_check(check) == 1); }
                finally { EVP_PKEY_CTX_free(check); }
                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        finally
        {
            EVP_PKEY_CTX_free(operation);
            OSSL_PARAM_free(parameters);
        }
    }

    private static void DeriveShared(Key privateKey, Key publicKey, Span<byte> output, bool allowUnpadded = false)
    {
        nint operation = EVP_PKEY_CTX_new_from_pkey(Context.Value, privateKey, "provider=default");
        Require(operation != 0);
        try
        {
            Require(EVP_PKEY_derive_init(operation) == 1 && EVP_PKEY_derive_set_peer(operation, publicKey) == 1);
            nuint length = (nuint)output.Length;
            fixed (byte* result = output)
                Require(EVP_PKEY_derive(operation, result, &length) == 1 && length > 0 && length <= (nuint)output.Length &&
                    (allowUnpadded || length == (nuint)output.Length));
            if (length < (nuint)output.Length)
            {
                int padding = output.Length - (int)length;
                output[..(int)length].CopyTo(output[padding..]);
                output[..padding].Clear();
            }
        }
        finally { EVP_PKEY_CTX_free(operation); }
    }

    public static byte[] Hash(string name, ReadOnlySpan<byte> input, int size)
    {
        byte[] output = new byte[size];
        fixed (byte* source = input)
        fixed (byte* destination = output)
            Require(EVP_Q_digest(Context.Value, name, "provider=default", source, (nuint)input.Length,
                destination, out nuint actual) == 1 && actual == (nuint)size);
        return output;
    }

    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_PKEY_CTX_set_group_name(nint context, string name);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_PKEY_get_octet_string_param(Key key, string name, byte* output, nuint capacity, out nuint length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint OSSL_PARAM_BLD_new();
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void OSSL_PARAM_BLD_free(nint builder);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint OSSL_PARAM_BLD_to_param(nint builder);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void OSSL_PARAM_free(nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int OSSL_PARAM_BLD_push_utf8_string(nint builder, byte* name, byte* value, nuint length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int OSSL_PARAM_BLD_push_octet_string(nint builder, byte* name, byte* value, nuint length);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_fromdata_init(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_fromdata(nint context, out nint key, int selection, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_public_check(nint context);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int EVP_PKEY_get_bn_param(Key key, string name, out nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint BN_bin2bn(byte* source, int size, nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int BN_num_bits(nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int BN_bn2binpad(nint value, byte* destination, int capacity);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BN_free(nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int OSSL_PARAM_BLD_push_BN(nint builder, byte* name, nint value);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_decapsulate_init(nint context, nint parameters);
    [DefaultDllImportSearchPaths(SearchPath)]
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int EVP_PKEY_decapsulate(nint context, byte* secret, nuint* length, byte* ciphertext, nuint ciphertextLength);
}
