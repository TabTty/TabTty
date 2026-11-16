using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private sealed class TerminalClosure(MainWindow window)
    {
        private readonly ConditionalWeakTable<WindowTab, Task<CloseResult>> requests = new();

        internal Task<CloseResult> CloseTabAsync(WindowTab item)
        {
            if (requests.TryGetValue(item, out var pending)) return pending;
            if (window.closing || window.GroupOf(item) is not { } group || item.Content is not TerminalTab terminal)
                return Task.FromResult(new CloseResult(CloseDisposition.TargetChanged));

            var transfer = terminal.TransferWindow;
            var approval = new CloseApproval(terminal.ConnectionGeneration, transfer, transfer?.OperationRevision ?? 0,
                terminal.Macro, terminal.IsRunning || terminal.HasActiveTransfer || terminal.HasActiveMacro);
            var execution = RunAsync();
            requests.Add(item, execution);
            return execution;

            async Task<CloseResult> RunAsync()
            {
                // WPF dialogs pump messages. Save the request before presenting one.
                await Task.Yield();
                bool removed = false;
                bool started = false;
                bool adopted = false;
                TransferWindowCloseResult? transferOutcome = null;
                try
                {
                    if (!TargetStillMatches()) return Changed();
                    if (approval.NeedsConfirmation && !window.ConfirmClose([terminal]))
                        return new(CloseDisposition.Declined);
                    if (!TargetStillMatches()) return Changed();

                    started = item.IsClosing = true;
                    window.app.Ai.EndSharing(terminal);
                    Task<TransferWindowCloseResult> transferClose = terminal.CloseFileTransferAsync();
                    terminal.Disconnect();
                    Task sessionCleanup = terminal.Completion;
                    Task<bool> wait = window.WaitForCleanupAsync(sessionCleanup, terminal.AbandonCleanup);
                    await Task.WhenAll(wait, transferClose);
                    adopted = await wait;
                    transferOutcome = await transferClose;
                    if (!transferOutcome.Value.Dismissed)
                        return new(CloseDisposition.TransferRetained, adopted, transferOutcome);

                    window.RemoveTerminalTab(item);
                    removed = window.GroupOf(item) is null;
                    return new(removed ? CloseDisposition.Removed : CloseDisposition.CleanupFailed, adopted, transferOutcome);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException || IsPresentationFailure(exception))
                {
                    Report(exception.Message, Text("Error"));
                    return new(started ? CloseDisposition.CleanupFailed : CloseDisposition.ReviewFailed, adopted, transferOutcome);
                }
                finally
                {
                    if (!removed)
                    {
                        item.IsClosing = false;
                        terminal.Macro?.SetCloseReview(false);
                        requests.Remove(item);
                    }
                }
            }

            bool TargetStillMatches()
            {
                var currentTransfer = terminal.TransferWindow;
                return !window.closing && window.GroupOf(item) == group && ReferenceEquals(item.Content, terminal) &&
                    ReferenceEquals(terminal.ConnectionGeneration, approval.Generation) &&
                    (currentTransfer is null || (ReferenceEquals(currentTransfer, approval.Transfer) &&
                        currentTransfer.OperationRevision == approval.TransferRevision)) &&
                    (terminal.Macro is null || ReferenceEquals(terminal.Macro, approval.Macro));
            }

            CloseResult Changed()
            {
                Report(Text("CloseTargetChanged"), Text("CloseTab"));
                return new(CloseDisposition.TargetChanged);
            }
        }

        private void Report(string message, string title)
        {
            try { window.app.Modal(() => ThemedMessageBox.Show(window, message, title, icon: MessageBoxImage.Warning)); }
            catch (Exception exception) when (IsPresentationFailure(exception)) { }
        }

        internal static bool IsPresentationFailure(Exception error) => error is InvalidOperationException or Win32Exception;

        private readonly record struct CloseApproval(object Generation, FileTransferWindow? Transfer, long TransferRevision,
            object? Macro, bool NeedsConfirmation);

        internal enum CloseDisposition { Declined, TargetChanged, ReviewFailed, TransferRetained, CleanupFailed, Removed }
        internal readonly record struct CloseResult(CloseDisposition Disposition, bool CleanupAdopted = false,
            TransferWindowCloseResult? Transfer = null);
    }
}
