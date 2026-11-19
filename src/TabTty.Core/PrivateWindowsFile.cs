using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TabTty.Core;

[SupportedOSPlatform("windows")]
internal static class PrivateWindowsFile
{
    private const InheritanceFlags DirectoryInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    internal static FileStream Create(string path, FileSystemRights access, FileShare share, int bufferSize, FileOptions options)
    {
        var security = new FileSecurity();
        Protect(security, InheritanceFlags.None);
        return new FileInfo(path).Create(FileMode.CreateNew, access, share, bufferSize, options, security);
    }

    internal static byte[] DirectoryDescriptor()
    {
        var security = new DirectorySecurity();
        Protect(security, DirectoryInheritance);
        return security.GetSecurityDescriptorBinaryForm();
    }

    private static void Protect(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
    }

    internal static bool HasPrivateAccess(FileStream file) => HasPrivateAccess(file.GetAccessControl(), InheritanceFlags.None);

    internal static bool HasPrivateAccess(DirectoryInfo directory) => HasPrivateAccess(
        directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), DirectoryInheritance);

    private static bool HasPrivateAccess(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return security.AreAccessRulesProtected && identity.User!.Equals(security.GetOwner(typeof(SecurityIdentifier))) &&
            rules.Count == 1 && rules[0] is FileSystemAccessRule rule && rule.IdentityReference.Equals(identity.User) &&
            rule.InheritanceFlags == inheritance && rule.PropagationFlags == PropagationFlags.None &&
            rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights == FileSystemRights.FullControl;
    }
}
