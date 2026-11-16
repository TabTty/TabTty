using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace TabTty.AiTransport;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (!arguments.SequenceEqual(["--stdio"]))
        {
            Console.Error.WriteLine("bridge_usage");
            return 2;
        }
        bool attached = false;
        try
        {
            var endpoint = new AiPipe();
            using var launcher = AiPipe.OpenLauncher();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = await endpoint.ConnectAsync(deadline.Token).ConfigureAwait(false);
            using var gui = endpoint.VerifyPeer(pipe, server: true, "TabTty.exe", out _);
            await AiPrivateChannel.HandshakeAsync(pipe, server: false, deadline.Token).ConfigureAwait(false);
            attached = true;
            using var channel = new AiPrivateChannel(pipe, gui, launcher);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await new McpStdio(channel, input, output).RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or JsonException or
            InvalidOperationException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(attached ? "bridge_attachment_ended" :
                error is InvalidDataException or JsonException ? "bridge_version_mismatch" : "tabtty_unavailable");
            return 1;
        }
    }
}
