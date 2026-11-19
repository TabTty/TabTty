using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation.Provider;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckFullScreenApplicationAsync(App app, string directory, string executable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string path = Path.Combine(directory, "vim.ttty");
        await ConnectionProfile.CreateTelnet("Independent Vim", "127.0.0.1", port,
            recordRaw: true, recordText: true, recordTerminal: true).SaveNewAsync(path, token);

        var window = new MainWindow(app) { Width = 1250, Height = 830 };
        var tab = new TerminalTab(path);
        window.AddTerminalTab(tab);
        window.Show();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Connected += _ => ready.TrySetResult();
        tab.Start();

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (string argument in new[] { "--not-a-term", "-N", "-u", "NONE", "-i", "NONE", "-n" })
            start.ArgumentList.Add(argument);
        start.Environment["TERM"] = "xterm-256color";
        start.Environment["LANG"] = "C.UTF-8";
        using var vim = new Process { StartInfo = start };
        bool started = false;
        string phase = "Telnet startup";
        try
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            await ready.Task.WaitAsync(token);
            var session = tab.InputSession ?? throw new InvalidOperationException("The Vim session did not connect.");
            var stream = peer.GetStream();
            await stream.ReadExactlyAsync(new byte[15], token);
            var surface = FindControls<TerminalSurface>(tab).Single();
            const string normal = "preserved normal buffer";
            const string marker = "independent-vim-marker";
            byte[] prelude = "preserved normal buffer\r\n"u8.ToArray();
            await stream.WriteAsync(prelude, token);
            await WaitForViewportAsync("normal buffer", snapshot => ContainsText(snapshot, normal));

            using var raw = new MemoryStream();
            raw.Write(prelude);
            started = vim.Start();
            if (!started) throw new InvalidOperationException("Vim did not start.");
            Task<string> diagnostics = vim.StandardError.ReadToEndAsync(token);
            Task output = RelayVimOutputAsync(vim.StandardOutput.BaseStream, stream, raw, token);
            Task input = stream.CopyToAsync(vim.StandardInput.BaseStream, relay.Token);

            await WaitForViewportAsync("alternate screen", snapshot => snapshot.IsAlternateScreen && !ContainsText(snapshot, normal));
            await session.SendTextAsync("i" + marker, token);
            await WaitForViewportAsync("Vim insertion", snapshot =>
                snapshot.IsAlternateScreen && ContainsText(snapshot, marker) && !ContainsText(snapshot, normal));
            int previousColumns = surface.Snapshot!.Columns;
            window.Width -= 180;
            await WaitForViewportAsync("alternate screen after resize", snapshot =>
                snapshot.Columns < previousColumns && snapshot.IsAlternateScreen && ContainsText(snapshot, marker));
            phase = "Vim exit";
            await session.SendKeyAsync(TerminalKey.Escape, cancellationToken: token);
            await session.SendTextAsync(":qa!", token);
            await session.SendEnterAsync(token);
            await vim.WaitForExitAsync(token);
            await output;
            if (vim.ExitCode != 0) throw new InvalidOperationException(await diagnostics);
            byte[] boundary = "\r\nrecording boundary\r\n"u8.ToArray();
            raw.Write(boundary);
            await stream.WriteAsync(boundary, token);
            await WaitForViewportAsync("restored normal buffer", snapshot => !snapshot.IsAlternateScreen && ContainsText(snapshot, normal) &&
                !ContainsText(snapshot, marker) && ContainsText(snapshot, "recording boundary"));

            await session.PauseRecordingAsync(token);
            await stream.WriteAsync("paused payload\r\n"u8.ToArray(), token);
            await WaitForViewportAsync("paused recording", snapshot => ContainsText(snapshot, "paused payload"));
            await session.ResumeRecordingAsync(token);
            byte[] resumed = "resumed payload\r\n"u8.ToArray();
            await stream.WriteAsync(resumed, token);
            await WaitForViewportAsync("resumed recording", snapshot => ContainsText(snapshot, "resumed payload"));
            ITextProvider automation = new TerminalAutomationPeer(surface);
            if (!automation.DocumentRange.GetText(-1).Contains("resumed payload", StringComparison.Ordinal))
                throw new InvalidOperationException("UI Automation did not expose the restored terminal view.");

            relay.Cancel();
            phase = "recording drain";
            try { await input; }
            catch (OperationCanceledException) when (relay.IsCancellationRequested) { }
            peer.Client.Shutdown(SocketShutdown.Send);
            await tab.Completion.WaitAsync(token);
            byte[] observed = raw.ToArray();
            if (observed.AsSpan().IndexOf("\u001b[?1049h"u8) < 0 || observed.AsSpan().IndexOf("\u001b[?1049l"u8) < 0)
                throw new InvalidDataException("Vim did not emit both alternate-screen transitions.");
            string[] parts = Directory.GetFiles(directory, "*.raw.tttylog", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray();
            if (parts.Length != 2 || !(await File.ReadAllBytesAsync(parts[0], token)).AsSpan().SequenceEqual(observed) ||
                !(await File.ReadAllBytesAsync(parts[1], token)).AsSpan().SequenceEqual(resumed))
                throw new InvalidDataException("The two Raw recording intervals differ from the peer's bytes.");

            async Task WaitForViewportAsync(string description, Func<TerminalSnapshot, bool> accepted)
            {
                phase = description;
                try
                {
                    while (surface.Snapshot is not { } snapshot || !surface.IsCurrentSnapshotRendered || !accepted(snapshot))
                        await Task.Delay(20, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    var rendered = surface.Snapshot;
                    Console.Error.WriteLine($"Grid {rendered?.Columns}x{rendered?.ScreenRows}, alternate {rendered?.IsAlternateScreen}, current frame {surface.IsCurrentSnapshotRendered}.");
                    if (rendered is not null) Console.Error.WriteLine(string.Join(" | ", rendered.Take(4)));
                    throw;
                }
            }

            static bool ContainsText(TerminalSnapshot snapshot, string text) =>
                snapshot.Any(row => row.Contains(text, StringComparison.Ordinal));
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            throw new TimeoutException($"The Vim fixture timed out at {phase}.", error);
        }
        finally
        {
            relay.Cancel();
            if (started && !vim.HasExited) vim.Kill();
            tab.Disconnect();
            if (tab.Completion.IsCompleted && !window.IsClosing) window.Close();
        }
    }

    private static async Task RelayVimOutputAsync(Stream output, Stream destination, Stream recording, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await output.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            await recording.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }
}
