using System.Diagnostics;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("Pass the directory containing the packaged TabTty and bridge.");
string directory = Path.GetFullPath(args[0]);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var app = Process.Start(new ProcessStartInfo(Path.Combine(directory, "TabTty.exe"))
{
    UseShellExecute = false,
    CreateNoWindow = true,
}) ?? throw new InvalidOperationException("TabTty did not start.");
try
{
    using var bridge = StartBridge();
    try
    {
        var client = new { name = "Independent test client", version = "1" };
        var init = await Request(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = client });
        Check(init.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25");
        await Write(new { jsonrpc = "2.0", method = "notifications/initialized" });
        var legacyList = (await Request(2, "tools/list", new { })).GetProperty("result");
        Check(!legacyList.TryGetProperty("resultType", out _) && legacyList.GetProperty("tools").GetArrayLength() == 7);
        var list = (await Request(3, "tools/call", new { name = "tabtty_list_shared_sessions", arguments = new { } }))
            .GetProperty("result");
        var structured = list.GetProperty("structuredContent");
        Check(structured.GetProperty("sessions").GetArrayLength() == 0 &&
            structured.GetProperty("pairing_code").GetString()?.Length == 8);
        using var mirror = JsonDocument.Parse(list.GetProperty("content")[0].GetProperty("text").GetString()!);
        Check(JsonElement.DeepEquals(structured, mirror.RootElement));
        string originalId = structured.GetProperty("counterpart_id").GetString()!;
        string originalCode = structured.GetProperty("pairing_code").GetString()!;
        string secondId = await CheckFreshAttachment();
        string restartedId = await CheckFreshAttachment();
        Check(secondId != restartedId);
        Console.WriteLine("PASS McpPackagedBridgesHaveSeparateAttachments");
        var unknown = (await Request(4, "tools/call", new { name = "tabtty_read_state", arguments = new { session = "foreign" } }))
            .GetProperty("result");
        Check(unknown.GetProperty("isError").GetBoolean() &&
            unknown.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "not_shared");

        var metadata = new Dictionary<string, object>
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientCapabilities"] = new { },
            ["io.modelcontextprotocol/clientInfo"] = client,
        };
        var discovery = (await Request(5, "server/discover", new { _meta = metadata })).GetProperty("result");
        Check(discovery.GetProperty("resultType").GetString() == "complete" && discovery.GetProperty("ttlMs").GetInt32() == 300000);
        var modernList = (await Request(6, "tools/list", new { _meta = metadata })).GetProperty("result");
        Check(modernList.GetProperty("cacheScope").GetString() == "public" && modernList.GetProperty("tools").GetArrayLength() == 7);
        metadata["io.modelcontextprotocol/protocolVersion"] = "1900-01-01";
        var unsupported = (await Request(7, "server/discover", new { _meta = metadata })).GetProperty("error");
        Check(unsupported.GetProperty("code").GetInt32() == -32022 &&
            unsupported.GetProperty("data").GetProperty("supported").GetArrayLength() == 2 &&
            unsupported.GetProperty("data").GetProperty("requested").GetString() == "1900-01-01");
        metadata["io.modelcontextprotocol/protocolVersion"] = "2026-07-28";
        metadata.Remove("io.modelcontextprotocol/clientCapabilities");
        Check((await Request(8, "tools/list", new { _meta = metadata })).GetProperty("error").GetProperty("code").GetInt32() == -32602);
        await bridge.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":8,"id":9,"method":"tools/list"}""");
        await bridge.StandardInput.FlushAsync(deadline.Token);
        Check((await Read()).GetProperty("error").GetProperty("code").GetInt32() == -32700);

        // Fill the ordinary-call bucket, then exercise the reserved return path.
        bool sawBusy = false;
        for (int request = 20; request < 220 && !sawBusy; request++)
        {
            await Write(new { jsonrpc = "2.0", id = request, method = "tools/call",
                @params = new { name = "tabtty_read_state", arguments = new { session = "unshared" } } });
            JsonElement result = (await Read()).GetProperty("result").GetProperty("structuredContent");
            sawBusy |= result.GetProperty("error").GetProperty("code").GetString() == "busy";
        }
        Check(sawBusy);
        var returned = (await Request(220, "tools/call", new { name = "tabtty_return_control",
            arguments = new { session = "unshared", control = "unknown",
                reason = string.Concat(Enumerable.Repeat("🌱", 256)) } })).GetProperty("result");
        Check(returned.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "not_shared");
        var oversized = (await Request(221, "tools/call", new { name = "tabtty_return_control",
            arguments = new { session = "unshared", control = "unknown", reason = new string('a', 16 * 1024) } }))
            .GetProperty("result").GetProperty("structuredContent");
        Check(oversized.GetProperty("error").GetProperty("code").GetString() == "resource_limit");
        bridge.StandardInput.Close();
        await bridge.WaitForExitAsync(deadline.Token);
        Check(bridge.ExitCode == 0 && !app.HasExited);
        Console.WriteLine("PASS McpBridgeLegacyModernAndUnsharedDiscovery");

        // A second copy must forward to the same verified GUI even after the
        // MCP attachment closes. A forwarding error would show an error dialog.
        for (int i = 0; i < 2; i++)
        {
            using var second = Process.Start(new ProcessStartInfo(Path.Combine(directory, "TabTty.exe"))
                { UseShellExecute = false, CreateNoWindow = true })!;
            try { await second.WaitForExitAsync(deadline.Token); }
            finally { if (!second.HasExited) second.Kill(); }
            Check(second.ExitCode == 0 && !app.HasExited);
        }
        Console.WriteLine("PASS VerifiedGuiActivation");

        async Task<string> CheckFreshAttachment()
        {
            using var peer = StartBridge();
            try
            {
                await Request(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = client }, peer);
                await Write(new { jsonrpc = "2.0", method = "notifications/initialized" }, peer);
                var reply = await Request(2, "tools/call", new { name = "tabtty_list_shared_sessions", arguments = new { } }, peer);
                var listing = reply.GetProperty("result").GetProperty("structuredContent");
                string id = listing.GetProperty("counterpart_id").GetString()!;
                string code = listing.GetProperty("pairing_code").GetString()!;
                Check(id.Length == 32 && id != originalId && code.Length == 8 && code != originalCode &&
                    listing.GetProperty("sessions").GetArrayLength() == 0 && !bridge.HasExited);
                peer.StandardInput.Close();
                await peer.WaitForExitAsync(deadline.Token);
                Check(peer.ExitCode == 0);
                return id;
            }
            finally { if (!peer.HasExited) peer.Kill(); }
        }

        async Task<JsonElement> Request(int id, string method, object parameters, Process? peer = null)
        {
            await Write(new { jsonrpc = "2.0", id, method, @params = parameters }, peer);
            JsonElement response = await Read(peer);
            Check(response.GetProperty("id").GetInt32() == id);
            return response;
        }
        async Task Write(object message, Process? peer = null)
        {
            var input = (peer ?? bridge).StandardInput;
            await input.WriteLineAsync(JsonSerializer.Serialize(message));
            await input.FlushAsync(deadline.Token);
        }
        async Task<JsonElement> Read(Process? peer = null)
        {
            var process = peer ?? bridge;
            string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            if (line is null) throw new IOException("Bridge closed stdout: " + await process.StandardError.ReadToEndAsync(deadline.Token));
            using var message = JsonDocument.Parse(line);
            return message.RootElement.Clone();
        }
    }
    finally
    {
        if (!bridge.HasExited) bridge.Kill();
    }
}
finally
{
    // Only close the exact process started by this fixture.
    if (!app.HasExited && !app.CloseMainWindow()) app.Kill();
    try { await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (TimeoutException) { if (!app.HasExited) app.Kill(); }
}

using (var unavailable = StartBridge())
{
    try
    {
        string output = await unavailable.StandardOutput.ReadToEndAsync(deadline.Token);
        string diagnostic = await unavailable.StandardError.ReadToEndAsync(deadline.Token);
        await unavailable.WaitForExitAsync(deadline.Token);
        Check(output.Length == 0 && diagnostic.Trim() == "tabtty_unavailable" && unavailable.ExitCode != 0);
        Console.WriteLine("PASS McpBridgeReportsMissingGui");
    }
    finally
    {
        if (!unavailable.HasExited) unavailable.Kill();
    }
}

Process StartBridge() => Process.Start(new ProcessStartInfo(Path.Combine(directory, "TabTty.McpBridge.exe"), "--stdio")
{
    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
    RedirectStandardOutput = true, RedirectStandardError = true,
}) ?? throw new InvalidOperationException("Bridge did not start.");

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Unexpected response from the packaged MCP bridge.");
}
