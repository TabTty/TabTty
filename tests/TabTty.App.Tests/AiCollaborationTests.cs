using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckAiHighlightCoverageAsync(App app, string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string path = Path.Combine(directory, "ai-highlight.ttty");
        await ConnectionProfile.CreateTelnet("Highlight coverage", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .SaveNewAsync(path, token);
        var rules = new HighlightSet(Guid.NewGuid(), "Coverage", true,
            [new TerminalHighlightRule(Guid.NewGuid(), "Error", "ERROR", true, false, new(0xFF0000, null, null, null))]);
        var host = new MainWindow(app);
        var tab = new TerminalTab(path, null, (40, 32), rules);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Connected += _ => ready.TrySetResult();
        var ai = app.Ai;
        var observer = ai.Attach("highlight fixture", @"C:\Fixtures\Highlight.exe", 301)!;
        host.Show();
        host.AddTerminalTab(tab);
        try
        {
            tab.Start();
            using var peer = await listener.AcceptTcpClientAsync(token);
            await ready.Task.WaitAsync(token);
            var wire = peer.GetStream();
            await wire.ReadExactlyAsync(new byte[15], token);
            Check(ai.Share(tab, observer) is null, "The highlight fixture could not share its terminal.");
            string session = tab.AiSharing!.Id;
            await Exchange("ERROR");
            await WaitForRow(1, 1);
            var blank = await Read(2, 1);
            var mixed = await Read(1, 2);
            Check(blank.GetProperty("highlight_status").GetString() == "pending" &&
                blank.GetProperty("lines")[0].GetProperty("cells")[0].GetProperty("blank").GetBoolean(),
                "An unevaluated blank row was not returned as pending with its cells.");
            Check(mixed.GetProperty("highlight_status").GetString() == "pending" &&
                mixed.GetProperty("lines")[0].GetProperty("highlights").GetArrayLength() == 1,
                "The mixed region lost a completed highlight or did not report an unevaluated row.");

            await Exchange("\u001b[2;1H ");
            await WaitForRow(2, 0);
            var evaluated = await Read(1, 2);
            Check(evaluated.GetProperty("highlight_status").GetString() == "current" &&
                evaluated.GetProperty("lines")[1].GetProperty("highlights").GetArrayLength() == 0,
                "An evaluated zero-match row was treated as pending.");

            await Exchange("\u001b[?1049h\u001b[HERROR");
            var alternate = await Read(1, 1);
            Check(alternate.GetProperty("source").GetString() == "alternate" &&
                alternate.GetProperty("lines")[0].GetProperty("highlights").ValueKind == JsonValueKind.Null,
                "The alternate screen returned regular-screen highlights.");

            async Task Exchange(string text)
            {
                await wire.WriteAsync(Encoding.UTF8.GetBytes(text + "\u001b[5n"), token);
                await wire.ReadExactlyAsync(new byte[4], token);
            }

            async Task WaitForRow(int row, int matches)
            {
                while (tab.CaptureAiHighlightRow(row)?.Spans.Length != matches) await Task.Delay(15, token);
            }

            Task<JsonElement> Read(int row, int rows) => CommitAiToolAsync(ai, observer, "tabtty_read_state",
                new { session, include_highlights = true, region = new { row, column = 1, rows, columns = 5 } }, token);
        }
        finally
        {
            ai.EndSharing(tab);
            ai.Detach(observer);
            tab.Disconnect();
            await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            host.Close();
        }
    }

    private static async Task CheckAiPasteFailureDispositionAsync(App app, string directory)
    {
        foreach (var (serial, refuseRemainder, kind, expected) in new[]
        {
            (true, false, "paste", "not_sent"),
            (false, false, "paste", "unknown"),
            (false, true, "paste", "possibly_partial"),
            (false, true, "text", "possibly_partial"),
        })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token = deadline.Token;
            var host = new MainWindow(app, () => true);
            var tab = new TerminalTab(Path.Combine(directory, "ai-paste-disposition.ttty"));
            using var wire = new IncompleteBroadcastWire(serial, refuseRemainder);
            var profile = serial ? ConnectionProfile.CreateSerial("Serial paste", new("COM254")) :
                ConnectionProfile.CreateTelnet("Telnet paste", "127.0.0.1", transmitEncoding: "iso-8859-1");
            await using var terminal = new TerminalSession(wire, profile, null, 100);
            typeof(TerminalTab).GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(tab, terminal);
            host.AddTerminalTab(tab);
            host.Show();
            host.SelectNotificationTab(tab);
            var ai = app.Ai;
            var peer = ai.Attach("paste fixture", @"C:\Fixtures\Paste.exe", 302)!;
            try
            {
                Check(ai.Share(tab, peer) is null && ai.Grant(tab) is null, "The paste fixture could not obtain input control.");
                string session = tab.AiSharing!.Id, control = tab.AiSharing.Control!.Id;
                object action;
                JsonElement reply;
                while (true)
                {
                    string observation = (await CommitAiToolAsync(ai, peer, "tabtty_read_state", new { session }, token))
                        .GetProperty("observation").GetString()!;
                    action = new { session, control, action_seq = 1, observation, input = new { kind, text = serial ? "A" : "ÿ" } };
                    reply = await CommitAiToolAsync(ai, peer, "tabtty_send_input", action, token);
                    if (!reply.TryGetProperty("error", out var error) || error.GetProperty("code").GetString() != "stale_observation") break;
                    // The first layout can resize the fixture after observation, before any bytes are attempted.
                    await Task.Delay(15, token);
                }
                Check(reply.GetProperty("delivery").GetString() == expected &&
                    (tab.AiSharing.Control is not null) == serial && wire.Calls == (serial ? 1 : 2) &&
                    wire.Accepted == (serial ? null : (byte)0xFF),
                    "AI paste confused definite non-submission with an uncertain write.");
                if (serial)
                {
                    var replay = await CommitAiToolAsync(ai, peer, "tabtty_send_input", action, token);
                    Check(replay.GetProperty("delivery").GetString() == "not_sent" && wire.Calls == 1 && !tab.Reports.HasUnread,
                        "A duplicate rejected paste replayed input or raised an uncertainty incident.");
                }
            }
            finally
            {
                ai.EndSharing(tab);
                ai.Detach(peer);
                typeof(TerminalTab).GetProperty(nameof(TerminalTab.IsRunning))!.SetValue(tab, false);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Closed += (_, _) => closed.TrySetResult();
                host.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task CheckAiQueuedPasteTakeoverAsync(App app, string directory)
    {
        foreach (bool bracketed in new[] { true, false })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token = deadline.Token;
            var host = new MainWindow(app, () => true);
            var tab = new TerminalTab(Path.Combine(directory, "ai-queued-paste.ttty"));
            using var wire = new HeldPasteReplyWire();
            await using var terminal = new TerminalSession(wire, ConnectionProfile.CreateTelnet("Queued paste", "127.0.0.1"), null, 100);
            typeof(TerminalTab).GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(tab, terminal);
            host.AddTerminalTab(tab);
            host.Show();
            host.SelectNotificationTab(tab);
            var ai = app.Ai;
            var peer = ai.Attach("queued paste fixture", @"C:\Fixtures\QueuedPaste.exe", 303)!;
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void InputQueued() => queued.TrySetResult();
            terminal.InputSendQueued += InputQueued;
            try
            {
                if (bracketed)
                    await terminal.ProcessPayloadAsync(TerminalReceiveSource.Data, "\u001b[?2004h"u8.ToArray(), token);
                Check(ai.Share(tab, peer) is null && ai.Grant(tab) is null, "The queued paste fixture could not obtain control.");
                string session = tab.AiSharing!.Id, control = tab.AiSharing.Control!.Id;
                Check(await terminal.ProcessOutputAsync(token), "The fixture did not receive the status query.");
                await wire.ReplyStarted.Task.WaitAsync(token);
                Task<JsonElement> sending;
                while (true)
                {
                    string observation = (await CommitAiToolAsync(ai, peer, "tabtty_read_state", new { session }, token))
                        .GetProperty("observation").GetString()!;
                    sending = CommitAiToolAsync(ai, peer, "tabtty_send_input",
                        new { session, control, action_seq = 1, observation, input = new { kind = "paste", text = "A" } }, token);
                    if (await Task.WhenAny(sending, queued.Task).WaitAsync(token) == queued.Task) break;
                    var previous = await sending;
                    Check(previous.GetProperty("error").GetProperty("code").GetString() == "stale_observation",
                        "The paste finished before it could wait for the writer.");
                }
                Check(wire.PasteWrites == 0 && tab.AiSharing.Control?.Attempt is { Admitted: false },
                    "A paste waiting for the writer was admitted prematurely.");
                ai.EndControl(tab);
                var reply = await sending.WaitAsync(token);
                wire.ReleaseReply.TrySetResult();
                await wire.ReplyCompleted.Task.WaitAsync(token);
                while (tab.IsAiTakingControl) await Task.Delay(10, token);
                Check(reply.GetProperty("delivery").GetString() == "not_sent" && !reply.GetProperty("sequence_consumed").GetBoolean() &&
                    wire.PasteWrites == 0 && terminal.AcceptsInput && !tab.Reports.HasUnread &&
                    !FindControls<TextBlock>(tab.Notices).Any(text => text.Text.Contains(App.Text("AiUncertainDelivery"), StringComparison.Ordinal)),
                    "Returning control wrote an unstarted paste or reported it as uncertain.");
            }
            finally
            {
                terminal.InputSendQueued -= InputQueued;
                wire.ReleaseReply.TrySetResult();
                ai.EndSharing(tab);
                ai.Detach(peer);
                typeof(TerminalTab).GetProperty(nameof(TerminalTab.IsRunning))!.SetValue(tab, false);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Closed += (_, _) => closed.TrySetResult();
                host.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task CheckAiPasteTakeoverDeliveryAsync(App app, string directory)
    {
        const string input = "ABC";
        foreach (int accepted in new[] { 1, input.Length })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token = deadline.Token;
            var host = new MainWindow(app, () => true);
            var tab = new TerminalTab(Path.Combine(directory, "ai-paste-takeover-delivery.ttty"));
            using var wire = new TakeoverPasteWire(accepted);
            await using var terminal = new TerminalSession(wire, ConnectionProfile.CreateTelnet("Paste takeover", "127.0.0.1"), null, 100);
            typeof(TerminalTab).GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(tab, terminal);
            host.AddTerminalTab(tab);
            host.Show();
            host.SelectNotificationTab(tab);
            var ai = app.Ai;
            var peer = ai.Attach("paste takeover fixture", @"C:\Fixtures\PasteTakeover.exe", 304)!;
            try
            {
                Check(ai.Share(tab, peer) is null && ai.Grant(tab) is null, "The paste takeover fixture could not obtain input control.");
                string session = tab.AiSharing!.Id, control = tab.AiSharing.Control!.Id;
                Task<JsonElement> sending;
                while (true)
                {
                    string observation = (await CommitAiToolAsync(ai, peer, "tabtty_read_state", new { session }, token))
                        .GetProperty("observation").GetString()!;
                    sending = CommitAiToolAsync(ai, peer, "tabtty_send_input",
                        new { session, control, action_seq = 1, observation, input = new { kind = "paste", text = input } }, token);
                    if (await Task.WhenAny(sending, wire.Accepted.Task).WaitAsync(token) == wire.Accepted.Task) break;
                    var previous = await sending;
                    Check(previous.GetProperty("error").GetProperty("code").GetString() == "stale_observation",
                        "The paste finished before the first write.");
                }

                using var releaseCancellation = new ManualResetEventSlim();
                var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Register after the writer starts so this callback runs before the linked paste token is cancelled.
                using var registration = tab.AiSharing.Control!.Lease.Token.Register(() =>
                {
                    cancellationStarted.TrySetResult();
                    releaseCancellation.Wait(token);
                });
                try
                {
                    ai.EndControl(tab);
                    await cancellationStarted.Task.WaitAsync(token);
                    wire.ReleaseWrite.TrySetResult();
                    var reply = await sending.WaitAsync(token);
                    bool complete = accepted == input.Length;
                    Check(reply.GetProperty("delivery").GetString() == (complete ? "submitted" : "possibly_partial") &&
                        reply.GetProperty("sequence_consumed").GetBoolean() && wire.Calls == 1 && wire.Content == input[..accepted],
                        "Paste delivery did not reflect the confirmed wire progress during takeover.");
                    var notice = FindControls<TextBlock>(tab.Notices).SingleOrDefault(label =>
                        label.Text.Contains(App.Text("AiUncertainDelivery"), StringComparison.Ordinal));
                    if (complete)
                        Check(notice is null, "A completed paste raised a delivery warning.");
                    else
                    {
                        Check(reply.GetProperty("error").GetProperty("code").GetString() == "send_uncertain" &&
                            notice is not null &&
                            notice.Text.Contains(TerminalTab.BroadcastTargetLabel(terminal), StringComparison.Ordinal) &&
                            notice.Text.Contains(string.Format(App.Text("ConfirmedInputPrefix"), accepted), StringComparison.Ordinal),
                            "A partial paste did not report the target and confirmed byte count.");
                        FindControls<Button>(notice!.Parent as DependencyObject ?? tab.Notices)
                            .Single(button => Equals(button.Content, App.Text("OK"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(wire.Calls == 1, "Acknowledging the delivery warning replayed input.");
                    }
                }
                finally { releaseCancellation.Set(); }
                while (tab.IsAiTakingControl) await Task.Delay(10, token);
            }
            finally
            {
                wire.ReleaseWrite.TrySetResult();
                ai.EndSharing(tab);
                ai.Detach(peer);
                typeof(TerminalTab).GetProperty(nameof(TerminalTab.IsRunning))!.SetValue(tab, false);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Closed += (_, _) => closed.TrySetResult();
                host.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class TakeoverPasteWire(int accepted) : ISessionWire
    {
        internal TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        internal string Content { get; private set; } = "";

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public async ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (++Calls != 1) throw new InvalidOperationException("Unexpected paste write after takeover.");
            Content = System.Text.Encoding.ASCII.GetString(buffer.Span[..accepted]);
            Accepted.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(token);
            return accepted;
        }
        public void Dispose() { }
    }

    private sealed class HeldPasteReplyWire : ISessionWire
    {
        internal TaskCompletionSource ReplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReplyCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PasteWrites { get; private set; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            "\u001b[5n"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult(4);
        }
        public ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            PasteWrites++;
            return ValueTask.FromResult(buffer.Length);
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            Check(buffer.Span.SequenceEqual("\u001b[0n"u8), "An unexpected reply reached the held writer.");
            ReplyStarted.TrySetResult();
            await ReleaseReply.Task.WaitAsync(token);
            ReplyCompleted.TrySetResult();
        }
        public void Dispose() { }
    }

    private static async Task<JsonElement> CommitAiToolAsync(AiCollaboration ai, AiCollaboration.Counterpart peer,
        string tool, object arguments, CancellationToken token)
    {
        var reply = await ai.CallAsync(peer, tool, JsonSerializer.SerializeToElement(arguments), token);
        JsonElement result = default;
        await ai.CommitReplyAsync(peer, reply, (body, _) => result = JsonSerializer.SerializeToElement(body), token);
        return result;
    }
    private sealed class HistoryClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(long milliseconds) => timestamp += milliseconds;
    }

    private static async Task AiSharesAnExistingTerminal(App app, string directory)
    {
        CheckInputFormOrder();
        PipeIdentityIncludesIntegrityLevel();
        Console.WriteLine("PASS PipeIdentityIncludesIntegrityLevel");
        await OwnedPipeSurvivesEarlyClientDisconnect();
        await CheckMcpAdmissionContracts();
        using (var localPipe = new System.IO.Pipes.NamedPipeClientStream("TabTty-closed-channel-test"))
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
        {
            var channel = new TabTty.AiTransport.AiPrivateChannel(localPipe, self.SafeHandle);
            channel.Dispose();
            Check(!channel.TrySend(new { revoked = "ended" }, priority: true),
                "Revoking a share after pipe shutdown should leave local takeover available.");
            channel.Stop();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string path = Path.Combine(directory, "ai-session.ttty");
        await ConnectionProfile.CreateTelnet("AI fixture", "127.0.0.1", port).SaveNewAsync(path, token);
        var window = new MainWindow(app);
        var tab = new TerminalTab(path);
        window.Show();
        window.AddTerminalTab(tab);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Connected += _ => ready.TrySetResult();
        var ai = app.Ai;
        var owner = ai.Attach("fixture bridge", @"C:\Fixtures\Selected\Host.exe", 101)!;
        var stranger = ai.Attach("fixture bridge", @"C:\Fixtures\Other\Host.exe", 202)!;
        try
        {
            owner.Name = PermissionDisplay.SingleLine("_Client\u2028\u2029\u202e名");
            stranger.Name = owner.Name;
            var aiMenu = new ContextMenu();
            tab.FillAiMenu(aiMenu);
            var picker = (MenuItem)aiMenu.Items[0];
            Check(owner.Name == "_Client名" && new[] { owner, stranger }.All(recipient =>
                picker.Items.OfType<MenuItem>().Any(item => item.Header is TextBlock label &&
                    label.Text.StartsWith(recipient.Label + "\n", StringComparison.Ordinal) &&
                    label.Text.Contains(string.Format(App.Text("AiLauncherProcess"), recipient.LauncherPath, recipient.LauncherId)))),
                "The picker must separate the claimed name from the OS-observed launcher and process ID.");
            Check(PermissionDisplay.Literal("記録\u202E名\u2028末") == "記録<U+202E>名<U+2028>末",
                "The permission dialog must visibly distinguish identity text containing display controls.");
            tab.Start();
            using var connection = await listener.AcceptTcpClientAsync(token);
            NetworkStream wire = connection.GetStream();
            await wire.ReadExactlyAsync(new byte[15], token);
            await ready.Task.WaitAsync(token);
            window.UpdateLayout();
            byte[] history = Encoding.UTF8.GetBytes(
                "earlier あ\r\nsecond\r\n\u001b[?1049h\u001b[?2004hSCREEN\u001b[s\u001b[2;1H\u001b[31m\uFEFFあ\u001b[0m\u001b[u");
            await wire.WriteAsync(history, token);
            await WaitForScreen();

            var empty = await Call(owner, "list_shared_sessions", new { });
            Check(empty.GetProperty("sessions").GetArrayLength() == 0, "An unshared terminal was listed.");
            Check(empty.GetProperty("pairing_code").GetString() == owner.Code && owner.Code != stranger.Code,
                "The integration cannot be distinguished by its pairing code.");
            app.Broadcast.Start(window);
            Check(ai.Share(tab, owner) == "AiBroadcastTarget", "An active checked broadcast target was shared.");
            var group = (TabControl)window.FindName("tabs");
            var terminalItem = group.SelectedItem;
            window.OpenForm("Settings", _ => { });
            window.UpdateLayout();
            var form = (FormView)group.SelectedContent;
            var broadcastBand = (TextBlock)window.FindName("broadcastStatus");
            Check(broadcastBand.IsVisible && broadcastBand.Text == app.Broadcast.Status(window) &&
                !FindControls<TextBlock>(form.Notices).Any(label => label.Text.Contains(broadcastBand.Text, StringComparison.Ordinal)),
                "Broadcast status must stay in the window's right side while a settings tab is selected.");
            group.SelectedItem = terminalItem;
            window.UpdateLayout();
            Check(broadcastBand.IsVisible && !FindControls<TextBlock>(tab).Any(label => label.Text == broadcastBand.Text),
                "Switching back to the terminal hid or duplicated the window's broadcast status.");
            app.Broadcast.Stop(window);
            group.SelectedItem = group.Items.OfType<TabItem>().Single(item => item.Content == form);
            await Decide(owner, grant: false, approve: false, background: true);
            group.SelectedItem = terminalItem;
            Check(!tab.IsAiShared, "Cancel disclosed output.");
            window.UpdateLayout();
            var areaBeforeSharing = tab.TerminalArea.RenderSize;
            await Decide(owner, grant: false, approve: true);
            window.UpdateLayout();
            Check(tab.TerminalArea.RenderSize == areaBeforeSharing, "AI status changed the terminal area.");
            var connectionStatus = FindControls<TextBlock>(tab).Single(label =>
                label.Text.Contains(App.Text("TelnetUnencrypted"), StringComparison.Ordinal));
            var aiStatus = FindControls<TextBlock>(tab).Single(label => label.Text == App.Text("AiReadOnly"));
            Check(connectionStatus.TranslatePoint(new Point(), tab.TerminalArea).Y >= areaBeforeSharing.Height &&
                aiStatus.TranslatePoint(new Point(), tab.TerminalArea).Y >= areaBeforeSharing.Height &&
                connectionStatus.Parent is DockPanel { Height: 30 } footer &&
                connectionStatus.VerticalAlignment == VerticalAlignment.Center && aiStatus.VerticalAlignment == VerticalAlignment.Center &&
                ReferenceEquals(footer, aiStatus.Parent),
                "Connection and AI status must share one compact row below the terminal.");
            AiAction("AiEndSharing");
            Check(tab.AiSharing?.Peer == owner && !tab.IsAiControlling && !tab.BroadcastChecked,
                "Confirmed sharing changed input ownership or retained a broadcast check.");
            await Decide(stranger, grant: false, approve: false);
            Check(tab.AiSharing?.Peer == owner, "Cancelling a switch revoked the prior recipient.");
            await Decide(owner, grant: true, approve: true, () =>
            {
                ai.EndSharing(tab);
                Check(ai.Share(tab, owner) is null, "Failed to create a new sharing generation.");
            });
            Check(!tab.IsAiControlling, "A stale dialog gave control to a new sharing generation.");
            app.Broadcast.Start(window);
            app.Broadcast.SetAll(window, true);
            Check(!tab.BroadcastChecked, "Broadcast selected a shared tab.");
            app.Broadcast.Stop(window);

            string session = tab.AiSharing!.Id;
            var denied = await Call(stranger, "read_state", new { session });
            Check(Code(denied) == "not_shared" && !denied.TryGetProperty("session", out _), "A foreign handle disclosed data.");
            var live = await Call(owner, "read_state", new { session });
            Check(live.GetProperty("source").GetString() == "alternate", "The live alternate screen was not exposed.");
            var clipped = await Call(owner, "read_state", new { session, region = new { row = 2, column = 2, rows = 1, columns = 1 } });
            var wideCell = clipped.GetProperty("lines")[0].GetProperty("cells")[0];
            Check(wideCell.GetProperty("column").GetInt32() == 1 && wideCell.GetProperty("width").GetInt32() == 2 &&
                wideCell.GetProperty("text").GetString() == "\uFEFFあ" && wideCell.GetProperty("foreground").GetUInt32() == 0xCD0000,
                "A clipped screen capture did not preserve the wide cell, format mark, or foreground.");
            var old = await Call(owner, "read_history", new { session });
            Check(old.GetRawText().Contains("earlier", StringComparison.Ordinal) &&
                !old.GetRawText().Contains("SCREEN", StringComparison.Ordinal), "Parked history used the wrong screen.");
            var search = await Call(owner, "search_history", new { session, query = "あ" });
            Check(search.GetProperty("matches").GetArrayLength() == 1, "Literal Japanese search missed retained output.");
            var firstLine = old.GetProperty("fragments").EnumerateArray().First(fragment =>
                fragment.GetProperty("text").GetString()!.Contains("earlier", StringComparison.Ordinal));
            Check(firstLine.GetProperty("logical_line_id").GetInt64() ==
                search.GetProperty("matches")[0].GetProperty("logical_line_id").GetInt64(),
                "Read and search disagree on the public identity of one retained line.");
            var context = search.GetProperty("matches")[0].GetProperty("context_range").GetString();
            var contextPage = await Call(owner, "read_history", new { session, range = context, max_rows = 1 });
            string pageCursor = contextPage.GetProperty("next_cursor").GetString()!;
            int liveHandles = owner.Handles.Count;
            var staged = await ai.CallAsync(owner, "tabtty_read_history",
                JsonSerializer.SerializeToElement(new { session, cursor = pageCursor }), token);
            JsonElement unpublished = default;
            await ai.PublishReplyAsync(owner, staged, (body, accepted) =>
            {
                Check(accepted, "The fixture did not reach queue admission.");
                unpublished = JsonSerializer.SerializeToElement(body);
                return false;
            }, token);
            string? undisclosed = unpublished.GetProperty("range").GetString();
            Check(owner.Handles.Count == liveHandles && undisclosed is not null && !owner.Handles.ContainsKey(undisclosed),
                "Queue rejection activated successor IDs or affected live handles.");
            var continuation = await Call(owner, "read_history", new { session, cursor = pageCursor });
            Check(continuation.GetProperty("fragments")[0].GetProperty("text").GetString() == "second",
                "History continuation did not resume at the next retained row.");

            string beforeChange = live.GetProperty("observation").GetString()!;
            Task<JsonElement> wait = Call(owner, "wait", new { session, kind = "change", observation = beforeChange, timeout_ms = 3000 });
            await wire.WriteAsync("!"u8.ToArray(), token);
            Check((await wait).GetProperty("outcome").GetString() == "changed", "The wait missed a screen change.");

            var surface = FindControls<TerminalSurface>(tab).Single();
            var composition = new DeferredComposition(surface);
            surface.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            Check(ai.Grant(tab) == "AiInputBusy", "Grant interrupted an active IME composition.");
            surface.CancelComposition();

            await Decide(owner, grant: true, approve: false);
            Check(!tab.IsAiControlling, "Cancelling input consent granted control.");
            await Decide(owner, grant: true, approve: true);
            Check(tab.IsAiControlling && tab.InputSession is null, "Grant failed to exclude manual input.");
            window.UpdateLayout();
            Check(tab.TerminalArea.RenderSize == areaBeforeSharing, "The takeover button changed the terminal area.");
            var takeover = AiAction("AiTakeControl");
            tab.ShowSearch();
            window.UpdateLayout();
            var closeSearch = FindControls<Button>(tab).Single(button => Equals(button.Content, App.Text("CloseSearch")));
            var actions = (FrameworkElement)takeover.Parent;
            var searchBar = (FrameworkElement)closeSearch.Parent;
            var actionBounds = new Rect(actions.TranslatePoint(new Point(), tab.TerminalArea), actions.RenderSize);
            var searchBounds = new Rect(searchBar.TranslatePoint(new Point(), tab.TerminalArea), searchBar.RenderSize);
            Check(!actionBounds.IntersectsWith(searchBounds) && tab.TerminalArea.RenderSize == areaBeforeSharing,
                "The search overlay obstructed the floating AI controls or resized the terminal.");
            for (int i = 0; i < 16; i++)
                tab.Notices.ShowReport(i, string.Concat(Enumerable.Repeat("録画ファイルに書き込めませんでした。", 5)), () => { });
            window.UpdateLayout();
            var overflowButton = FindControls<Button>(tab.Notices).Single(button =>
                Equals(button.Content, string.Format(App.Text("NoticeStackShow"), 16)));
            Check(overflowButton.IsVisible && AiAction("AiTakeControl") == takeover && AiAction("AiEndSharing").IsVisible &&
                !new Rect(tab.Notices.TranslatePoint(new Point(), tab.TerminalArea), tab.Notices.RenderSize)
                    .IntersectsWith(new Rect(searchBar.TranslatePoint(new Point(), tab.TerminalArea), searchBar.RenderSize)) &&
                tab.TerminalArea.RenderSize == areaBeforeSharing,
                "Overflow hid the active AI controls, covered search, or changed the terminal size.");
            for (int i = 0; i < 16; i++) tab.Notices.Remove(i);
            closeSearch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string control = tab.AiSharing.Control!.Id;
            string observation = await Observe();
            var first = new { session, control, action_seq = 1, observation, input = new { kind = "key", key = "ArrowUp" } };
            var otherListing = await Call(stranger, "list_shared_sessions", new { });
            Check(otherListing.GetProperty("sessions").GetArrayLength() == 0 &&
                Code(await Call(stranger, "send_input", first)) == "not_shared" &&
                Code(await Call(stranger, "return_control", new { session, control })) == "not_shared" &&
                tab.IsAiControlling && connection.Available == 0,
                "A second integration shared the first integration's input authority.");
            var sent = await Call(owner, "send_input", first);
            Check(sent.GetProperty("delivery").GetString() == "submitted", "A valid terminal key was not submitted.");
            await Bytes("\u001b[A");
            var duplicate = await Call(owner, "send_input", first);
            Check(duplicate.GetProperty("delivery").GetString() == "submitted" && connection.Available == 0,
                "A repeated action produced more terminal bytes.");
            var stale = await Call(owner, "send_input", new { session, control, action_seq = 2, observation,
                input = new { kind = "text", text = "STALE" } });
            Check(Code(stale) == "stale_observation" && connection.Available == 0, "Old observations can send a second action.");

            observation = await Observe();
            var textSent = await Call(owner, "send_input", new { session, control, action_seq = 2, observation,
                input = new { kind = "text", text = "A" } });
            Check(textSent.GetProperty("delivery").GetString() == "submitted", "Ordinary AI text was not submitted.");
            await Bytes("A");
            observation = await Observe();
            Task<JsonElement> paste = Call(owner, "send_input", new { session, control, action_seq = 3, observation,
                input = new { kind = "paste", text = "AB\nC", character_delay_ms = 10000 } });
            await Bytes("\u001b[200~A");
            var backgroundItem = group.Items.OfType<TabItem>().Single(item => item.Content == form);
            group.SelectedItem = backgroundItem;
            takeover.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(tab.IsAiShared && !tab.IsAiControlling, "Takeover ended reading or retained the grant.");
            await Bytes("\u001b[201~");
            var interrupted = await paste;
            Check(interrupted.GetProperty("delivery").GetString() == "possibly_partial",
                "A cancelled transmitted prefix was described as definitely unsent.");
            string prefix = string.Format(App.Text("ConfirmedInputPrefix"), 1);
            Window? incident = window.OwnedWindows.OfType<Window>().SingleOrDefault(dialog =>
                FindControls<DialogText>(dialog).Any(value => value.IsVisible && value.Text.Contains(prefix, StringComparison.Ordinal)));
            Check(incident is not null && tab.Reports.HasUnread && group.SelectedItem == backgroundItem,
                "The confirmed input prefix was absent from the background incident or changed selection.");
            FindControls<Button>(incident!).Single(button => Equals(button.Content, App.Text("OK")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!tab.Reports.HasUnread && !incident!.IsVisible, "Acknowledgement did not clear the same delivery incident.");
            group.SelectedItem = terminalItem;
            while (tab.IsAiTakingControl) await Task.Delay(10, token);
            Check(tab.InputSession is not null && connection.Available == 0, "Takeover sent the cancelled tail or blocked manual input.");

            Check(ai.Grant(tab) is null, "The user could not grant control again.");
            string currentControl = tab.AiSharing.Control!.Id;
            Check(Code(await Call(owner, "return_control", new { session, control })) == "control_required" && tab.IsAiControlling,
                "Returning an old grant revoked the current one.");
            observation = await Observe();
            await tab.AiSession!.ResizeAsync(45, 12, token);
            await tab.AiSession.ResizeAsync(live.GetProperty("columns").GetInt32(), live.GetProperty("rows").GetInt32(), token);
            var obsolete = await Call(owner, "send_input", new { session, control = currentControl, action_seq = 1, observation,
                input = new { kind = "text", text = "STALE" } });
            Check(Code(obsolete) == "stale_observation", "A dimension change that was undone reused an old observation.");
            const string handoff = "Review the result";
            await Call(owner, "return_control", new { session, control = currentControl, reason = handoff });
            string attributed = string.Format(App.Text("AiHandoffMessage"), handoff);
            window.UpdateLayout();
            Check(tab.TerminalArea.RenderSize == areaBeforeSharing, "Handoff details changed the terminal area.");
            Check(tab.IsAiShared && !tab.IsAiControlling &&
                FindControls<TextBlock>(tab).Any(label => label.ToolTip is string details &&
                    details.EndsWith("\n" + attributed, StringComparison.Ordinal) && label.Focusable),
                "The AI's handoff message must be distinct from TabTty status while reading continues.");
            AiCollaboration.Reply read = await ai.CallAsync(owner, "tabtty_read_state",
                JsonSerializer.SerializeToElement(new { session }), token);
            Check(ai.CanDeliver(owner, read), "The authorized result could not be committed.");
            window.UpdateLayout();
            var endSharing = AiAction("AiEndSharing");
            endSharing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!ai.CanDeliver(owner, read) && !tab.BroadcastChecked, "Sharing end permitted disclosure or restored a broadcast check.");
            Check(Code(await Call(owner, "read_history", new { session })) == "not_shared", "The ended share remained readable.");
            await ChangedSearchCannotCommit();
            await PublicationChecksTheLiveSourceAndAuthority();
            await CheckControlReturnBudget();
            await ReadFromPausedMacro();
            Check(ai.Share(tab, owner) is null && ai.Grant(tab) is null, "The original integration could not obtain a fresh grant.");
            string closingSession = tab.AiSharing!.Id;
            string closingControl = tab.AiSharing.Control!.Id;
            int fixtures = 0;
            while (owner.Handles.Count < 256)
                owner.Handles.Add($"fixture-{fixtures++}", new AiCollaboration.Handle(null, long.MaxValue, new object()));
            string oldest = owner.Handles.Keys.First();
            var lastState = await Call(owner, "read_state", new { session = closingSession });
            Check(owner.Handles.Count <= 256 && owner.Handles.ContainsKey(lastState.GetProperty("observation").GetString()!) &&
                !owner.Handles.ContainsKey(oldest), "A screen read at capacity returned an evicted observation.");
            ai.Detach(owner);
            var reattached = ai.Attach("fixture bridge", owner.LauncherPath, owner.LauncherId)!;
            try
            {
                reattached.Name = owner.Name;
                var listing = await Call(reattached, "list_shared_sessions", new { });
                var oldInput = new { session = closingSession, control = closingControl, action_seq = 1,
                    observation = lastState.GetProperty("observation").GetString(), input = new { kind = "key", key = "ArrowUp" } };
                Check(!tab.IsAiShared && reattached.Id != owner.Id && listing.GetProperty("sessions").GetArrayLength() == 0 &&
                    Code(await Call(reattached, "read_state", new { session = closingSession })) == "not_shared" &&
                    Code(await Call(reattached, "send_input", oldInput)) == "not_shared" && connection.Available == 0,
                    "Reattaching the same integration restored a previous session or input grant.");
                Console.WriteLine("PASS McpIntegrationGrantsAreIsolatedAndExpireOnDetach");
            }
            finally { ai.Detach(reattached); }

            Button AiAction(string key)
            {
                var button = FindControls<Button>(tab).Single(candidate =>
                    Equals(candidate.Content, App.Text(key)) && candidate.IsVisible);
                Point position = button.TranslatePoint(new Point(), tab.TerminalArea);
                Check(position.X >= 0 && position.Y >= 0 && position.Y < tab.TerminalArea.ActualHeight / 2 &&
                    position.X + button.ActualWidth <= tab.TerminalArea.ActualWidth + 0.01 &&
                    button.Focusable && System.Windows.Automation.AutomationProperties.GetName(button) == App.Text(key),
                    "The AI action must be a keyboard-accessible overlay inside the terminal.");
                return button;
            }

            async Task Decide(AiCollaboration.Counterpart recipient, bool grant, bool approve,
                Action? beforeDecision = null, bool background = false)
            {
                var menu = new ContextMenu();
                tab.FillAiMenu(menu);
                string title = App.Text(grant ? "AiGiveControl" : "AiShareOutput");
                MenuItem operation = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title));
                if (!grant) operation = operation.Items.OfType<MenuItem>().Single(
                    item => item.Header is TextBlock label && label.Text.StartsWith(recipient.Label + "\n", StringComparison.Ordinal));
                var review = window.Dispatcher.InvokeAsync(() =>
                {
                    Window dialog = app.Windows.OfType<Window>().Single(candidate => candidate.Owner == window && candidate.Title == title);
                    try
                    {
                        var buttons = FindControls<Button>(dialog).ToArray();
                        Check(tab.HasPendingDecision && window.IsEnabled && (!background ||
                            !dialog.IsActive && !dialog.ShowActivated && group.SelectedContent == form),
                            "AI consent took over a different view or failed to identify its pending tab.");
                        Check(buttons.Single(button => button.IsDefault).IsCancel, "Consent should default to Cancel.");
                        var values = FindControls<DialogText>(dialog).Select(field => field.Text).ToArray();
                        Check(values.Contains(tab.TabCaption) && values.Contains(recipient.Code) &&
                            values.Contains($"127.0.0.1:{port}") && values.Contains(recipient.LauncherPath) &&
                            values.Contains(recipient.LauncherId.ToString(System.Globalization.CultureInfo.CurrentCulture)) &&
                            FindControls<TextBlock>(dialog).Any(label => label.Text == App.Text("AiUnattendedConsent")),
                            "Consent did not identify the session destination, recipient and unattended access.");
                        beforeDecision?.Invoke();
                        buttons.Single(button => Equals(button.Content, approve ? title : App.Text("Cancel")))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                }, DispatcherPriority.Background);
                operation.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await review.Task.WaitAsync(token);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!tab.HasPendingDecision, "An answered consent remained pending.");
            }

            async Task ChangedSearchCannotCommit()
            {
                await wire.WriteAsync("\u001b[?1049l\rBEFORE"u8.ToArray(), token);
                await WaitForNormal("BEFORE");
                Check(ai.Share(tab, owner) is null, "The existing terminal could not be shared again.");
                string sharedSession = tab.AiSharing!.Id;
                foreach (string invalid in new[] { "a\rb", "a\nb", "a\r\nb", string.Concat(Enumerable.Repeat("😀", 513)) })
                    Check(Code(await Search(invalid)) == "invalid_request", "History search accepted an invalid query.");
                Check((await Search(string.Concat(Enumerable.Repeat("😀", 512)))).GetProperty("status").GetString() == "ok",
                    "History search rejected exactly 1,024 UTF-16 code units.");
                foreach (string separator in new[] { "\u0085", "\u2028", "\u2029" })
                    Check((await Search(separator)).GetProperty("status").GetString() == "ok",
                        "History search rejected a literal Unicode separator.");
                var captured = await ai.CallAsync(owner, "tabtty_search_history",
                    JsonSerializer.SerializeToElement(new { session = sharedSession, query = "BEFORE" }), token);
                await wire.WriteAsync("\rAFTER "u8.ToArray(), token);
                await WaitForNormal("AFTER ");
                await wire.WriteAsync("\rBEFORE"u8.ToArray(), token);
                await WaitForNormal("BEFORE");
                JsonElement result = default;
                await ai.CommitReplyAsync(owner, captured, (body, _) => result = JsonSerializer.SerializeToElement(body), token);
                Check(Code(result) == "range_changed", "A changed search page was committed to the bridge.");
                await wire.WriteAsync(Encoding.UTF8.GetBytes("\r\nあb"), token);
                await WaitForNormal("あb");
                var tail = await Call(owner, "read_history", new { session = sharedSession, max_rows = 1, max_text_bytes = 1 });
                Check(tail.GetProperty("fragments")[0].GetProperty("text").GetString() == "b", "A short AI tail did not return its last character.");
                string earlier = tail.GetProperty("earlier_range").GetString()!;
                Check(Code(await Call(owner, "read_history", new { session = sharedSession, range = earlier, max_text_bytes = 1 })) ==
                    "resource_limit", "An undersized backward read consumed the earlier range.");
                var retried = await Call(owner, "read_history", new { session = sharedSession, range = earlier, max_text_bytes = 3 });
                Check(retried.GetProperty("fragments")[0].GetProperty("text").GetString() == "あ", "The earlier range could not be retried.");
                await wire.WriteAsync("\r\n@QQ"u8.ToArray(), token);
                await WaitForNormal("@QQ");
                var found = await Call(owner, "search_history", new { session = sharedSession, query = "Q", limit = 1, context_lines = 0 });
                string searchCursor = found.GetProperty("next_cursor").GetString()!;
                await wire.WriteAsync("\r\nNEW"u8.ToArray(), token);
                await WaitForNormal("NEW");
                var resumed = await Call(owner, "search_history", new { session = sharedSession, cursor = searchCursor });
                string scannedRange = resumed.GetProperty("scanned_range").GetString()!;
                var scanned = await Call(owner, "read_history", new { session = sharedSession, range = scannedRange });
                var last = scanned.GetProperty("fragments").EnumerateArray().Last();
                Check(last.GetProperty("text").GetString() == "Q" && !last.GetProperty("line_end").GetBoolean(),
                    "A resumed search disclosed a hard ending written after capture.");
                ai.EndSharing(tab);

                async Task<JsonElement> Search(string query)
                {
                    var reply = await ai.CallAsync(owner, "tabtty_search_history",
                        JsonSerializer.SerializeToElement(new { session = sharedSession, query }), token);
                    JsonElement body = default;
                    await ai.CommitReplyAsync(owner, reply, (value, _) => body = JsonSerializer.SerializeToElement(value), token);
                    return body;
                }

                async Task WaitForNormal(string expected)
                {
                    while (true)
                    {
                        var state = await tab.AiSession!.ObserveAiAsync((model, info) =>
                        {
                            var page = TerminalHistory.Read(model, info, null, null, 1, 64 * 1024,
                                TerminalHistory.PageOutputBudget, token);
                            return (info.IsAlternate, page.Fragments.LastOrDefault()?.text);
                        }, token);
                        if (!state.IsAlternate && state.Item2 == expected) return;
                        await Task.Delay(10, token);
                    }
                }
            }

            async Task PublicationChecksTheLiveSourceAndAuthority()
            {
                var clock = new HistoryClock();
                using var collab = new AiCollaboration(app, clock);
                var peer = collab.Attach("controlled publication", @"C:\Fixtures\History\Host.exe", 303)!;
                Check(collab.Share(tab, peer) is null, "The fixture could not share a retained terminal.");
                string session = tab.AiSharing!.Id;
                var page = await Prepare(new { session });
                string range = (await Publish(page)).GetProperty("range").GetString()!;
                long expiry = peer.Handles[range].Expires;
                while (peer.Handles.Count < 256)
                    peer.Handles.Add(Guid.NewGuid().ToString("N"), new AiCollaboration.Handle(tab.AiSharing, expiry, new object()));
                string[] live = peer.Handles.Keys.ToArray();
                var pending = await Prepare(new { session, range });
                await collab.PublishReplyAsync(peer, pending, (_, accepted) =>
                {
                    Check(accepted, "The fixture did not reach the final queue.");
                    return false;
                }, token);
                Check(live.SequenceEqual(peer.Handles.Keys),
                    "A queue rejection evicted live IDs from a full registry.");

                pending = await Prepare(new { session, range });
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try
                    {
                        await collab.PublishReplyAsync(peer, pending, (_, _) =>
                            throw new InvalidOperationException("A cancelled reply was divulged."), cancelled.Token);
                        throw new InvalidOperationException("Publication ignored cancellation.");
                    }
                    catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
                }
                Check(live.SequenceEqual(peer.Handles.Keys), "Cancellation changed the live registry.");
                pending = await Prepare(new { session, range });
                clock.Advance(60_001);
                Check(Code(await Publish(pending)) == "cursor_expired", "The fixed source expiry was bypassed.");
                pending = await Prepare(new { session });
                collab.EndSharing(tab);
                Check(Code(await Publish(pending)) == "not_shared", "Revocation disclosed the prepared page.");

                Task<AiCollaboration.Reply> Prepare(object request) =>
                    collab.CallAsync(peer, "tabtty_read_history", JsonSerializer.SerializeToElement(request), token);
                async Task<JsonElement> Publish(AiCollaboration.Reply reply)
                {
                    JsonElement result = default;
                    await collab.CommitReplyAsync(peer, reply, (body, _) => result = JsonSerializer.SerializeToElement(body), token);
                    return result;
                }
            }

            async Task CheckControlReturnBudget()
            {
                var clock = new HistoryClock();
                using var collab = new AiCollaboration(app, clock);
                var peer = collab.Attach("return budget", @"C:\Fixtures\Host.exe", 304)!;
                var other = collab.Attach("independent budget", @"C:\Fixtures\Host.exe", 305)!;
                Check(collab.Share(tab, peer) is null && collab.Grant(tab) is null, "Could not grant input.");
                string session = tab.AiSharing!.Id;
                string control = tab.AiSharing.Control!.Id;
                Check(Code(await Return(peer, "unshared")) == "not_shared", "Unknown return disclosed a session.");
                // One handoff and six idempotent acknowledgements use the remaining burst.
                for (int i = 0; i < 7; i++)
                    Check(Code(await Return(peer, session)) is null, "A return within the burst failed.");
                await WaitForTakeover();
                Check(collab.Grant(tab) is null, "Could not grant input again.");
                control = tab.AiSharing.Control!.Id;
                var limited = await Return(peer, session);
                Check(Code(limited) == "busy" && limited.GetProperty("error").GetProperty("retry_after_ms").GetInt32() == 250 &&
                    tab.IsAiControlling, "A limited return changed a grant or gave an incorrect retry delay.");
                Check(Code(await Return(other, session)) == "not_shared", "One peer consumed another's return budget.");
                var listing = await collab.CallAsync(peer, "tabtty_list_shared_sessions",
                    JsonSerializer.SerializeToElement(new { }), token);
                Check(JsonSerializer.SerializeToElement(listing.Body).GetProperty("status").GetString() == "ok",
                    "Returning control consumed the ordinary-call budget.");
                collab.EndControl(tab);
                Check(!tab.IsAiControlling, "Local takeover waited for an integration's return budget.");
                clock.Advance(249);
                limited = await Return(peer, session);
                Check(Code(limited) == "busy" && limited.GetProperty("error").GetProperty("retry_after_ms").GetInt32() == 1,
                    "The return budget refilled too early.");
                clock.Advance(1);
                Check(Code(await Return(peer, session)) is null, "The return budget did not recover at the boundary.");
                await WaitForTakeover();

                async Task<JsonElement> Return(AiCollaboration.Counterpart counterpart, string target)
                {
                    var reply = await collab.CallAsync(counterpart, "tabtty_return_control",
                        JsonSerializer.SerializeToElement(new { session = target, control }), token);
                    return JsonSerializer.SerializeToElement(reply.Body);
                }

                async Task WaitForTakeover()
                {
                    while (tab.IsAiTakingControl) await Task.Delay(10, token);
                }
            }

            async Task ReadFromPausedMacro()
            {
                string source = Path.Combine(directory, "ai-macro.tttym");
                await File.WriteAllTextAsync(source, """
                    #tttym 1
                    Connect("ai-session.ttty");
                    WaitPrompt(Literal("MACRO-DONE"));
                    """, token);
                Task decide = DecideMacroRequest(app, window, source, App.Text("MacroNoArguments"), "MacroRunOnce");
                await window.OpenExternalMacroAsync(new InstanceLaunch(source, []));
                await decide;
                TerminalTab macroTab = window.TerminalTabs.Single(candidate => candidate.Macro is not null);
                using var macroConnection = await listener.AcceptTcpClientAsync(token);
                await macroConnection.GetStream().ReadExactlyAsync(new byte[15], token);
                while (macroTab.Macro!.Status.State != MacroRunState.Waiting)
                    await Task.Delay(10, token);
                macroTab.Macro.Pause();
                while (macroTab.Macro.Status.State != MacroRunState.Paused)
                    await Task.Delay(10, token);
                try
                {
                    Check(ai.Share(macroTab, owner) is null, "A paused macro could not be observed.");
                    string macroSession = macroTab.AiSharing!.Id;
                    var readable = await Call(owner, "read_state", new { session = macroSession });
                    Check(readable.GetProperty("status").GetString() == "ok", "The paused macro's terminal could not be read.");
                    Check(ai.Grant(macroTab) == "AiMacroOwnsInput", "AI control was granted during a paused macro.");
                }
                finally
                {
                    ai.EndSharing(macroTab);
                    macroTab.Disconnect();
                    await macroTab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            async Task WaitForScreen()
            {
                while (true)
                {
                    var state = await tab.AiSession!.ObserveAiAsync((model, info) =>
                    {
                        var text = new StringBuilder();
                        model.ReadAiScreenRow(0, 1, 6, cell => text.Append(cell.Text));
                        return (info.IsAlternate, text.ToString());
                    }, token);
                    if (state.IsAlternate && state.Item2 == "SCREEN") return;
                    await Task.Delay(10, token);
                }
            }
            async Task<string> Observe() => (await Call(owner, "read_state", new { session }))
                .GetProperty("observation").GetString()!;
            async Task Bytes(string expected)
            {
                byte[] actual = new byte[Encoding.UTF8.GetByteCount(expected)];
                await wire.ReadExactlyAsync(actual, token);
                Equal(Encoding.UTF8.GetString(actual), expected);
            }
        }
        finally
        {
            ai.Detach(owner);
            ai.Detach(stranger);
            tab.Disconnect();
            await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (!window.IsClosing) window.Close();
        }

        Task<JsonElement> Call(AiCollaboration.Counterpart peer, string tool, object arguments) =>
            CommitAiToolAsync(ai, peer, "tabtty_" + tool, arguments, token);
        static string? Code(JsonElement result) => result.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString() : null;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckInputFormOrder()
    {
        var schema = TabTty.AiTransport.McpArguments.Schemas["tabtty_send_input"]
            .GetProperty("properties").GetProperty("input");
        var reversed = JsonSerializer.SerializeToElement(new { oneOf = schema.GetProperty("oneOf").EnumerateArray().Reverse() });
        foreach (var (kind, action) in new (string, string?)[]
        {
            ("text", null), ("paste", null), ("key", null), ("mouse", "press"), ("mouse", "release"),
            ("mouse", "wheel"), ("mouse", "move"),
        })
        {
            if (!TabTty.AiTransport.McpArguments.TrySelectInputForm(schema, kind, action, out var published) ||
                !TabTty.AiTransport.McpArguments.TrySelectInputForm(reversed, kind, action, out var reordered) ||
                !JsonElement.DeepEquals(published, reordered))
                throw new InvalidOperationException("Schema order changed the input definition.");
        }
        if (TabTty.AiTransport.McpArguments.TrySelectInputForm(reversed, "mouse", "unknown", out _))
            throw new InvalidOperationException("An unsupported mouse action selected an input definition.");
        Console.WriteLine("PASS McpInputFormOrder");
    }

    private static async Task CheckMcpAdmissionContracts()
    {
        using var lease = new AiInputLease();
        bool expired = true;
        int writes = 0;
        var attempt = new AiInputAttempt(lease, 1, CancellationToken.None, () => expired);
        try
        {
            await lease.StartWrite(attempt, Write);
            throw new InvalidOperationException("An expired observation reached admission.");
        }
        catch (AiInputRejectedException error) when (error.Code == "stale_observation")
        {
            CheckMcpContract(!attempt.Admitted && writes == 0, "Rejecting an expired observation changed admission state.");
        }
        expired = false;
        var fresh = new AiInputAttempt(lease, 1, CancellationToken.None, () => expired);
        await lease.StartWrite(fresh, Write);
        expired = true;
        await lease.StartWrite(fresh, Write);
        CheckMcpContract(fresh.Admitted && writes == 2, "An admitted action did not retain its admission across writes.");
        Console.WriteLine("PASS McpObservationExpiryAtFirstAdmission");

        using var ordinary = TerminalHistory.WorkQuota.Reserve(60 * 1024 * 1024);
        using var control = TerminalHistory.ControlWorkQuota.Reserve(4 * 1024 * 1024);
        CheckMcpContract(ordinary.Size + control.Size == 64L * 1024 * 1024, "Control did not retain its share of the work budget.");
        Console.WriteLine("PASS McpControlWorkRetainsCapacity");

        using var pipe = new System.IO.Pipes.NamedPipeClientStream("TabTty-buffer-lifecycle-fixture");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        using var channel = new TabTty.AiTransport.AiPrivateChannel(pipe, self.SafeHandle);
        const int ordinaryLimit = 8 * 1024 * 1024 - 512 * 1024;
        CheckMcpContract(channel.Payload.TryReserve(ordinaryLimit, priority: false), "The ordinary payload allowance was unavailable.");
        CheckMcpContract(!channel.TrySend(new { message = "ordinary" }), "A private frame bypassed the shared payload budget.");
        var owner = new CountedReservation();
        CheckMcpContract(channel.TrySend(new byte[16], priority: true, owner), "Ordinary work exhausted reserved control capacity.");
        channel.Stop();
        channel.Payload.Release(ordinaryLimit);
        CheckMcpContract(owner.Releases == 1 && channel.Payload.TryReserve(ordinaryLimit, priority: false),
            "Stopping before the writer started failed to release its queued reservation exactly once.");
        channel.Payload.Release(ordinaryLimit);
        Console.WriteLine("PASS McpSharedPayloadAndUnstartedWriterCleanup");

        ValueTask Write()
        {
            writes++;
            return ValueTask.CompletedTask;
        }
    }

    private static void CheckMcpContract(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountedReservation : IDisposable
    {
        internal int Releases { get; private set; }
        public void Dispose() => Releases++;
    }
}
