using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TabTty.AiTransport;

internal static class ProtocolControlCheck
{
    internal static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string suffix = Guid.NewGuid().ToString("N");
        using var tunnel = Server("TabTty-test-rpc-" + suffix);
        using var connected = new NamedPipeClientStream(".", "TabTty-test-rpc-" + suffix,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var input = Server("TabTty-test-stdin-" + suffix);
        using var host = new NamedPipeClientStream(".", "TabTty-test-stdin-" + suffix,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(Connect(tunnel, connected), Connect(input, host));
        using var process = Process.GetCurrentProcess();
        using var channel = new AiPrivateChannel(connected, process.SafeHandle);
        using var output = new MemoryStream();
        Task bridge = new McpStdio(channel, input, output).RunAsync();
        try
        {
            var metadata = new Dictionary<string, object>
            {
                [McpProtocol.VersionKey] = McpProtocol.Modern,
                [McpProtocol.CapabilitiesKey] = new { },
            };
            await Send(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new
            { _meta = metadata, name = "tabtty_wait", arguments = new { session = "fixture", kind = "delay" } } });
            var wait = await Read();
            int waitId = wait.GetProperty("id").GetInt32();
            Verify(wait.GetProperty("name").ValueEquals("tabtty_wait"));

            await Send(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { _meta = metadata } });
            Verify((await Read()).GetProperty("name").ValueEquals("protocol"));
            // Leave admission pending while the host uses the separate control path.
            await Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 1 } });
            Verify((await Read()).GetProperty("cancel").GetInt32() == waitId);
            await Send(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new
            { _meta = metadata, name = "tabtty_return_control", arguments = new { session = "fixture", control = "fixture" } } });
            Verify((await Read()).GetProperty("name").ValueEquals("tabtty_return_control"));
            Console.WriteLine("PASS McpControlMessagesPassPendingProtocolAdmission");
        }
        finally
        {
            channel.Stop();
            await bridge;
        }

        Task Connect(NamedPipeServerStream server, NamedPipeClientStream client) =>
            Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token));

        async Task Send(object message)
        {
            await host.WriteAsync(AiFrames.Serialize(message), deadline.Token);
            await host.WriteAsync("\n"u8.ToArray(), deadline.Token);
        }

        async Task<JsonElement> Read()
        {
            while (true)
            {
                JsonElement message = await AiFrames.ReadPrivateAsync(tunnel, deadline.Token);
                if (!message.TryGetProperty("heartbeat", out _)) return message;
            }
        }
    }

    private static NamedPipeServerStream Server(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static void Verify(bool condition)
    {
        if (!condition) throw new InvalidOperationException("The fixture received an unexpected private control message.");
    }
}
