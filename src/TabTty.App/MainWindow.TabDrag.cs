using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private WindowTab? dragCandidate;
    private TabControl? dragCapture;
    private Point dragOrigin;
    private TabDrag? tabDrag;

    private sealed class TabDrag(TabControl source, WindowTab item, TabHeaderPanel panel, FloatingTabHeader header)
    {
        internal readonly TabControl Source = source;
        internal readonly WindowTab Item = item;
        internal readonly TabHeaderPanel SourcePanel = panel;
        internal readonly FloatingTabHeader Header = header;
        internal readonly Size Size = LayoutInformation.GetLayoutSlot(item).Size;
        internal readonly Cursor? PreviousCursor = Mouse.OverrideCursor;
        internal Point? LastPoint;
        internal nint LastWindow;
        internal TabDrop? Drop;
        internal double InitialOffset;
        internal bool OutsideApp;
    }

    private sealed record TabDrop(MainWindow Window, TabControl Group, TabHeaderPanel Panel, ScrollViewer Strip, TabItem? Before)
    {
        internal bool Available => Window.CanAcceptTabs && Group.IsLoaded && Window.Groups.Contains(Group)
            && (Before is null || Group.Items.Contains(Before));
    }

    private void ConfigureTabDrag()
    {
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Key.Escape || tabDrag is null && dragCandidate is null) return;
            CancelTabDrag();
            e.Handled = true;
        }), true);
        Deactivated += (_, _) => CancelTabDrag();
        Closed += (_, _) => CancelTabDrag();
    }

    private void FindDragCandidate(object sender, MouseButtonEventArgs e)
    {
        var group = (TabControl)sender;
        if (Mouse.Captured is not null || Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null ||
            Ancestor<WindowTab>(e.OriginalSource as DependencyObject) is not { IsClosing: false } item ||
            !group.Items.Contains(item) || item.Content is FormView { IsBusy: true }) return;
        dragOrigin = e.GetPosition(group);
        // Keep the normal press routed to the tab header while watching for
        // movement or release outside the group.
        if (Mouse.Capture(group, CaptureMode.SubTree))
        {
            dragCapture = group;
            dragCandidate = item;
        }
    }

    private void StartTabDrag(object sender, MouseEventArgs e)
    {
        var group = (TabControl)sender;
        if (dragCandidate is null && tabDrag is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelTabDrag();
            return;
        }
        Point local = e.GetPosition(group);
        Point screen = group.PointToScreen(local);
        if (tabDrag is null && dragCandidate is { } candidate)
        {
            if (Math.Abs(local.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(local.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!BeginTabDrag(group, candidate, screen)) CancelTabDrag();
        }
        else UpdateTabDrag(screen);
        e.Handled = true;
    }

    private void FinishTabDrag(object sender, MouseButtonEventArgs e)
    {
        if (tabDrag is null) { CancelTabDrag(); return; }
        var group = (TabControl)sender;
        EndTabDrag(group.PointToScreen(e.GetPosition(group)));
        e.Handled = true;
    }

    internal bool BeginTabDrag(TabControl source, TabItem item, Point screenPoint)
    {
        if (tabDrag is not null || !CanAcceptTabs || item is not WindowTab { IsClosing: false } tab ||
            tab.Content is FormView { IsBusy: true } || !source.Items.Contains(tab) ||
            source.Template.FindName("tabHeaderPanel", source) is not TabHeaderPanel panel ||
            (Mouse.Captured is not null && Mouse.Captured != source) || !source.CaptureMouse()) return false;
        dragCapture = source;
        try
        {
            tabDrag = new TabDrag(source, tab, panel, new FloatingTabHeader(tab, screenPoint));
            dragCandidate = null;
            Mouse.OverrideCursor = Cursors.Arrow;
            UpdateTabDrag(screenPoint);
            return true;
        }
        catch
        {
            CancelTabDrag();
            throw;
        }
    }

    internal void UpdateTabDrag(Point screenPoint)
    {
        if (tabDrag is not { } drag) return;
        if (!CanAcceptTabs || drag.Item.IsClosing || !drag.Source.Items.Contains(drag.Item))
        {
            CancelTabDrag();
            return;
        }
        if (drag.Drop is { Available: false })
        {
            CancelTabDrag();
            drag.Item.ReportMoveFailure(new InvalidOperationException(Text("TabMoveDestinationUnavailable")));
            return;
        }
        drag.Header.Follow(screenPoint);
        nint hit = WindowFromPoint(new PixelPoint((int)screenPoint.X, (int)screenPoint.Y));
        nint exposed = GetAncestor(hit, 2); // GA_ROOT
        if (exposed == 0) exposed = hit;
        GetWindowThreadProcessId(exposed, out uint process);
        drag.OutsideApp = process != 0 && process != Environment.ProcessId;
        var window = process == Environment.ProcessId ? HwndSource.FromHwnd(exposed)?.RootVisual as MainWindow : null;
        // Opening or closing a gap can move controls. Without physical motion
        // the same point retains its displayed slot (or its lack of one).
        if (drag.LastPoint == screenPoint && drag.LastWindow == exposed &&
            (drag.Drop is null || window is { CanAcceptTabs: true, IsEnabled: true } && app.Broadcast.CanMove(this, window))) return;
        drag.LastPoint = screenPoint;
        drag.LastWindow = exposed;
        TabDrop? next = window is { CanAcceptTabs: true, IsEnabled: true } && app.Broadcast.CanMove(this, window)
            ? window.FindTabDrop(screenPoint, drag.Item) : null;
        if (drag.Drop?.Strip != next?.Strip)
        {
            drag.Drop?.Strip.ScrollToHorizontalOffset(drag.InitialOffset);
            drag.InitialOffset = next?.Strip.HorizontalOffset ?? 0;
        }
        if (drag.Drop?.Panel is { } previous && previous != drag.SourcePanel && previous != next?.Panel)
            previous.Preview(null, null, default);
        bool slotChanged = next != drag.Drop;
        drag.Drop = next;
        if (next?.Panel == drag.SourcePanel)
            drag.SourcePanel.Preview(drag.Item, next.Before, drag.Size);
        else
        {
            drag.SourcePanel.Preview(drag.Item, null, default);
            next?.Panel.Preview(null, next.Before, drag.Size);
        }
        if (next is not null && slotChanged)
        {
            next.Panel.UpdateLayout();
            if (next.Panel.GapBounds is { } gap)
                next.Panel.BringIntoView(gap);
        }
    }

    private TabDrop? FindTabDrop(Point screenPoint, WindowTab dragged)
    {
        var hit = VisualTreeHelper.HitTest(this, PointFromScreen(screenPoint))?.VisualHit;
        if (Ancestor<ButtonBase>(hit) is not null) return null;
        var group = Ancestor<TabControl>(hit);
        if (group is null || !Groups.Contains(group) ||
            group.Template.FindName("tabStripRow", group) is not FrameworkElement bar ||
            group.Template.FindName("tabHeaderPanel", group) is not TabHeaderPanel panel ||
            group.Template.FindName("tabStripScroller", group) is not ScrollViewer strip ||
            !new Rect(bar.RenderSize).Contains(bar.PointFromScreen(screenPoint))) return null;
        bool trailingSpace = strip.PointFromScreen(screenPoint).X >= strip.ActualWidth;
        TabItem? before = trailingSpace ? null : panel.HeaderAfterDrop(panel.PointFromScreen(screenPoint).X, dragged);
        return new TabDrop(this, group, panel, strip, before);
    }

    internal void EndTabDrag(Point screenPoint)
    {
        if (tabDrag is not { } started) return;
        TabDrop? shown = started.Drop;
        UpdateTabDrag(screenPoint);
        if (tabDrag is not { } drag) return;
        TabDrop? drop = drag.Drop;
        CancelTabDrag();
        if (drag.OutsideApp)
        {
            DetachTab(drag.Item, screenPoint);
            return;
        }
        if (drop != shown) return;
        if (drop is not null)
        {
            if (!drop.Available)
            {
                drag.Item.ReportMoveFailure(new InvalidOperationException(Text("TabMoveDestinationUnavailable")));
                return;
            }
            int index = drop.Before is null ? drop.Group.Items.Count : drop.Group.Items.IndexOf(drop.Before);
            int original = drag.Source.Items.IndexOf(drag.Item);
            if (drop.Group != drag.Source || index != original && index != original + 1)
                MoveTab(drag.Item, drop.Window, index, drop.Group);
        }
    }

    internal void CancelTabDrag()
    {
        var drag = tabDrag;
        var capture = dragCapture;
        dragCapture = null;
        dragCandidate = null;
        tabDrag = null;
        if (drag is not null)
        {
            drag.Drop?.Panel.Preview(null, null, default);
            drag.Drop?.Strip.ScrollToHorizontalOffset(drag.InitialOffset);
            drag.SourcePanel.Preview(null, null, default);
            drag.Header.Dispose();
            Mouse.OverrideCursor = drag.PreviousCursor;
        }
        if (capture?.IsMouseCaptured == true) capture.ReleaseMouseCapture();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PixelPoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
