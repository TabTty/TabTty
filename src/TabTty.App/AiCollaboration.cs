using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.AiTransport;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class AiCollaboration(App app, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Counterpart> peers = [];
    private readonly Dictionary<string, Sharing> shares = [];
    private bool disposed;
    private long captureOrdinal;
    private const int HandleLimit = 256;
    private const int HandleLifetime = 60_000;
    internal IReadOnlyCollection<Counterpart> Peers => peers.Values;

    internal sealed class Counterpart(string location, string launcherPath, uint launcherId, long now)
    {
        internal string Id { get; } = NewId();
        internal string Code { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        internal string Location { get; } = location;
        internal string LauncherPath { get; } = launcherPath;
        internal uint LauncherId { get; } = launcherId;
        internal string? Name { get; set; }
        internal string Label => Name is { Length: > 0 } name ? $"{Code} · {name} ({Text("AiUnverified")})" : Code;
        internal Dictionary<string, Handle> Handles { get; } = [];
        internal CancellationTokenSource Lifetime { get; } = new();
        internal AiRateLimit Calls { get; } = AiRateLimit.OrdinaryCalls(now);
        private readonly AiRateLimit returns = AiRateLimit.ControlReturns(now);
        internal bool TryTakeReturn(long now, out int retry)
        {
            lock (returns) return returns.Take(now, out retry);
        }
        internal int InFlight { get; set; }
        internal long SharingRevision { get; set; }
        internal Action<string>? SharingEnded { get; set; }
    }

    internal sealed class Sharing(TerminalTab tab, Counterpart peer, TerminalSession session)
    {
        internal string Id { get; } = NewId();
        internal string Generation { get; } = NewId();
        internal TerminalTab Tab { get; } = tab;
        internal Counterpart Peer { get; } = peer;
        internal TerminalSession Session { get; } = session;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal Control? Control { get; set; }
        internal string? LastControl { get; set; }
        internal string? Reason { get; set; }
        internal bool Waiting { get; set; }
        internal long Completed { get; set; }
        internal long Admitted { get; set; }
        internal TaskCompletionSource AuthorityChanged { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void SignalAuthorityChange()
        {
            var prior = AuthorityChanged;
            AuthorityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            prior.TrySetResult();
        }
        internal string State => Tab.IsAiTakingControl ? "taking_control" : Control is null ? "read_only" : "controlling";
    }

    internal sealed class Control(AiInputLease lease)
    {
        internal string Id { get; } = NewId();
        internal AiInputLease Lease { get; } = lease;
        internal int HighWater { get; set; }
        internal Task? Sending { get; set; }
        internal AiInputAttempt? Attempt { get; set; }
        internal byte[]? PendingDigest { get; set; }
        internal Dictionary<int, SendOutcome> Ledger { get; } = [];
    }

    internal sealed record SendOutcome(byte[] Digest, object Result);
    internal readonly record struct Handle(Sharing? Share, long Expires, object Value);
    private sealed record Observation(AiTerminalState State, string? Control, long Completed, bool CanSend);
    private sealed record Listing(string[] Ids, int Offset, int Limit);
    internal sealed record Reply(object Body, Sharing? Share, long? SharingRevision = null,
        TerminalHistory.Span[]? HistoryDependencies = null, StagedHistory? History = null);
    private sealed class ToolException(string code, object? detail = null) : Exception(code)
    {
        internal object? Detail { get; } = detail;
    }

    private AiRateLimit? applicationCalls;
    private long Now => (long)clock.GetElapsedTime(0, clock.GetTimestamp()).TotalMilliseconds;
    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    internal Counterpart? Attach(string location, string launcherPath, uint launcherId)
    {
        app.Dispatcher.VerifyAccess();
        if (disposed || peers.Count >= 8) return null;
        Counterpart peer;
        do { peer = new(location, launcherPath, launcherId, Now); } while (peers.Values.Any(other => other.Code == peer.Code));
        peers.Add(peer.Id, peer);
        return peer;
    }

    internal void Detach(Counterpart peer)
    {
        app.Dispatcher.VerifyAccess();
        if (!peers.Remove(peer.Id)) return;
        _ = peer.Lifetime.CancelAsync();
        foreach (Sharing share in shares.Values.Where(share => share.Peer == peer).ToArray()) EndSharing(share.Tab);
        foreach (string key in peer.Handles.Keys.ToArray()) RemoveHandle(peer, key);
    }

    internal string? ShareUnavailable(TerminalTab tab)
    {
        if (EndpointFailed) return "AiEndpointUnavailable";
        if (tab.AiSession is null || tab.IsRunning && tab.AiSession.AiConnectionEnded) return "AiSessionUnavailable";
        if (tab.BroadcastChecked && tab.AiHost is { } host && app.Broadcast.IsActive(host))
            return "AiBroadcastTarget";
        return null;
    }

    internal string? Share(TerminalTab tab, Counterpart peer)
    {
        app.Dispatcher.VerifyAccess();
        if (!peers.ContainsKey(peer.Id)) return "AiNoCounterpart";
        if (ShareUnavailable(tab) is { } problem) return problem;
        if (tab.AiSharing?.Peer == peer) return null;
        EndSharing(tab);
        var share = new Sharing(tab, peer, tab.AiSession!);
        tab.AiSharing = share;
        tab.BroadcastChecked = false;
        shares.Add(share.Id, share);
        peer.SharingRevision++;
        tab.AiAuthorityChanged();
        return null;
    }

    internal string? GrantUnavailable(TerminalTab tab)
    {
        if (tab.HasActiveMacro) return "AiMacroOwnsInput";
        if (tab.IsAiTakingControl || tab.HasAiGrantConflict) return "AiInputBusy";
        return tab.AiSharing is { Control: null } share && tab.InputSession == share.Session
            ? null : "AiSessionUnavailable";
    }

    internal string? Grant(TerminalTab tab)
    {
        app.Dispatcher.VerifyAccess();
        if (GrantUnavailable(tab) is { } problem) return problem;
        Sharing share = tab.AiSharing!;
        if (share.Session.TryGrantAiInput() is not { } lease) return "AiInputBusy";
        share.Control = new Control(lease);
        share.SignalAuthorityChange();
        share.Reason = null;
        tab.AiAuthorityChanged();
        return null;
    }

    internal void EndSharing(TerminalTab tab)
    {
        app.Dispatcher.VerifyAccess();
        if (tab.AiSharing is not { } share) return;
        share.Control?.Lease.Revoke();
        shares.Remove(share.Id);
        share.Peer.SharingRevision++;
        share.Peer.SharingEnded?.Invoke(share.Id);
        _ = share.Lifetime.CancelAsync();
        foreach (var key in share.Peer.Handles.Where(entry => entry.Value.Share == share).Select(entry => entry.Key).ToArray())
            RemoveHandle(share.Peer, key);
        EndControl(tab);
        tab.AiSharing = null;
        tab.AiAuthorityChanged();
    }

    internal void EndControl(TerminalTab tab, string? reason = null)
    {
        app.Dispatcher.VerifyAccess();
        if (tab.AiSharing is not { Control: { } control } share) return;
        control.Lease.Revoke();
        share.Control = null;
        share.SignalAuthorityChange();
        share.LastControl = control.Id;
        share.Reason = reason;
        tab.IsAiTakingControl = true;
        tab.AiAuthorityChanged();
        _ = CompleteTakeoverAsync(share, control);
    }

    private async Task CompleteTakeoverAsync(Sharing share, Control control)
    {
        TerminalTab tab = share.Tab;
        Task cleanup = CleanupAsync();
        using var delay = new CancellationTokenSource();
        if (await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5), clock, delay.Token)) != cleanup)
        {
            tab.AiCleanupDelayed = true;
            tab.AiAuthorityChanged();
        }
        await cleanup;
        await delay.CancelAsync();
        tab.IsAiTakingControl = false;
        tab.AiCleanupDelayed = false;
        tab.AiAuthorityChanged();

        async Task CleanupAsync()
        {
            try
            {
                if (control.Sending is { } sending) await sending;
            }
            finally
            {
                try { await share.Session.ReleaseAiInputAsync(control.Lease); }
                catch (Exception exception) when (exception is IOException or SocketException or
                    OperationCanceledException or InvalidOperationException or CryptographicException)
                {
                    tab.ReportAiUncertain();
                }
            }
        }
    }

    internal bool CanDeliver(Counterpart peer, Reply reply)
    {
        app.Dispatcher.VerifyAccess();
        return peers.ContainsKey(peer.Id) && (reply.SharingRevision is null || reply.SharingRevision == peer.SharingRevision) &&
            (reply.Share is not { } share ||
            share.Peer == peer && shares.TryGetValue(share.Id, out var current) && current == share);
    }

    private bool TryTakeCall(Counterpart peer, out int retry)
    {
        applicationCalls ??= new AiRateLimit(100, 200, Now);
        return peer.Calls.Take(Now, out retry) && applicationCalls.Take(Now, out retry);
    }

    internal Task<Reply> CallAsync(Counterpart peer, string name, JsonElement arguments, CancellationToken token)
    {
        app.Dispatcher.VerifyAccess();
        if (name == "tabtty_return_control" && !peer.TryTakeReturn(Now, out int retry))
            return Task.FromResult(new Reply(Error("busy", new { retry_after_ms = retry }), null));
        return CallAdmittedAsync(peer, name, arguments, token);
    }

    private async Task<Reply> CallAdmittedAsync(Counterpart peer, string name, JsonElement arguments, CancellationToken token)
    {
        app.Dispatcher.VerifyAccess();
        if (!peers.ContainsKey(peer.Id)) return new Reply(Error("not_shared"), null);
        Sharing? share = null;
        bool ordinary = name != "tabtty_return_control";
        if (ordinary)
        {
            if (peer.InFlight >= 16) return new Reply(Error("busy", new { retry_after_ms = 50 }), null);
            if (!TryTakeCall(peer, out int retry))
                return new Reply(Error("busy", new { retry_after_ms = retry }), null);
            peer.InFlight++;
        }
        try
        {
            if (name is "tabtty_read_state" or "tabtty_read_history" or "tabtty_search_history")
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                token.ThrowIfCancellationRequested();
                if (!peers.ContainsKey(peer.Id)) return new Reply(Error("not_shared"), null);
            }
            Prune(peer);
            var args = new Arguments(arguments);
            if (name == "tabtty_list_shared_sessions") return new Reply(List(peer, args), null, peer.SharingRevision);
            string id = args.String("session", 256);
            if (!shares.TryGetValue(id, out var candidate) || candidate.Peer != peer) throw new ToolException("not_shared");
            share = candidate;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, share.Lifetime.Token, peer.Lifetime.Token);
            var work = linked.Token;
            object result = name switch
            {
                "tabtty_read_state" => await ReadStateAsync(share, args, work),
                "tabtty_read_history" => await ReadHistoryAsync(share, args, work),
                "tabtty_search_history" => await SearchHistoryAsync(share, args, work),
                "tabtty_wait" => await WaitAsync(share, args, work),
                "tabtty_send_input" => await SendAsync(share, args, work),
                "tabtty_return_control" => Return(share, args),
                _ => throw new ToolException("invalid_request"),
            };
            if (!CanDeliver(peer, new Reply(result, share)))
            {
                if (result is HistoryResult discarded) discarded.Staged.Dispose();
                return new Reply(Error("not_shared"), null);
            }
            if (result is HistoryResult history) return new Reply(history.Body, share, HistoryDependencies: history.Dependencies, History: history.Staged);
            return new Reply(result, share);
        }
        catch (ToolException exception) { return new Reply(Error(exception.Message, exception.Detail), share); }
        catch (ResourceLimitException) { return new Reply(Error("resource_limit"), share); }
        catch (OperationCanceledException) { return new Reply(Error("cancelled"), share); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException)
        { return new Reply(Error("invalid_request"), share); }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException)
        { return new Reply(Error("busy"), share); }
        finally { if (ordinary) peer.InFlight--; }
    }

    internal Task CommitReplyAsync(Counterpart peer, Reply reply, Action<object, bool> deliver, CancellationToken token)
        => PublishReplyAsync(peer, reply, (body, accepted) => { deliver(body, accepted); return accepted; }, token);

    internal async Task PublishReplyAsync(Counterpart peer, Reply reply, Func<object, bool, bool> deliver, CancellationToken token)
    {
        using var ownedPreparation = reply.History;
        var priority = reply.History is null ? DispatcherPriority.Normal : DispatcherPriority.Background;
        bool done = await Attempt();
        if (done) return;
        await Task.Delay(10, token).ConfigureAwait(false);
        if (!await Attempt())
            await app.Dispatcher.InvokeAsync(() =>
            {
                object error = !CanDeliver(peer, reply) ? Error("not_shared") :
                    reply.History?.Source is { } id && !HasSource(id) ? Error("cursor_expired") :
                    Error("busy", new { retry_after_ms = 50 });
                deliver(error, false);
            }, priority, token).Task.ConfigureAwait(false);

        Task<bool> Attempt() => app.Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!CanDeliver(peer, reply)) { deliver(Error("not_shared"), false); return true; }
            if (reply.History is not { } staged || reply.Share is not { } share ||
                reply.HistoryDependencies is not { } dependencies)
            {
                deliver(reply.Body, true);
                return true;
            }
            if (staged.Source is { } sourceId && !HasSource(sourceId))
            {
                deliver(Error("cursor_expired"), false);
                return true;
            }
            peer.Handles.EnsureCapacity(HandleLimit);
            int evictionCount = Math.Max(0, peer.Handles.Count + staged.Handles.Count - HandleLimit);
            string[] evictions = peer.Handles.Keys.Take(evictionCount).ToArray();
            var released = new IDisposable?[evictions.Length];
            bool disclosed = false, expired = false;
            if (!share.Session.TryPublishHistory(dependencies, Publish, token, out string? error)) return false;
            foreach (var owner in released) owner?.Dispose();
            if (disclosed) StartExpiryTimer();
            if (expired || staged.Source is { } id && !disclosed && !HasSource(id)) error = "cursor_expired";
            if (error is not null)
                deliver(error == "busy" ? Error(error, new { retry_after_ms = 50 }) : Error(error), false);
            return true;

            void Publish()
            {
                token.ThrowIfCancellationRequested();
                if (staged.Source is { } id && !HasSource(id)) { expired = true; return; }
                disclosed = deliver(reply.Body, true);
                if (!disclosed) return;
                long expires = Now + HandleLifetime;
                for (int i = 0; i < evictions.Length; i++)
                    if (peer.Handles.Remove(evictions[i], out var removed)) released[i] = removed.Value as IDisposable;
                foreach (var (handleId, value) in staged.Handles)
                    peer.Handles.Add(handleId, new Handle(share, expires, value));
            }
        }, priority, token).Task;

        bool HasSource(string id) => peer.Handles.TryGetValue(id, out var handle) &&
            handle.Share == reply.Share && handle.Expires > Now;
    }

    private object List(Counterpart peer, Arguments args)
    {
        Listing listing;
        if (args.Has("cursor"))
        {
            args.Only("cursor");
            listing = Resolve<Listing>(peer, null, args.String("cursor", 256));
        }
        else
        {
            args.Only("limit");
            listing = new Listing(shares.Values.Where(share => share.Peer == peer).Select(share => share.Id).ToArray(),
                0, args.Integer("limit", 1, 200, 50));
        }
        var sessions = listing.Ids.Skip(listing.Offset).Take(listing.Limit)
            .Select(id => shares.GetValueOrDefault(id)).OfType<Sharing>().Select(share => new
            {
                session = share.Id,
                connection_generation = share.Generation,
                display_name = share.Tab.TabCaption,
                connected = share.Tab.IsRunning && !share.Session.AiConnectionEnded && !share.Tab.InputLifetime.IsCancellationRequested,
                control_state = share.State,
                control = share.Control?.Id,
                receive_encoding = share.Session.AiReceiveEncoding,
                transmit_encoding = share.Session.AiTransmitEncoding,
                rows = share.Tab.AiGrid.Rows,
                columns = share.Tab.AiGrid.Columns,
                input_kinds = new[] { "text", "key", "paste", "mouse" },
            }).ToArray();
        int offset = listing.Offset + listing.Limit;
        return new
        {
            status = "ok", counterpart_id = peer.Id, pairing_code = peer.Code, interface_version = 1, sessions,
            next_cursor = offset < listing.Ids.Length ? Store(peer, null, listing with { Offset = offset }) : null,
        };
    }

    private object Return(Sharing share, Arguments args)
    {
        args.Only("session", "control", "reason");
        string id = args.String("control", 256);
        string? reason = args.Has("reason") ? PermissionDisplay.SingleLine(args.String("reason", 256, allowEmpty: true)) : null;
        if (share.Control?.Id == id) EndControl(share.Tab, reason);
        else if (share.Control is not null || share.LastControl != id) throw new ToolException("control_required");
        return Ok(share, new { cleanup_pending = share.Tab.IsAiTakingControl });
    }

    private async Task<object> SendAsync(Sharing share, Arguments args, CancellationToken token)
    {
        args.Only("session", "control", "action_seq", "observation", "input");
        if (share.Control is not { } control || args.String("control", 256) != control.Id)
            throw new ToolException("control_required");
        int sequence = args.Integer("action_seq", 1, int.MaxValue);
        string observationId = args.String("observation", 256);
        if (sequence <= control.HighWater && !control.Ledger.ContainsKey(sequence))
            return DeliveryError("already_processed", "unknown", sequence, true, control);
        if (sequence > (long)control.HighWater + 1)
        {
            if (control.Attempt?.Admitted == true && sequence == (long)control.HighWater + 2)
                return DeliveryError("busy", "not_sent", sequence, false, control);
            return DeliveryError("sequence_gap", "not_sent", sequence, false, control);
        }
        InputAction action;
        try { action = InputAction.Parse(args.Object("input")); }
        catch (Exception exception) when (exception is ToolException or ArgumentException or InvalidOperationException)
        {
            string code = exception is ToolException ? exception.Message : "invalid_request";
            return sequence <= control.HighWater || control.Attempt?.Admitted == true ? Error("sequence_conflict") :
                DeliveryError(code, "not_sent", sequence, false, control);
        }
        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { observationId, action }));
        if (sequence <= control.HighWater)
        {
            var prior = control.Ledger[sequence];
            return CryptographicOperations.FixedTimeEquals(prior.Digest, digest) ? prior.Result : Error("sequence_conflict");
        }
        if (control.Sending is not null)
        {
            if (control.Attempt?.Admitted != true) return Error("busy");
            if (control.PendingDigest is not { } pending || !CryptographicOperations.FixedTimeEquals(pending, digest))
                return Error("sequence_conflict");
            return Ok(share, new { action_seq = sequence, sequence_consumed = true, delivery = "pending" });
        }
        if (!TryResolve(share.Peer, share, observationId, out Observation? observation) || observation is null ||
            observation.Control != control.Id || !observation.CanSend || observation.Completed != share.Completed)
            return DeliveryError("stale_observation", "not_sent", sequence, false, control);
        share.Tab.AiSendRate ??= new AiRateLimit(5, 10, Now);
        if (!share.Tab.AiSendRate.Take(Now, out int retry))
            return DeliveryError("busy", "not_sent", sequence, false, control, retry);

        ResourceReservation inputMemory;
        try { inputMemory = observationWork.Reserve(action.PasteText is { } payload ? payload.Length * 2L + 512 : 512); }
        catch (ResourceLimitException) { return DeliveryError("resource_limit", "not_sent", sequence, false, control); }
        using var reservedInput = inputMemory;
        var attempt = new AiInputAttempt(control.Lease, observation.State.LayoutEpoch, token);
        control.Attempt = attempt;
        control.PendingDigest = digest;
        Task<object> sending = PerformAsync();
        control.Sending = sending;
        object result;
        try { result = await sending; }
        finally
        {
            control.Sending = null;
            control.Attempt = null;
            control.PendingDigest = null;
            if (attempt.Admitted) share.Completed = ++share.Admitted;
        }
        if (attempt.Admitted)
        {
            control.HighWater = sequence;
            control.Ledger.Add(sequence, new SendOutcome(digest, result));
            control.Ledger.Remove(sequence - 256);
        }
        if (DeliveryUncertain(result) || token.IsCancellationRequested && attempt.Admitted)
        {
            if (DeliveryUncertain(result)) share.Tab.ReportAiUncertain();
            if (share.Control == control) EndControl(share.Tab);
        }
        if (attempt.Admitted) share.Tab.RefreshInputOutput();
        return result;

        async Task<object> PerformAsync()
        {
            // Publish the in-flight reservation before the session can complete or reenter the dispatcher.
            await Task.Yield();
            string? code = null;
            string delivery = "submitted";
            (int Row, int Column)? effective = null;
            try
            {
                if (action.PasteText is { } text)
                {
                    using var paste = await Task.Run(() => TerminalPaste.Create(text), token);
                    bool complete = await share.Session.SendAiPasteAsync(paste, action.CharacterDelay,
                        action.LineDelay, action.Kind == "paste", attempt, share.Tab.InputLifetime);
                    if (!complete) code = "cancelled";
                }
                else effective = await share.Session.SendAiInputAsync(action.Input, action.Mouse, attempt, share.Tab.InputLifetime);
            }
            catch (AiInputRejectedException exception) { code = exception.Code; }
            catch (ResourceLimitException) { code = "resource_limit"; }
            catch (ArgumentException) { code = "unsupported_action"; }
            catch (OperationCanceledException) { code = "cancelled"; }
            catch (Exception exception) when (exception is IOException or SocketException or
                InvalidOperationException or CryptographicException)
            { code = "busy"; }
            if (code is not null)
            {
                delivery = attempt.Admitted ? attempt.CompletedWrites > 0 ? "possibly_partial" : "unknown" : "not_sent";
                return DeliveryError(attempt.Admitted ? "send_uncertain" : code, delivery, sequence, attempt.Admitted, control);
            }
            return Ok(share, new
            {
                action_seq = sequence, sequence_consumed = true, delivery,
                next_action_seq = sequence < int.MaxValue && share.Control == control ? sequence + 1 : (int?)null,
                effective_row = effective?.Row, effective_column = effective?.Column,
            });
        }
    }

    private static bool DeliveryUncertain(object result) => result is Dictionary<string, object?> fields &&
        fields.GetValueOrDefault("delivery") is "possibly_partial" or "unknown";

    private static object DeliveryError(string code, string delivery, int sequence, bool consumed, Control control, int? retry = null)
    {
        var result = Error(code, retry is { } milliseconds ? new { retry_after_ms = milliseconds } : null);
        result["delivery"] = delivery;
        result["action_seq"] = sequence;
        result["sequence_consumed"] = consumed;
        long next = (long)control.HighWater + (control.Attempt?.Admitted == true ? 2 : 1);
        if (!control.Lease.Token.IsCancellationRequested && code != "send_uncertain" && next <= int.MaxValue)
            result["next_action_seq"] = next;
        return result;
    }

    private async Task<object> WaitAsync(Sharing share, Arguments args, CancellationToken token)
    {
        args.Only("session", "kind", "timeout_ms", "observation");
        string kind = args.String("kind", 8);
        if (kind is not ("change" or "delay") || args.Has("observation") != (kind == "change"))
            throw new ToolException("invalid_request");
        int timeout = args.Integer("timeout_ms", 0, 10_000, 1000);
        Observation? prior = kind == "change"
            ? Resolve<Observation>(share.Peer, share, args.String("observation", 256), "stale_observation") : null;
        if (share.Waiting) throw new ToolException("busy");
        share.Waiting = true;
        try
        {
            Control? original = share.Control;
            Task authority = share.AuthorityChanged.Task;
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task deadline = Task.Delay(TimeSpan.FromMilliseconds(timeout), clock, delay.Token);
            AiTerminalState state;
            string outcome;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var update = await share.Session.ObserveAiChangeAsync(token);
                    state = update.State;
                    if (share.Session.AiConnectionEnded || !share.Tab.IsRunning || share.Control != original ||
                        share.Tab.InputLifetime.IsCancellationRequested)
                    { outcome = "interrupted"; break; }
                    if (prior is not null && prior.State != state) { outcome = "changed"; break; }
                    if (deadline.IsCompleted) { outcome = kind == "change" ? "no_change" : "elapsed"; break; }
                    await Task.WhenAny(update.Change, authority, deadline);
                    if (authority.IsCompleted)
                    { outcome = "interrupted"; break; }
                }
            }
            finally { await delay.CancelAsync(); }
            string id = Store(share.Peer, share, new Observation(state, share.Control?.Id, share.Completed, false));
            return Ok(share, new { outcome, observation = id, rows = state.Rows, columns = state.Columns,
                source = state.IsAlternate ? "alternate" : "normal" });
        }
        finally { share.Waiting = false; }
    }

    private string Store(Counterpart peer, Sharing? share, object value, long? expires = null)
    {
        if (!peers.ContainsKey(peer.Id) || share is not null && !shares.ContainsKey(share.Id))
        {
            if (value is IDisposable owner) owner.Dispose();
            throw new ToolException("not_shared");
        }
        string id = NewId();
        StoreHandle(peer, share, id, value, expires);
        return id;
    }

    private void StoreHandle(Counterpart peer, Sharing? share, string id, object value, long? expires)
    {
        Prune(peer);
        while (peer.Handles.Count >= HandleLimit) RemoveHandle(peer, peer.Handles.Keys.First());
        peer.Handles.Add(id, new Handle(share, expires ?? Now + HandleLifetime, value));
        StartExpiryTimer();
    }

    private void StartExpiryTimer()
    {
        if (expiryTimer is null)
        {
            expiryTimer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            expiryTimer.Tick += (_, _) =>
            {
                foreach (var attached in peers.Values) Prune(attached);
                if (!peers.Values.Any(peer => peer.Handles.Count > 0)) expiryTimer?.Stop();
            };
        }
        expiryTimer.Start();
    }

    private void Prune(Counterpart peer)
    {
        long now = Now;
        foreach (var key in peer.Handles.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            RemoveHandle(peer, key);
    }

    private static bool TryResolve<T>(Counterpart peer, Sharing? share, string id, out T? value) where T : class
    {
        value = peer.Handles.TryGetValue(id, out var handle) && handle.Share == share ? handle.Value as T : null;
        return value is not null;
    }

    private static T Resolve<T>(Counterpart peer, Sharing? share, string id, string error = "cursor_expired") where T : class =>
        TryResolve<T>(peer, share, id, out T? value) && value is not null ? value : throw new ToolException(error);

    private static void RemoveHandle(Counterpart peer, string key)
    {
        if (peer.Handles.Remove(key, out var handle) && handle.Value is IDisposable owner) owner.Dispose();
    }

    private static Dictionary<string, object?> Error(string code, object? detail = null)
    {
        var reason = new Dictionary<string, object?> { ["code"] = code };
        if (detail is not null)
            foreach (var property in detail.GetType().GetProperties()) reason.Add(property.Name, property.GetValue(detail));
        return new() { ["status"] = "error", ["error"] = reason };
    }

    private static Dictionary<string, object?> Ok(Sharing share, object details)
    {
        var result = new Dictionary<string, object?>
        {
            ["status"] = "ok", ["session"] = share.Id, ["connection_generation"] = share.Generation,
            ["control_state"] = share.State, ["control"] = share.Control?.Id,
        };
        foreach (var property in details.GetType().GetProperties()) result.Add(property.Name, property.GetValue(details));
        return result;
    }

    internal void ShowSetup(TerminalTab tab)
    {
        if (Window.GetWindow(tab) is not { } owner) return;
        string bridge = Path.Combine(AppContext.BaseDirectory, "TabTty.McpBridge.exe");
        string configuration = JsonSerializer.Serialize(new { mcpServers = new { tabtty = new { command = bridge, args = new[] { "--stdio" } } } },
            new JsonSerializerOptions { WriteIndented = true });
        var form = new MainWindow.FormWindow(owner, Text("AiSetup")) { Width = 670 };
        form.AddField("AiSetupInstructions", new TextBox { Text = Text("AiSetupHelp"), TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true, BorderThickness = new Thickness(0) });
        form.AddField("AiSetupConfiguration", new TextBox { Text = configuration, IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap });
        form.AddButtons(() => { }, acceptKey: "CloseDialog");
        app.Modal(form.ShowDialog);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        expiryTimer?.Stop();
        _ = listenerStop.CancelAsync();
        foreach (var peer in peers.Values.ToArray()) Detach(peer);
    }
    private DispatcherTimer? expiryTimer;
}
