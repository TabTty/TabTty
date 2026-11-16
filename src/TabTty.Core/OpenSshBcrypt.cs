using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TabTty.Core;

internal static unsafe class OpenSshBcrypt
{
    internal static void Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, uint rounds, Span<byte> output)
    {
        if (password.Length is < 1 or > 16384 || salt.Length is < 1 or > 64 ||
            rounds is < 1 or > 1024 || output.Length != 48)
            throw new InvalidDataException("Invalid OpenSSH bcrypt key-derivation parameters.");
        fixed (byte* input = password)
        fixed (byte* saltPointer = salt)
        fixed (byte* result = output)
        {
            if (TabTtyBcryptPbkdf(input, (nuint)password.Length, saltPointer, (nuint)salt.Length, rounds, result,
                    (nuint)output.Length, &Hash, &Random) == 0)
                return;
        }
        CryptographicOperations.ZeroMemory(output);
        throw new CryptographicException("OpenSSH bcrypt key derivation failed.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Hash(byte* destination, byte* source, nuint length)
    {
        try
        {
            if (length > 16384) return 0;
            byte[] digest = OpenSsl.Hash("SHA512", new ReadOnlySpan<byte>(source, (int)length), 64);
            try { digest.CopyTo(new Span<byte>(destination, 64)); }
            finally { CryptographicOperations.ZeroMemory(digest); }
            return 1;
        }
        catch (Exception exception) when (exception is CryptographicException or DllNotFoundException or EntryPointNotFoundException)
        { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Random(byte* destination, nuint length)
    {
        try
        {
            if (length > 48) return 0;
            OpenSsl.Fill(new Span<byte>(destination, (int)length));
            return 1;
        }
        catch (Exception exception) when (exception is CryptographicException or DllNotFoundException or EntryPointNotFoundException)
        { return 0; }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory |
        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32)]
    [DllImport("TabTty.Native.dll", EntryPoint = "TermTabBcryptPbkdf", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TabTtyBcryptPbkdf(byte* password, nuint passwordLength, byte* salt, nuint saltLength,
        uint rounds, byte* key, nuint keyLength,
        delegate* unmanaged[Cdecl]<byte*, byte*, nuint, int> hash,
        delegate* unmanaged[Cdecl]<byte*, nuint, int> random);
}
