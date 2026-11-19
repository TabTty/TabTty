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
        int held = 0;
        var stops = new List<double>();
        var disconnects = new List<double>();
        host.Show();
        try
        {
            for (; held < logSlots; held++) await ApplicationResources.LogIo.WaitAsync(token);
            for (int i = 0; i < sessions; i++)
            {
                string label = $"Overload {i:D2}";
                string profile = Path.Combine(directory, label + ".ttty");
                await ConnectionProfile.CreateTelnet(label, "127.0.0.1", port, recordRaw: true).SaveNewAsync(profile, token);
                var tab = new TerminalTab(profile, null, (120, 40));
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
                if (!(await File.ReadAllBytesAsync(log, token)).AsSpan().SequenceEqual(recorded))
                    throw new InvalidDataException("An accepted recording lost bytes under overload.");

            stops.Sort();
            disconnects.Sort();
            double stopP95Ms = P95(stops), disconnectP95Ms = P95(disconnects);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                sessions, heldLogSlots = logSlots, payloadHeld,
                stopP95Ms, stopMaxMs = stops[^1], disconnectP95Ms, disconnectMaxMs = disconnects[^1],
                matchedRecordings = logs.Length,
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            if (stopP95Ms > 250 || disconnectP95Ms > 250)
                throw new InvalidOperationException("The overload acceptance latency exceeded 250 ms p95.");
        }
        finally
        {
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

        static double P95(List<double> sorted) => sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
    }
}
