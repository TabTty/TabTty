using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private readonly TextBlock aiStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button aiTakeover = new() { Margin = new Thickness(8, 0, 0, 0) };
    private readonly DockPanel aiStatusBar = new() { Margin = new Thickness(8, 2, 8, 2) };
    private TerminalSession? retainedAiSession;
    internal AiCollaboration.Sharing? AiSharing { get; set; }
    internal TerminalSession? AiSession => session ?? retainedAiSession;
    internal (int Columns, int Rows) AiGrid => snapshot is { } view ? (view.Columns, view.ScreenRows) : appliedGrid;
    internal bool HasAiHighlightRules => ((App)Application.Current).Highlights.Find(highlightSetId) is { Enabled: true, Rules.Count: > 0 };
    internal (long Id, long Revision, TerminalHighlightSpan[] Spans)? CaptureAiHighlightRow(int row) =>
        panes[output].Highlight.CaptureAiRow(row);
    internal bool IsAiShared => AiSharing is not null;
    internal bool IsAiTakingControl { get; set; }
    internal bool IsAiControlling => AiSharing?.Control is not null;
    internal bool AiCleanupDelayed { get; set; }
    internal AiCollaboration.RateLimit? AiSendRate { get; set; }
    internal bool HasAiGrantConflict => HasPendingInput || reviewingPaste || Volatile.Read(ref pendingMouse) != 0 ||
        HasMouseCapture || panes.Keys.Any(pane => pane.HasComposition) || Broadcast.HasUnfinishedOperation(this);

    private AiCollaboration AiCollaboration => ((App)Application.Current).Ai;
    internal void ReportAiUncertain() => SetStatus(statusKey, Text("AiUncertainDelivery"));
    internal string? AiStateLabel => IsAiTakingControl && IsAiShared ? Text("AiTakingControl")
        : IsAiControlling ? Text("AiControlling") : IsAiShared ? Text("AiReadOnly") : null;

    private void InitializeAiControls()
    {
        DockPanel.SetDock(aiStatusBar, Dock.Bottom);
        DockPanel.SetDock(aiTakeover, Dock.Right);
        aiStatusBar.Children.Add(aiTakeover);
        aiStatusBar.Children.Add(aiStatus);
        panel.Children.Add(aiStatusBar);
        AutomationProperties.SetLiveSetting(aiStatus, AutomationLiveSetting.Polite);
        aiTakeover.Click += (_, _) => AiCollaboration.EndControl(this);
        UpdateAiControls();
    }

    internal void AiAuthorityChanged()
    {
        foreach (var pane in panes.Keys) pane.SetConnected(InputSession is not null);
        UpdateAiControls();
        CaptionChanged?.Invoke();
    }

    private void UpdateAiControls()
    {
        string? state = IsAiTakingControl ? Text("AiTakingControl") : AiStateLabel;
        aiStatusBar.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        aiTakeover.Visibility = IsAiControlling ? Visibility.Visible : Visibility.Collapsed;
        aiTakeover.Content = Text("AiTakeControl");
        AutomationProperties.SetName(aiTakeover, Text("AiTakeControl"));
        string detail = AiSharing is { } share ? "  " + share.Peer.Label : "";
        if (AiCleanupDelayed) detail += "  " + Text("AiCleanupPending");
        if (AiSharing?.Reason is { Length: > 0 } reason) detail += "  " + reason;
        string updated = state + detail;
        if (aiStatus.Text == updated) return;
        aiStatus.Text = updated;
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
            var choice = new MenuItem { Header = peer.Label, ToolTip = peer.Location + "\n" + Text("AiCounterpartScope") };
            choice.Click += (_, _) =>
            {
                if (AiCollaboration.Share(this, peer) is { } problem) SetStatus(statusKey, Text(problem));
            };
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
                Add("AiGiveControl", () =>
                {
                    if (AiCollaboration.Grant(this) is { } problem) SetStatus(statusKey, Text(problem));
                }, conflict);
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
}
