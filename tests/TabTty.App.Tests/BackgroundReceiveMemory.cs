using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task BackgroundReceiveMemory(string directory, int receivers, string report)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var sampling = new CancellationTokenSource();
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var controls = new TabControl();
        var placeholder = new TabItem { Header = "Other tab", Content = new TextBlock() };
        controls.Items.Add(placeholder);
        var window = new Window { Content = controls, Width = 1250, Height = 830, ShowInTaskbar = false };
        var fixtures = new List<(TerminalTab Tab, TabItem Item, TcpClient Peer, string Directory, TerminalSurface Surface)>();
        var phases = new[] { "visible_seed", "unselected_receive", "minimized_receive", "eof_and_drain", "restoration" };
        var peaks = new (long Private, long WorkingSet)[phases.Length];
        int phase = 0;
        bool completed = false;
        var monitor = Task.Run(async () =>
        {
            using var process = Process.GetCurrentProcess();
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                int index = Volatile.Read(ref phase);
                var peak = peaks[index];
                peaks[index] = (Math.Max(peak.Private, process.PrivateMemorySize64),
                    Math.Max(peak.WorkingSet, process.WorkingSet64));
                await Task.Delay(20);
            }
        });
        byte[] seed = Rows("seed", 5200);
        byte[] background = Rows("background", 12000);
        byte[] hidden = Rows("minimized", 12000);
        byte[] tail = "retained final line\r\n"u8.ToArray();
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] bytes in new[] { seed, background, hidden, tail }) expected.AppendData(bytes);
        string digest = Convert.ToHexString(expected.GetHashAndReset());
        string? failure = null;
        try
        {
            window.Show();
            for (int index = 0; index < receivers; index++)
            {
                string location = Path.Combine(directory, "receive-" + index);
                Directory.CreateDirectory(location);
                string profile = Path.Combine(location, "memory.ttty");
                await ConnectionProfile.CreateTelnet($"Memory {index}", "127.0.0.1", port,
                    recordRaw: true, recordText: true, recordTerminal: true).SaveNewAsync(profile, token);
                var tab = new TerminalTab(profile, null, (120, 40));
                var item = new TabItem { Header = $"Session {index}", Content = tab };
                controls.Items.Add(item);
                controls.SelectedItem = item;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.Connected += _ => ready.TrySetResult();
                tab.Start();
                var peer = await listener.AcceptTcpClientAsync(token);
                var surface = FindControls<TerminalSurface>(tab).Single();
                fixtures.Add((tab, item, peer, location, surface));
                await ready.Task.WaitAsync(token);
                await peer.GetStream().ReadExactlyAsync(new byte[15], token);
                await Send(peer, seed, token);
                while (surface.Snapshot?.Count != 5000 || !surface.IsCurrentSnapshotRendered)
                    await Task.Delay(50, token);
            }

            controls.SelectedItem = placeholder;
            Volatile.Write(ref phase, 1);
            await Task.WhenAll(fixtures.Select(fixture => Send(fixture.Peer, background, token)));
            window.WindowState = WindowState.Minimized;
            Volatile.Write(ref phase, 2);
            await Task.WhenAll(fixtures.Select(fixture => Send(fixture.Peer, hidden, token)));
            Volatile.Write(ref phase, 3);
            foreach (var fixture in fixtures)
            {
                await Send(fixture.Peer, tail, token);
                fixture.Peer.Client.Shutdown(SocketShutdown.Send);
            }
            await Task.WhenAll(fixtures.Select(fixture => fixture.Tab.Completion)).WaitAsync(token);
            Volatile.Write(ref phase, 4);
            window.WindowState = WindowState.Normal;
            foreach (var fixture in fixtures)
            {
                controls.SelectedItem = fixture.Item;
                while (fixture.Surface.Snapshot is not { Count: 5000 } snapshot ||
                    !snapshot.Contains("retained final line") || !fixture.Surface.IsCurrentSnapshotRendered)
                    await Task.Delay(50, token);
                foreach (string pattern in new[] { "*.raw.tttylog", "*.log", "*.term.tttylog" })
                {
                    string file = Directory.EnumerateFiles(fixture.Directory, pattern, SearchOption.AllDirectories).Single();
                    await using var stream = File.OpenRead(file);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != digest)
                        throw new InvalidDataException("A recording differed from the fixture payload.");
                }
            }
            completed = true;
        }
        catch (Exception error)
        {
            failure = error.Message;
            throw;
        }
        finally
        {
            sampling.Cancel();
            await monitor;
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                receivers, completed, failure, digest,
                bytesPerReceiver = seed.Length + background.Length + hidden.Length + tail.Length,
                peakPrivate = peaks.Max(peak => peak.Private),
                peakWorkingSet = peaks.Max(peak => peak.WorkingSet),
                phases = phases.Select((name, index) => new { name, peakPrivate = peaks[index].Private, peakWorkingSet = peaks[index].WorkingSet }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var fixture in fixtures)
            {
                fixture.Peer.Dispose();
                fixture.Tab.Disconnect();
                controls.Items.Remove(fixture.Item);
                await fixture.Tab.ReleaseRemovedOutputAsync();
            }
            window.Close();
        }

        static byte[] Rows(string label, int count)
        {
            var text = new StringBuilder();
            string fill = new('X', 96);
            for (int line = 0; line < count; line++) text.Append($"{label} {line:D5} {fill}\r\n");
            return Encoding.ASCII.GetBytes(text.ToString());
        }

        static async Task Send(TcpClient peer, byte[] bytes, CancellationToken token)
        {
            var stream = peer.GetStream();
            for (int offset = 0; offset < bytes.Length; offset += 4096)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(4096, bytes.Length - offset)), token).ConfigureAwait(false);
                await Task.Delay(1, token).ConfigureAwait(false);
            }
        }
    }
}
