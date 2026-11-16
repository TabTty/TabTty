using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private sealed class HistoryClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(long milliseconds) => timestamp += milliseconds;
    }

    private static async Task AiSharesAnExistingTerminal(App app, string directory)
    {
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
        var owner = ai.Attach("fixture bridge")!;
        var stranger = ai.Attach("fixture bridge")!;
        try
        {
            tab.Start();
            using var connection = await listener.AcceptTcpClientAsync(token);
            NetworkStream wire = connection.GetStream();
            await wire.ReadExactlyAsync(new byte[15], token);
            await ready.Task.WaitAsync(token);
            window.UpdateLayout();
            long handoffs = NativeTerminalCoreDiagnostics.ManagedHandoffs;
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
            app.Broadcast.Stop(window);
            Check(ai.Share(tab, owner) is null && !tab.BroadcastChecked, "Sharing did not clear the saved check.");
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
            Check(NativeTerminalCoreDiagnostics.ManagedHandoffs == handoffs, "AI screen capture handed off the native terminal.");
            var old = await Call(owner, "read_history", new { session });
            Check(old.GetRawText().Contains("earlier", StringComparison.Ordinal) &&
                !old.GetRawText().Contains("SCREEN", StringComparison.Ordinal), "Parked history used the wrong screen.");
            var search = await Call(owner, "search_history", new { session, query = "あ" });
            Check(search.GetProperty("matches").GetArrayLength() == 1, "Literal Japanese search missed retained output.");
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

            Check(ai.Grant(tab) is null && tab.InputSession is null, "Grant failed to exclude manual input.");
            string control = tab.AiSharing.Control!.Id;
            string observation = await Observe();
            var first = new { session, control, action_seq = 1, observation, input = new { kind = "key", key = "ArrowUp" } };
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
            Task<JsonElement> paste = Call(owner, "send_input", new { session, control, action_seq = 2, observation,
                input = new { kind = "paste", text = "AB\nC", character_delay_ms = 10000 } });
            await Bytes("\u001b[200~A");
            ai.EndControl(tab);
            Check(tab.IsAiShared && !tab.IsAiControlling, "Takeover ended reading or retained the grant.");
            await Bytes("\u001b[201~");
            var interrupted = await paste;
            Check(interrupted.GetProperty("delivery").GetString() == "possibly_partial",
                "A cancelled transmitted prefix was described as definitely unsent.");
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
            AiCollaboration.Reply read = await ai.CallAsync(owner, "tabtty_read_state",
                JsonSerializer.SerializeToElement(new { session }), token);
            Check(ai.CanDeliver(owner, read), "The authorized result could not be committed.");
            ai.EndSharing(tab);
            Check(!ai.CanDeliver(owner, read) && !tab.BroadcastChecked, "Sharing end permitted disclosure or restored a broadcast check.");
            Check(Code(await Call(owner, "read_history", new { session })) == "not_shared", "The ended share remained readable.");
            await ChangedSearchCannotCommit();
            await PublicationChecksTheLiveSourceAndAuthority();
            await ReadFromPausedMacro();

            async Task ChangedSearchCannotCommit()
            {
                await wire.WriteAsync("\u001b[?1049l\rBEFORE"u8.ToArray(), token);
                await WaitForNormal("BEFORE");
                Check(ai.Share(tab, owner) is null, "The existing terminal could not be shared again.");
                string sharedSession = tab.AiSharing!.Id;
                var captured = await ai.CallAsync(owner, "tabtty_search_history",
                    JsonSerializer.SerializeToElement(new { session = sharedSession, query = "BEFORE" }), token);
                await wire.WriteAsync("\rAFTER "u8.ToArray(), token);
                await WaitForNormal("AFTER ");
                await wire.WriteAsync("\rBEFORE"u8.ToArray(), token);
                await WaitForNormal("BEFORE");
                JsonElement result = default;
                await ai.CommitReplyAsync(owner, captured, (body, _) => result = JsonSerializer.SerializeToElement(body), token);
                Check(Code(result) == "range_changed", "A changed search page was committed to the bridge.");
                ai.EndSharing(tab);

                async Task WaitForNormal(string expected)
                {
                    while (true)
                    {
                        var state = await tab.AiSession!.ObserveAiAsync((model, info) =>
                            (info.IsAlternate, model.AiNormalRows.Last().GetText()), token);
                        if (!state.IsAlternate && state.Item2 == expected) return;
                        await Task.Delay(10, token);
                    }
                }
            }

            async Task PublicationChecksTheLiveSourceAndAuthority()
            {
                var clock = new HistoryClock();
                using var collab = new AiCollaboration(app, clock);
                var peer = collab.Attach("controlled publication")!;
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

            async Task ReadFromPausedMacro()
            {
                string source = Path.Combine(directory, "ai-macro.tttym");
                await File.WriteAllTextAsync(source, """
                    #tttym 1
                    Connect("ai-session.ttty");
                    WaitPrompt(Literal("MACRO-DONE"));
                    """, token);
                await window.OpenMacroAsync(source, []);
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

        async Task<JsonElement> Call(AiCollaboration.Counterpart peer, string tool, object arguments)
        {
            var reply = await ai.CallAsync(peer, "tabtty_" + tool, JsonSerializer.SerializeToElement(arguments), token);
            JsonElement result = default;
            await ai.CommitReplyAsync(peer, reply, (body, _) => result = JsonSerializer.SerializeToElement(body), token);
            return result;
        }
        static string? Code(JsonElement result) => result.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString() : null;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
