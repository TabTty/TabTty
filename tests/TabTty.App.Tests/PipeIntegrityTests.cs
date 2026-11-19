using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TabTty.AiTransport;

internal static partial class Program
{
    private static async Task OwnedPipeSurvivesEarlyClientDisconnect()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = new AiPipe(fixture: Guid.NewGuid());
        using var listener = endpoint.OpenListeners(1)[0];
        var rules = listener.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier));
        CheckMcpContract(rules.OfType<PipeAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow) &&
            rules.OfType<PipeAccessRule>().All(rule => rule.AccessControlType != AccessControlType.Allow ||
                !rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance)),
            "The published pipe grants the client server-instance creation rights.");

        using (var disconnected = await endpoint.ConnectAsync(deadline.Token)) { }
        Task<bool> accepting = AiPipe.AcceptAsync(listener, deadline.Token);
        using var next = await endpoint.ConnectAsync(deadline.Token);
        CheckMcpContract(await accepting, "The owned listener did not recover after a client left.");
        await next.WriteAsync(new byte[] { 42 }, deadline.Token);
        byte[] received = new byte[1];
        await listener.ReadExactlyAsync(received, deadline.Token);
        CheckMcpContract(received[0] == 42, "The next client could not exchange data on the owned listener.");
        Console.WriteLine("PASS McpOwnedPipeSurvivesEarlyDisconnect");
    }

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
