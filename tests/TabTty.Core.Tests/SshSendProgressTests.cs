using TabTty.Core;

internal static partial class ArchitectureOwnershipContracts
{
    internal static async Task SshTinyAlternatingPayloadPreservesOrderAsync(int port)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        string path = Path.Combine(directory.Path, "burst.ttty");
        var profile = ConnectionProfile.CreateSsh("SSH stderr burst", "127.0.0.1", "tabtty-stderr-burst", port, recordRaw: true);
        await profile.SaveNewAsync(path, token);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        await using var session = await TerminalSession.ConnectAsync(path, token, interaction);
        await session.SendTextAsync("burst", token);
        for (int index = 0; index < 1536; index++)
            if (!await session.ProcessOutputAsync(token))
                throw new InvalidOperationException("The terminal ended during the alternating data/stderr burst.");
        while (!(await session.ReadOutputAsync(token) ?? throw new IOException("The burst ended before its marker."))
            .Contains("burst finished")) { }
        if (!session.AcceptsInput) throw new InvalidOperationException("Exit status ended the reverse input direction.");
        await session.SendTextAsync("reverse", token);
        IReadOnlyList<string>? last = null;
        while (await session.ReadOutputAsync(token) is { } output) last = output;
        if (last?.Contains("reverse confirmed") != true)
            throw new InvalidOperationException("Input after exit status did not reach the peer.");
        await session.PauseRecordingAsync(token);
        string file = Directory.EnumerateFiles(Path.GetFullPath(profile.LogDirectory, directory.Path), "*.raw.tttylog").Single();
        byte[] expected = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("AB", 768)) +
            "\r\nburst finished\r\nreverse confirmed");
        if (!(await File.ReadAllBytesAsync(file, token)).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Alternating SSH data/stderr or exit status changed the exact raw payload.");
    }

    internal static async Task SshStartupFailurePreservesOriginalRecordingAsync(int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        byte[] expected = "early data early stderr"u8.ToArray();
        await CheckAsync(cancel: false);
        await CheckAsync(cancel: true);

        async Task CheckAsync(bool cancel)
        {
            using var directory = new TemporaryDirectory();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            string path = Path.Combine(directory.Path, "startup.ttty");
            string user = cancel ? "tabtty-shell-cancelled" : "tabtty-shell-refused";
            var profile = ConnectionProfile.CreateSsh("Startup recording", "127.0.0.1", user, port, recordRaw: true,
                recordText: true, recordTerminal: true);
            await profile.SaveNewAsync(path, token);
            string logs = Path.GetFullPath(profile.LogDirectory, directory.Path);
            Task<TerminalSession> connecting = TerminalSession.ConnectAsync(path, stop.Token, interaction);
            if (cancel)
            {
                while (!Directory.Exists(logs) || !Directory.EnumerateFiles(logs, "*.raw.tttylog")
                    .Any(file => new FileInfo(file).Length == expected.Length))
                {
                    if (connecting.IsCompleted) throw new InvalidOperationException("The held shell did not remain pending.");
                    await Task.Delay(15, token);
                }
                stop.Cancel();
            }
            try
            {
                await using var unexpected = await connecting.WaitAsync(token);
                throw new InvalidOperationException("A failed startup activated its session.");
            }
            catch (OperationCanceledException) when (cancel && stop.IsCancellationRequested && !token.IsCancellationRequested) { }
            catch (IOException error) when (!cancel && error.Message == "The SSH server refused terminal startup.") { }

            foreach (string pattern in new[] { "*.raw.tttylog", "*.log", "*.term.tttylog" })
            {
                string file = Directory.EnumerateFiles(logs, pattern).Single();
                var bytes = await File.ReadAllBytesAsync(file, token);
                if (!bytes.AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException($"Startup failure changed its admitted output in {pattern}.");
                using var closed = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        }
    }

    internal static async Task SftpLateOpenAndClosePreserveTheOriginalSlotAsync(int port, bool timeOut = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancelOpen = new CancellationTokenSource();
        using var cancelReopen = new CancellationTokenSource();
        var token = deadline.Token;
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        var old = await OpenAsync("tabtty-delayed-sftp");
        try
        {
            if (timeOut)
            {
                Task<SftpClient> attempt = old.OpenTransferAsync("utf-8", token);
                await ExpectLineAsync(old, "old open pending\n");
                try
                {
                    using var unexpected = await attempt;
                    throw new InvalidOperationException("The unanswered OPEN unexpectedly completed.");
                }
                catch (TimeoutException) { }
                await ExpectReopenTimeoutAsync();
                await old.WriteAsync("release open"u8.ToArray(), token);
                await ExpectLineAsync(old, "old close pending\n");
                await ExpectReopenTimeoutAsync();
                await old.WriteAsync("release close"u8.ToArray(), token);
                using var reopened = await old.OpenTransferAsync("utf-8", token);
                await reopened.CreateDirectoryAsync("/home/after-expiry"u8.ToArray(), token);
                if (await reopened.StatAsync("/home/after-expiry"u8.ToArray(), token) is null)
                    throw new InvalidOperationException("An expired waiter changed the new SFTP instance.");
                return;
            }
            Task<SftpClient> opening = old.OpenTransferAsync("utf-8", cancelOpen.Token);
            await ExpectLineAsync(old, "old open pending\n");
            cancelOpen.Cancel();
            await ExpectCancelledAsync(opening);
            Task<SftpClient> cancelledReopen = old.OpenTransferAsync("utf-8", cancelReopen.Token);
            try
            {
                using var unexpected = await old.OpenTransferAsync("utf-8", token);
                throw new InvalidOperationException("More than one reopen waiter was admitted.");
            }
            catch (SftpException error) when (error.Failure == SftpFailure.Capacity) { }
            cancelReopen.Cancel();
            await ExpectCancelledAsync(cancelledReopen);
            Task<SftpClient> reopening = old.OpenTransferAsync("utf-8", token);
            await old.WriteAsync("release open"u8.ToArray(), token);
            await ExpectLineAsync(old, "old close pending\n");
            if (reopening.IsCompleted)
                throw new InvalidOperationException("Late OPEN released the closing slot before CLOSE.");
            await old.WriteAsync("probe"u8.ToArray(), token);
            await ExpectLineAsync(old, "terminal alive\n");

            var replacement = await OpenAsync("tabtty-send-progress");
            try
            {
                using var newSftp = await replacement.OpenTransferAsync("utf-8", token);
                await newSftp.CreateDirectoryAsync("/home/replacement"u8.ToArray(), token);
                await old.WriteAsync("release close"u8.ToArray(), token);
                using var oldSftp = await reopening.WaitAsync(token);
                await oldSftp.CreateDirectoryAsync("/home/reopened"u8.ToArray(), token);
                if ((await newSftp.StatAsync("/home/replacement"u8.ToArray(), token)) is null ||
                    (await newSftp.StatAsync("/home/reopened"u8.ToArray(), token)) is not null)
                    throw new InvalidOperationException("Old channel completion changed the replacement provider's state.");
            }
            finally { await replacement.CloseAsync(); }
        }
        finally { await old.CloseAsync(); }

        async Task<SshSessionWire> OpenAsync(string user)
        {
            var profile = ConnectionProfile.CreateSsh("SFTP slot fixture", "127.0.0.1", user, port, methods: ["password"]);
            if (timeOut)
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(profile.ToEditorJson())!;
                json["connection"]!["waitSeconds"] = 2;
                profile = profile.Edit(json.ToJsonString());
            }
            return (SshSessionWire)await SshSessionWire.OpenAsync(profile, interaction, _ => Task.FromResult((80, 24)),
                (_, _, _) => Task.CompletedTask, token, token);
        }

        async Task ExpectReopenTimeoutAsync()
        {
            var reopening = old.OpenTransferAsync("utf-8", token);
            await old.WriteAsync("probe"u8.ToArray(), token);
            await ExpectLineAsync(old, "terminal alive\n");
            try
            {
                using var unexpected = await reopening;
                throw new InvalidOperationException("An unanswered channel released the original SFTP slot.");
            }
            catch (SftpException error) when (error.Failure == SftpFailure.Timeout) { }
        }

        async Task ExpectLineAsync(ISessionWire wire, string expected)
        {
            byte[] bytes = new byte[expected.Length];
            byte[] scratch = new byte[bytes.Length];
            for (int offset = 0; offset < bytes.Length;)
            {
                int read = await wire.DeliverPayloadAsync(scratch.AsMemory(0, bytes.Length - offset),
                    (source, payload, _) =>
                    {
                        if (source != TerminalReceiveSource.Data)
                            throw new InvalidOperationException("Unexpected stderr during SFTP slot retirement.");
                        payload.CopyTo(bytes.AsSpan(offset));
                        return Task.CompletedTask;
                    }, token);
                if (read <= 0) throw new IOException("The terminal channel ended during SFTP slot retirement.");
                offset += read;
            }
            if (System.Text.Encoding.ASCII.GetString(bytes) != expected)
                throw new InvalidOperationException("Unexpected terminal data during SFTP slot retirement.");
        }

        async Task ExpectCancelledAsync(Task<SftpClient> attempt)
        {
            try
            {
                using var unexpected = await attempt.WaitAsync(token);
                throw new InvalidOperationException("A cancelled SFTP attempt remained active.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    private static async Task CancelledPacketLeavesTheCipherAndCreditUntouchedAsync()
    {
        using var memory = new MemoryStream();
        using var writer = new SshPackets(memory);
        using var reader = new SshPackets(memory);
        byte[] key = new byte[16], nonce = new byte[12];
        const string cipher = "aes128-gcm@openssh.com";
        writer.SetKeys(true, key.ToArray(), nonce.ToArray(), true, cipher, null, []);
        reader.SetKeys(false, key.ToArray(), nonce.ToArray(), true, cipher, null, []);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        byte[] payload = "channel data"u8.ToArray();
        var abandoned = new SshSendProgress(payload.Length);
        try
        {
            await writer.SendChannelAsync(payload, default, stopped.Token,
                () => throw new InvalidOperationException("Uncommitted credit was consumed."), abandoned, payload.Length);
            throw new InvalidOperationException("Precommit cancellation did not stop the packet.");
        }
        catch (OperationCanceledException) { }
        abandoned.Settle();
        if (memory.Length != 0 || abandoned.State != new SshSendState(payload.Length, 0, null, null, true))
            throw new InvalidOperationException("Cancellation changed the uncommitted packet or its disposition.");

        var progress = new SshSendProgress(payload.Length);
        int credit = 0;
        await writer.SendChannelAsync(payload, default, default, () => credit += payload.Length, progress, payload.Length);
        progress.Settle();
        memory.Position = 0;
        if (!(await reader.ReadAsync(default)).AsSpan().SequenceEqual(payload) || credit != payload.Length ||
            progress.State != new SshSendState(payload.Length, payload.Length, null, null, true))
            throw new InvalidOperationException("The packet after precommit cancellation used a changed cipher or credit state.");
    }

    internal static async Task SshTerminalAndSftpRetainCommittedPacketsAsync(int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var profile = ConnectionProfile.CreateSsh("Send progress", "127.0.0.1", "tabtty-send-progress", port,
            methods: ["password"]);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        PacketWriteFixture? packets = null;

        var transport = await ConnectAsync();
        try
        {
            const int packet = 32768;
            byte[] input = new byte[2 * packet + 8];
            var progress = new SshSendProgress(input.Length);
            SshSendState initial = progress.State;
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var abandoned = new CancellationTokenSource();
            Task waiting = progress.WaitForChangeAsync(initial, token);
            using var pause = packets!.PauseWrite(ordinal: 2);
            var sending = transport.SubmitPreparedAsync(input, stopped.Token, progress).AsTask();
            await pause.Issued.Task.WaitAsync(token);
            await waiting;
            SshSendState pending = progress.State;
            if (pending != new SshSendState(input.Length, packet, new(packet, packet), null, false) ||
                pending.Uncommitted != new SshSendRange(2 * packet, 8))
                throw new InvalidOperationException($"The terminal's committed range was incorrect: {pending}.");
            Task observation = progress.WaitForChangeAsync(pending, abandoned.Token);
            abandoned.Cancel();
            try { await observation; throw new InvalidOperationException("The abandoned observer still waited."); }
            catch (OperationCanceledException) { }
            stopped.Cancel();
            if (sending.IsCompleted || progress.State != pending)
                throw new InvalidOperationException("Stop ended a write after cipher commitment.");
            Task uncertain = progress.WaitForChangeAsync(pending, token);
            pause.Dispose();
            await uncertain;
            var result = await sending.WaitAsync(token);
            if (result.Status != TransportSubmission.Uncertain || result.DefiniteBytes != packet ||
                result.Error is not IOException ||
                result.Ssh != new SshSendState(input.Length, packet, null, new(packet, packet), true) ||
                packets.WritesSinceArm != 2)
                throw new InvalidOperationException("The terminal lost its definite prefix, uncertain packet or unsent suffix.");
        }
        finally { await transport.DisconnectAsync().WaitAsync(token); }

        transport = await ConnectAsync();
        try
        {
            using var sftp = await transport.OpenTransferAsync("utf-8", token);
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var pause = packets!.PauseWrite(ordinal: 1);
            Task mutation = sftp.CreateDirectoryAsync("/home/uncertain-final-fixture"u8.ToArray(), stopped.Token);
            await pause.Issued.Task.WaitAsync(token);
            stopped.Cancel();
            if (mutation.IsCompleted)
                throw new InvalidOperationException("The SFTP request released an in-flight final mutation packet.");
            pause.Dispose();
            try { await mutation.WaitAsync(token); throw new InvalidOperationException("The failed final packet was reported as successful."); }
            catch (SftpException error) when (error.Failure == SftpFailure.UnknownOutcome) { }
            if (sftp.IsOpen) throw new InvalidOperationException("SFTP admitted a follow-up after an uncertain frame.");
        }
        finally { await transport.DisconnectAsync().WaitAsync(token); }

        var pasteProfile = ConnectionProfile.CreateSsh("Paste progress", "127.0.0.1", "tabtty-paste-progress", port,
            methods: ["password"]);
        await using (var terminal = new TerminalSession(await OpenWireAsync(pasteProfile), pasteProfile, null, 100))
        {
            using var paste = TerminalPaste.Create("A😀Z");
            using var pause = packets!.PauseWrite(ordinal: 3);
            Task<bool> sending = terminal.SendPasteAsync(paste, 0, 0, token, token);
            await pause.Issued.Task.WaitAsync(token);
            pause.Dispose();
            try { await sending.WaitAsync(token); throw new InvalidOperationException("An uncertain paste succeeded."); }
            catch (PasteWriteException error) when (error.DefiniteBytes == 1) { }
            if (packets.WritesSinceArm != 3)
                throw new InvalidOperationException("The paste replayed bytes after the uncertain packet.");
        }

        async Task<SessionTransport> ConnectAsync()
        {
            var wire = await OpenWireAsync(profile);
            return new SessionTransport(wire, profile, (80, 24), (_, _, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        }

        Task<ISessionWire> OpenWireAsync(ConnectionProfile settings) =>
            SshSessionWire.OpenAsync(settings, interaction, _ => Task.FromResult((80, 24)),
                (_, _, _) => Task.CompletedTask, token, token, stream => packets = new(stream));
    }

    private sealed class PacketWriteFixture(Stream stream) : Stream
    {
        private PausedWrite? pause;
        private int writes;
        internal int WritesSinceArm => Volatile.Read(ref writes);

        internal PausedWrite PauseWrite(int ordinal)
        {
            Volatile.Write(ref writes, 0);
            var next = new PausedWrite(ordinal);
            Volatile.Write(ref pause, next);
            return next;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (Volatile.Read(ref pause) is not { } held || Interlocked.Increment(ref writes) != held.Ordinal)
            {
                await stream.WriteAsync(buffer, token);
                return;
            }
            // The cipher state has advanced, and only the packet length field has reached the socket.
            await stream.WriteAsync(buffer[..4], token);
            held.Issued.TrySetResult();
            await held.Release.Task.WaitAsync(token);
            throw new IOException("Controlled failure after the encrypted write was issued.");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            stream.ReadAsync(buffer, token);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        internal sealed class PausedWrite(int ordinal) : IDisposable
        {
            internal int Ordinal { get; } = ordinal;
            internal TaskCompletionSource Issued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Dispose() => Release.TrySetResult();
        }
    }
}
