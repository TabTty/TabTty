using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private sealed record TabSwitchTiming(int Sample, double DelayBeforeSubmissionMs, double DispatchWaitMs,
        double SelectionMs, double LayoutMs, double TotalMs, double GcPauseMs);

    private static async Task ArchitectureWorkload(App app, string directory, int port, int duration, string report)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 150));
        using var end = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var token = end.Token;
        var windows = new[] { new MainWindow(app), new MainWindow(app) };
        var tabs = new List<TerminalTab>();
        var disconnects = new List<string>();
        var peers = new List<AiCollaboration.Counterpart>();
        var reads = new int[4];
        var observationErrors = new Dictionary<string, int>();
        int transfers = 0, pastes = 0, stops = 0;
        var latencies = new List<double>();
        var samplesByKind = new[] { new List<double>(), new List<double>(), new List<double>() };
        var tabSwitchTiming = new List<TabSwitchTiming>();
        var stopLatencies = new List<double>();
        TimeSpan gcAtStart = default, gcDuringMeasurement = default;
        long peakState = 0, peakAi = 0, peakLogs = 0, peakReceive = 0, peakPrivate = 0, peakWorkingSet = 0;
        TimeSpan cpuStart = default, cpuElapsed = default;
        using var process = Process.GetCurrentProcess();
        var transferTask = Task.CompletedTask;
        var broadcastTask = Task.CompletedTask;
        var observeTask = Task.CompletedTask;
        var interaction = new SshInteraction((key, _) => Task.FromResult(key.Destination.Port == port),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        string[] patterns = ["INFO", "WARN", "ERROR", "code=[0-9]+", "count=[0-9]+", "[0-9]{4}",
            "status", "progress", "接続状態", "X{16}"];
        uint[] colors = [0x80CBC4, 0xFFD54F, 0xEF5350, 0xFFAB91, 0xFFCC80, 0xC5CAE9,
            0x81D4FA, 0xAED581, 0xF48FB1, 0xB0BEC5];
        var highlights = new HighlightSet(Guid.NewGuid(), "Fixture highlights", true, patterns.Select((pattern, index) =>
            new TerminalHighlightRule(Guid.NewGuid(), pattern, pattern, true, false, new(colors[index], null, null, null))).ToArray());
        var watch = new Stopwatch();
        long payloadBytes = 0;
        double actualSeconds = 0;
        bool measured = false;
        long rawBefore = 0;
        string? failureMessage = null;
        var rawLogs = new List<(string File, long Bytes, bool CycleMatches)>();
        int textLogs = 0, terminalLogs = 0;
        long textBytes = 0, terminalBytes = 0, transferBytes = 0;
        bool menuCheckAvailable = true;
        try
        {
            try { RequireDesktopInput(); }
            catch (DesktopInputUnavailableException exception)
            {
                menuCheckAvailable = false;
                Console.WriteLine($"NOT RUN workload menu interaction: {exception.Message}");
            }
            foreach (var window in windows) { window.Width = 1250; window.Height = 830; window.Show(); }
            for (int i = 0; i < 20; i++)
            {
                string path = Path.Combine(directory, $"load-{i:D2}.ttty");
                await ConnectionProfile.CreateSsh($"Load {i:D2}", "127.0.0.1", "tabtty-load-" + i, port,
                    recordRaw: true, recordText: true, recordTerminal: true).SaveNewAsync(path, token);
                var tab = new TerminalTab(path, interaction, (120, 40), highlights);
                tabs.Add(tab);
                windows[i / 10].AddTerminalTab(tab);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.Connected += _ => ready.TrySetResult();
                tab.Disconnected += () =>
                {
                    if (!token.IsCancellationRequested)
                        disconnects.Add($"{tab.TabCaption}: {tab.ConnectionStatus}");
                };
                tab.Start();
                await ready.Task.WaitAsync(token);
            }

            int transferIndex = Environment.GetEnvironmentVariable("TABTTY_ARCH_DISTRIBUTION") == "one-busy" ? 10 : 18;
            var transferSession = tabs[transferIndex].InputSession!;
            using var sftp = await transferSession.OpenFileTransferAsync(token);
            var owner = new FileOperationOwnership();
            var files = new TransferFiles(sftp, transferSession.FileTransferWait, owner);
            var operations = new FileOperations(files, owner, token);
            var upload = await files.ResolveAsync(false, directory, null, token);
            var remote = await files.ResolveAsync(true, ".", null, token);
            string downloadsPath = Path.Combine(directory, "downloads");
            Directory.CreateDirectory(downloadsPath);
            var downloads = await files.ResolveAsync(false, downloadsPath, null, token);
            string payload = Path.Combine(directory, "payload.bin");
            byte[] fixture = Enumerable.Range(0, 262145).Select(i => unchecked((byte)i)).ToArray();
            string expectedDigest = Convert.ToHexString(SHA256.HashData(fixture));
            await File.WriteAllBytesAsync(payload, fixture, token);
            var uploadItem = (await files.ListAsync(upload, token)).Single(item => item.Label == "payload.bin");

            // Start the fixture producers only once the windows and all streams are ready.
            await tabs[19].InputSession!.SendTextAsync("START", token);
            await Task.Delay(TimeSpan.FromSeconds(35), token);
            peers.Add(app.Ai.Attach("synthetic history peer A", "history host", 1)!);
            peers.Add(app.Ai.Attach("synthetic history peer B", "history host", 1)!);
            for (int i = 0; i < peers.Count; i++)
                if (app.Ai.Share(tabs[10 + i], peers[i]) is { } failure)
                    throw new InvalidOperationException(failure);
            app.Broadcast.Start(windows[0]);
            app.Broadcast.SetAll(windows[0], false);
            foreach (var target in tabs.Skip(1).Take(2)) app.Broadcast.Select(windows[0], target, true);
            var historyCommands = tabs.Skip(10).Take(peers.Count).Select(tab =>
            {
                string session = tab.AiSharing!.Id;
                return new[]
                {
                    JsonSerializer.SerializeToElement(new { session, max_rows = 12 }),
                    JsonSerializer.SerializeToElement(new { session, query = "code=0123", limit = 4 }),
                };
            }).ToArray();
            // Finish configuring the fixture window before injecting its first input.
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            rawBefore = RecordedBytes();

            gcAtStart = GC.GetTotalPauseDuration();
            cpuStart = process.TotalProcessorTime;
            watch.Start();
            Task sampleInteractions = Task.Run(MeasureInteractionsAsync, token);
            transferTask = app.Dispatcher.InvokeAsync(TransferLoop, DispatcherPriority.Background, token).Task.Unwrap();
            broadcastTask = app.Dispatcher.InvokeAsync(BroadcastLoop, DispatcherPriority.Background, token).Task.Unwrap();
            observeTask = Task.WhenAll(
                app.Dispatcher.InvokeAsync(() => ObserveLoop(0), DispatcherPriority.Background, token).Task.Unwrap(),
                app.Dispatcher.InvokeAsync(() => ObserveLoop(1), DispatcherPriority.Background, token).Task.Unwrap());
            await sampleInteractions;
            if (disconnects.Count != 0) throw new InvalidOperationException(disconnects[0]);
            if (transferTask.IsFaulted || broadcastTask.IsFaulted || observeTask.IsFaulted)
                await (transferTask.IsFaulted ? transferTask : broadcastTask.IsFaulted ? broadcastTask : observeTask);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, duration - watch.Elapsed.TotalSeconds)), token);
            payloadBytes = RecordedBytes() - rawBefore;
            actualSeconds = watch.Elapsed.TotalSeconds;
            gcDuringMeasurement = GC.GetTotalPauseDuration() - gcAtStart;
            process.Refresh();
            cpuElapsed = process.TotalProcessorTime - cpuStart;
            end.Cancel();
            await Task.WhenAll(transferTask, broadcastTask, observeTask);
            if (transfers == 0 || pastes == 0 || stops == 0 || reads[0] == 0 || reads[1] == 0)
                throw new InvalidOperationException("The three operation paths were not all exercised.");
            measured = true;

            async Task MeasureInteractionsAsync()
            {
                for (int sample = 0; sample < duration / 3; sample++)
                {
                    double due = sample * 3000;
                    double remaining;
                    while ((remaining = due - watch.Elapsed.TotalMilliseconds) > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining)), token).ConfigureAwait(false);
                    double submittedAt = watch.Elapsed.TotalMilliseconds;
                    double callbackAt = 0, selectionAt = 0, layoutAt = 0;
                    TerminalSurface? surface = null;
                    TerminalSnapshot? previousViewport = null;
                    TaskCompletionSource<double>? paint = sample % 3 == 0
                        ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                    TimeSpan gcBefore = GC.GetTotalPauseDuration();
                    var window = windows[sample % 2];
                    if (sample % 3 == 1 && !menuCheckAvailable) continue;
                    await app.Dispatcher.InvokeAsync(() =>
                    {
                        if (sample % 3 == 0)
                        {
                            callbackAt = watch.Elapsed.TotalMilliseconds;
                            var strip = (TabControl)window.FindName("tabs");
                            int next = (strip.SelectedIndex + 1) % strip.Items.Count;
                            var tab = (TerminalTab)((TabItem)strip.Items[next]).Content;
                            surface = FindControls<TerminalSurface>(tab).Single();
                            if (tab.HasPendingFrame) previousViewport = surface.Snapshot;
                            surface.ViewportRendered += OnViewportPaint;
                            strip.SelectedIndex = next;
                            selectionAt = watch.Elapsed.TotalMilliseconds;
                            window.UpdateLayout();
                            layoutAt = watch.Elapsed.TotalMilliseconds;
                        }
                        else if (sample % 3 == 1)
                        {
                            var button = (Button)window.FindName("menuButton");
                            button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent, button));
                            window.UpdateLayout();
                        }
                        else
                        {
                            _ = tabs[19].InputSession!.SendTextAsync("sample", token).ContinueWith(
                                task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        }
                    }, DispatcherPriority.Input, token).Task.ConfigureAwait(false);
                    double? viewportAt = paint is null ? null : await paint.Task.WaitAsync(token).ConfigureAwait(false);
                    if (sample % 3 == 1)
                    {
                        await app.Dispatcher.InvokeAsync(() =>
                        {
                            var menu = (Menu)window.FindName("mainMenu");
                            if (!menu.IsVisible || !ReferenceEquals(System.Windows.PresentationSource.FromVisual(menu),
                                System.Windows.PresentationSource.FromVisual(window)))
                                throw new InvalidOperationException("The application menu did not open in its window.");
                        }, DispatcherPriority.Loaded, token).Task.ConfigureAwait(false);
                    }
                    if (sample % 3 == 1)
                    {
                        await app.Dispatcher.InvokeAsync(() =>
                        {
                            var button = (Button)window.FindName("menuButton");
                            button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent, button));
                        }, DispatcherPriority.Input, token).Task.ConfigureAwait(false);
                    }
                    double latency = (viewportAt ?? watch.Elapsed.TotalMilliseconds) - due;
                    if (sample % 3 == 0)
                        tabSwitchTiming.Add(new(sample, submittedAt - due, callbackAt - submittedAt,
                            selectionAt - callbackAt, layoutAt - selectionAt, latency,
                            (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds));
                    latencies.Add(latency);
                    samplesByKind[sample % 3].Add(latency);
                    peakState = Math.Max(peakState, ApplicationResources.TerminalState.Used);
                    peakAi = Math.Max(peakAi, TerminalHistory.WorkQuota.Used);
                    peakLogs = Math.Max(peakLogs, ApplicationResources.LogPayload.Used);
                    peakReceive = Math.Max(peakReceive, ApplicationResources.Receive.Used);
                    process.Refresh();
                    peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    if ((sample + 1) % 100 == 0)
                        Console.WriteLine($"Measured {sample + 1} scheduled interactions.");

                    void OnViewportPaint()
                    {
                        if (!surface!.IsPresentationActive || !surface.IsCurrentSnapshotRendered ||
                            ReferenceEquals(previousViewport, surface.Snapshot)) return;
                        surface.ViewportRendered -= OnViewportPaint;
                        paint!.SetResult(watch.Elapsed.TotalMilliseconds);
                    }
                }
            }

            async Task TransferLoop()
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        string up = await RequireAdmission(operations.Copy([uploadItem], remote, Approve));
                        if (token.IsCancellationRequested) return;
                        if (up != "TransferFinished") throw new InvalidOperationException(up);
                        var item = (await files.ListAsync(remote, token)).Single(value => value.Label == "payload.bin");
                        string down = await RequireAdmission(operations.Copy([item], downloads, Approve));
                        if (token.IsCancellationRequested) return;
                        if (down != "TransferFinished" || Convert.ToHexString(SHA256.HashData(
                            await File.ReadAllBytesAsync(Path.Combine(downloadsPath, "payload.bin"), token))) != expectedDigest)
                            throw new InvalidDataException("The concurrent payload differed.");
                        transfers++;
                        transferBytes += 2L * fixture.Length;
                        // Remove through the operation interface before the next upload.
                        string removed = await RequireAdmission(operations.Delete(remote, [item], Approve));
                        if (removed != "TransferFinished" && !token.IsCancellationRequested)
                            throw new InvalidOperationException(removed);
                        await Task.Delay(3000, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            Task<string> RequireAdmission(Task<string>? operation)
            {
                token.ThrowIfCancellationRequested();
                return operation ?? throw new InvalidOperationException(
                    $"SFTP mutation refused (open={sftp.IsOpen}, pending={owner.IsBusy}, refresh={owner.NeedsRefresh}). {tabs[transferIndex].ConnectionStatus}");
            }
            static ValueTask<TransferChoice> Approve(TransferQuestion question)
                => ValueTask.FromResult(TransferChoice.Overwrite);

            async Task BroadcastLoop()
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        Task<BroadcastInput.PasteOutcome> paste = app.Broadcast.PasteAsync(windows[0], tabs[0],
                            TerminalPaste.Create("0123456789"), (_, _) => throw new InvalidOperationException("Unexpected preview."),
                            new((pastes + stops) % 3 == 2 ? 10000 : 200, 0, false));
                        if ((pastes + stops) % 3 == 2)
                        {
                            await Task.Delay(400, token);
                            long timestamp = Stopwatch.GetTimestamp();
                            await app.Dispatcher.InvokeAsync(() => app.Broadcast.StopPaste(windows[0]),
                                DispatcherPriority.Input, token).Task;
                            stopLatencies.Add(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
                            if (await paste != BroadcastInput.PasteOutcome.Stopped)
                                throw new InvalidOperationException("Stop did not cancel the paced paste.");
                            stops++;
                        }
                        else if (await paste == BroadcastInput.PasteOutcome.Submitted) pastes++;
                        await Task.Delay(300, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                }
            }
            async Task ObserveLoop(int index)
            {
                var peer = peers[index];
                try
                {
                    for (int iteration = 0; !token.IsCancellationRequested; iteration++)
                    {
                        var request = historyCommands[index][iteration % 2];
                        var lap = await app.Ai.CallAsync(peer, iteration % 2 == 0 ? "tabtty_read_history" : "tabtty_search_history", request, token);
                        await app.Ai.CommitReplyAsync(peer, lap, (body, admitted) =>
                        {
                            var result = (IReadOnlyDictionary<string, object?>)body;
                            bool success = admitted && Equals(result["status"], "ok");
                            reads[index + (success ? 0 : 2)]++;
                            if (!success && result["error"] is IReadOnlyDictionary<string, object?> error &&
                                error["code"] is string code)
                                observationErrors[code] = observationErrors.GetValueOrDefault(code) + 1;
                        }, token);
                        await Task.Delay(240, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        catch (Exception error)
        {
            failureMessage = error.Message;
            if (watch.IsRunning)
            {
                actualSeconds = watch.Elapsed.TotalSeconds;
                payloadBytes = RecordedBytes() - rawBefore;
                process.Refresh();
                cpuElapsed = process.TotalProcessorTime - cpuStart;
            }
            throw;
        }
        finally
        {
            end.Cancel();
            app.Broadcast.Stop(windows[0]);
            foreach (var peer in peers) app.Ai.Detach(peer);
            foreach (var tab in tabs) tab.Disconnect();
            try { await Task.WhenAll(tabs.Select(tab => tab.Completion)).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { foreach (var window in windows) if (!window.IsClosing) window.Close(); }
            if (Directory.Exists(Path.Combine(directory, "Logs")) &&
                Environment.GetEnvironmentVariable("TABTTY_ARCH_CYCLE") is { } cyclePath)
            {
                var logs = new DirectoryInfo(Path.Combine(directory, "Logs"));
                var textFiles = logs.GetFiles("*.log");
                var terminalFiles = logs.GetFiles("*.term.tttylog");
                textLogs = textFiles.Length;
                terminalLogs = terminalFiles.Length;
                textBytes = textFiles.Sum(file => file.Length);
                terminalBytes = terminalFiles.Sum(file => file.Length);
                byte[] cycleData = await File.ReadAllBytesAsync(cyclePath);
                foreach (var file in logs.GetFiles("*.raw.tttylog"))
                    rawLogs.Add((file.Name, file.Length, await MatchesCycleAsync(file, cycleData)));
            }
            {
                latencies.Sort();
                stopLatencies.Sort();
                foreach (var samples in samplesByKind) samples.Sort();
                double p95 = Percentile(latencies), stopP95 = Percentile(stopLatencies);
                string fixture = Environment.GetEnvironmentVariable("TABTTY_ARCH_FIXTURE") ?? "";
                string cycle = Environment.GetEnvironmentVariable("TABTTY_ARCH_CYCLE") ?? "";
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    sessions = tabs.Count, windows = 2, duration, completed = measured,
                    distribution = Environment.GetEnvironmentVariable("TABTTY_ARCH_DISTRIBUTION"),
                    actualSeconds, throughputBytesPerSecond = actualSeconds > 0 ? payloadBytes / actualSeconds : 0,
                    interactions = latencies.Count, localP95Ms = p95, localMaxMs = latencies.LastOrDefault(),
                    gcPauseMs = gcDuringMeasurement.TotalMilliseconds,
                    stopP95Ms = stopP95, stopMaxMs = stopLatencies.LastOrDefault(),
                    tabSwitchP95Ms = Percentile(samplesByKind[0]),
                    tabSwitchMaxMs = samplesByKind[0].LastOrDefault(),
                    tabSwitchOver100Ms = samplesByKind[0].Count(sample => sample > 100),
                    tabSwitchTiming,
                    menuCheckAvailable,
                    menuP95Ms = menuCheckAvailable ? Percentile(samplesByKind[1]) : (double?)null,
                    inputP95Ms = Percentile(samplesByKind[2]),
                    localTargetPassed = menuCheckAvailable ? latencies.Count > 0 && p95 <= 100 : (bool?)null,
                    stopTargetPassed = stopLatencies.Count > 0 && stopP95 <= 250,
                    failureMessage, disconnects,
                    transfers, pastes, stops, acceptedReads = reads.Take(2), declinedReads = reads.Skip(2),
                    observationErrors,
                    peakState, peakAi, peakLogs, peakReceive, peakPrivate, peakWorkingSet,
                    appCpuSeconds = cpuElapsed.TotalSeconds, transferBytes,
                    rawLogs = rawLogs.Select(file => new { file.File, file.Bytes, file.CycleMatches }),
                    textLogs, terminalLogs, textBytes, terminalBytes,
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    processors = Environment.ProcessorCount,
                    packageSha256 = Hash(typeof(App).Assembly.Location),
                    fixtureSha256 = Hash(fixture), cycleSha256 = Hash(cycle)
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        long RecordedBytes() => Directory.Exists(Path.Combine(directory, "Logs"))
            ? Directory.GetFiles(Path.Combine(directory, "Logs"), "*.raw.tttylog").Sum(path => new FileInfo(path).Length) : 0;
        static async Task<bool> MatchesCycleAsync(FileInfo file, byte[] cycle)
        {
            using var input = file.OpenRead();
            var buffer = new byte[128 * 1024];
            int offset = 0, count;
            while ((count = await input.ReadAsync(buffer)) != 0)
            {
                int position = 0;
                while (position < count)
                {
                    int length = Math.Min(count - position, cycle.Length - offset);
                    if (!buffer.AsSpan(position, length).SequenceEqual(cycle.AsSpan(offset, length))) return false;
                    offset = (offset + length) % cycle.Length;
                    position += length;
                }
            }
            return true;
        }
        static string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        static double Percentile(List<double> samples) => samples.Count == 0 ? 0 : samples[(int)Math.Ceiling(samples.Count * .95) - 1];
    }
}
