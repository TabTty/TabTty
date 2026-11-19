using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private sealed class TerminalClosure(MainWindow window)
    {
        private readonly ConditionalWeakTable<WindowTab, Request> requests = new();
        private readonly ConditionalWeakTable<WindowTab, AutoAttempt> automaticAttempts = new();
        // Only presentation is serialized here. Resource work can finish while a review is open.
        private readonly SemaphoreSlim prompts = new(1);
        private Task<CloseDisposition>? windowRequest;
        private Task? disconnectedRequest;

        internal Task<CloseResult> CloseTabAsync(WindowTab item) => CloseTabAsync(item, false);

        internal void CloseAutomatically(WindowTab item)
        {
            if (!item.CanCloseAsDisconnected || item.Content is not TerminalTab terminal) return;
            var attempt = automaticAttempts.GetOrCreateValue(item);
            if (ReferenceEquals(attempt.Generation, terminal.ConnectionGeneration)) return;
            attempt.Generation = terminal.ConnectionGeneration;
            _ = CloseTabAsync(item, true);
        }

        private Task<CloseResult> CloseTabAsync(WindowTab item, bool disconnectedOnly)
        {
            if (requests.TryGetValue(item, out var pending)) return pending.Completion.Task;
            if (window.closing || Capture(item) is not { } target)
                return Task.FromResult(new CloseResult(CloseDisposition.TargetChanged));
            var request = Register(target);
            _ = ReviewOneAsync(request, disconnectedOnly);
            return request.Completion.Task;
        }

        internal Task CloseDisconnectedTabsAsync() =>
            disconnectedRequest ??= CloseManyAsync(false);

        internal Task<CloseDisposition> CloseWindowAsync() =>
            windowRequest ??= CloseManyAsync(true);

        private async Task ReviewOneAsync(Request request, bool disconnectedOnly)
        {
            await Task.Yield();
            var target = request.Target;
            CloseDisposition? refused;
            try
            {
                refused = await ReviewAsync([target], disconnectedOnly, () => !window.closing,
                    () => request.Started);
            }
            catch (Exception exception) when (Expected(exception))
            {
                Finish(request, new(CloseDisposition.ReviewFailed));
                return;
            }
            if (request.Started) return;
            if (refused is { } reason)
            {
                Finish(request, new(reason));
                return;
            }
            Begin(request);
        }

        private async Task<CloseDisposition> CloseManyAsync(bool entireWindow)
        {
            WindowTab[] scope = window.AllTabs.ToArray();
            var targets = scope.Where(item => entireWindow || item.CanCloseAsDisconnected)
                .Select(Capture).OfType<Target>().ToArray();
            Request[] pendingAtEntry = targets.Select(target =>
                requests.TryGetValue(target.Item, out var request) ? request : null).OfType<Request>().ToArray();
            await Task.Yield();
            bool claimedWindow = false;
            try
            {
                if (window.closed) return CloseDisposition.Removed;
                if (window.closing) return CloseDisposition.TargetChanged;
                // A review underway has not authorized termination yet. Wait only for
                // that decision, then distinguish its accepted work from new targets.
                await Task.WhenAll(pendingAtEntry.Select(request => request.Decision.Task));
                if (window.closed) return CloseDisposition.Removed;

                var joined = pendingAtEntry.Where(request => request.Started).ToList();
                var joinedItems = joined.Select(request => request.Target.Item).ToHashSet();
                var unreviewed = new List<Target>();
                foreach (Target target in targets)
                {
                    if (joinedItems.Contains(target.Item)) continue;
                    if (requests.TryGetValue(target.Item, out var existing) && existing.Started)
                        joined.Add(existing);
                    else unreviewed.Add(target);
                }
                var refused = await ReviewAsync(unreviewed, !entireWindow,
                    () => !window.closing && (!entireWindow || WindowScopeMatches(scope)));
                if (refused is { } reason) return reason;

                // Do not yield between validating the review and claiming its targets.
                if (entireWindow)
                {
                    claimedWindow = window.closing = true;
                    window.SetWindowCloseControls(pending: true);
                }
                foreach (Target target in unreviewed)
                {
                    if (requests.TryGetValue(target.Item, out var existing))
                    {
                        joined.Add(existing);
                        if (!existing.Started) Begin(existing);
                        continue;
                    }
                    var request = Register(target);
                    joined.Add(request);
                    Begin(request);
                }

                CloseResult[] outcomes = await Task.WhenAll(joined.Select(request => request.Completion.Task));
                if (outcomes.Any(result => result.Disposition != CloseDisposition.Removed))
                    return CloseDisposition.CleanupFailed;
                if (!entireWindow) return CloseDisposition.Removed;
                if (window.closed) return CloseDisposition.Removed;
                if (!WindowScopeMatches(scope) || window.TerminalTabs.Any())
                    return Changed();
                window.readyToClose = true;
                window.Close();
                return window.closed ? CloseDisposition.Removed : CloseDisposition.CleanupFailed;
            }
            catch (Exception exception) when (Expected(exception))
            {
                Report(exception.Message, Text("Error"));
                return CloseDisposition.CleanupFailed;
            }
            finally
            {
                if (claimedWindow && !window.closed)
                {
                    window.closing = window.readyToClose = false;
                    window.SetWindowCloseControls(pending: false);
                }
                if (entireWindow) windowRequest = null;
                else disconnectedRequest = null;
                if (!entireWindow) window.CloseIfEmpty();
            }
        }

        private async Task<CloseDisposition?> ReviewAsync(IReadOnlyList<Target> targets, bool disconnectedOnly,
            Func<bool> scopeMatches, Func<bool>? alreadyAccepted = null)
        {
            await prompts.WaitAsync();
            TerminalTab[] reviewed = [];
            bool accepted = false;
            try
            {
                if (alreadyAccepted?.Invoke() == true) return null;
                bool Matches() => scopeMatches() && targets.All(target =>
                    (requests.TryGetValue(target.Item, out var existing) && existing.Started) ||
                    (TargetMatches(target) && (!disconnectedOnly || target.Terminal.IsDisconnected)));
                if (!Matches()) return Changed();
                reviewed = targets.Where(target =>
                    !(requests.TryGetValue(target.Item, out var existing) && existing.Started)).Select(target => target.Terminal)
                    .Where(terminal => terminal.IsRunning || terminal.HasActiveTransfer || terminal.HasActiveMacro).ToArray();
                if (reviewed.Length != 0 && !window.ConfirmClose(reviewed)) return CloseDisposition.Declined;
                if (!Matches()) return Changed();
                accepted = true;
                return null;
            }
            finally
            {
                if (!accepted)
                    foreach (TerminalTab terminal in reviewed) terminal.Macro?.SetCloseReview(false);
                prompts.Release();
            }
        }

        private Request Register(Target target)
        {
            var request = new Request(target);
            requests.Add(target.Item, request);
            return request;
        }

        private void Begin(Request request)
        {
            request.Started = request.Target.Item.IsClosing = true;
            request.Target.Terminal.ClosePending = true;
            window.app.Ai.EndSharing(request.Target.Terminal);
            request.Target.Terminal.ProtectRecordingClose();
            request.Decision.TrySetResult();
            _ = ExecuteAsync(request);
        }

        private async Task ExecuteAsync(Request request)
        {
            // The disconnect event itself may be raised in the finally block of this
            // session's completion, so never wait on it in the event handler.
            await Task.Yield();
            var target = request.Target;
            var terminal = target.Terminal;
            Task<TransferWindowCloseResult>? transfer = null;
            Task<CleanupDisposition>? actualCleanup = null;
            Task<CleanupDisposition>? outputRelease = null;
            bool adopted = false;
            var outcome = CloseDisposition.CleanupFailed;
            TransferWindowCloseResult? transferOutcome = null;
            try
            {
                transfer = terminal.CloseFileTransferAsync();
                terminal.Disconnect();
                actualCleanup = ObserveCleanupAsync(terminal.Completion);
                Task<bool> wait = WaitForCleanupAsync(request, actualCleanup);
                await Task.WhenAll(wait, transfer);
                adopted = await wait;
                transferOutcome = await transfer;
                if (!transferOutcome.Value.Dismissed)
                    outcome = CloseDisposition.TransferRetained;
                else if (adopted || await actualCleanup == CleanupDisposition.Completed)
                {
                    if (!ReferenceEquals(terminal.ConnectionGeneration, target.Generation))
                    {
                        outcome = Changed();
                    }
                    else
                    {
                        var (removed, released) = window.RemoveTerminalTab(target.Item);
                        if (removed)
                        {
                            outcome = CloseDisposition.Removed;
                            outputRelease = ObserveCleanupAsync(released);
                            if ((!adopted || outputRelease.IsCompleted) && await outputRelease == CleanupDisposition.Failed)
                                Report(Text("CloseCleanupFailed"), Text("Error"));
                        }
                    }
                }
                else Report(Text("CloseCleanupFailed"), Text("Error"));
            }
            catch (Exception exception) when (Expected(exception))
            {
                actualCleanup ??= ObserveCleanupAsync(terminal.Completion);
                if (transfer is not null)
                {
                    try { transferOutcome = await transfer; }
                    catch (Exception transferError) when (Expected(transferError)) { }
                }
                Report(exception.Message, Text("Error"));
            }
            finally
            {
                Finish(request, new(outcome, adopted, transferOutcome, actualCleanup, outputRelease,
                    target.Terminal.RecordingCloseStatus));
                window.CloseIfEmpty();
            }
        }

        internal async Task ReviewRecordingLossAsync(WindowTab item)
        {
            if (!requests.TryGetValue(item, out var request) || !request.Started ||
                request.Cleanup is not { } cleanup) return;
            await prompts.WaitAsync();
            try
            {
                var terminal = request.Target.Terminal;
                if (cleanup.IsCompleted || !terminal.RecordingReleasePending) return;
                if (!window.app.CanOwnCleanup)
                {
                    Report(Text("RecordingCleanupCapacity"), Text("CleanupPending"));
                    return;
                }
                if (!window.ConfirmCleanupLoss() || cleanup.IsCompleted || !terminal.RecordingReleasePending) return;
                if (window.app.OwnCleanup(cleanup))
                {
                    if (terminal.AbandonCleanup()) request.LossAccepted.TrySetResult();
                }
                else Report(Text("RecordingCleanupCapacity"), Text("CleanupPending"));
            }
            catch (Exception error) when (IsPresentationFailure(error)) { }
            finally { prompts.Release(); }
        }

        private async Task<bool> WaitForCleanupAsync(Request request, Task cleanup)
        {
            request.Cleanup = cleanup;
            Task completed = await Task.WhenAny(cleanup, request.LossAccepted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != cleanup && completed != request.LossAccepted.Task &&
                request.Target.Terminal.RecordingReleasePending)
                await ReviewRecordingLossAsync(request.Target.Item);

            await Task.WhenAny(cleanup, request.LossAccepted.Task);
            return request.LossAccepted.Task.IsCompleted;
        }

        private static async Task<CleanupDisposition> ObserveCleanupAsync(Task cleanup)
        {
            try { await cleanup; return CleanupDisposition.Completed; }
            catch (Exception exception) when (Expected(exception)) { return CleanupDisposition.Failed; }
        }

        private void Finish(Request request, CloseResult result)
        {
            request.Decision.TrySetResult();
            if (result.Disposition != CloseDisposition.Removed)
            {
                request.Target.Item.IsClosing = false;
                request.Target.Terminal.ClosePending = false;
                request.Target.Terminal.Macro?.SetCloseReview(false);
                requests.Remove(request.Target.Item);
            }
            request.Completion.TrySetResult(result);
        }

        private Target? Capture(WindowTab item)
        {
            if (item.Content is not TerminalTab terminal || window.GroupOf(item) is not { } group) return null;
            var transfer = terminal.TransferWindow;
            return new(item, terminal, group, terminal.ConnectionGeneration, transfer, transfer?.OperationRevision ?? 0, terminal.Macro);
        }

        private bool TargetMatches(Target target)
        {
            var terminal = target.Terminal;
            var transfer = terminal.TransferWindow;
            return window.GroupOf(target.Item) == target.Group && ReferenceEquals(target.Item.Content, terminal) &&
                ReferenceEquals(terminal.ConnectionGeneration, target.Generation) &&
                (transfer is null || (ReferenceEquals(transfer, target.Transfer) && transfer.OperationRevision == target.TransferRevision)) &&
                (terminal.Macro is null || ReferenceEquals(terminal.Macro, target.Macro));
        }

        private bool WindowScopeMatches(WindowTab[] scope) =>
            window.AllTabs.All(item => scope.Contains(item) && item.Content is not FormView { BlocksClose: true });

        private CloseDisposition Changed()
        {
            Report(Text("CloseTargetChanged"), Text("CloseTab"));
            return CloseDisposition.TargetChanged;
        }

        private void Report(string message, string title)
        {
            try { window.app.Modal(() => ThemedMessageBox.Show(window, message, title, icon: MessageBoxImage.Warning)); }
            catch (Exception exception) when (IsPresentationFailure(exception)) { }
        }

        private static bool IsPresentationFailure(Exception error) => error is InvalidOperationException or Win32Exception;
        private static bool Expected(Exception error) => error is IOException or OperationCanceledException || IsPresentationFailure(error);

        private sealed class AutoAttempt { public object? Generation; }
        private sealed record Target(WindowTab Item, TerminalTab Terminal, TabControl Group, object Generation,
            FileTransferWindow? Transfer, long TransferRevision, object? Macro);

        private sealed class Request(Target target)
        {
            internal Target Target { get; } = target;
            internal TaskCompletionSource Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<CloseResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task? Cleanup;
            internal TaskCompletionSource LossAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Started;
        }

        internal enum CloseDisposition { Declined, TargetChanged, ReviewFailed, TransferRetained, CleanupFailed, Removed }
        internal enum CleanupDisposition { Completed, Failed }
        internal readonly record struct CloseResult(CloseDisposition Disposition, bool CleanupAdopted = false,
            TransferWindowCloseResult? Transfer = null, Task<CleanupDisposition>? Cleanup = null,
            Task<CleanupDisposition>? OutputRelease = null, RecordingStatus? Recording = null);
    }
}
