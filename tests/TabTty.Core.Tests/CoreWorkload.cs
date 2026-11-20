using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabTty.Core;

internal static class CoreWorkload
{
    public static byte[] Cycle()
    {
        var text = new StringBuilder();
        string filler = new('X', 96);
        for (int index = 0; index < 4096; index++)
        {
            string digits = index.ToString("D4", CultureInfo.InvariantCulture);
            text.Append((index % 6) switch
            {
                0 => $"INFO status index={digits} {filler}\n",
                1 => $"WARN count={digits} {filler}\r\n",
                2 => $"ERROR code={digits} {filler}\n",
                3 => $"status index={digits} 接続状態を確認しています {filler}\n",
                4 => $"\u001b[31mERROR\u001b[0m status index={digits} {filler}\n",
                _ => $"progress count={digits} 10%\rprogress count={digits} 100% {filler}\r\n",
            });
        }
        byte[] cycle = Encoding.UTF8.GetBytes(text.ToString());
        if (cycle.Length != 529731 || Convert.ToHexStringLower(SHA256.HashData(cycle)) !=
            "60de481a2dec1f4446b0773e64a5c18e43311defd4d86cd48702779a50581ec9")
            throw new InvalidOperationException("The C# workload differs from the independently generated cycle.");
        return cycle;
    }

    public static async Task RunAsync(int seconds, string output)
    {
        byte[] cycle = Cycle();
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, "cycle.bin"), cycle);
        foreach (bool busy in new[] { false, true }) await RunModeAsync(seconds, output, cycle, busy);
    }

    private static async Task RunModeAsync(int seconds, string output, byte[] cycle, bool busy)
    {
        const int warmup = 20;
        const int rate = 1048576;
        string mode = busy ? "one-busy" : "equal";
        string directory = Path.Combine(output, mode);
        Directory.CreateDirectory(directory);
        var sessions = new List<TerminalSession>();
        var wires = new List<WorkloadWire>();
        var readers = new List<Task>();
        long started = 0;
        var latency = new List<double>();
        var completionLatency = new List<double>();
        var inputSamples = new List<object>();
        using var process = Process.GetCurrentProcess();
        long peakWorking = 0, peakPrivate = 0, peakState = 0, peakLogs = 0;
        long measuredBytes = 0;
        int frames = 0;
        TimeSpan cpuBefore = default;
        try
        {
            for (int index = 0; index < 20; index++)
            {
                string path = Path.Combine(directory, $"session-{index:D2}.ttty");
                var profile = ConnectionProfile.CreateSerial($"Stress {index:D2}", new SerialSettings("COM1"),
                    recordRaw: true, recordText: true, recordTerminal: true);
                await profile.SaveNewAsync(path);
                var recording = SessionRecording.Open(path, profile);
                var wire = new WorkloadWire(cycle, busy ? index == 0 ? rate : 0 : rate / 20d, warmup + seconds);
                var session = new TerminalSession(wire, profile, recording, 5000);
                await session.ResizeAsync(120, 40);
                sessions.Add(session);
                wires.Add(wire);
            }
            started = Stopwatch.GetTimestamp();
            foreach (WorkloadWire wire in wires) wire.Started = started;
            foreach (TerminalSession session in sessions)
                readers.Add(Task.Run(async () => { while (await session.ProcessOutputAsync()) { } }));
            string[] patterns = ["INFO", "WARN", "ERROR", "code=[0-9]+", "count=[0-9]+", "[0-9]{4}",
                "status", "progress", "接続状態", "X{16}"];
            var rules = new TerminalHighlightRules(patterns.Select(pattern => new TerminalHighlightRule(
                Guid.NewGuid(), pattern, pattern, true, false, new TerminalHighlightStyle(0xFFFF00, null, null, null))).ToArray());
            var suspended = new HashSet<Guid>();
            double nextReport = 10;
            double nextHighlight = 0;
            bool measuring = false;
            Task inputTask = SampleInputAsync();
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < warmup + seconds)
            {
                double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                if (!measuring && elapsed >= warmup)
                {
                    measuring = true;
                    measuredBytes = wires.Sum(wire => wire.Delivered);
                    cpuBefore = process.TotalProcessorTime;
                }
                for (int index = 0; index < 2; index++)
                {
                    var snapshot = (TerminalSnapshot)await sessions[index].GetOutputAsync();
                    if (elapsed >= nextHighlight)
                    {
                        int first = Math.Max(0, snapshot.Count - 40);
                        string visible = string.Join('\n', snapshot.Skip(first));
                        _ = rules.Match(visible, suspended);
                    }
                    if (measuring) frames++;
                }
                if (elapsed >= nextHighlight) nextHighlight = elapsed + 2;
                process.Refresh();
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakState = Math.Max(peakState, ApplicationResources.TerminalState.Used);
                peakLogs = Math.Max(peakLogs, ApplicationResources.LogPayload.Used);
                if (elapsed >= nextReport)
                {
                    Console.WriteLine($"CORE-STRESS {mode} {elapsed:F0}s payload={wires.Sum(wire => wire.Delivered)} state={peakState} logs={peakLogs}");
                    nextReport += 10;
                }
                await Task.Delay(17);
            }
            await inputTask;
            await Task.WhenAll(readers);
            foreach (TerminalSession session in sessions)
            {
                await session.DisposeAsync();
                if (session.RecordingError is { } error) throw new InvalidOperationException(error);
            }
            string[] raw = Directory.GetFiles(Path.Combine(directory, "Logs"), "*.raw.tttylog");
            for (int index = 0; index < wires.Count; index++)
            {
                string file = raw.Single(path => Path.GetFileName(path).StartsWith($"Stress {index:D2}_", StringComparison.Ordinal));
                using var stream = File.OpenRead(file);
                if (stream.Length != wires[index].Delivered) throw new InvalidOperationException("Raw stress recording lost bytes.");
                byte[] actual = SHA256.HashData(stream);
                using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (long remaining = stream.Length; remaining > 0; remaining -= Math.Min(remaining, cycle.Length))
                    expected.AppendData(cycle.AsSpan(0, (int)Math.Min(remaining, cycle.Length)));
                if (!actual.AsSpan().SequenceEqual(expected.GetHashAndReset()))
                    throw new InvalidOperationException("Raw stress recording changed the generated byte stream.");
            }
            latency.Sort();
            completionLatency.Sort();
            var report = new
            {
                kind = "Headless core diagnostic with synthetic transport; separate from SSH/WPF acceptance",
                mode, sessions = 20, columns = 120, rows = 40, retainedRows = 5000,
                warmupSeconds = warmup, measurementSeconds = seconds,
                payloadBytes = wires.Sum(wire => wire.Delivered),
                measuredBytesPerSecond = (wires.Sum(wire => wire.Delivered) - measuredBytes) / (double)seconds,
                cycleSha256 = Convert.ToHexStringLower(SHA256.HashData(cycle)),
                frames, interactions = latency.Count,
                inputP95Milliseconds = latency[(int)Math.Ceiling(latency.Count * 0.95) - 1],
                inputMaxMilliseconds = latency[^1], peakWorkingSetBytes = peakWorking, peakPrivateBytes = peakPrivate,
                sendCompletionP95Milliseconds = completionLatency[(int)Math.Ceiling(completionLatency.Count * 0.95) - 1],
                inputSamples,
                peakChargedTerminalBytes = peakState, peakChargedLogBytes = peakLogs,
                cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                logicalProcessors = Environment.ProcessorCount,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                rawHashesVerified = raw.Length,
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));

            async Task SampleInputAsync()
            {
                for (int due = warmup; due < warmup + seconds; due += 3)
                {
                    double wait;
                    while ((wait = due - Stopwatch.GetElapsedTime(started).TotalSeconds) > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait * 1000)));
                    long input = Stopwatch.GetTimestamp();
                    TimeSpan paused = GC.GetTotalPauseDuration();
                    int collections = GC.CollectionCount(2);
                    Task send = sessions[latency.Count % sessions.Count].SendTextAsync("x");
                    double accepted = (Stopwatch.GetElapsedTime(started).TotalSeconds - due) * 1000;
                    await send;
                    double completed = (Stopwatch.GetElapsedTime(started).TotalSeconds - due) * 1000;
                    latency.Add(accepted);
                    completionLatency.Add(completed);
                    inputSamples.Add(new { dueSeconds = due, acceptanceMilliseconds = accepted,
                        completionMilliseconds = completed, sendMilliseconds = Stopwatch.GetElapsedTime(input).TotalMilliseconds,
                        pauseMilliseconds = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds,
                        gen2Collections = GC.CollectionCount(2) - collections });
                }
            }
        }
        finally { foreach (TerminalSession session in sessions) await session.DisposeAsync(); }
    }

    private sealed class WorkloadWire(byte[] cycle, double rate, int duration) : ISessionWire
    {
        private static readonly int[] Splits = [1, 7, 64, 4096];
        private int split;
        public long Started { get; set; }
        public long Delivered { get; private set; }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (rate == 0)
            {
                double left = duration - Stopwatch.GetElapsedTime(Started).TotalSeconds;
                if (left > 0) await Task.Delay(TimeSpan.FromSeconds(left), cancellationToken);
                return 0;
            }
            double due = Delivered / rate - Stopwatch.GetElapsedTime(Started).TotalSeconds;
            if (due > 0) await Task.Delay(TimeSpan.FromSeconds(due), cancellationToken);
            if (Stopwatch.GetElapsedTime(Started).TotalSeconds >= duration) return 0;
            int count = Math.Min(buffer.Length, Splits[split++ % Splits.Length]);
            for (int offset = 0; offset < count;)
            {
                int start = (int)((Delivered + offset) % cycle.Length);
                int length = Math.Min(count - offset, cycle.Length - start);
                cycle.AsMemory(start, length).CopyTo(buffer[offset..]);
                offset += length;
            }
            Delivered += count;
            return count;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
