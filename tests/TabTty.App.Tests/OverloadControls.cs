using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckOverloadControlsAsync(App app, string directory, string report)
    {
        const int sessions = 20;
        const int logSlots = 128;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        var fixtures = new List<(TerminalTab Tab, TerminalSession Session, TcpClient Peer)>();
        byte[] recorded = "overload recording\r\n\u001b[5n"u8.ToArray();
        byte[] wrapped = System.Text.Encoding.ASCII.GetBytes(new string('x', 80 * 1024) + "\u001b[5n");
        const string highlightLabel = "Overload 19";
        var highlights = new HighlightSet(Guid.NewGuid(), "Overload highlights", true,
            [new TerminalHighlightRule(Guid.NewGuid(), "Keyword", "keyword", true, false, new(0xFFAA00, null, null, null))]);
        int held = 0;
        var stops = new List<double>();
        var disconnects = new List<double>();
        var takeovers = new List<double>();
        AiCollaboration.Counterpart? counterpart = null;
        AiCollaboration.Counterpart? other = null;
        host.Show();
        try
        {
            for (; held < logSlots; held++) await ApplicationResources.LogIo.WaitAsync(token);
            for (int i = 0; i < sessions; i++)
            {
                string label = $"Overload {i:D2}";
                string profile = Path.Combine(directory, label + ".ttty");
                await ConnectionProfile.CreateTelnet(label, "127.0.0.1", port, recordRaw: true).SaveNewAsync(profile, token);
                var tab = new TerminalTab(profile, null, (120, 40), label == highlightLabel ? highlights : null);
                host.AddTerminalTab(tab);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.Connected += _ => ready.TrySetResult();
                tab.Start();
                await ready.Task.WaitAsync(token);
                var peer = await listener.AcceptTcpClientAsync(token);
                fixtures.Add((tab, tab.InputSession!, peer));
                await peer.GetStream().ReadExactlyAsync(new byte[15], token);
                await peer.GetStream().WriteAsync(recorded, token);
                byte[] reply = new byte[4];
                await peer.GetStream().ReadExactlyAsync(reply, token);
                if (!reply.AsSpan().SequenceEqual("\u001b[0n"u8))
                    throw new InvalidDataException("Terminal receive did not stay live under recording I/O pressure.");
            }
            long payloadHeld = ApplicationResources.LogPayload.Used;
            if (ApplicationResources.LogIo.CurrentCount != 0 || payloadHeld < recorded.Length * sessions)
                throw new InvalidOperationException("The recording scheduler and the held log bytes were not both observed.");

            var highlighted = fixtures[^1];
            var sharedWire = highlighted.Peer.GetStream();
            await sharedWire.WriteAsync(wrapped, token);
            byte[] wrappedReply = new byte[4];
            await sharedWire.ReadExactlyAsync(wrappedReply, token);
            CheckMcpContract(wrappedReply.AsSpan().SequenceEqual("\u001b[0n"u8), "The wrapped line did not reach the terminal.");
            counterpart = app.Ai.Attach("overload fixture", @"C:\Fixtures\Host.exe", 301)!;
            CheckMcpContract(app.Ai.Share(highlighted.Tab, counterpart) is null, "The fixture could not share the highlighted terminal.");
            other = app.Ai.Attach("independent fixture", @"C:\Fixtures\Other.exe", 302)!;
            CheckMcpContract(app.Ai.Share(fixtures[0].Tab, other) is null, "The fixture could not share the independent terminal.");
            var otherScreen = await ReadScreen(other, new { session = fixtures[0].Tab.AiSharing!.Id });
            CheckMcpContract(otherScreen.GetProperty("capture_ordinal").GetInt64() == 1,
                "The independent terminal did not start at its own capture ordinal.");

            string shared = highlighted.Tab.AiSharing!.Id;
            var captures = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                ReadScreen(counterpart, new { session = shared, include_highlights = false })));
            var admitted = captures.Where(result => result.GetProperty("status").GetString() == "ok").ToArray();
            CheckMcpContract(admitted.Length == 2 && captures.Count(result => result.TryGetProperty("error", out var error) &&
                error.GetProperty("code").GetString() == "resource_limit") == 1, "The third screen capture bypassed its allowance.");
            CheckMcpContract(admitted.Select(result => result.GetProperty("capture_ordinal").GetInt64()).Order().SequenceEqual([1L, 2L]),
                "An independent capture affected the ordinals of the highlighted share.");
            foreach (var capture in admitted)
            {
                string? cursor = capture.GetProperty("next_cursor").GetString();
                CheckMcpContract(cursor is not null, "The fixture did not produce a paged screen capture.");
                while (cursor is not null)
                {
                    var page = await ReadScreen(counterpart, new { session = shared, cursor });
                    cursor = page.GetProperty("next_cursor").GetString();
                }
            }
            var read = JsonSerializer.SerializeToElement(new { session = highlighted.Tab.AiSharing!.Id,
                include_highlights = true, region = new { row = 1, column = 1, rows = 16, columns = 40 } });
            for (int i = 0; i < sessions; i++)
            {
                CheckMcpContract(app.Ai.Grant(highlighted.Tab) is null, "The fixture could not grant input.");
                Task<AiCollaboration.Reply> screen = app.Ai.CallAsync(counterpart, "tabtty_read_state", read, token);
                await Dispatcher.Yield(DispatcherPriority.Background);
                takeovers.Add(await AcceptAsync(() => app.Ai.EndControl(highlighted.Tab)));
                var result = JsonSerializer.SerializeToElement((await screen).Body);
                CheckMcpContract(result.GetProperty("status").GetString() == "ok" && result.TryGetProperty("highlight_status", out _) &&
                    !highlighted.Tab.IsAiControlling, "Reading highlights broke takeover or screen publication.");
                while (highlighted.Tab.IsAiTakingControl) await Task.Delay(5, token);
            }
            app.Ai.EndSharing(highlighted.Tab);

            app.Broadcast.Start(host);
            app.Broadcast.SetAll(host, false);
            app.Broadcast.Select(host, fixtures[1].Tab, true);
            var target = fixtures[1].Peer.GetStream();
            for (int i = 0; i < sessions; i++)
            {
                Task<BroadcastInput.PasteOutcome> paste = app.Broadcast.PasteAsync(host, fixtures[0].Tab,
                    TerminalPaste.Create("ab"), (_, _) => throw new InvalidOperationException("Unexpected paste review under overload."),
                    new(10_000, 0, false));
                byte[] first = new byte[1];
                await target.ReadExactlyAsync(first, token);
                if (first[0] != (byte)'a') throw new InvalidDataException("The previous stopped tail reached the peer.");
                stops.Add(await AcceptAsync(() => app.Broadcast.StopPaste(host)));
                if (await paste.WaitAsync(token) != BroadcastInput.PasteOutcome.Stopped)
                    throw new InvalidOperationException("Cancel did not settle the pending paste.");
            }
            app.Broadcast.Stop(host);
            if (target.DataAvailable) throw new InvalidDataException("A stopped paste suffix reached the peer.");
            foreach (var fixture in fixtures)
            {
                disconnects.Add(await AcceptAsync(fixture.Tab.Disconnect));
                if (fixture.Session.AcceptsInput || !fixture.Session.RecordingReleasePending)
                    throw new InvalidOperationException("Disconnect did not refuse new input while the original recording remained held.");
            }
            ApplicationResources.LogIo.Release(held);
            held = 0;
            await Task.WhenAll(fixtures.Select(fixture => fixture.Tab.Completion)).WaitAsync(token);
            string[] logs = Directory.GetFiles(directory, "*.raw.tttylog", SearchOption.AllDirectories);
            if (logs.Length != sessions) throw new InvalidDataException("The overload fixture did not create one recording per session.");
            foreach (string log in logs)
                if (!(await File.ReadAllBytesAsync(log, token)).AsSpan().SequenceEqual(
                    Path.GetFileName(log).StartsWith(highlightLabel + "-", StringComparison.Ordinal) ? [.. recorded, .. wrapped] : recorded))
                    throw new InvalidDataException("An accepted recording lost bytes under overload.");

            stops.Sort();
            disconnects.Sort();
            takeovers.Sort();
            double stopP95Ms = P95(stops), disconnectP95Ms = P95(disconnects);
            double takeoverP95Ms = P95(takeovers);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                sessions, heldLogSlots = logSlots, payloadHeld,
                stopP95Ms, stopMaxMs = stops[^1], disconnectP95Ms, disconnectMaxMs = disconnects[^1],
                takeoverP95Ms, takeoverMaxMs = takeovers[^1], matchedRecordings = logs.Length,
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            if (stopP95Ms > 250 || disconnectP95Ms > 250 || takeoverP95Ms > 250)
                throw new InvalidOperationException("The overload acceptance latency exceeded 250 ms p95.");
        }
        finally
        {
            if (counterpart is not null) app.Ai.Detach(counterpart);
            if (other is not null) app.Ai.Detach(other);
            if (held != 0) ApplicationResources.LogIo.Release(held);
            app.Broadcast.Stop(host);
            foreach (var fixture in fixtures)
            {
                fixture.Peer.Dispose();
                fixture.Tab.Disconnect();
            }
            await Task.WhenAll(fixtures.Select(fixture => fixture.Tab.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            if (!host.IsClosing) host.Close();
        }

        Task<double> AcceptAsync(Action operation) => Task.Run(async () =>
        {
            long submitted = Stopwatch.GetTimestamp();
            await app.Dispatcher.InvokeAsync(operation, DispatcherPriority.Input, token).Task.ConfigureAwait(false);
            return Stopwatch.GetElapsedTime(submitted).TotalMilliseconds;
        }, token);

        async Task<JsonElement> ReadScreen(AiCollaboration.Counterpart recipient, object arguments)
        {
            var reply = await app.Ai.CallAsync(recipient, "tabtty_read_state", JsonSerializer.SerializeToElement(arguments), token);
            return JsonSerializer.SerializeToElement(reply.Body);
        }

        static double P95(List<double> sorted) => sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
    }
}
