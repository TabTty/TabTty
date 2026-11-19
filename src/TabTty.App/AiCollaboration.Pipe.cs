using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
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
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var endpoint = new AiPipe();
                var listeners = endpoint.OpenListeners(8);
                await Task.WhenAll(listeners.Select(ServeConnectionsAsync)).ConfigureAwait(false);

                async Task ServeConnectionsAsync(NamedPipeServerStream listener)
                {
                    using (listener)
                    {
                        try
                        {
                            while (await AiPipe.AcceptAsync(listener, stop.Token).ConfigureAwait(false))
                            {
                                try { await RunPeerAsync(endpoint, listener, stop.Token).ConfigureAwait(false); }
                                finally { listener.Disconnect(); }
                            }
                        }
                        catch
                        {
                            await stop.CancelAsync().ConfigureAwait(false);
                            throw;
                        }
                    }
                }
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

    private async Task RunPeerAsync(AiPipe endpoint, NamedPipeServerStream pipe, CancellationToken token)
    {
        Counterpart? peer = null;
        var requests = new Dictionary<int, Request>();
        var gate = new object();
        var frameMemory = new ResourceQuota(8 * MiB - 512 * KiB, observationWork, collectOnPressure: false);
        var controlMemory = new ResourceQuota(512 * KiB, TerminalHistory.ControlWorkQuota, collectOnPressure: false);
        bool nameReceived = false;
        try
        {
            using (var process = endpoint.VerifyPeer(pipe, server: false, "TabTty.McpBridge.exe", out string location))
            using (var launcher = endpoint.OpenLauncher(process, out string launcherPath, out uint launcherId))
            using (var channel = new AiPrivateChannel(pipe, process, launcher, keepPipeOpen: true))
            {
                using (var handshake = AiFrames.Deadline(token, 5))
                    await AiPrivateChannel.HandshakeAsync(pipe, server: true, handshake.Token).ConfigureAwait(false);
                peer = await app.Dispatcher.InvokeAsync(() => Attach(location, launcherPath, launcherId),
                    DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
                if (peer is null) return;
                Counterpart attached = peer;
                await app.Dispatcher.InvokeAsync(() => attached.SharingEnded = id =>
                {
                    if (!channel.TrySend(new { revoked = id }, priority: true)) channel.Stop();
                }).Task.ConfigureAwait(false);
                await channel.RunAsync(Receive, token).ConfigureAwait(false);

                void Receive(AiFrames.PrivateFrame frame)
                {
                    JsonElement message = frame.Message;
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
                        if (!nameReceived && name is not null && name.EnumerateRunes().Count() <= 128)
                        {
                            nameReceived = true;
                            _ = app.Dispatcher.InvokeAsync(() => attached.Name ??= PermissionDisplay.SingleLine(name),
                                DispatcherPriority.Normal, channel.Token);
                        }
                        return;
                    }
                    int id = message.GetProperty("id").GetInt32();
                    string nameOfTool = message.GetProperty("name").GetString() ?? "";
                    JsonElement arguments = message.GetProperty("arguments");
                    Request? request = null;
                    int retryAfter = 50;
                    lock (gate)
                    {
                        if (requests.ContainsKey(id)) throw new InvalidDataException();
                        int ordinary = requests.Values.Count(value => value.Ordinary);
                        bool isOrdinary = nameOfTool != "tabtty_return_control";
                        if (requests.Count < 18 && (isOrdinary ? ordinary < 16 : attached.TryTakeReturn(Now, out retryAfter)))
                        {
                            try
                            {
                                if (isOrdinary || frame.Length <= AiPrivateChannel.MaxControlFrame)
                                {
                                    request = new Request(isOrdinary, channel.Token,
                                        (isOrdinary ? frameMemory : controlMemory).Reserve(frame.RetainedBytes));
                                    requests.Add(id, request);
                                }
                            }
                            catch (ResourceLimitException) { }
                        }
                    }
                    if (request is null)
                    {
                        // Return overflow stays on the pipe reader, ahead of any GUI work.
                        int retry = retryAfter > 0 ? retryAfter : 50;
                        bool accepted = nameOfTool == "protocol"
                            ? channel.TrySend(new { id, protocol_retry_after_ms = retry })
                            : channel.TrySend(new { id, result = Error("busy", new { retry_after_ms = retry }) },
                                priority: nameOfTool == "tabtty_return_control");
                        if (!accepted) channel.Stop();
                        return;
                    }
                    _ = ServeAsync(id, nameOfTool, arguments, request);
                }

                async Task ServeAsync(int id, string name, JsonElement arguments, Request request)
                {
                    try
                    {
                        if (name == "protocol")
                        {
                            int retry = await app.Dispatcher.InvokeAsync(() => TryTakeCall(attached, out int delay) ? 0 : delay,
                                DispatcherPriority.Background, request.Token).Task.ConfigureAwait(false);
                            if (!channel.TrySend(new { id, protocol_retry_after_ms = retry })) channel.Stop();
                            return;
                        }
                        Reply reply = await app.Dispatcher.InvokeAsync(() => CallAdmittedAsync(attached, name, arguments, request.Token),
                            request.Ordinary ? DispatcherPriority.Background : DispatcherPriority.Normal,
                            request.Token).Task.Unwrap().ConfigureAwait(false);
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
                            try { reservedFrame = (request.Ordinary ? frameMemory : controlMemory).Reserve(frame.Length); }
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
                    catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonException or InvalidDataException)
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
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or
            OperationCanceledException or InvalidOperationException or JsonException or KeyNotFoundException or
            ArgumentException or FormatException or UnauthorizedAccessException) { }
        finally
        {
            if (peer is not null && !app.Dispatcher.HasShutdownStarted)
                await app.Dispatcher.InvokeAsync(() =>
                {
                    peer.SharingEnded = null;
                    Detach(peer);
                }).Task.ConfigureAwait(false);
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
