using System.Windows;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private readonly List<TabDecision> pendingDecisions = [];
    internal bool HasPendingDecision => pendingDecisions.Count != 0;
    internal Window? DecisionHost => tabHost?.Invoke() ?? Window.GetWindow(this);

    internal void ActivatePendingDecision() => pendingDecisions.FirstOrDefault()?.Activate();

    internal void PendingDecisionHostChanged()
    {
        foreach (var decision in pendingDecisions) decision.HostMoved();
    }

    internal void CancelPendingDecisions()
    {
        foreach (var decision in pendingDecisions.ToArray()) decision.RemoveTarget(this);
    }

    internal void TrackDecision(TabDecision decision, bool pending)
    {
        if (pending) pendingDecisions.Add(decision);
        else pendingDecisions.Remove(decision);
        CaptionChanged?.Invoke();
    }

    internal Task PresentDecisionAsync(Window dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (removed || ClosePending) throw new OperationCanceledException(token);
        var decision = new TabDecision(dialog);
        decision.SetTargets([this]);
        return decision.ShowAsync(token);
    }
}

// One dialog and answer belong to an operation even as its terminal moves. A shared
// vault unlock can have several live consumers and outlive its first terminal.
internal sealed class TabDecision(Window dialog, bool shared = false, Action? hostMoved = null)
{
    internal static async Task<bool> ShowFormAsync(App app, MainWindow.FormWindow form,
        CancellationToken token, TerminalTab? tab = null)
    {
        token.ThrowIfCancellationRequested();
        if (tab is not null)
        {
            await tab.PresentDecisionAsync(form, token);
            return form.Accepted;
        }
        using var cancellation = token.Register(() => form.Dispatcher.BeginInvoke(() =>
        {
            if (form.IsVisible) form.Close();
        }));
        bool accepted = app.Modal(form.ShowDialog) == true;
        token.ThrowIfCancellationRequested();
        return accepted;
    }

    private TerminalTab[] targets = [];
    private DialogText? targetNames;
    private DialogField? targetField;
    private bool shown;
    private bool retired;

    internal void SetTargets(TerminalTab[] next)
    {
        if (retired) return;
        foreach (var tab in targets.Except(next)) tab.TrackDecision(this, false);
        foreach (var tab in next.Except(targets)) tab.TrackDecision(this, true);
        targets = next;
        if (dialog is MainWindow.FormWindow form)
        {
            targetNames ??= new DialogText("", shared ? 100 : null) { IsTabStop = false };
            targetField ??= form.AddPinnedField("DecisionTargetTabLabel", targetNames);
            targetField.Visibility = next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            targetNames.Text = string.Join(Environment.NewLine, next.Select(tab => PermissionDisplay.Literal(tab.TabCaption)));
        }
        UpdateOwner();
    }

    internal void RemoveTarget(TerminalTab tab)
    {
        if (shared) SetTargets(targets.Where(target => target != tab).ToArray());
        else if (shown) dialog.Close();
    }

    internal void Activate()
    {
        if (dialog.IsVisible) dialog.Activate();
    }

    internal void HostMoved()
    {
        UpdateOwner();
        hostMoved?.Invoke();
    }

    private void UpdateOwner()
    {
        // Shared unlock is positioned beside its current consumer. OS ownership would
        // close it as soon as that particular consumer's window exits.
        if (shared || targets.FirstOrDefault()?.DecisionHost is not { } owner) return;
        Window? visibleOwner = owner.IsVisible && owner.WindowState != WindowState.Minimized ? owner : null;
        if (dialog.Owner != visibleOwner) dialog.Owner = visibleOwner;
        dialog.ShowInTaskbar = visibleOwner is null;
    }

    internal async Task ShowAsync(CancellationToken token, bool modal = false)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Retire()
        {
            if (retired) return;
            retired = true;
            foreach (var tab in targets) tab.TrackDecision(this, false);
            targets = [];
        }
        void Closed(object? sender, EventArgs args) { Retire(); closed.TrySetResult(); }
        void Escape(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            dialog.Close();
        }
        dialog.Closed += Closed;
        dialog.PreviewKeyDown += Escape;
        try
        {
            token.ThrowIfCancellationRequested();
            if (targets.FirstOrDefault() is { } tab)
            {
                var owner = tab.DecisionHost ?? dialog.Owner;
                dialog.ShowActivated = owner is MainWindow main ? main.IsCurrentView(tab) :
                    owner is { IsActive: true } && tab.IsVisible;
            }
            if (dialog is MainWindow.FormWindow form) form.IsDecision = !modal;
            using var cancellation = token.Register(() => dialog.Dispatcher.BeginInvoke(() =>
            {
                if (dialog.IsVisible) dialog.Close();
            }));
            shown = true;
            if (modal) ((App)Application.Current).Modal(dialog.ShowDialog);
            else dialog.Show();
            await closed.Task;
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            dialog.Closed -= Closed;
            dialog.PreviewKeyDown -= Escape;
            Retire();
        }
    }
}
