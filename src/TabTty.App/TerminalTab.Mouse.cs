using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private bool sendMouseToHost = true;
    private bool mouseNoticeShown;
    private readonly TextBlock mouseStatus = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock mouseNoticeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border mouseNotice = new()
    {
        Padding = new Thickness(10, 6, 10, 6), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private readonly DispatcherTimer mouseNoticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private void InitializeMouseRoutingControls()
    {
        ConfigureStatusIndicator(mouseStatus);
        DockPanel.SetDock(mouseStatus, Dock.Right);
        statusBar.Children.Add(mouseStatus);
        statusBar.SizeChanged += (_, e) => mouseStatus.MaxWidth = e.NewSize.Width * 0.4;

        mouseNotice.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        mouseNoticeText.SetResourceReference(TextBlock.ForegroundProperty, "WindowForeground");
        mouseNotice.Child = mouseNoticeText;
        AutomationProperties.SetLiveSetting(mouseNoticeText, AutomationLiveSetting.Polite);
        Canvas.SetBottom(mouseNotice, 18);
        Canvas.SetRight(mouseNotice, 18);
        terminalArea.SizeChanged += (_, e) => mouseNotice.MaxWidth = Math.Clamp(e.NewSize.Width - 36, 0, 440);
        terminalOverlays.Children.Add(mouseNotice);
        mouseNoticeTimer.Tick += (_, _) => HideMouseNotice();
    }

    internal void AddMouseRoutingCommand(ContextMenu menu, TerminalSurface? pane = null)
    {
        if (InputSession is null || pane is not null && (sendMouseToHost || !pane.MouseTrackingRequested)) return;
        bool send = !sendMouseToHost;
        object generation = ConnectionGeneration;
        menu.Items.Add(MainWindow.NamedAction(Text(send ? "SendMouseToHost" : "UseMouseInTabTty"), () =>
        {
            if (ConnectionGeneration != generation || InputSession is null) return;
            SetMouseRouting(send);
        }));
    }

    private void SetMouseRouting(bool send)
    {
        sendMouseToHost = send;
        foreach (var pane in panes.Keys) pane.SetMouseRouting(send);
        UpdateMouseRouting();
    }

    private void ResetMouseRouting()
    {
        mouseNoticeShown = false;
        foreach (var pane in panes.Keys) pane.ResetMouseTracking();
        SetMouseRouting(send: true);
    }

    private void UpdateMouseRouting()
    {
        bool requested = InputSession is not null && output.MouseTrackingRequested;
        bool remote = requested && output.CanReportMouse;
        string label = requested ? Text(remote ? "MouseDestinationHost" : "MouseDestinationTabTty") : "";
        if (mouseStatus.Text != label)
        {
            mouseStatus.Text = label;
            mouseStatus.ToolTip = label;
            AutomationProperties.SetName(mouseStatus, label);
            mouseStatus.Visibility = requested ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!remote || !output.IsPresentationActive)
        {
            HideMouseNotice();
            return;
        }
        mouseNoticeText.Text = Text("MouseSentToHostNotice");
        if (mouseNoticeShown) return;
        mouseNoticeShown = true;
        mouseNotice.Visibility = Visibility.Visible;
        mouseNoticeTimer.Start();
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(mouseNoticeText)?
            .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void HideMouseNotice()
    {
        mouseNoticeTimer.Stop();
        mouseNotice.Visibility = Visibility.Collapsed;
    }
}
