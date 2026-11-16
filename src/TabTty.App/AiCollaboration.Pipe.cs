using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using TabTty.AiTransport;
using TabTty.Core;

namespace TabTty.App;

internal sealed partial class AiCollaboration
{
    private readonly CancellationTokenSource listenerStop = new();
    internal bool EndpointFailed { get; private set; }

    internal void Listen()
    {
        var token = listenerStop.Token;
        _ = Task.Run(async () =>
        {
            var slots = new SemaphoreSlim(8, 8);
            try
            {
                var endpoint = new AiPipe();
                NamedPipeServerStream waiting = endpoint.Listen(first: true);
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await waiting.WaitForConnectionAsync(token).ConfigureAwait(false);
                        if (!slots.Wait(0))
                        {
                            waiting.Disconnect();
                            continue;
                        }
                        var connected = waiting;
                        try { waiting = endpoint.Listen(first: false); }
                        catch { connected.Dispose(); slots.Release(); throw; }
                        _ = RunPeerAsync(endpoint, connected, slots, token);
                    }
                }
                finally { waiting.Dispose(); }
            }
            catch (Exception error) when (token.IsCancellationRequested &&
                error is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (!app.Dispatcher.HasShutdownStarted)
                    _ = app.Dispatcher.BeginInvoke(() => EndpointFailed = true);
            }
        }, token);
    }

    private async Task RunPeerAsync(AiPipe endpoint, NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken token)
    {
        Counterpart? peer = null;
        var requests = new Dictionary<int, Request>();
        var gate = new object();
        var attachmentMemory = new ResourceQuota(8 * MiB, observationWork, collectOnPressure: false);
        var frameMemory = new ResourceQuota(8 * MiB - 512 * KiB, attachmentMemory, collectOnPressure: false);
        try
        {
            using (pipe)
            using (var process = endpoint.VerifyPeer(pipe, server: false, "TabTty.McpBridge.exe", out string location))
            using (var channel = new AiPrivateChannel(pipe, process))
            {
                using (var handshake = AiFrames.Deadline(token, 5))
                    await AiPrivateChannel.HandshakeAsync(pipe, server: true, handshake.Token).ConfigureAwait(false);
                peer = await app.Dispatcher.InvokeAsync(() => Attach(location), DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
                if (peer is null) return;
                Counterpart attached = peer;
                await app.Dispatcher.InvokeAsync(() => attached.SharingEnded = id =>
                {
                    if (!channel.TrySend(new { revoked = id }, priority: true)) channel.Stop();
                }).Task.ConfigureAwait(false);
                await channel.RunAsync(ReceiveAsync, token).ConfigureAwait(false);

                async Task ReceiveAsync(JsonElement message)
                {
                    if (message.TryGetProperty("cancel", out var cancellation))
                    {
                        Request? active;
                        lock (gate) requests.TryGetValue(cancellation.GetInt32(), out active);
                        active?.Cancel();
                        return;
                    }
                    if (message.TryGetProperty("client_name", out var client))
                    {
                        string? name = client.GetString();
                        if (name is not null && name.EnumerateRunes().Count() <= 128)
                            await app.Dispatcher.InvokeAsync(() => attached.Name = CleanDisplay(name)).Task.ConfigureAwait(false);
                        return;
                    }
                    int id = message.GetProperty("id").GetInt32();
                    string nameOfTool = message.GetProperty("name").GetString() ?? "";
                    JsonElement arguments = message.GetProperty("arguments");
                    if (nameOfTool == "protocol")
                    {
                        int retry = await app.Dispatcher.InvokeAsync(() => TryTakeCall(attached, out int delay) ? 0 : delay,
                            DispatcherPriority.Normal, channel.Token).Task.ConfigureAwait(false);
                        if (!channel.TrySend(new { id, protocol_retry_after_ms = retry })) channel.Stop();
                        return;
                    }
                    Request? request = null;
                    lock (gate)
                    {
                        if (requests.ContainsKey(id)) throw new InvalidDataException();
                        int ordinary = requests.Values.Count(value => value.Ordinary);
                        bool isOrdinary = nameOfTool != "tabtty_return_control";
                        if (requests.Count < 18 && (!isOrdinary || ordinary < 16))
                        {
                            try
                            {
                                long weight = message.GetRawText().Length * 2L + 256;
                                if (isOrdinary || weight <= 4 * KiB)
                                {
                                    request = new Request(isOrdinary, channel.Token,
                                        (isOrdinary ? frameMemory : attachmentMemory).Reserve(weight));
                                    requests.Add(id, request);
                                }
                            }
                            catch (ResourceLimitException) { }
                        }
                    }
                    if (request is null)
                    {
                        if (!channel.TrySend(new { id, result = Error("busy", new { retry_after_ms = 50 }) }))
                            channel.Stop();
                        return;
                    }
                    _ = ServeAsync(id, nameOfTool, arguments, request);
                }

                async Task ServeAsync(int id, string name, JsonElement arguments, Request request)
                {
                    try
                    {
                        Reply reply = await app.Dispatcher.InvokeAsync(() => CallAsync(attached, name, arguments, request.Token),
                            DispatcherPriority.Normal, request.Token).Task.Unwrap().ConfigureAwait(false);
                        using var prepared = reply.History;
                        byte[] frame = AiFrames.Serialize(new { id, result = reply.Body, session = reply.Share?.Id,
                            listing = reply.SharingRevision.HasValue ? true : (bool?)null });
                        bool fits = frame.Length <= AiFrames.MaxFrame;
                        if (fits && reply.History is not null)
                        {
                            var body = JsonSerializer.SerializeToElement(reply.Body, AiFrames.JsonOptions);
                            fits = AiFrames.Serialize(AiFrames.ToolResult(body, modern: true)).Length <= AiFrames.MaxResult;
                        }
                        byte[] limited = AiFrames.Serialize(new { id, result = Error("resource_limit") });
                        bool stopChannel = false;
                        ResourceReservation? reservedFrame = null;
                        if (fits)
                        {
                            try { reservedFrame = (request.Ordinary ? frameMemory : attachmentMemory).Reserve(frame.Length); }
                            catch (ResourceLimitException) { }
                        }
                        try
                        {
                            await PublishReplyAsync(attached, reply, (body, accepted) =>
                            {
                                // The enqueue is ordered with GUI revocation and is the disclosure commit.
                                bool queued;
                                bool disclosed = false;
                                if (accepted)
                                {
                                    if (reservedFrame is { } memory)
                                    {
                                        queued = channel.TrySend(frame, priority: !request.Ordinary, memory);
                                        if (queued) reservedFrame = null;
                                        disclosed = queued;
                                    }
                                    else queued = channel.TrySend(limited, priority: true);
                                }
                                else queued = channel.TrySend(new { id, result = body }, priority: true);
                                if (!queued) stopChannel = true;
                                return disclosed;
                            }, request.Token).ConfigureAwait(false);
                        }
                        finally { reservedFrame?.Dispose(); }
                        if (stopChannel) channel.Stop();
                    }
                    catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonException)
                    {
                        if (!request.Token.IsCancellationRequested) channel.Stop();
                    }
                    finally
                    {
                        lock (gate) requests.Remove(id);
                        request.Dispose();
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or
            InvalidOperationException or JsonException or KeyNotFoundException or ArgumentException) { }
        finally
        {
            if (peer is not null && !app.Dispatcher.HasShutdownStarted)
                await app.Dispatcher.InvokeAsync(() =>
                {
                    peer.SharingEnded = null;
                    Detach(peer);
                }).Task.ConfigureAwait(false);
            slots.Release();
        }
    }

    private sealed class Request(bool ordinary, CancellationToken lifetime, ResourceReservation memory) : IDisposable
    {
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal bool Ordinary { get; } = ordinary;
        internal CancellationToken Token => stop.Token;
        internal void Cancel()
        {
            lock (stop)
            {
                try { _ = stop.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (stop) stop.Dispose();
            memory.Dispose();
        }
    }
}
