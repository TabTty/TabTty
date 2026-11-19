using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TabTty.AiTransport;

internal sealed class McpStdio(AiPrivateChannel channel, Stream input, Stream output)
{
    private readonly object gate = new();
    private readonly Dictionary<int, Pending> pending = [];
    private readonly Dictionary<int, ProtocolRequest> protocolAdmissions = [];
    private readonly Channel<Outgoing> responses = Channel.CreateBounded<Outgoing>(20);
    private int nextId;
    private bool legacyInitialized, legacyReady;
    private readonly AiRateLimit ordinaryCalls = AiRateLimit.OrdinaryCalls(Environment.TickCount64);
    private readonly AiRateLimit controlReturns = AiRateLimit.ControlReturns(Environment.TickCount64);
    private sealed record Pending(int Id, JsonElement PublicId, string Key, bool Modern, bool Ordinary);
    private sealed record ProtocolRequest(JsonElement PublicId, string Key, string Method, object Result);
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
            while (responses.Reader.TryRead(out var response)) channel.Payload.Release(response.Bytes.Length);
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
            lock (gate) Dispatch(message);
        }
    }

    private void Dispatch(JsonElement message)
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
        const int metadataLimit = 16 * 1024;
        if (metadata.ValueKind != JsonValueKind.Undefined &&
            (metadata.ValueKind != JsonValueKind.Object || Utf8Length(metadata) > metadataLimit))
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
        if (pending.Values.Any(value => value.Key == key) || protocolAdmissions.Values.Any(value => value.Key == key))
        {
            Error(id, -32600, "Request ID is already in use");
            return;
        }
        string? tool = method == "tools/call" && Get(parameters, "name") is { ValueKind: JsonValueKind.String } name
            ? name.GetString() : null;
        var budget = tool == "tabtty_return_control" ? controlReturns : ordinaryCalls;
        if (!budget.Take(Environment.TickCount64, out int retryAfter))
        {
            if (method == "tools/call") ToolResult(id.Value, modern, ErrorBody("busy", retryAfter));
            else Error(id, -31001, "Busy", new { retry_after_ms = retryAfter });
            return;
        }
        switch (method)
        {
            case "initialize" when !modern:
                if (legacyInitialized || protocolAdmissions.Values.Any(request => request.Method == "initialize") ||
                    Get(parameters, "protocolVersion").ValueKind != JsonValueKind.String ||
                    Get(parameters, "capabilities").ValueKind != JsonValueKind.Object ||
                    Get(parameters, "clientInfo").ValueKind != JsonValueKind.Object || Utf8Length(parameters) > metadataLimit)
                { Error(id, -32602, "Invalid initialization"); return; }
                if (!AnnounceClient(id.Value, Get(parameters, "clientInfo"))) return;
                StartProtocol(id.Value, key, method, new { protocolVersion = McpProtocol.Legacy, capabilities = McpProtocol.Capabilities,
                    serverInfo = McpProtocol.ServerInfo });
                break;
            case "ping" when !modern:
                StartProtocol(id.Value, key, method, new { });
                break;
            case "server/discover" when modern:
                StartProtocol(id.Value, key, method, new { resultType = "complete", supportedVersions = McpProtocol.Versions,
                    capabilities = McpProtocol.Capabilities,
                    _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = McpProtocol.ServerInfo },
                    ttlMs = 300000, cacheScope = "public" });
                break;
            case "tools/list":
                if (Get(parameters, "cursor").ValueKind != JsonValueKind.Undefined)
                { Error(id, -32602, "Invalid tool-list cursor"); return; }
                StartProtocol(id.Value, key, method, modern
                    ? new { resultType = "complete", tools = McpProtocol.Tools.Values, ttlMs = 300000, cacheScope = "public" }
                    : (object)new { tools = McpProtocol.Tools.Values });
                break;
            case "tools/call":
                var args = Get(parameters, "arguments");
                if (tool is null || !McpProtocol.Tools.ContainsKey(tool) ||
                    args.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                { Error(id, -32602, "Invalid tool call"); return; }
                if (args.ValueKind == JsonValueKind.Undefined) args = McpProtocol.EmptyArguments;
                {
                    bool ordinary = tool != "tabtty_return_control";
                    if (pending.Count + protocolAdmissions.Count >= 18 || ordinary && OrdinaryPending >= 16)
                    { ToolResult(id.Value, modern, ErrorBody("busy", 50), priority: !ordinary); return; }
                    int internalId = checked(++nextId);
                    // The tool name is from the fixed schema table and the argument
                    // object has already been validated. Preserve its wire encoding.
                    string prefix = string.Create(CultureInfo.InvariantCulture,
                        $"{{\"id\":{internalId},\"name\":\"{tool}\",\"arguments\":");
                    ReadOnlySpan<byte> arguments = JsonMarshal.GetRawUtf8Value(args);
                    int length = prefix.Length + arguments.Length + 1;
                    if (length > (ordinary ? AiFrames.MaxFrame : AiPrivateChannel.MaxControlFrame))
                    { ToolResult(id.Value, modern, ErrorBody("resource_limit"), priority: !ordinary); return; }
                    byte[] frame = new byte[length];
                    Encoding.UTF8.GetBytes(prefix, frame);
                    arguments.CopyTo(frame.AsSpan(prefix.Length));
                    frame[^1] = (byte)'}';
                    // A child JsonElement keeps its entire document alive. Retain only the
                    // bounded ID while waiting for a GUI reply or a writable stdout.
                    var publicId = JsonSerializer.SerializeToElement(id.Value);
                    pending.Add(internalId, new Pending(internalId, publicId, key, modern, ordinary));
                    if (!channel.TrySend(frame, priority: !ordinary))
                    {
                        pending.Remove(internalId);
                        ToolResult(id.Value, modern, ErrorBody("busy", 50), priority: !ordinary);
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
            if (McpProtocol.ClientName(info) is { Length: > 0 } name && !clientNameAnnounced)
            {
                if (channel.TrySend(new { client_name = name })) clientNameAnnounced = true;
            }
            return true;
        }
        catch (JsonException) { Error(id, -32602, "Invalid client information"); return false; }
    }

    private bool clientNameAnnounced;

    private int OrdinaryPending => protocolAdmissions.Count + pending.Values.Count(value => value.Ordinary);

    private void StartProtocol(JsonElement publicId, string key, string method, object result)
    {
        if (OrdinaryPending >= 16 || protocolAdmissions.Count + pending.Count >= 18)
        {
            Error(publicId, -31001, "Busy", new { retry_after_ms = 50 });
            return;
        }
        int id = checked(++nextId);
        if (channel.TrySend(new { id, name = "protocol", arguments = new { } }))
            protocolAdmissions.Add(id, new ProtocolRequest(JsonSerializer.SerializeToElement(publicId), key, method, result));
        else Error(publicId, -31001, "Busy", new { retry_after_ms = 50 });
    }

    private void Cancel(JsonElement id)
    {
        if (!ValidId(id)) return;
        string key = IdKey(id);
        lock (gate)
        {
            Pending? match = pending.Values.FirstOrDefault(item => item.Key == key);
            if (match is null)
            {
                foreach (var admission in protocolAdmissions)
                    if (admission.Value.Key == key)
                    {
                        protocolAdmissions.Remove(admission.Key);
                        break;
                    }
                return;
            }
            pending.Remove(match.Id);
            queuedReplies.Remove(match.Id);
            if (!channel.TrySend(new { cancel = match.Id }, priority: true)) channel.Stop();
        }
    }

    private void ReceivePrivateAsync(AiFrames.PrivateFrame frame)
    {
        JsonElement message = frame.Message;
        lock (gate)
        {
            if (Get(message, "revoked") is { ValueKind: JsonValueKind.String } revoked)
            {
                string idOfShare = revoked.GetString()!;
                foreach (var response in queuedReplies.Where(entry => entry.Value.Session == idOfShare || entry.Value.Listing)
                    .Select(entry => entry.Key).ToArray()) queuedReplies.Remove(response);
                return;
            }
            int id = message.GetProperty("id").GetInt32();
            if (protocolAdmissions.Remove(id, out var admission))
            {
                int retry = message.GetProperty("protocol_retry_after_ms").GetInt32();
                if (retry != 0) Error(admission.PublicId, -31001, "Busy", new { retry_after_ms = retry });
                else
                {
                    if (admission.Method == "initialize") legacyInitialized = true;
                    Respond(admission.PublicId, admission.Result);
                }
                return;
            }
            if (!pending.TryGetValue(id, out var request)) return;
            var session = Get(message, "session");
            queuedReplies[id] = (session.ValueKind == JsonValueKind.String ? session.GetString() : null,
                Get(message, "listing").ValueKind == JsonValueKind.True);
            ToolResult(request.PublicId, request.Modern, message.GetProperty("result"), id, priority: !request.Ordinary);
        }
    }

    private void ToolResult(JsonElement id, bool modern, JsonElement body, int? request = null, bool priority = false)
    {
        object result = AiFrames.ToolResult(body, modern);
        if (AiFrames.Serialize(result).Length > AiFrames.MaxResult)
        {
            ToolResult(id, modern, ErrorBody("resource_limit"), request, priority);
            return;
        }
        Enqueue(AiFrames.Serialize(new { jsonrpc = "2.0", id, result }), request, priority);
    }

    private void Respond(JsonElement id, object result) => Enqueue(AiFrames.Serialize(new { jsonrpc = "2.0", id, result }));

    private void Error(JsonElement? id, int code, string message, object? data = null) =>
        Enqueue(AiFrames.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new { code, message, data } }));

    private void Enqueue(byte[] bytes, int? request = null, bool priority = false)
    {
        if (bytes.Length <= AiFrames.MaxFrame && channel.Payload.TryReserve(bytes.Length, priority))
        {
            if ((priority || responses.Reader.Count < 16) && responses.Writer.TryWrite(new Outgoing(bytes, request))) return;
            channel.Payload.Release(bytes.Length);
        }
        channel.Stop();
    }

    private async Task WriteAsync()
    {
        await foreach (Outgoing response in responses.Reader.ReadAllAsync(channel.Token).ConfigureAwait(false))
        {
            Task? write = null;
            try
            {
                lock (gate)
                {
                    if (response.Request is { } request)
                    {
                        if (!pending.Remove(request, out var original)) continue;
                        if (!queuedReplies.Remove(request))
                        {
                            ToolResult(original.PublicId, original.Modern, ErrorBody("not_shared"), priority: !original.Ordinary);
                            continue;
                        }
                    }
                }
                using var deadline = AiFrames.Deadline(channel.Token);
                write = output.WriteAsync(response.Bytes, deadline.Token).AsTask();
                await write.WaitAsync(deadline.Token).ConfigureAwait(false);
                await output.WriteAsync("\n"u8.ToArray(), deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                await output.FlushAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                if (write is { IsCompleted: false })
                    _ = write.ContinueWith(completed =>
                    {
                        _ = completed.Exception;
                        channel.Payload.Release(response.Bytes.Length);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else channel.Payload.Release(response.Bytes.Length);
            }
        }
    }

    private static bool ValidId(JsonElement id) => id.ValueKind == JsonValueKind.String && id.GetString()!.Length <= 256 ||
        id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _);
    private static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s" + id.GetString() : "n" + id.GetInt64();
    private static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : default;
    private static bool TextEquals(JsonElement value, string text) => value.ValueKind == JsonValueKind.String && value.ValueEquals(text);
    private static int Utf8Length(JsonElement value) => JsonMarshal.GetRawUtf8Value(value).Length;
    private static JsonElement ErrorBody(string code, int? retry = null) => JsonSerializer.SerializeToElement(new
    { status = "error", error = new { code, retry_after_ms = retry } }, AiFrames.JsonOptions);
}
