using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private static readonly Size GroupMinimum = new(240, 160 + 43);
    private SplitLayout groups = null!;
    private TabControl activeGroup = null!;
    private IEnumerable<TabControl> Groups => groups.Views.Cast<TabControl>();
    private IEnumerable<WindowTab> AllTabs => Groups.SelectMany(group => group.Items.OfType<WindowTab>());
    internal IEnumerable<TerminalTab> TerminalTabs => AllTabs.Select(tab => tab.Content).OfType<TerminalTab>();

    private void InitializeGroups()
    {
        groupHost.Child = null;
        activeGroup = tabs;
        ConfigureGroup(tabs);
        groups = new SplitLayout(tabs);
        groupHost.Child = new SplitViewport(groups)
        {
            Content = groups, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        broadcastBand.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        broadcastBand.SizeChanged += (_, _) => UpdateGroupChrome();
        windowRoot.SizeChanged += (_, _) => UpdateGroupChrome();
        app.Broadcast.Register(this);
        RefreshBroadcast();
    }

    private void UpdateGroupChrome()
    {
        bool separateCaption = Groups.Skip(1).Any() || windowRoot.ActualWidth is > 0 and < 520;
        groupHost.Margin = new Thickness(0, separateCaption ? 43 : 0, 0,
            broadcastBand.Visibility == Visibility.Visible ? broadcastBand.ActualHeight : 0);
        foreach (var group in Groups) group.Padding = new Thickness(0, 0, separateCaption ? 0 : 176, 0);
    }

    private void ConfigureGroup(TabControl group)
    {
        group.MinWidth = GroupMinimum.Width;
        group.MinHeight = GroupMinimum.Height;
        group.SelectionChanged += (_, args) =>
        {
            if (args.Source != group) return;
            foreach (TerminalTab previous in args.RemovedItems.OfType<WindowTab>().Select(item => item.Content).OfType<TerminalTab>())
                previous.CancelPaneComposition();
            if (group.SelectedItem is TabItem item) item.BringIntoView();
            if (groups is not null) LabelGroups();
        };
        group.PreviewGotKeyboardFocus += (_, _) => ActivateGroup(group);
        group.Loaded += (_, _) => LabelGroups();
        group.PreviewMouseDown += (_, e) =>
        {
            ActivateGroup(group);
            if (e.OriginalSource is DependencyObject source && Ancestor<TabItem>(source) is { } item && !item.IsSelected)
                SelectedTab?.CancelPaneComposition();
        };
        group.PreviewMouseLeftButtonDown += FindDragCandidate;
        group.PreviewMouseMove += StartTabDrag;
        group.DragOver += OnTabDragOver;
        group.Drop += OnTabDrop;
    }

    private void ActivateGroup(TabControl group)
    {
        if (activeGroup == group) return;
        SelectedTab?.CancelPaneComposition();
        activeGroup = group;
        LabelGroups();
    }

    private void LabelGroups()
    {
        foreach (var group in Groups)
        {
            AutomationProperties.SetName(group, GroupName(group));
            MarkActiveGroup(group, group == activeGroup);
        }
    }

    private string GroupName(TabControl group)
    {
        int index = Groups.TakeWhile(candidate => candidate != group).Count() + 1;
        string name = (group.SelectedItem as WindowTab)?.Caption ?? Text("EmptyTabGroup");
        return string.Format(Text(group == activeGroup ? "ActiveTabGroupName" : "TabGroupName"), index, name);
    }

    private void SplitGroup(TabControl group, Orientation orientation)
    {
        if (!SplitLayout.CanSplit(group, orientation, GroupMinimum)) return;
        var added = new TabControl { AllowDrop = true, Template = (ControlTemplate)Resources["TabGroupTemplate"] };
        ConfigureGroup(added);
        try { groups.Split(group, added, orientation); }
        catch (ResourceLimitException exception) { ShowError(this, exception); return; }
        welcomePanel.Visibility = Visibility.Collapsed;
        UpdateGroupChrome();
        ActivateGroup(added);
        added.Focus();
    }

    private void RemoveGroup(TabControl source, TabControl destination)
    {
        if (source == destination || !Groups.Contains(source) || !Groups.Contains(destination)) return;
        if (source.Items.OfType<WindowTab>().Any(item => item.IsClosing || item.Content is FormView { IsBusy: true }))
        {
            ShowError(this, new InvalidOperationException(Text("SplitGroupBusy")));
            return;
        }
        foreach (var item in source.Items.OfType<WindowTab>().ToArray())
            if (!MoveTab(item, this, destinationGroup: destination)) return;
        groups.Remove(source);
        if (!Groups.Skip(1).Any() && !AllTabs.Any()) welcomePanel.Visibility = Visibility.Visible;
        UpdateGroupChrome();
        ActivateGroup(destination);
        FocusGroup(destination);
    }

    private static void FocusGroup(TabControl group)
    {
        if (group.SelectedContent is TerminalTab terminal) terminal.FocusPane();
        else if (group.SelectedItem is TabItem item) item.Focus();
        else group.Focus();
    }

    private MenuItem GroupCommands(TabControl group)
    {
        var menu = new MenuItem { Header = Text("TabGroups") };
        var horizontal = MenuAction("SplitGroupsSide", () => SplitGroup(group, Orientation.Horizontal));
        horizontal.IsEnabled = SplitLayout.CanSplit(group, Orientation.Horizontal, GroupMinimum);
        var vertical = MenuAction("SplitGroupsStack", () => SplitGroup(group, Orientation.Vertical));
        vertical.IsEnabled = SplitLayout.CanSplit(group, Orientation.Vertical, GroupMinimum);
        menu.Items.Add(horizontal);
        menu.Items.Add(vertical);
        var remove = new MenuItem { Header = Text("RemoveTabGroup"), IsEnabled = Groups.Skip(1).Any() };
        foreach (var destination in Groups.Where(candidate => candidate != group))
            remove.Items.Add(NamedAction(GroupName(destination), () => RemoveGroup(group, destination)));
        menu.Items.Add(remove);
        var focus = new MenuItem { Header = Text("FocusTabGroup") };
        foreach (var candidate in Groups)
            focus.Items.Add(NamedAction(GroupName(candidate), () => { ActivateGroup(candidate); FocusGroup(candidate); }));
        menu.Items.Add(focus);
        return menu;
    }

    internal static MenuItem NamedAction(string label, Action action)
    {
        var item = new MenuItem();
        SetMenuItemName(item, label);
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem BroadcastCommands()
    {
        var commands = new MenuItem { Header = Text("BroadcastInput") };
        FillBroadcastCommands(commands);
        commands.SubmenuOpened += (_, e) => { if (e.Source == commands) UpdateBroadcastCommandState(commands); };
        return commands;
    }

    private void FillBroadcastCommands(MenuItem menu)
    {
        menu.Items.Add(MenuAction("BroadcastStart", () => app.Broadcast.Start(this)));
        menu.Items.Add(MenuAction("BroadcastStop", () => app.Broadcast.Stop(this)));
        menu.Items.Add(MenuAction("BroadcastSelectAll", () => app.Broadcast.SetAll(this, true)));
        menu.Items.Add(MenuAction("BroadcastClearAll", () => app.Broadcast.SetAll(this, false)));
        menu.Items.Add(MenuAction("StopPaste", () => app.Broadcast.StopPaste(this)));
        UpdateBroadcastCommandState(menu);
    }

    private void UpdateBroadcastCommandState(MenuItem menu)
    {
        ((MenuItem)menu.Items[0]).IsEnabled = app.Broadcast.CanStart(this);
        ((MenuItem)menu.Items[1]).IsEnabled = app.Broadcast.CanStop(this);
        ((MenuItem)menu.Items[2]).IsEnabled = ((MenuItem)menu.Items[3]).IsEnabled = app.Broadcast.CanSelect(this);
        ((MenuItem)menu.Items[4]).IsEnabled = app.Broadcast.CanStopPaste(this);
    }

    internal void RefreshBroadcast()
    {
        bool visible = app.Broadcast.IsActive(this);
        bool editable = app.Broadcast.CanSelect(this);
        foreach (var tab in AllTabs) tab.UpdateBroadcast(visible, editable);
        string status = app.Broadcast.Status(this);
        broadcastStatus.Text = status;
        var visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (visibility == broadcastBand.Visibility) return;
        broadcastBand.Visibility = visibility;
        UpdateGroupChrome();
    }

    private TabControl? GroupOf(TabItem item) => Groups.FirstOrDefault(group => group.Items.Contains(item));

    private static void MarkActiveGroup(TabControl group, bool isActive)
    {
        if (group.Template?.FindName("tabStripRow", group) is not Border strip) return;
        if (isActive) strip.SetResourceReference(Border.BorderBrushProperty, SystemColors.HighlightBrushKey);
        else strip.ClearValue(Border.BorderBrushProperty);
    }
}
