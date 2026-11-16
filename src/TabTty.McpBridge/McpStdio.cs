using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TabTty.AiTransport;

internal sealed class McpStdio(AiPrivateChannel channel, Stream input, Stream output)
{
    private readonly object gate = new();
    private readonly Dictionary<int, Pending> pending = [];
    private readonly Dictionary<int, TaskCompletionSource<int>> protocolAdmissions = [];
    private readonly Channel<Outgoing> responses = Channel.CreateBounded<Outgoing>(20);
    private const int MaxQueued = 8 * 1024 * 1024;
    private int queued, nextId;
    private bool legacyInitialized, legacyReady;
    private double requestsLeft = 40;
    private long refilledAt = Stopwatch.GetTimestamp();
    private sealed record Pending(int Id, JsonElement PublicId, string Key, bool Modern, bool Ordinary);
    private sealed record Outgoing(byte[] Bytes, int? Request = null);
    private readonly Dictionary<int, (string? Session, bool Listing)> queuedReplies = [];

    internal async Task RunAsync()
    {
        Task rpc = channel.RunAsync(ReceivePrivateAsync, CancellationToken.None);
        Task read = ReadAsync(), write = WriteAsync();
        try { await Task.WhenAny(rpc, read, write).ConfigureAwait(false); }
        finally
        {
            channel.Stop();
            responses.Writer.TryComplete();
            input.Dispose();
            output.Dispose();
            try { await Task.WhenAll(rpc, read, write).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or
                EndOfStreamException or ObjectDisposedException) { }
        }
    }

    private async Task ReadAsync()
    {
        var reader = new AiFrames.StdioReader(input);
        while (!channel.Token.IsCancellationRequested)
        {
            var frame = await reader.ReadAsync(channel.Token).ConfigureAwait(false);
            if (frame is null) return;
            JsonElement message;
            try { message = AiFrames.Parse(frame.Value); }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            {
                Error(null, -32700, "Parse error");
                continue;
            }
            await DispatchAsync(message).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(JsonElement message)
    {
        JsonElement? id = message.TryGetProperty("id", out var rawId) && ValidId(rawId) ? rawId : null;
        if (!message.TryGetProperty("jsonrpc", out var rpc) || !TextEquals(rpc, "2.0") ||
            !message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String ||
            message.TryGetProperty("id", out _) && id is null)
        { Error(id, -32600, "Invalid request"); return; }
        string method = methodElement.GetString()!;
        var parameters = Get(message, "params");
        if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        { if (id is not null) Error(id, -32602, "Invalid params"); return; }
        if (id is null)
        {
            if (method == "notifications/initialized" && legacyInitialized) legacyReady = true;
            if (method == "notifications/cancelled") Cancel(Get(parameters, "requestId"));
            return;
        }
        var metadata = Get(parameters, "_meta");
        var version = Get(metadata, McpProtocol.VersionKey);
        var capabilities = Get(metadata, McpProtocol.CapabilitiesKey);
        bool modern = method == "server/discover" || version.ValueKind != JsonValueKind.Undefined ||
            capabilities.ValueKind != JsonValueKind.Undefined;
        if (metadata.ValueKind != JsonValueKind.Undefined &&
            (metadata.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(metadata.GetRawText()) > 16 * 1024))
        { Error(id, -32602, "Invalid metadata"); return; }
        if (modern)
        {
            if (version.ValueKind != JsonValueKind.String || capabilities.ValueKind != JsonValueKind.Object)
            { Error(id, -32602, "Missing protocol metadata"); return; }
            if (!TextEquals(version, McpProtocol.Modern))
            {
                string requested = version.GetString()!;
                if (requested.Length > 64) { Error(id, -32602, "Invalid protocol version"); return; }
                Error(id, -32022, "Unsupported protocol version", new { supported = McpProtocol.Versions, requested });
                return;
            }
            if (!AnnounceClient(id.Value, Get(metadata, McpProtocol.ClientKey))) return;
        }
        else if (method is not ("initialize" or "ping") && !legacyReady)
        { Error(id, -32600, "Initialize first"); return; }
        string key = IdKey(id.Value);
        lock (gate)
        {
            if (pending.Values.Any(value => value.Key == key))
            { Error(id, -32600, "Request ID is already in use"); return; }
        }
        string? tool = method == "tools/call" && Get(parameters, "name") is { ValueKind: JsonValueKind.String } name
            ? name.GetString() : null;
        if (tool != "tabtty_return_control" && !TakeRequest())
        {
            if (method == "tools/call") ToolResult(id.Value, modern, ErrorBody("busy", 50));
            else Error(id, -31001, "Busy", new { retry_after_ms = 50 });
            return;
        }
        if (method is "initialize" or "ping" or "server/discover" or "tools/list")
        {
            int retry = await AdmitProtocolCallAsync().ConfigureAwait(false);
            if (retry != 0)
            {
                Error(id, -31001, "Busy", new { retry_after_ms = retry });
                return;
            }
        }
        switch (method)
        {
            case "initialize" when !modern:
                if (legacyInitialized || Get(parameters, "protocolVersion").ValueKind != JsonValueKind.String ||
                    Get(parameters, "capabilities").ValueKind != JsonValueKind.Object ||
                    Get(parameters, "clientInfo").ValueKind != JsonValueKind.Object)
                { Error(id, -32602, "Invalid initialization"); return; }
                if (!AnnounceClient(id.Value, Get(parameters, "clientInfo"))) return;
                legacyInitialized = true;
                Respond(id.Value, new { protocolVersion = McpProtocol.Legacy, capabilities = McpProtocol.Capabilities,
                    serverInfo = McpProtocol.ServerInfo });
                break;
            case "ping" when !modern:
                Respond(id.Value, new { });
                break;
            case "server/discover" when modern:
                Respond(id.Value, new { resultType = "complete", supportedVersions = McpProtocol.Versions,
                    capabilities = McpProtocol.Capabilities,
                    _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = McpProtocol.ServerInfo },
                    ttlMs = 300000, cacheScope = "public" });
                break;
            case "tools/list":
                if (Get(parameters, "cursor").ValueKind != JsonValueKind.Undefined)
                { Error(id, -32602, "Invalid tool-list cursor"); return; }
                Respond(id.Value, modern
                    ? new { resultType = "complete", tools = McpProtocol.Tools.Values, ttlMs = 300000, cacheScope = "public" }
                    : (object)new { tools = McpProtocol.Tools.Values });
                break;
            case "tools/call":
                var args = Get(parameters, "arguments");
                if (tool is null || !McpProtocol.Tools.ContainsKey(tool) ||
                    args.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                { Error(id, -32602, "Invalid tool call"); return; }
                if (args.ValueKind == JsonValueKind.Undefined) args = McpProtocol.EmptyArguments;
                lock (gate)
                {
                    bool ordinary = tool != "tabtty_return_control";
                    if (pending.Count >= 18 || ordinary && pending.Values.Count(value => value.Ordinary) >= 16)
                    { ToolResult(id.Value, modern, ErrorBody("busy", 50)); return; }
                    int internalId = checked(++nextId);
                    pending.Add(internalId, new Pending(internalId, id.Value, key, modern, ordinary));
                    if (!channel.TrySend(new { id = internalId, name = tool, arguments = args }, priority: !ordinary))
                    {
                        pending.Remove(internalId);
                        ToolResult(id.Value, modern, ErrorBody("busy", 50));
                    }
                }
                break;
            default:
                Error(id, -32601, "Method not found");
                break;
        }
    }

    private bool AnnounceClient(JsonElement id, JsonElement info)
    {
        try
        {
            if (McpProtocol.ClientName(info) is { } name && name != lastClientName)
            {
                if (channel.TrySend(new { client_name = name })) lastClientName = name;
            }
            return true;
        }
        catch (JsonException) { Error(id, -32602, "Invalid client information"); return false; }
    }

    private string? lastClientName;

    private async Task<int> AdmitProtocolCallAsync()
    {
        int id;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            id = checked(++nextId);
            protocolAdmissions.Add(id, completion);
        }
        try
        {
            if (!channel.TrySend(new { id, name = "protocol", arguments = new { } })) return 50;
            return await completion.Task.WaitAsync(channel.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) protocolAdmissions.Remove(id);
        }
    }

    private void Cancel(JsonElement id)
    {
        if (!ValidId(id)) return;
        string key = IdKey(id);
        lock (gate)
        {
            Pending? match = pending.Values.FirstOrDefault(item => item.Key == key);
            if (match is null) return;
            pending.Remove(match.Id);
            queuedReplies.Remove(match.Id);
            if (!channel.TrySend(new { cancel = match.Id }, priority: true)) channel.Stop();
        }
    }

    private Task ReceivePrivateAsync(JsonElement message)
    {
        lock (gate)
        {
            if (Get(message, "revoked") is { ValueKind: JsonValueKind.String } revoked)
            {
                string idOfShare = revoked.GetString()!;
                foreach (var response in queuedReplies.Where(entry => entry.Value.Session == idOfShare || entry.Value.Listing)
                    .Select(entry => entry.Key).ToArray()) queuedReplies.Remove(response);
                return Task.CompletedTask;
            }
            int id = message.GetProperty("id").GetInt32();
            if (protocolAdmissions.TryGetValue(id, out var admission))
            {
                admission.TrySetResult(message.GetProperty("protocol_retry_after_ms").GetInt32());
                return Task.CompletedTask;
            }
            if (!pending.TryGetValue(id, out var request)) return Task.CompletedTask;
            var session = Get(message, "session");
            queuedReplies[id] = (session.ValueKind == JsonValueKind.String ? session.GetString() : null,
                Get(message, "listing").ValueKind == JsonValueKind.True);
            ToolResult(request.PublicId, request.Modern, message.GetProperty("result"), id);
        }
        return Task.CompletedTask;
    }

    private void ToolResult(JsonElement id, bool modern, JsonElement body, int? request = null)
    {
        object result = AiFrames.ToolResult(body, modern);
        if (AiFrames.Serialize(result).Length > AiFrames.MaxResult)
        {
            ToolResult(id, modern, ErrorBody("resource_limit"), request);
            return;
        }
        Enqueue(AiFrames.Serialize(new { jsonrpc = "2.0", id, result }), request);
    }

    private void Respond(JsonElement id, object result) => Enqueue(AiFrames.Serialize(new { jsonrpc = "2.0", id, result }));

    private void Error(JsonElement? id, int code, string message, object? data = null) =>
        Enqueue(AiFrames.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new { code, message, data } }));

    private void Enqueue(byte[] bytes, int? request = null)
    {
        int total = Interlocked.Add(ref queued, bytes.Length);
        if (bytes.Length <= AiFrames.MaxFrame && total <= MaxQueued && responses.Writer.TryWrite(new Outgoing(bytes, request))) return;
        Interlocked.Add(ref queued, -bytes.Length);
        channel.Stop();
    }

    private async Task WriteAsync()
    {
        await foreach (Outgoing response in responses.Reader.ReadAllAsync(channel.Token).ConfigureAwait(false))
        {
            try
            {
                lock (gate)
                {
                    if (response.Request is { } request)
                    {
                        if (!pending.Remove(request, out var original)) continue;
                        if (!queuedReplies.Remove(request))
                        {
                            ToolResult(original.PublicId, original.Modern, ErrorBody("not_shared"));
                            continue;
                        }
                    }
                }
                using var deadline = AiFrames.Deadline(channel.Token);
                await output.WriteAsync(response.Bytes, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                await output.WriteAsync("\n"u8.ToArray(), deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                await output.FlushAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally { Interlocked.Add(ref queued, -response.Bytes.Length); }
        }
    }

    private bool TakeRequest()
    {
        long now = Stopwatch.GetTimestamp();
        requestsLeft = Math.Min(40, requestsLeft + Stopwatch.GetElapsedTime(refilledAt, now).TotalSeconds * 20);
        refilledAt = now;
        if (requestsLeft < 1) return false;
        requestsLeft--;
        return true;
    }

    private static bool ValidId(JsonElement id) => id.ValueKind == JsonValueKind.String && id.GetString()!.Length <= 256 ||
        id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _);
    private static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s" + id.GetString() : "n" + id.GetInt64();
    private static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : default;
    private static bool TextEquals(JsonElement value, string text) => value.ValueKind == JsonValueKind.String && value.ValueEquals(text);
    private static JsonElement ErrorBody(string code, int? retry = null) => JsonSerializer.SerializeToElement(new
    { status = "error", error = new { code, retry_after_ms = retry } }, AiFrames.JsonOptions);
}
