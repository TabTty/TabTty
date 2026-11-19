using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class BroadcastInput(App app)
{
    private readonly Dictionary<MainWindow, WindowState> windows = [];
    private readonly HashSet<Mode> draining = [];
    private Mode? global;
    private long revision;

    private sealed class WindowState
    {
        public Mode? Local;
        public long SelectionRevision = -1;
        public string? Detail;
    }

    private sealed class Mode(MainWindow? window, long selectionRevision)
    {
        public MainWindow? Window { get; } = window;
        public long SelectionRevision { get; } = selectionRevision;
        public CancellationTokenSource Stop { get; } = new();
        public SemaphoreSlim Queue { get; } = new(1);
        public ResourceQuota PendingText { get; } = new(2 * 1024 * 1024);
        public int Waiting;
        public Operation? Paste;
        public Gesture? Mouse;
        public string? Progress;
        public bool Locked => Paste is not null || Mouse is not null;
    }

    internal sealed record Target(TerminalTab Tab, TerminalSession Session, CancellationToken Lifetime)
    {
        public bool Ready => !Lifetime.IsCancellationRequested && Tab.InputSession == Session;
    }


    private sealed record Operation(TerminalTab Source, CancellationToken SourceLifetime, Target[] Targets)
    {
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal sealed record PastePreview(IReadOnlyList<string> Targets, (int Character, int Line, bool Remember) Pacing);
    internal sealed record PasteDecision(int Character, int Line, bool Remember);
    internal enum PasteOutcome { Inactive, Stopped, Submitted }

    private sealed record MouseRoute(Mode? Mode, TerminalTab Source, CancellationToken Lifetime, long Capture);
    private readonly Dictionary<TerminalSurface, MouseRoute> mouseRoutes = [];

    private sealed class Gesture(TerminalSurface source, TerminalTab sourceTab, long capture, Target[] targets)
    {
        public TerminalSurface Source { get; } = source;
        public long Capture { get; } = capture;
        public TerminalTab SourceTab { get; } = sourceTab;
        public Target[] Targets { get; } = targets;
        public Dictionary<int, Dictionary<Target, (int Generation, TerminalMouseInput Last)>> Presses { get; } = [];
        public HashSet<int> Held { get; } = [];
        public int PendingReleases;
        public bool Cancelled;
    }

    public void Register(MainWindow window)
    {
        windows.Add(window, new WindowState { SelectionRevision = global?.SelectionRevision ?? -1 });
        window.Closed += (_, _) =>
        {
            if (windows.Remove(window, out var state) && state.Local is { } local) Stop(local);
            if (windows.Count == 0 && global is { } active) Stop(active);
            Refresh();
        };
    }

    public void ScopeChanged() { revision++; Refresh(); }

    private Mode? Active(MainWindow window) => global ?? windows.GetValueOrDefault(window)?.Local;
    internal bool HasUnfinishedOperation(TerminalTab tab) => windows.Keys.Select(Active).OfType<Mode>().Concat(draining)
        .Any(mode => mode.Paste?.Source == tab ||
            mode.Paste?.Targets.Any(target => target.Tab == tab) == true ||
            mode.Mouse is { } mouse && (mouse.SourceTab == tab || mouse.Targets.Any(target => target.Tab == tab)));
    public bool CanStart(MainWindow window) => windows.ContainsKey(window) && global is null &&
        !draining.Any(mode => mode.Window is null || mode.Window == window || app.BroadcastAcrossWindows) &&
        (app.BroadcastAcrossWindows ? windows.Values.All(state => state.Local is null) : Active(window) is null);
    public bool CanStop(MainWindow window) => Active(window) is not null ||
        app.BroadcastAcrossWindows && windows.Values.Any(state => state.Local is not null);

    public void Start(MainWindow window)
    {
        if (!CanStart(window)) return;
        var mode = new Mode(app.BroadcastAcrossWindows ? null : window, revision);
        foreach (var host in Scope(mode))
        {
            var state = windows[host];
            if (state.SelectionRevision != revision)
                foreach (var tab in host.TerminalTabs) tab.BroadcastChecked = true;
            state.SelectionRevision = revision;
            state.Detail = null;
        }
        if (mode.Window is null) global = mode;
        else windows[window].Local = mode;
        Refresh();
    }

    public void Stop(MainWindow window)
    {
        if (global is { } active) Stop(active);
        else if (app.BroadcastAcrossWindows)
        {
            foreach (var local in windows.Values.Select(state => state.Local).OfType<Mode>().ToArray()) Stop(local);
        }
        else if (Active(window) is { } local) Stop(local);
        Refresh();
    }

    private void Stop(Mode mode)
    {
        if (mode.Stop.IsCancellationRequested) return;
        if (global == mode) global = null;
        if (mode.Window is { } host && windows.TryGetValue(host, out var state) && state.Local == mode)
            state.Local = null;
        draining.Add(mode);
        Task paste = mode.Paste?.Finished.Task ?? Task.CompletedTask;
        mode.Stop.Cancel();
        if (mode.Mouse is { } gesture)
        {
            gesture.Source.CancelRemoteGesture();
            _ = FinishStoppedModeAsync(mode, Task.WhenAll(paste, CancelGestureAsync(mode, gesture)));
        }
        else _ = FinishStoppedModeAsync(mode, paste);
    }

    private async Task FinishStoppedModeAsync(Mode mode, Task cleanup)
    {
        try
        {
            await cleanup;
            await mode.Queue.WaitAsync();
            mode.Queue.Release();
        }
        finally
        {
            draining.Remove(mode);
            Refresh();
        }
    }

    public void SetAll(MainWindow window, bool value)
    {
        if (Active(window) is not { Locked: false } mode) return;
        foreach (var tab in Tabs(mode)) tab.BroadcastChecked = value;
        Refresh();
    }

    public bool CanMove(MainWindow from, MainWindow to) => from == to ||
        (windows.GetValueOrDefault(from)?.Local?.Locked != true && windows.GetValueOrDefault(to)?.Local?.Locked != true);
    public bool CanMoveToNewWindow(MainWindow from) => windows.GetValueOrDefault(from)?.Local?.Locked != true;
    public void StopPaste(MainWindow host) => Active(host)?.Paste?.Stop.Cancel();

    public string Status(MainWindow host)
    {
        string? detail = windows.GetValueOrDefault(host)?.Detail;
        if (Active(host) is not { } mode)
            return draining.Any(value => value.Window is null || value.Window == host || app.BroadcastAcrossWindows)
                ? detail is null ? Text("BroadcastCleanupPending") : $"{Text("BroadcastCleanupPending")}\n{detail}"
                : detail ?? "";
        var checkedTabs = Tabs(mode).Where(tab => tab.BroadcastChecked).ToArray();
        int ready = checkedTabs.Count(tab => tab.InputSession is not null);
        string status = string.Format(CultureInfo.CurrentCulture, Text("BroadcastStatus"),
            Text(mode.Window is null ? "BroadcastGlobal" : "BroadcastLocal"), ready, checkedTabs.Length);
        if ((mode.Window is null) != app.BroadcastAcrossWindows)
            status += "   " + string.Format(Text("BroadcastNextScope"),
                Text(app.BroadcastAcrossWindows ? "BroadcastGlobal" : "BroadcastLocal"));
        if (ready == 0) status += "   " + Text(checkedTabs.Length == 0 ? "BroadcastNone" : "BroadcastUnready");
        detail = mode.Progress ?? detail;
        return detail is null ? status : $"{status}\n{detail}";
    }

    public void Refresh()
    {
        foreach (var window in windows.Keys.ToArray()) window.RefreshBroadcast();
    }

    private MainWindow[] Scope(Mode mode) => mode.Window is { } local ? [local] : [.. windows.Keys];
    private IEnumerable<TerminalTab> Tabs(Mode mode) => Scope(mode).SelectMany(window => window.TerminalTabs);
    private Target[] Recipients(Mode mode) => Tabs(mode).Where(tab => tab.BroadcastChecked)
        .Select(tab => tab.InputSession is { } session ? new Target(tab, session, tab.InputLifetime) : null)
        .OfType<Target>().ToArray();

    private async Task<bool> SendAsync(Mode mode, TerminalInput input, CancellationToken sourceLifetime)
    {
        if (mode.Paste is not null) return false;
        ResourceReservation memory;
        try { memory = mode.PendingText.Reserve(input.Text.Length * 2L); }
        catch (ResourceLimitException)
        {
            ReportFailure(mode, Text("InputQueueFull"));
            return false;
        }
        using (memory)
            return await DispatchAsync(mode, Recipients(mode), (target, token) => target.Session.PrepareInputAsync(input, null, token),
                origin: sourceLifetime);
    }

    public Task<PasteOutcome> PasteAsync(MainWindow host, TerminalTab source, TerminalPaste text,
        Func<PastePreview, CancellationToken, Task<PasteDecision?>> preview, PasteDecision? pacing = null,
        CancellationToken origin = default) =>
        RunPasteAsync(host, source, text, null, (_, request, token) => preview(request, token), pacing, origin);

    internal Task<PasteOutcome> PasteFromClipboardAsync(MainWindow host, TerminalTab source,
        Func<CancellationToken, Task<TerminalPaste?>> capture,
        Func<TerminalPaste, PastePreview, CancellationToken, Task<PasteDecision?>> preview, CancellationToken origin) =>
        RunPasteAsync(host, source, null, capture, preview, null, origin);

    private async Task<PasteOutcome> RunPasteAsync(MainWindow host, TerminalTab source, TerminalPaste? supplied,
        Func<CancellationToken, Task<TerminalPaste?>>? capture,
        Func<TerminalPaste, PastePreview, CancellationToken, Task<PasteDecision?>> preview,
        PasteDecision? pacing, CancellationToken origin)
    {
        if (Active(host) is not { } mode) return PasteOutcome.Inactive;
        if (mode.Locked || mode.Waiting != 0 || source.InputSession is null) return PasteOutcome.Stopped;
        Target[] targets = Recipients(mode);
        if (targets.Length == 0) return PasteOutcome.Stopped;
        using var sharedSource = supplied?.HoldSource();
        var operation = new Operation(source, source.InputLifetime, targets);
        mode.Paste = operation;
        mode.Progress = Text("Pasting");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(operation.Stop.Token, operation.SourceLifetime,
            mode.Stop.Token, origin);
        try
        {
            // Admission is synchronous. Preview and the broadcast status can
            // wait behind input that has already reached the dispatcher.
            await Dispatcher.Yield(DispatcherPriority.Background);
            stop.Token.ThrowIfCancellationRequested();
            Refresh();
            using var captured = capture is null ? null : await capture(stop.Token);
            TerminalPaste? text = supplied ?? captured;
            if (text is null) return PasteOutcome.Stopped;
            stop.Token.ThrowIfCancellationRequested();
            var defaults = app.PasteDefaults;
            var delays = pacing ?? new PasteDecision(defaults.Character, defaults.Line, defaults.Remember);
            if (text.RequiresConfirmation)
            {
                Task<PasteDecision?> answer = preview(text, new PastePreview(
                    Array.AsReadOnly(targets.Select(target => TerminalTab.BroadcastTargetLabel(target.Session)).ToArray()), defaults), stop.Token);
                try
                {
                    if (await answer.WaitAsync(stop.Token) is not { } decision) return PasteOutcome.Stopped;
                    delays = decision;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    _ = ObservePreviewAsync(answer, text.HoldSource());
                    return PasteOutcome.Stopped;
                }
                stop.Token.ThrowIfCancellationRequested();
                if (mode.Paste != operation) return PasteOutcome.Stopped;
                if (delays.Character is < 0 or > 10000 || delays.Line is < 0 or > 10000)
                    throw new ArgumentException(Text("BadDelay"));
                app.PasteDefaults = delays.Remember ? (delays.Character, delays.Line, true) : default;
            }
            stop.Token.ThrowIfCancellationRequested();
            var amounts = new Dictionary<Target, int>();
            int characters = text.CharacterCount;
            var timer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                if (mode.Paste != operation) return;
                mode.Progress = (stop.IsCancellationRequested ? Text("BroadcastCleanupPending") + "\n" : "") +
                    string.Join("\n", targets.Select(target => $"{target.Tab.TabCaption} — {amounts.GetValueOrDefault(target)}/{characters}"));
                Refresh();
            };
            timer.Start();
            try
            {
                var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool sent = await DispatchAsync(mode, targets, async (target, token) =>
                    await target.Session.PreparePasteAsync(text, delays.Character, delays.Line, token, target.Lifetime,
                        new Progress<int>(value => { if (mode.Paste == operation) amounts[target] = value; }), commit.Task),
                    softStop: stop.Token, commitPaste: commit);
                return sent ? PasteOutcome.Submitted : PasteOutcome.Stopped;
            }
            finally { timer.Stop(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return PasteOutcome.Stopped; }
        finally
        {
            if (mode.Paste == operation)
            {
                mode.Paste = null;
                mode.Progress = null;
            }
            operation.Stop.Dispose();
            operation.Finished.TrySetResult();
            Refresh();
        }
    }

    private static async Task ObservePreviewAsync(Task preview, IDisposable source)
    {
        using var retainedSource = source;
        try { await preview.ConfigureAwait(false); }
        catch (Exception) when (preview.IsFaulted || preview.IsCanceled) { }
    }

    public bool IsActive(MainWindow window) => Active(window) is not null;
    public bool IsLocal(MainWindow window) => Active(window)?.Window == window;
    public bool CanSelect(MainWindow window) => Active(window) is { Locked: false };
    public bool CanStopPaste(MainWindow window) => Active(window)?.Paste is not null;
    public void Select(MainWindow window, TerminalTab tab, bool selected)
    {
        if (!CanSelect(window) || tab.IsAiShared || !window.TerminalTabs.Contains(tab)) return;
        tab.BroadcastChecked = selected;
        Refresh();
    }

    public Task<bool>? Send(MainWindow host, TerminalInput input, CancellationToken sourceLifetime) =>
        Active(host) is { } mode ? SendAsync(mode, input, sourceLifetime) : null;

    public void StartMouse(MainWindow? host, TerminalTab source, TerminalSurface pane, long capture)
        => mouseRoutes[pane] = new(host is null ? null : Active(host), source, source.InputLifetime, capture);

    public bool UsesBroadcastMouse(MainWindow? host, TerminalSurface pane)
        => mouseRoutes.TryGetValue(pane, out var route) ? route.Mode is not null : host is not null && IsActive(host);

    public async Task<bool> RouteMouseAsync(MainWindow? host, TerminalSurface pane, long capture, TerminalMouseInput input)
    {
        if (mouseRoutes.TryGetValue(pane, out var route))
        {
            if (route.Capture != capture) return true;
            if (route.Mode is not { } captured) return false;
            if (!route.Lifetime.IsCancellationRequested && !captured.Stop.IsCancellationRequested)
                await MouseAsync(captured, pane, route.Source, capture, input);
            return true;
        }
        if (capture != 0) return true;
        if (host is null || Active(host) is not { } mode) return false;
        await MouseAsync(mode, pane, null, 0, input);
        return true;
    }

    public void StopMouse(TerminalSurface pane, long capture)
    {
        if (mouseRoutes.TryGetValue(pane, out var route) && route.Capture == capture)
            mouseRoutes.Remove(pane);
    }

    public bool ShouldActivateDestination(MainWindow sourceHost, TerminalTab? moved) =>
        Active(sourceHost)?.Mouse is not { } gesture || moved?.OwnsPane(gesture.Source) == true;

    internal readonly record struct TabMoveOutcome(bool Placed, bool GestureCancelled);

    public TabMoveOutcome MoveTerminalTab(MainWindow from, MainWindow to, TerminalTab? tab, Func<bool> place)
    {
        if (!CanMove(from, to)) return default;
        tab?.BeginHostMove();
        bool placed, cancelled;
        try { placed = place(); }
        finally
        {
            cancelled = tab?.EndHostMove() == true;
            Refresh();
        }
        return new(placed, cancelled);
    }

    private Task MouseAsync(Mode mode, TerminalSurface source, TerminalTab? sourceTab, long capture, TerminalMouseInput input)
    {
        if (mode.Paste is not null) return Task.CompletedTask;
        if (input.Button is >= 3)
        {
            var targets = mode.Mouse is { } active ? active.Targets.Where(target => target.Ready).ToArray() : Recipients(mode);
            return DispatchAsync(mode, targets, (target, token) =>
                target.Session.PrepareInputAsync(new(TerminalInputKind.Mouse, Mouse: input), null, token));
        }

        if (!input.Motion && !input.Release)
        {
            if (sourceTab is null) return Task.CompletedTask;
            mode.Mouse ??= new Gesture(source, sourceTab, capture, Recipients(mode));
            if (mode.Mouse.Source != source || mode.Mouse.Capture != capture || !mode.Mouse.Held.Add(input.Button)) return Task.CompletedTask;
            Refresh();
        }
        if (mode.Mouse is not { Cancelled: false } gesture || gesture.Source != source || gesture.Capture != capture) return Task.CompletedTask;
        if (input.Release)
        {
            if (!gesture.Held.Remove(input.Button)) return Task.CompletedTask;
            gesture.PendingReleases++;
        }
        return DispatchAsync(mode, gesture.Targets, (target, token) =>
        {
            if (!input.Motion && !input.Release)
                return target.Session.PrepareInputAsync(new(TerminalInputKind.Mouse, Mouse: input), null, token);
            if (gesture.Presses.TryGetValue(input.Button, out var recipients) && recipients.TryGetValue(target, out var prior))
                return target.Session.PrepareInputAsync(new(TerminalInputKind.Mouse, Mouse: input), prior.Generation, token);
            return Task.FromResult<TerminalSession.PreparedInput?>(null);
        }, (target, generation) =>
        {
            if (generation is not { } actual) return;
            if (!gesture.Presses.TryGetValue(input.Button, out var recipients))
                gesture.Presses.Add(input.Button, recipients = []);
            if (input.Release) recipients.Remove(target);
            else recipients[target] = (actual, input);
        }, skip: () => gesture.Cancelled, include: input.Motion || input.Release ? target => target.Ready &&
            gesture.Presses.TryGetValue(input.Button, out var recipients) && recipients.ContainsKey(target) : null, after: () =>
        {
            if (!input.Release || mode.Stop.IsCancellationRequested || gesture.Cancelled) return;
            gesture.PendingReleases--;
            gesture.Presses.Remove(input.Button);
            if (gesture.Held.Count == 0 && gesture.PendingReleases == 0 && mode.Mouse == gesture) mode.Mouse = null;
            Refresh();
        });
    }

    public void CaptureFailed(MainWindow? host)
    {
        if (host is not null && Active(host) is { Locked: false } mode)
        {
            ReportGestureCancellation(mode);
            Refresh();
        }
    }

    public void CaptureLost(TerminalSurface source, long capture)
    {
        if (!mouseRoutes.TryGetValue(source, out var route) || route.Capture != capture ||
            route.Mode is not { } mode || mode.Mouse is not { Cancelled: false } gesture ||
            gesture.Source != source || gesture.Capture != capture) return;
        gesture.Cancelled = true;
        ReportGestureCancellation(mode);
        _ = CancelGestureAsync(mode, gesture);
    }

    private void ReportGestureCancellation(Mode mode)
    {
        foreach (var window in Scope(mode))
            if (windows.TryGetValue(window, out var state)) state.Detail = Text("BroadcastGestureCancelled");
    }

    private async Task CancelGestureAsync(Mode mode, Gesture gesture)
    {
        await mode.Queue.WaitAsync();
        try
        {
            if (mode.Mouse != gesture) return;
            foreach (var presses in gesture.Presses.Values)
            foreach (var (target, press) in presses)
            {
                if (!target.Ready) continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(target.Lifetime);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var release = await target.Session.PrepareInputAsync(new(TerminalInputKind.Mouse,
                        Mouse: press.Last with { Release = true, Motion = false }), press.Generation, timeout.Token);
                    if (release is not null) await release.SendAsync(timeout.Token);
                }
                catch (Exception exception) when (IsInputError(exception)) { }
            }
        }
        finally
        {
            if (mode.Mouse == gesture) mode.Mouse = null;
            mode.Queue.Release();
            Refresh();
        }
    }

    private async Task<bool> DispatchAsync(Mode mode, Target[] targets,
        Func<Target, CancellationToken, Task<TerminalSession.PreparedInput?>> prepare,
        Action<Target, int?>? delivered = null, CancellationToken softStop = default, CancellationToken origin = default,
        Func<bool>? skip = null, Func<Target, bool>? include = null,
        Action? after = null, TaskCompletionSource<bool>? commitPaste = null)
    {
        if (mode.Waiting >= 256)
        {
            ReportFailure(mode, Text("InputQueueFull"));
            return false;
        }
        mode.Waiting++;
        var prepared = new Dictionary<Target, TerminalSession.PreparedInput>();
        var submitted = new HashSet<Target>();
        Target? failed = null;
        bool entered = false;
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(mode.Stop.Token, softStop, origin);
            using var preflight = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
            preflight.CancelAfter(TimeSpan.FromSeconds(30));
            await mode.Queue.WaitAsync(preflight.Token);
            entered = true;
            if (skip?.Invoke() == true) return false;
            if (include is not null) targets = targets.Where(include).ToArray();
            foreach (var target in targets)
            {
                failed = target;
                if (!target.Ready) throw new IOException(Text("Disconnected"));
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(preflight.Token, target.Lifetime);
                var request = await prepare(target, lifetime.Token);
                if (request is not null) prepared.Add(target, request);
            }
            foreach (var target in targets)
            {
                failed = target;
                if (!target.Ready) throw new IOException(Text("Disconnected"));
            }
            if (skip?.Invoke() == true) return false;
            preflight.Token.ThrowIfCancellationRequested();
            if (commitPaste is not null)
            {
                var sending = Task.WhenAll(prepared.Select(async pair =>
                {
                    try
                    {
                        // Let every target reserve its ordered start before granting any sends.
                        Task<bool> send = pair.Value.SendAsync(admission.Token);
                        if (await pair.Value.PasteReady! && await commitPaste.Task) submitted.Add(pair.Key);
                        await send;
                        pair.Key.Tab.RefreshInputOutput();
                    }
                    catch (Exception exception) when (IsInputError(exception))
                    {
                        if (!admission.IsCancellationRequested)
                        {
                            failed = pair.Key;
                            admission.Cancel();
                            throw;
                        }
                    }
                }));
                try
                {
                    foreach (var (target, request) in prepared)
                    {
                        failed = target;
                        if (await Task.WhenAny(request.PasteReady!, sending).WaitAsync(preflight.Token) == sending &&
                            !request.PasteReady!.IsCompleted)
                            await sending;
                        if (!await request.PasteReady!.WaitAsync(preflight.Token) || !target.Ready)
                            throw new IOException(Text("Disconnected"));
                    }
                    preflight.Token.ThrowIfCancellationRequested();
                    foreach (var (target, request) in prepared)
                    {
                        failed = target;
                        if (!target.Ready) throw new IOException(Text("Disconnected"));
                        if (!request.CanCommitPaste) throw new IOException(Text("PasteTargetChanged"));
                    }
                    commitPaste.SetResult(true);
                }
                finally
                {
                    commitPaste.TrySetResult(false);
                    await sending;
                }
            }
            else
            {
                foreach (var (target, request) in prepared)
                {
                    if (skip?.Invoke() == true) return false;
                    failed = target;
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(admission.Token, target.Lifetime);
                    lifetime.CancelAfter(TimeSpan.FromSeconds(30));
                    if (await request.SendAsync(lifetime.Token))
                    {
                        submitted.Add(target);
                        delivered?.Invoke(target, request.MouseGeneration);
                        target.Tab.RefreshInputOutput();
                    }
                }
            }
            return submitted.Count > 0 && !admission.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (mode.Stop.IsCancellationRequested || softStop.IsCancellationRequested || origin.IsCancellationRequested)
        { return false; }
        catch (Exception exception) when (IsInputError(exception))
        {
            string details = string.Join("\n", targets.Select(target => $"{target.Tab.TabCaption} — " +
                Text(target == failed ? "BroadcastFailedTarget" : submitted.Contains(target) ? "BroadcastSubmitted" : "BroadcastNotSent")));
            ReportFailure(mode, $"{exception.Message}\n{details}");
            return false;
        }
        finally
        {
            foreach (var request in prepared.Values) request.Dispose();
            if (entered) mode.Queue.Release();
            mode.Waiting--;
            after?.Invoke();
        }
    }

    private void ReportFailure(Mode mode, string details)
    {
        foreach (var window in Scope(mode))
            if (windows.TryGetValue(window, out var state)) state.Detail = $"{Text("BroadcastFailed")}\n{details}";
        Stop(mode);
        Refresh();
    }

    private static bool IsInputError(Exception exception) => exception is IOException or SocketException or
        ArgumentException or InvalidOperationException or CryptographicException or OperationCanceledException;
}
