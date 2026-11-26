using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace TabTty.Core;

internal enum MacroRunState { Running, Waiting, PauseRequested, Paused, Stopping, Completed, Stopped, Failed }
[Flags]
internal enum MacroHold { None = 0, UserPause = 1, RecordingFailure = 2, RecordingReady = 4, HistoryLoss = 8, CloseReview = 16 }
[Flags]
internal enum MacroAction { None = 0, Pause = 1, Resume = 2, Stop = 4, RetryRecording = 8, ContinueAfterHistoryLoss = 16 }
internal sealed record MacroRunStatus(MacroRunState State, MacroHold Holds, string Operation, MacroPosition Position,
    string? Result, long HistoryLossRevision, bool PlaintextNotice, string? DisplayDetail, bool PostConnection,
    MacroAction Actions);

internal readonly record struct MacroConnection(TerminalSession Session, MacroProgram? PostConnection);

internal interface IMacroHost
{
    Task<MacroConnection> ConnectAsync(MacroRun run, string profilePath, RecordingOverrides recording, CancellationToken token);
    Task<VaultSecret?> ReadPasswordAsync(CredentialReference reference, CancellationToken token);
    Task<MacroDialogResult> DialogAsync(MacroDialogRequest request, CancellationToken token);
    Task SetMinimizedAsync(bool minimized, CancellationToken token);
    string ErrorMessage(MacroErrorCode code) => code.ToString();
}

internal sealed partial class MacroRun : IDisposable
{
    private readonly object gate = new();
    private readonly IMacroHost host;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly CancellationTokenSource stop = new(), attemptStop = new();
    private readonly ResourceQuota memory;
    private readonly ResourceQuota patterns = new(32);
    private readonly ConditionalWeakTable<object, ResourceReservation> allocations = new();
    private readonly ConditionalWeakTable<MacroPattern, ResourceReservation> patternAllocations = new();
    private readonly ResourceReservation initialMemory;
    private readonly MacroReceiveHistory history = new();
    private TaskCompletionSource changed = Signal();
    private MacroHold holds;
    private MacroRunState? lifecycle;
    private MacroRunState State => lifecycle ?? (holds != MacroHold.None
        ? inFlight ? MacroRunState.PauseRequested : MacroRunState.Paused
        : operation.Length == 0 ? MacroRunState.Running : MacroRunState.Waiting);
    private string operation = "", reason = "Stopped";
    private string? displayDetail;
    private MacroPosition position;
    private long generation, receivedRevision, pausedTicks, pauseStarted;
    private bool inFlight, worker, ready, connectAttempted, admitted = true, disposed, plaintextNotice;
    private Task cancellation = Task.CompletedTask;
    private int instructions;
    private long sliceStart;
    private TerminalSession? session;
    private MacroWait? currentWait;
    private string baseDirectory;
    private readonly MacroList arguments;
    private sealed class MacroWait(long start, long? budget)
    {
        public long Start = start;
        public long? Budget = budget;
        public MacroReceiveCutoff? Cutoff;
        public long ScannedThrough = -1;
    }

    public MacroRun(MacroProgram program, IReadOnlyList<string> args, IMacroHost host, TimeProvider? time = null)
    {
        MacroLimits.ValidateArguments(args);
        if (!MacroLimits.Runs.Wait(0)) throw new MacroSourceException("RunLimit", new(1, 1));
        try { initialMemory = program.AdmitRun(out memory); }
        catch { MacroLimits.Runs.Release(); throw new MacroSourceException("MemoryLimit", new(1, 1)); }
        programSnapshot = program;
        position = program.EntryPosition;
        IsPostConnection = program.IsPostConnection;
        FullPath = program.FullPath;
        this.host = host; clock = time ?? TimeProvider.System; started = clock.GetTimestamp();
        baseDirectory = Path.GetDirectoryName(program.FullPath)!;
        arguments = new(MacroType.String, args.Cast<object>());
        try { Own(arguments); foreach (string arg in args) Own(arg); }
        catch (ResourceLimitException) { ReleaseAdmission(); throw new MacroSourceException("MemoryLimit", new(1, 1)); }
    }

    private MacroProgram? programSnapshot;
    private bool executingPostConnection;
    private bool retryingRecording;
    private MacroProgram Program => programSnapshot!;
    public string FullPath { get; }
    internal bool IsPostConnection { get; }
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string BaseName => GetFileBaseName(FullPath);
    internal string? RecordingMacroName => IsPostConnection ? null : BaseName;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Action? Changed;
    public MacroRunStatus Status
    {
        get { lock (gate) return new(State, holds, operation, position, lifecycle is MacroRunState.Failed or MacroRunState.Stopped ? reason : null,
            history.LossRevision, plaintextNotice, displayDetail, IsPostConnection || executingPostConnection, AvailableActions); }
    }
    private MacroAction AvailableActions =>
        lifecycle is not null
            ? MacroAction.None
            : MacroAction.Stop
              | (holds.HasFlag(MacroHold.UserPause) ? MacroAction.None : MacroAction.Pause)
              | ((holds & (MacroHold.UserPause | MacroHold.RecordingReady)) != 0 ? MacroAction.Resume : MacroAction.None)
              | (holds.HasFlag(MacroHold.RecordingFailure) && !retryingRecording ? MacroAction.RetryRecording : MacroAction.None)
              | (holds.HasFlag(MacroHold.HistoryLoss) ? MacroAction.ContinueAfterHistoryLoss : MacroAction.None);
    public bool IsActive { get { lock (gate) return lifecycle is null or MacroRunState.Stopping; } }
    public CancellationToken StopToken => stop.Token;
    internal CancellationToken AttemptToken => attemptStop.Token;
    public void Start(TerminalSession? connected = null)
    {
        lock (gate)
        {
            if (startedExecution) throw new InvalidOperationException("The macro has already started.");
            if (IsPostConnection && (connected is null || session != connected))
                throw new InvalidOperationException("The post-connection macro must own the connected terminal.");
            if (connected is not null) ready = true;
            startedExecution = true; Completion = Task.Run(ExecuteAsync);
        }
    }
    private bool startedExecution;

    private async Task ExecuteAsync()
    {
        try
        {
            await ExecuteProgramAsync().ConfigureAwait(false);
            await CheckpointAsync(position).ConfigureAwait(false);
            lock (gate) lifecycle = lifecycle == MacroRunState.Stopping ? MacroRunState.Stopped : MacroRunState.Completed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { lock (gate) lifecycle = MacroRunState.Stopped; }
        catch (MacroTermination termination)
        { lock (gate) { reason = termination.Reason; lifecycle = MacroRunState.Stopped; } }
        catch (MacroError error)
        {
            lock (gate)
            {
                if (lifecycle == MacroRunState.Stopping) lifecycle = MacroRunState.Stopped;
                else
                {
                    reason = error.Code.ToString();
                    displayDetail = error.DisplayDetail ?? error.CaughtMessage;
                    if (error.Position.Line > 0) position = error.Position;
                    lifecycle = MacroRunState.Failed;
                }
            }
        }
        catch (ResourceLimitException)
        { lock (gate) { reason = "MemoryLimit"; lifecycle = MacroRunState.Stopped; } }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
        { lock (gate) { reason = "OperationUnavailable"; lifecycle = MacroRunState.Failed; } }
        finally
        {
            ReleaseWorker();
            TerminalSession? observed;
            lock (gate) observed = session;
            if (observed is not null) await observed.ReleaseMacroObservationsAsync(this).ConfigureAwait(false);
            lock (gate)
            {
                currentWait = null;
                operation = "";
                inFlight = false;
                history.Clear();
                programSnapshot = null;
                arguments.ReleaseContents();
                session = null;
                Pulse();
            }
            ReleaseAdmission(); Notify();
        }
    }

    public void Pause() => ChangeUserHolds(MacroAction.Pause, MacroHold.UserPause, MacroHold.None);
    public void Resume()
    {
        ChangeUserHolds(MacroAction.Resume, MacroHold.None, MacroHold.UserPause | MacroHold.RecordingReady);
    }
    public void SetCloseReview(bool reviewing) => SetHold(MacroHold.CloseReview, reviewing);
    public void ContinueAfterHistoryLoss()
    {
        ChangeUserHolds(MacroAction.ContinueAfterHistoryLoss, MacroHold.None, MacroHold.HistoryLoss);
    }

    private void ChangeUserHolds(MacroAction action, MacroHold add, MacroHold remove)
    {
        lock (gate)
        {
            if (!AvailableActions.HasFlag(action)) return;
            CaptureDeadline();
            ChangeHolds((holds & ~remove) | add);
        }
        Notify();
    }

    internal async Task RetryRecordingAsync(Func<Task> retry)
    {
        lock (gate)
        {
            if (!AvailableActions.HasFlag(MacroAction.RetryRecording)) return;
            retryingRecording = true;
        }
        Notify();
        try { await retry().ConfigureAwait(false); }
        finally
        {
            lock (gate) retryingRecording = false;
            Notify();
        }
    }
    public void Stop(string result = "Stopped")
    {
        lock (gate)
        {
            if (!IsActive) return;
            if (lifecycle != MacroRunState.Stopping)
            {
                reason = result;
                lifecycle = MacroRunState.Stopping;
                generation++;
                Pulse();
            }
            if (!stop.IsCancellationRequested) cancellation = Task.WhenAll(cancellation, stop.CancelAsync());
            if (result != "AlternateScreen" && !attemptStop.IsCancellationRequested)
                cancellation = Task.WhenAll(cancellation, attemptStop.CancelAsync());
        }
        Notify();
    }

    public void RecordingFailed()
    {
        lock (gate)
        {
            if (lifecycle is not null) return;
            if ((holds & MacroHold.RecordingFailure) != 0) return;
            ChangeHolds((holds & ~MacroHold.RecordingReady) | MacroHold.RecordingFailure);
        }
        Notify();
    }
    public void RecordingRecovered()
    {
        lock (gate)
        {
            if (lifecycle is not null || !holds.HasFlag(MacroHold.RecordingFailure)) return;
            ChangeHolds((holds & ~MacroHold.RecordingFailure) | MacroHold.RecordingReady);
        }
        Notify();
    }
    private void SetHold(MacroHold hold, bool enabled)
    {
        lock (gate) { if (!IsActive) return; CaptureDeadline(); ChangeHolds(enabled ? holds | hold : holds & ~hold); }
        Notify();
    }
    private void ChangeHolds(MacroHold value)
    {
        if (holds == value) return;
        if ((value & ~holds) != MacroHold.None && !inFlight) passwordPreparation?.Invalidate();
        long now = clock.GetTimestamp();
        if (holds == MacroHold.None && value != MacroHold.None) pauseStarted = now;
        if (holds != MacroHold.None && value == MacroHold.None) pausedTicks += now - pauseStarted;
        holds = value; generation++;
        Pulse();
    }

    internal MacroProgram? CompilePostConnection(ConnectionProfile profile) => profile.CompilePostConnectionMacro(memory);

    internal void ObserveSession(TerminalSession value) { lock (gate) session = value; }

    internal void ObserveReceive(TerminalReceiveEvent update)
    {
        switch (update.Kind)
        {
            case TerminalReceiveEventKind.Character: Receive(update.Source, update.Character, update.Literal); break;
            case TerminalReceiveEventKind.AlternateScreen: Stop("AlternateScreen"); break;
            case TerminalReceiveEventKind.Batch: ReceiveBatch(); break;
            case TerminalReceiveEventKind.Eof: ReceiveEof(); break;
        }
    }
    internal void Receive(TerminalReceiveSource source, Rune character, bool literal)
    {
        bool notify = false;
        lock (gate)
        {
            if (lifecycle is not null) return;
            CaptureDeadline();
            if (history.Append(source, character, literal) && !holds.HasFlag(MacroHold.HistoryLoss))
            {
                ChangeHolds(holds | MacroHold.HistoryLoss);
                notify = true;
            }
        }
        if (notify) Notify();
    }
    internal void ReceiveBatch()
    {
        lock (gate)
        {
            if (!IsActive || currentWait is null) return;
            receivedRevision++;
            Pulse();
        }
    }
    internal void ReceiveEof()
    { lock (gate) { CaptureDeadline(); history.Eof = true; receivedRevision++; Pulse(); } }
    internal void Disconnected() => Stop("Disconnected");

    private long ActiveMilliseconds()
    {
        long end = holds == MacroHold.None ? clock.GetTimestamp() : pauseStarted;
        return (long)Math.Max(0, (end - started - pausedTicks) * 1000.0 / clock.TimestampFrequency);
    }
    private long ElapsedMilliseconds() => (long)clock.GetElapsedTime(started).TotalMilliseconds;
    private void CaptureDeadline()
    {
        if (currentWait is { Budget: { } budget, Cutoff: null } waiting && ActiveMilliseconds() - waiting.Start >= budget)
            waiting.Cutoff = history.Cutoff();
    }

    internal async Task WaitForContinuationAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (gate) { if (holds == MacroHold.None) return; wait = changed.Task; }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private async ValueTask CheckpointAsync(MacroPosition at)
    {
        bool sliceExpired = ++instructions >= 1024 || worker && clock.GetElapsedTime(sliceStart).TotalMilliseconds >= 5;
        if (sliceExpired)
        {
            ReleaseWorker();
            await Task.Yield(); instructions = 0;
        }
        while (true)
        {
            stop.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                position = at;
                if (worker && holds == MacroHold.None) return;
            }
            ReleaseWorker();
            await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
            await MacroLimits.Workers.WaitAsync(stop.Token).ConfigureAwait(false);
            worker = true;
            sliceStart = clock.GetTimestamp();
        }
    }
    private void ReleaseWorker() { if (worker) { worker = false; MacroLimits.Workers.Release(); } }

    private async Task<T> HostOperationAsync<T>(string name, Func<Task<T>> action, bool finishing = false)
    {
        ReleaseWorker();
        while (true)
        {
            await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
            lock (gate)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (holds != MacroHold.None) continue;
                operation = name;
                inFlight = finishing;
                Pulse();
                break;
            }
        }
        Notify();
        try { return await action().ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                operation = ""; inFlight = false;
                Pulse();
            }
            Notify();
        }
    }

    private async Task SleepAsync(long milliseconds)
    {
        if (milliseconds < 0) throw new MacroError(MacroErrorCode.InvalidArgument);
        if (milliseconds == 0)
        {
            ReleaseWorker();
            await Task.Yield();
            await CheckpointAsync(position).ConfigureAwait(false);
            return;
        }
        await HostOperationAsync("Sleep", async () =>
        {
            long start; lock (gate) start = ActiveMilliseconds();
            while (true)
            {
                await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
                long remaining; Task signal;
                lock (gate) { remaining = milliseconds - Math.Min(milliseconds, ActiveMilliseconds() - start); signal = changed.Task; }
                if (remaining == 0) break;
                await WaitForSignalAsync(signal, remaining).ConfigureAwait(false);
            }
            return true;
        }).ConfigureAwait(false);
    }

    private async Task WaitForSignalAsync(Task signal, long? milliseconds)
    {
        if (milliseconds is null) { await signal.WaitAsync(stop.Token).ConfigureAwait(false); return; }
        using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        Task timer = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(milliseconds.Value, int.MaxValue)), clock, timerStop.Token);
        await Task.WhenAny(signal, timer).WaitAsync(stop.Token).ConfigureAwait(false);
        timerStop.Cancel();
    }

    private async Task<MacroMatch> WaitAsync(MacroCandidate[] candidates, bool prompt, long? timeout)
    {
        RequireSession();
        if (timeout < 0 || candidates.Length is < 1 or > 16) throw new MacroError(MacroErrorCode.InvalidArgument);
        return await HostOperationAsync("Wait", async () =>
        {
            MacroWait waiting;
            lock (gate) { waiting = new(ActiveMilliseconds(), timeout); currentWait = waiting; }
            try
            {
                while (true)
                {
                    await WaitForContinuationAsync(stop.Token).ConfigureAwait(false);
                    MacroReceiveSnapshot snapshot; long revision, epoch; Task signal;
                    bool evaluatingCutoff;
                    lock (gate)
                    {
                        CaptureDeadline(); epoch = generation; revision = receivedRevision;
                        evaluatingCutoff = waiting.Cutoff is not null;
                        snapshot = history.Snapshot(waiting.ScannedThrough, waiting.Cutoff);
                        signal = changed.Task;
                    }
                    using var snapshotMemory = memory.Reserve(snapshot.Lines.Sum(line => 48L + line.Text.Length * 2L));
                    (long Line, MacroMatch Match)? found = null; MacroError? error = null;
                    long scannedThrough = waiting.ScannedThrough;
                    try
                    {
                        (found, scannedThrough) = await MacroReceiveHistory.SearchAsync(snapshot, candidates, prompt,
                            scannedThrough, () => Volatile.Read(ref generation) == epoch, stop.Token).ConfigureAwait(false);
                    }
                    catch (MacroError failure) { error = failure; }
                    long? remaining;
                    lock (gate)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        if (epoch != generation || holds != MacroHold.None) continue;
                        if (error is not null) throw error;
                        if (found is { } match && history.Consume(match.Line, checked((int)(match.Match.Index + match.Match.Length))))
                        { generation++; Own(match.Match); return match.Match; }
                        waiting.ScannedThrough = scannedThrough;
                        if (snapshot.Eof) throw new MacroError(MacroErrorCode.EndOfInput);
                        if (evaluatingCutoff) throw new MacroError(MacroErrorCode.WaitTimeout);
                        if (waiting.Cutoff is not null) continue;
                        if (revision != receivedRevision) continue;
                        remaining = timeout is { } limit ? Math.Max(0, limit - Math.Min(limit, ActiveMilliseconds() - waiting.Start)) : null;
                    }
                    await WaitForSignalAsync(signal, remaining).ConfigureAwait(false);
                }
            }
            finally { lock (gate) currentWait = null; }
        }).ConfigureAwait(false);
    }

    private TerminalSession RequireSession()
    {
        lock (gate) return ready && session is { } connected ? connected : throw new MacroError(MacroErrorCode.NotConnected);
    }
    internal ResourceReservation ReserveTransient(long bytes) => memory.Reserve(bytes);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var prior = changed; changed = Signal(); prior.TrySetResult(); }
    private void Notify() => Changed?.Invoke();

    private void Own(object value)
    {
        if (value.GetType().IsValueType || value is string captured && Program.OwnsConstantString(captured)) return;
        long size = value switch
        {
            string text => 32 + text.Length * 2L,
            MacroList list => list.ReservedBytes,
            MacroCandidate[] candidates => 32 + candidates.Length * 16L,
            MacroPattern pattern => 64 + pattern.Text.Length * 4L,
            MacroMatch match => 64 + match.Text.Length * 2L + match.Groups.Sum(g => 64L + g.Name.Length * 2L + g.Value.Length * 2L + g.Captures.Sum(c => 32L + c.Length * 2L)),
            MacroCapture group => 64 + group.Name.Length * 2L + group.Value.Length * 2L + group.Captures.Sum(c => 32L + c.Length * 2L),
            Dictionary<string, object> scope => 32 + scope.Count * 48L,
            _ => 64,
        };
        allocations.GetValue(value, _ => memory.Reserve(0)).Resize(size);
        if (value is MacroPattern candidate)
        {
            try
            {
                patternAllocations.GetValue(candidate, _ =>
                {
                    if (patterns.Used >= 32)
                    {
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: true);
                        GC.WaitForPendingFinalizers();
                    }
                    return patterns.Reserve(1);
                });
            }
            catch (ResourceLimitException) { throw new MacroError(MacroErrorCode.PatternLimit); }
        }
    }
    private void ReleaseAdmission()
    {
        if (!admitted) return;
        admitted = false;
        foreach (var entry in allocations) entry.Value.Dispose();
        foreach (var entry in patternAllocations) entry.Value.Dispose();
        allocations.Clear(); patternAllocations.Clear(); initialMemory.Dispose(); MacroLimits.Runs.Release();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop();
        Task release = Task.CompletedTask;
        if (!startedExecution)
        {
            TerminalSession? observed;
            lock (gate) { observed = session; session = null; }
            if (observed is not null) release = observed.ReleaseMacroObservationsAsync(this);
            ReleaseAdmission();
        }
        _ = Task.WhenAll(Completion, cancellation, release).ContinueWith(task =>
        {
            _ = task.Exception;
            stop.Dispose();
            attemptStop.Dispose();
        }, TaskScheduler.Default);
    }
}
