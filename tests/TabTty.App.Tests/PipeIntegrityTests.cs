using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TabTty.AiTransport;

internal static partial class Program
{
    private static void PipeIdentityIncludesIntegrityLevel()
    {
        using var user = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
        var original = AiPipe.ReadTokenIdentity(user.AccessToken);
        const uint queryAndAdjustDefault = 0x0088;
        if (!DuplicateTokenEx(user.AccessToken, queryAndAdjustDefault, 0, 1, 1, out var copy))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (copy)
        {
            if (original != AiPipe.ReadTokenIdentity(copy))
                throw new InvalidOperationException("A duplicate of the same token must match.");

            // Change only the label on this detached token. No process runs with it.
            var lower = new SecurityIdentifier(original.IntegritySid == "S-1-16-4096"
                ? "S-1-16-0" : "S-1-16-4096");
            var sid = new byte[lower.BinaryLength];
            lower.GetBinaryForm(sid, 0);
            int labelSize = Marshal.SizeOf<MandatoryLabel>();
            nint buffer = Marshal.AllocHGlobal(labelSize + sid.Length);
            try
            {
                nint labelSid = buffer + labelSize;
                Marshal.Copy(sid, 0, labelSid, sid.Length);
                Marshal.StructureToPtr(new MandatoryLabel(labelSid, 0x20), buffer, false);
                if (!SetTokenInformation(copy, 25, buffer, labelSize + sid.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var changed = AiPipe.ReadTokenIdentity(copy);
                if (original == changed || changed != (original with { IntegritySid = lower.Value }))
                    throw new InvalidOperationException("Pipe identity must distinguish the token integrity level.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MandatoryLabel(nint sid, uint attributes)
    {
        public nint Sid = sid;
        public uint Attributes = attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle source, uint access, nint attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle copy);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, int size);
}
