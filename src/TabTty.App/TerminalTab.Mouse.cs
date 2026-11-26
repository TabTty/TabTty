using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private bool sendMouseToHost = true;
    private bool mouseNoticeShownForCurrentRouting;
    private readonly TextBlock mouseStatus = new() { Visibility = Visibility.Collapsed };
    private readonly object mouseHintKey = new();

    internal void AddMouseRoutingCommand(ContextMenu menu, TerminalSurface? pane = null)
    {
        var surface = pane ?? output;
        if (InputSession is null || !surface.MouseTrackingRequested || pane is not null && sendMouseToHost) return;
        bool send = !sendMouseToHost;
        object generation = ConnectionGeneration;
        menu.Items.Add(MainWindow.NamedAction(Text(send ? "SendMouseToHost" : "UseMouseInTabTty"), () =>
        {
            if (ConnectionGeneration != generation || InputSession is null || !surface.MouseTrackingRequested) return;
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

        if (!remote)
        {
            mouseNoticeShownForCurrentRouting = false;
            HideMouseNotice();
            return;
        }
        if (mouseNoticeShownForCurrentRouting) return;
        mouseNoticeShownForCurrentRouting = true;
        Notices.ShowHint(mouseHintKey, Text("MouseSentToHostNotice"), TimeSpan.FromSeconds(6));
    }

    private void HideMouseNotice() => Notices.Remove(mouseHintKey);
}
