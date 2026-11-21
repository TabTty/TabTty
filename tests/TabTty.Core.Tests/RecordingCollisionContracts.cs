using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using TabTty.Core;

internal static class RecordingCollisionContracts
{
    private static readonly string[] Suffixes = [".log", ".raw.tttylog", ".term.tttylog"];

    internal static async Task RunAsync()
    {
        using var folder = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await FilePoliciesAsync(folder.Path, deadline.Token);
        await ConnectAndResumeAsync(folder.Path, deadline.Token);
        if (OperatingSystem.IsWindows()) await LockedWriteFailuresAsync(folder.Path, deadline.Token);
        Console.WriteLine("PASS RecordingCollisionPoliciesAndPartLifecycle");
    }

    private static ConnectionProfile Profile(string stem, LogCollisionPolicy collision, int port = 23)
    {
        var initial = ConnectionProfile.CreateTelnet("Console", "127.0.0.1", port,
            recordText: true, recordRaw: true, recordTerminal: true);
        var json = JsonNode.Parse(initial.ToEditorJson())!;
        json["logging"]!["directory"] = ".";
        json["logging"]!["filenameTemplate"] = stem;
        json["logging"]!["collision"] = LogCollisionPolicies.Format(collision);
        return initial.Edit(json.ToJsonString());
    }

    private static async Task FilePoliciesAsync(string directory, CancellationToken token)
    {
        string path = Path.Combine(directory, "profile.ttty");
        byte[] payload = "\u001b[31mdef\u001b[0m"u8.ToArray();
        await Capture("baseline", LogCollisionPolicy.Error);
        var expected = Suffixes.Select(suffix => File.ReadAllBytes(Path.Combine(directory, "baseline" + suffix))).ToArray();
        Bytes(expected[0], "def"u8.ToArray());
        Bytes(expected[1], payload);
        if (!expected[2].Contains((byte)27)) throw new InvalidOperationException("Terminal recording lost its controls.");
        byte[][] prefixes = ["abc"u8.ToArray(), [255, 0, 128], [27, 93, 255]];
        foreach (var policy in Enum.GetValues<LogCollisionPolicy>())
        {
            string stem = policy.ToString();
            await Capture(stem, policy);
            for (int i = 0; i < Suffixes.Length; i++)
                Bytes(File.ReadAllBytes(Path.Combine(directory, stem + Suffixes[i])), expected[i]);

            // Run both an all-existing group and a mix with a missing Terminal file.
            foreach (bool mixed in new[] { false, true })
            {
                for (int i = 0; i < Suffixes.Length; i++)
                {
                    string file = Path.Combine(directory, stem + Suffixes[i]);
                    if (mixed && i == 2) File.Delete(file);
                    else File.WriteAllBytes(file, prefixes[i]);
                }
                if (policy == LogCollisionPolicy.Error)
                {
                    await RefusesOpen(stem, policy);
                    Bytes(File.ReadAllBytes(Path.Combine(directory, stem + Suffixes[0])), prefixes[0]);
                    Bytes(File.ReadAllBytes(Path.Combine(directory, stem + Suffixes[1])), prefixes[1]);
                    continue;
                }
                await Capture(stem, policy);
                for (int i = 0; i < Suffixes.Length; i++)
                {
                    byte[] prefix = policy == LogCollisionPolicy.Append && (!mixed || i != 2) ? prefixes[i] : [];
                    Bytes(File.ReadAllBytes(Path.Combine(directory, stem + Suffixes[i])), [.. prefix, .. expected[i]]);
                }
            }
            if (OperatingSystem.IsWindows())
            {
                string locked = "lock-" + stem;
                string first = Path.Combine(directory, locked + Suffixes[1]);
                if (policy != LogCollisionPolicy.Error) File.WriteAllBytes(first, prefixes[1]);
                using (File.Open(Path.Combine(directory, locked + Suffixes[2]), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    await RefusesOpen(locked, policy);
                if (policy != LogCollisionPolicy.Error) Bytes(File.ReadAllBytes(first), prefixes[1]);
                using var reopened = File.Open(first, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            }
        }

        string serial = "append_<serial:0,4>";
        var serialPart = SessionRecording.Open(path, Profile(serial, LogCollisionPolicy.Append));
        await serialPart.FinishAsync();
        await serialPart.OpenPart(2).FinishAsync();
        foreach (string suffix in Suffixes)
            if (!File.Exists(Path.Combine(directory, "append_0000" + suffix)) ||
                !File.Exists(Path.Combine(directory, "append_0001" + suffix)))
                throw new InvalidOperationException("Append reused a serial part.");

        async Task Capture(string stem, LogCollisionPolicy policy)
        {
            var profile = Profile(stem, policy);
            var wire = new RecordingFailureWire { Next = payload };
            var part = SessionRecording.Open(path, profile);
            await using var session = new TerminalSession(wire, profile, part, 100);
            await session.ReadOutputAsync(token);
            await session.PauseRecordingAsync(token);
            if (part.Failure is { } failure) throw new InvalidOperationException(failure);
        }

        async Task RefusesOpen(string stem, LogCollisionPolicy policy)
        {
            try { await SessionRecording.Open(path, Profile(stem, policy)).FinishAsync(); }
            catch (IOException) { return; }
            throw new InvalidOperationException("Recording opened an unavailable group: " + stem);
        }
    }

    private static async Task ConnectAndResumeAsync(string directory, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string path = Path.Combine(directory, "daily.ttty");
        var append = Profile("daily", LogCollisionPolicy.Append, port);
        await append.SaveNewAsync(path, token);
        await using (var session = await TerminalSession.ConnectAsync(path, token))
        using (var server = await listener.AcceptTcpClientAsync(token))
        {
            await Receive("one\r\n"u8.ToArray());
            await session.PauseRecordingAsync(token);
            await Receive("paused\r\n"u8.ToArray());
            await File.WriteAllTextAsync(path, Profile("daily", LogCollisionPolicy.Error, port).ToEditorJson(), token);
            await session.ResumeRecordingAsync(token);
            await Receive("two\r\n"u8.ToArray());

            async Task Receive(byte[] payload)
            {
                await server.GetStream().WriteAsync(payload, token);
                await session.ReadOutputAsync(token);
            }
        }
        string output = Path.Combine(directory, "daily.raw.tttylog");
        Bytes(File.ReadAllBytes(output), "one\r\ntwo\r\n"u8.ToArray());
        try
        {
            await using var unexpected = await TerminalSession.ConnectAsync(path, token);
            throw new InvalidOperationException("Reconnection ignored the saved Error policy.");
        }
        catch (RecordingStartException) { }
        if (listener.Pending()) throw new InvalidOperationException("Transport started before recording preparation succeeded.");
        await File.WriteAllTextAsync(path, append.ToEditorJson(), token);
        await using (var session = await TerminalSession.ConnectAsync(path, token))
        using (var server = await listener.AcceptTcpClientAsync(token))
        {
            await server.GetStream().WriteAsync("next"u8.ToArray(), token);
            await session.ReadOutputAsync(token);
        }
        Bytes(File.ReadAllBytes(output), "one\r\ntwo\r\nnext"u8.ToArray());
    }

    [SupportedOSPlatform("windows")]
    private static async Task LockedWriteFailuresAsync(string directory, CancellationToken token)
    {
        string path = Path.Combine(directory, "fault.ttty");
        foreach (bool first in new[] { true, false })
        {
            string stem = first ? "first" : "subsequent";
            string text = Path.Combine(directory, stem + ".log");
            File.WriteAllBytes(text, "abc"u8.ToArray());
            var profile = Profile(stem, LogCollisionPolicy.Append);
            var wire = new RecordingFailureWire();
            var part = SessionRecording.Open(path, profile);
            await using var session = new TerminalSession(wire, profile, part, 100);
            // A reader permits writer acquisition but its range lock fails an actual Windows disk write.
            using (var reader = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                reader.Lock(first ? 3 : 4, 1);
                part.Enqueue(RecordingFormat.Raw, [65]);
                if (!first)
                {
                    part.Enqueue(RecordingFormat.Text, [66]);
                    part.FlushReceived();
                    while (reader.Length != 4) await Task.Delay(5, token);
                }
                part.Enqueue(RecordingFormat.Text, [67]);
                part.FlushReceived();
                await part.Incomplete.WaitAsync(token);
                await part.ActualRelease.WaitAsync(token);
            }
            if (first)
            {
                while (session.AcceptsInput || session.RecordingStartFailure is null) await Task.Delay(5, token);
                Bytes(File.ReadAllBytes(text), "abc"u8.ToArray());
            }
            else
            {
                if (!session.AcceptsInput || session.RecordingStartFailure is not null)
                    throw new InvalidOperationException("A subsequent append failure disconnected the session.");
                wire.Next = "gap\r\n"u8.ToArray();
                await session.ReadOutputAsync(token);
                await session.RetryRecordingAsync(token);
                wire.Next = "new\r\n"u8.ToArray();
                await session.ReadOutputAsync(token);
                await session.PauseRecordingAsync(token);
                Bytes(File.ReadAllBytes(text), "abcBnew\r\n"u8.ToArray());
            }
            Bytes(File.ReadAllBytes(Path.Combine(directory, stem + ".raw.tttylog")),
                first ? [65] : "Anew\r\n"u8.ToArray());
        }
    }

    private static void Bytes(byte[] actual, byte[] expected)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
    }
}
