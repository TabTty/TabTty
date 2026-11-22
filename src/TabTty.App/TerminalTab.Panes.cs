using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private readonly DockPanel panel = new();
    private readonly Grid terminalArea = new() { ClipToBounds = true };
    internal FrameworkElement TerminalArea => terminalArea;
    private readonly Dictionary<TerminalSurface, PaneState> panes = [];
    private SplitLayout? paneLayout;
    private SplitViewport? paneViewport;
    private bool presentationEnabled;
    private FrameworkElement GridHost => (FrameworkElement?)paneViewport ?? output.Scroller;

    internal Rect StatusOverlayBounds(bool right)
    {
        var area = new Rect(terminalArea.RenderSize);
        if (paneLayout is null) return area;
        Rect anchor = Rect.Empty;
        foreach (TerminalSurface pane in OrderedPanes)
        {
            var visible = Rect.Intersect(area, new Rect(pane.Scroller.TranslatePoint(new Point(), terminalArea),
                pane.Scroller.RenderSize));
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
            if (anchor.IsEmpty || visible.Top < anchor.Top ||
                visible.Top == anchor.Top && (right ? visible.Right > anchor.Right : visible.Left < anchor.Left))
                anchor = visible;
        }
        return anchor.IsEmpty ? area : anchor;
    }

    private sealed class PaneState(TerminalHighlightView highlight)
    {
        public TerminalHighlightView Highlight { get; } = highlight;
        public TerminalMatch? CurrentMatch;
    }

    private bool broadcastChecked = true;

    private readonly record struct ConnectionDestination(string LabelKey, string Endpoint)
    {
        internal static ConnectionDestination From(TerminalSession target)
        {
            string label = target.IsSsh ? "AiSshTarget" : target.IsSerial ? "AiSerialTarget" : "AiTelnetTarget";
            string endpoint = target.IsSsh ? target.FileTransferEndpoint : target.ConnectionDetails;
            return new(label, PermissionDisplay.Literal(endpoint));
        }

        public override string ToString() => string.Format(Text(LabelKey), Endpoint);
    }

    internal static string BroadcastTargetLabel(TerminalSession target) =>
        $"{ConnectionDestination.From(target)} — {PermissionDisplay.Literal(target.DisplayName)}";

    internal bool BroadcastChecked
    {
        get => broadcastChecked;
        set => broadcastChecked = value && !IsAiShared;
    }
    internal bool HasMouseCapture => panes.Keys.Any(pane => pane.IsMouseCaptured);
    internal bool OwnsPane(TerminalSurface pane) => panes.ContainsKey(pane);
    internal TerminalSession? InputSession => !ClosePending && IsRunning && !stop.IsCancellationRequested &&
        session is { AcceptsInput: true } active ? active : null;
    internal CancellationToken InputLifetime => IsRunning ? stop.Token : new CancellationToken(canceled: true);
    private BroadcastInput Broadcast => ((App)Application.Current).Broadcast;
    private bool IsBroadcastActive => Window.GetWindow(this) is MainWindow host && Broadcast.IsActive(host);

    internal void RefreshInputOutput()
    {
        Interlocked.Exchange(ref receivedRevision, 1);
        ScheduleFrame();
    }

    private void AttachPane(TerminalSurface pane)
    {
        var state = new PaneState(new TerminalHighlightView(pane, highlightState));
        panes.Add(pane, state);
        state.Highlight.Suspended += suspended =>
        {
            highlightSuspended = suspended;
            UpdateHighlightStatus();
        };
        pane.SetPresentationEnabled(presentationEnabled);
        pane.ConfigureFont(baseFontFamily, zoomFontSize);
        pane.SetConnected(InputSession is not null);
        pane.RemoteGestureStarted += capture => Broadcast.StartMouse(Window.GetWindow(this) as MainWindow, this, pane, capture);
        pane.RemoteGestureEnded += capture => Broadcast.StopMouse(pane, capture);
        pane.UnclampedMouse = () => Broadcast.UsesBroadcastMouse(Window.GetWindow(this) as MainWindow, pane);
        pane.RemoteCaptureLost += capture => Broadcast.CaptureLost(pane, capture);
        pane.RemoteCaptureFailed += () => Broadcast.CaptureFailed(Window.GetWindow(this) as MainWindow);
        pane.MouseInput += input =>
        {
            if (InputSession is null || pasteStop is not null) return;
            var route = Broadcast.RouteMouseAsync(Window.GetWindow(this) as MainWindow, pane, pane.ActiveMouseCapture, input);
            if (!route.IsCompletedSuccessfully || route.Result) { _ = route; return; }
            Interlocked.Increment(ref pendingMouse);
            if (!mouseInput.Writer.TryWrite(input))
            {
                Interlocked.Decrement(ref pendingMouse);
                SetStatus("Failed", Text("InputQueueFull"));
                Disconnect();
            }
        };
        pane.ViewportChanged += _ => { if (searchWork is null) ScheduleSearchRefresh(); };
        pane.PreviewTextInput += OnTextInput;
        pane.PreviewKeyDown += OnKeyDown;
        pane.Scroller.PreviewMouseDown += (_, _) =>
        {
            if (pane != output) output.CancelComposition();
        };
        pane.GotKeyboardFocus += (_, _) => ActivatePane(pane);
        pane.Scroller.SizeChanged += (_, _) => { if (paneLayout is null) ScheduleResize(); };
        pane.PresentationChanged += () =>
        {
            if (!pane.IsPresentationActive) return;
            ScheduleFrame(immediately: true);
            ScheduleResize();
        };
        pane.Scroller.Loaded += (_, _) =>
        {
            if (paneLayout is null || !panes.ContainsKey(pane)) return;
            SetPaneMinimum(pane);
            paneLayout.RefreshMinimums();
        };
        if (snapshot is { } view)
        {
            pane.SetSnapshot(view);
            state.Highlight.SetSnapshot(view);
            pane.SetSearch(null, searchMatches, scroll: false);
        }
        UpdatePaneMenu(pane);
    }

    private void ActivatePane(TerminalSurface pane)
    {
        if (pane == output || !panes.ContainsKey(pane)) return;
        output.CancelComposition();
        output = pane;
        LabelPanes();
        ScheduleFrame();
    }

    internal void CancelPaneComposition()
    {
        foreach (var pane in panes.Keys) pane.CancelComposition();
    }

    internal void BeginHostMove()
    {
        if (macroDialog is not null) macroDialog.Owner = null;
        foreach (var pane in panes.Keys) pane.BeginHostMove();
    }

    internal bool EndHostMove()
    {
        if (macroDialog is not null && MacroWindow is { } owner)
        {
            macroDialog.Owner = owner;
            owner.RestoreMacroHost();
        }
        bool cancelled = false;
        foreach (var pane in panes.Keys) cancelled |= pane.EndHostMove();
        return cancelled;
    }

    internal void FocusPane() => output.Focus();

    private IEnumerable<TerminalSurface> OrderedPanes => paneLayout is null ? [output] :
        paneLayout.Views.Cast<ScrollViewer>().Select(scroller => (TerminalSurface)scroller.Content);

    private string PaneName(TerminalSurface pane) => string.Format(Text(pane == output ? "ActivePaneName" : "PaneName"),
        OrderedPanes.TakeWhile(candidate => candidate != pane).Count() + 1);

    private void LabelPanes()
    {
        foreach (var pane in panes.Keys)
            AutomationProperties.SetName(pane, panes.Count == 1 ? Text("Terminal") : PaneName(pane));
    }

    private void ConfigurePaneFonts()
    {
        foreach (var pane in panes.Keys)
        {
            pane.ConfigureFont(baseFontFamily, zoomFontSize);
            if (paneLayout is not null) SetPaneMinimum(pane);
        }
        paneLayout?.RefreshMinimums();
    }

    private static void SetPaneMinimum(TerminalSurface pane)
    {
        Size minimum = pane.PaneMinimum();
        pane.Scroller.MinWidth = minimum.Width;
        pane.Scroller.MinHeight = minimum.Height;
    }

    internal void FillPaneCommands(MenuItem menu, TerminalSurface? pane = null)
    {
        var source = pane ?? output;
        foreach (Orientation orientation in Enum.GetValues<Orientation>())
        {
            var split = MainWindow.NamedAction(Text(orientation == Orientation.Horizontal ? "SplitPanesSide" : "SplitPanesStack"),
                () => SplitPane(source, orientation));
            split.IsEnabled = SplitLayout.CanSplit(source.Scroller, orientation, source.PaneMinimum());
            menu.Items.Add(split);
        }
        var close = MainWindow.NamedAction(Text("RemovePane"), () => RemovePane(source));
        close.IsEnabled = panes.Count > 1;
        menu.Items.Add(close);
        var focus = new MenuItem { Header = Text("FocusPane") };
        foreach (var candidate in OrderedPanes)
            focus.Items.Add(MainWindow.NamedAction(PaneName(candidate), () => { ActivatePane(candidate); candidate.Focus(); }));
        menu.Items.Add(focus);
    }

    private void SplitPane(TerminalSurface source, Orientation orientation)
    {
        if (!panes.ContainsKey(source) || !SplitLayout.CanSplit(source.Scroller, orientation, source.PaneMinimum())) return;
        var added = new TerminalSurface();
        double x = source.Scroller.HorizontalOffset, y = source.VerticalOffset;
        bool createdLayout = paneLayout is null;
        if (paneLayout is null)
        {
            terminalArea.Children.Remove(source.Scroller);
            SetPaneMinimum(source);
            paneLayout = new SplitLayout(source.Scroller);
            paneViewport = new SplitViewport(paneLayout)
            {
                Content = paneLayout, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            paneViewport.SizeChanged += (_, _) => ScheduleResize();
            terminalArea.Children.Insert(0, paneViewport);
        }
        added.ConfigureFont(baseFontFamily, zoomFontSize);
        SetPaneMinimum(added);
        try { paneLayout.Split(source.Scroller, added.Scroller, orientation); }
        catch (ResourceLimitException exception)
        {
            if (createdLayout) RestoreSinglePane(source);
            SetStatus(statusKey, exception.Message);
            return;
        }
        AttachPane(added);
        added.Scroller.ScrollToHorizontalOffset(x);
        added.Scroller.ScrollToVerticalOffset(y);
        ActivatePane(added);
        added.Focus();
        LabelPanes();
    }

    private void RemovePane(TerminalSurface pane)
    {
        if (paneLayout is null || panes.Count == 1 || !panes.ContainsKey(pane)) return;
        pane.CancelRemoteGesture(notify: true);
        pane.CancelComposition();
        pane.SetPresentationEnabled(false);
        paneLayout.Remove(pane.Scroller);
        panes[pane].Highlight.Detach();
        panes.Remove(pane);
        if (output == pane) output = OrderedPanes.First();
        if (panes.Count == 1) RestoreSinglePane(output);
        LabelPanes();
        output.Focus();
    }

    private void RestoreSinglePane(TerminalSurface pane)
    {
        paneLayout!.Children.Remove(pane.Scroller);
        terminalArea.Children.Remove(paneViewport!);
        paneViewport = null;
        paneLayout = null;
        pane.Scroller.MinWidth = pane.Scroller.MinHeight = 0;
        terminalArea.Children.Insert(0, pane.Scroller);
    }
}
