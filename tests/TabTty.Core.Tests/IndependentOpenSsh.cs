using System.Text;
using TabTty.Core;

internal static class IndependentOpenSsh
{
    internal static async Task RunAsync(int port, string username, string userKey, string hostFingerprint, string sharedDirectory)
    {
        using var local = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        string profile = Path.Combine(local.Path, "independent-ssh.ttty");
        await ConnectionProfile.CreateSsh("Independent OpenSSH", "127.0.0.1", username, port,
            methods: ["publicKey"], recordRaw: true).SaveNewAsync(profile, token);
        using var vault = await CredentialVault.CreateAsync(Path.Combine(local.Path, "test.tttykey"),
            "Independent OpenSSH fixture vault".AsMemory(), cancellationToken: token);
        string entry = await vault.ImportPrivateKeyAsync("Generated SSH test key", username, userKey, token);
        int verifiedKeys = 0;
        var interaction = new SshInteraction((key, _) =>
        {
            bool approved = key.Format == "ssh-ed25519" && key.Destination.Port == port && key.Fingerprint == hostFingerprint;
            if (approved) verifiedKeys++;
            return Task.FromResult(approved);
        }, (_, _) => throw new InvalidOperationException("The public-key fixture requested a password."),
            RequestPrivateKey: async (_, cancellation) => await vault.GetPrivateKeyAsync(entry, cancellation));

        const string marker = "TABTTY_INDEPENDENT_SSH_TERMINAL";
        byte[] payload = [.. "TABTTY_SFTP_ONLY"u8, .. Enumerable.Range(0, 70_000).Select(value => (byte)value)];
        await using (var session = await TerminalSession.ConnectAsync(profile, token, interaction,
            viewport: _ => Task.FromResult((120, 40))))
        {
            using var transfer = await session.OpenFileTransferAsync(token);
            byte[] directory = await transfer.ResolveAsync(Encoding.UTF8.GetBytes("/" + Path.GetFullPath(sharedDirectory).Replace('\\', '/')), token);
            byte[] staged = SftpClient.Child(directory, transfer.EncodeName("candidate.part"));
            const string file = "検証.bin";
            byte[] final = SftpClient.Child(directory, transfer.EncodeName(file));
            byte[] output = await transfer.OpenAsync(staged, write: true, token);
            var shell = ObserveShellAsync();
            for (int offset = 0; offset < payload.Length; offset += SftpClient.BlockSize)
            {
                int count = Math.Min(SftpClient.BlockSize, payload.Length - offset);
                await transfer.WriteAsync(output, offset, payload.AsMemory(offset, count), token);
            }
            await transfer.CloseAsync(output, token);
            await transfer.RenameAsync(staged, final, replace: false, token);
            var entries = await transfer.ListAsync(directory, token);
            if (!entries.Any(entry => transfer.DecodeName(entry.Name) == file))
                throw new InvalidOperationException("Independent SFTP did not list the published file.");
            if (!(await File.ReadAllBytesAsync(Path.Combine(sharedDirectory, file), token)).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The host file did not match the uploaded bytes.");

            byte[] input = await transfer.OpenAsync(final, write: false, token);
            using var downloaded = new MemoryStream();
            while (await transfer.ReadAsync(input, downloaded.Length, SftpClient.BlockSize, token) is { } bytes)
                downloaded.Write(bytes);
            await transfer.CloseAsync(input, token);
            if (!downloaded.ToArray().AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Independent SFTP returned the wrong file bytes.");
            await transfer.RemoveAsync(final, directory: false, token);
            if (File.Exists(Path.Combine(sharedDirectory, file)))
                throw new InvalidOperationException("Independent SFTP did not remove the file.");
            await shell;
            if (verifiedKeys == 0 || !session.AcceptsInput)
                throw new InvalidOperationException("The independent connection failed host verification or lost shell input.");
            session.Disconnect();

            async Task ObserveShellAsync()
            {
                await session.SendTextAsync("echo " + marker, token);
                await session.SendEnterAsync(token);
                while (await session.ReadOutputAsync(token) is { } rows)
                    if (rows.Any(row => row.Trim() == marker)) return;
                throw new InvalidOperationException("The real Windows shell did not produce the expected terminal output.");
            }
        }

        byte[] raw = await File.ReadAllBytesAsync(Directory.GetFiles(local.Path, "*.raw.tttylog", SearchOption.AllDirectories).Single(), token);
        if (raw.AsSpan().IndexOf("TABTTY_INDEPENDENT_SSH_TERMINAL"u8) < 0 || raw.AsSpan().IndexOf("TABTTY_SFTP_ONLY"u8) >= 0)
            throw new InvalidDataException("Raw did not isolate terminal data from the independent SFTP connection.");
    }
}
