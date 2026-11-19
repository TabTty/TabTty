using System.Security.Cryptography;

namespace TabTty.Core;

internal sealed record VaultFileEvidence(long Length, uint Volume, ulong Index, string Digest)
{
    internal static VaultFileEvidence? Read(string path, int maximum)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The credential vault must be an ordinary file.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > maximum) throw new InvalidDataException("Credential vault exceeds its size limit.");
            var identity = FileObjectIdentity.Read(file.SafeFileHandle);
            return new(file.Length, identity.Volume, identity.Index,
                Convert.ToHexString(SHA256.HashData(file)));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
