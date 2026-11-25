using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TabTty.AiTransport;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private readonly TextBlock aiStatus = new();
    private readonly WrapPanel aiActions = new();
    private AiCollaboration.Sharing? actionShare;
    private AiCollaboration.Control? actionControl;
    private TerminalSession? retainedAiSession;
    private long aiAuthorityRevision;
    internal AiCollaboration.Sharing? AiSharing { get; set; }
    internal TerminalSession? AiSession => removed ? null : session ?? retainedAiSession;
    internal MainWindow? AiHost => tabHost?.Invoke();
    internal (int Columns, int Rows) AiGrid => snapshot is { } view ? (view.Columns, view.ScreenRows) : appliedGrid;
    internal bool HasAiHighlightRules => ((App)Application.Current).Highlights.Find(highlightSetId) is { Enabled: true, Rules.Count: > 0 };
    internal (long Id, long Revision, TerminalHighlightSpan[] Spans)? CaptureAiHighlightRow(int row) =>
        panes[output].Highlight.CaptureAiRow(row);
    internal bool IsAiShared => AiSharing is not null;
    internal bool IsAiTakingControl { get; set; }
    internal bool IsAiControlling => AiSharing?.Control is not null;
    internal bool AiCleanupDelayed { get; set; }
    internal AiRateLimit? AiSendRate { get; set; }
    internal bool HasAiGrantConflict => HasPendingInput || pasteReview is not null || Volatile.Read(ref pendingMouse) != 0 ||
        HasMouseCapture || panes.Keys.Any(pane => pane.HasComposition) || Broadcast.HasUnfinishedOperation(this);

    private AiCollaboration AiCollaboration => ((App)Application.Current).Ai;
    internal void ReportAiUncertain(TerminalSession target)
    {
        if (!removed)
            Reports.Report(new object(), $"{BroadcastTargetLabel(target)} — {Text("AiUncertainDelivery")}");
    }
    internal string? AiStateLabel => IsAiTakingControl && IsAiShared ? Text("AiTakingControl")
        : IsAiControlling ? Text("AiControlling") : IsAiShared ? Text("AiReadOnly") : null;

    private void InitializeAiControls()
    {
        ConfigureStatusIndicator(aiStatus);
        DockPanel.SetDock(aiStatus, Dock.Right);
        statusBar.Children.Add(aiStatus);
        AutomationProperties.SetLiveSetting(aiStatus, AutomationLiveSetting.Polite);
        UpdateAiControls();
    }

    private void UpdateAiActions()
    {
        var share = AiSharing;
        if (actionShare != share)
        {
            actionShare = share;
            actionControl = null;
            aiActions.Children.Clear();
            if (share is not null)
                aiActions.Children.Add(ActionButton("AiEndSharing", () =>
                {
                    if (AiSharing == share) AiCollaboration.EndSharing(this);
                }));
        }
        if (share is null) { Notices.Remove(aiActions); return; }
        if (share.Control != actionControl)
        {
            if (actionControl is not null) aiActions.Children.RemoveAt(0);
            actionControl = share.Control;
            if (actionControl is { } control)
                aiActions.Children.Insert(0, ActionButton("AiTakeControl", () =>
                {
                    if (AiSharing == share && share.Control == control) AiCollaboration.EndControl(this);
                }));
        }
        foreach (Button button in aiActions.Children)
        {
            string label = Text((string)button.Tag);
            button.Content = label;
            AutomationProperties.SetName(button, label);
        }
        Notices.ShowContent(aiActions, aiActions, keepVisible: true);

        static Button ActionButton(string label, Action act)
        {
            var button = new Button { Tag = label, Margin = new Thickness(2) };
            button.Click += (_, _) => act();
            return button;
        }
    }

    internal void AiAuthorityChanged()
    {
        aiAuthorityRevision++;
        foreach (var pane in panes.Keys) pane.SetConnected(InputSession is not null);
        UpdateAiControls();
        CaptionChanged?.Invoke();
    }

    private void UpdateAiControls()
    {
        string? state = IsAiTakingControl ? Text("AiTakingControl") : AiStateLabel;
        aiStatus.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateAiActions();
        string detail = AiSharing is { } share ? "  " + share.Peer.Label : "";
        if (AiCleanupDelayed) detail += "  " + Text("AiCleanupPending");
        if (AiSharing?.Reason is { Length: > 0 } reason)
            detail += "\n" + string.Format(Text("AiHandoffMessage"), reason);
        string updated = state + detail;
        if (Equals(aiStatus.ToolTip, updated)) return;
        aiStatus.Text = state ?? "";
        aiStatus.ToolTip = updated;
        AutomationProperties.SetName(aiStatus, updated);
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(aiStatus) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    internal void FillAiMenu(ContextMenu menu)
    {
        var shareMenu = new MenuItem { Header = Text("AiShareOutput") };
        string? unavailable = AiCollaboration.ShareUnavailable(this);
        shareMenu.IsEnabled = unavailable is null && AiCollaboration.Peers.Count > 0;
        if (unavailable is not null) shareMenu.ToolTip = Text(unavailable);
        else if (AiCollaboration.Peers.Count == 0) shareMenu.ToolTip = Text("AiNoCounterpart");
        ToolTipService.SetShowOnDisabled(shareMenu, true);
        foreach (var peer in AiCollaboration.Peers)
        {
            string recipient = peer.Label + "\n" + LauncherDescription(peer);
            var choice = new MenuItem { Header = new TextBlock { Text = recipient, MaxWidth = 540,
                    TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = recipient + "\n" + PermissionDisplay.Literal(peer.Location) + "\n" + Text("AiCounterpartScope") };
            AutomationProperties.SetName(choice, recipient);
            choice.Click += (_, _) => ConfirmAiPermission(peer, grantControl: false);
            shareMenu.Items.Add(choice);
        }
        menu.Items.Add(shareMenu);
        if (IsAiShared)
        {
            if (IsAiControlling)
                Add("AiTakeControl", () => AiCollaboration.EndControl(this));
            else
            {
                string? conflict = AiCollaboration.GrantUnavailable(this);
                var peer = AiSharing!.Peer;
                Add("AiGiveControl", () => ConfirmAiPermission(peer, grantControl: true), conflict);
            }
            Add("AiEndSharing", () => AiCollaboration.EndSharing(this));
        }
        Add("AiSetup", () => AiCollaboration.ShowSetup(this));

        void Add(string label, Action action, string? disabled = null)
        {
            var item = new MenuItem { Header = Text(label), IsEnabled = disabled is null };
            if (disabled is not null) item.ToolTip = Text(disabled);
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private static string LauncherDescription(AiCollaboration.Counterpart peer) =>
        string.Format(Text("AiLauncherProcess"), PermissionDisplay.Literal(peer.LauncherPath), peer.LauncherId);

    private Window? aiConsent;

    private async void ConfirmAiPermission(AiCollaboration.Counterpart peer, bool grantControl)
    {
        if (aiConsent is { } existing) { existing.Activate(); return; }
        var collaboration = AiCollaboration;
        var share = AiSharing;
        if (grantControl ? share?.Peer != peer : share?.Peer == peer) return;
        string? unavailable = grantControl ? collaboration.GrantUnavailable(this) : collaboration.ShareUnavailable(this);
        if (unavailable is not null) { SetStatus(statusKey, Text(unavailable)); return; }
        if (ClosePending || AiSession is not { } sessionAtPrompt || AiHost is not { } owner || !collaboration.Peers.Contains(peer)) return;
        var generation = ConnectionGeneration;
        var revision = aiAuthorityRevision;
        string title = Text(grantControl ? "AiGiveControl" : "AiShareOutput");
        string message = string.Format(Text(grantControl ? "AiConfirmControl" : "AiConfirmShare"),
            PermissionDisplay.Literal(TabCaption), peer.Code, LauncherDescription(peer), ConnectionDestination.From(sessionAtPrompt));
        message += "\n\n" + Text("AiUnattendedConsent");
        if (!grantControl && share is not null)
            message += "\n\n" + string.Format(Text("AiConfirmSwitch"), share.Peer.Code);
        var answer = MessageBoxResult.Cancel;
        var dialog = ThemedMessageBox.Create(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel, selected => answer = selected, modelessDecision: true);
        aiConsent = dialog;
        try { await PresentDecisionAsync(dialog, IsRunning ? InputLifetime : CancellationToken.None); }
        catch (OperationCanceledException) { return; }
        finally { aiConsent = null; }
        if (answer != MessageBoxResult.OK) return;
        if (ClosePending || ConnectionGeneration != generation || AiSession != sessionAtPrompt || AiSharing != share ||
            aiAuthorityRevision != revision ||
            AiHost != owner || !collaboration.Peers.Contains(peer))
        {
            SetStatus(statusKey, Text("AiConsentExpired"));
            return;
        }
        string? problem = grantControl ? collaboration.Grant(this) : collaboration.Share(this, peer);
        if (problem is not null) SetStatus(statusKey, Text(problem));
    }
}
