using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private static readonly Size GroupMinimum = new(240, 160 + 43);
    private SplitLayout groups = null!;
    private TabControl activeGroup = null!;
    private TerminalTab? broadcastStatusTab;
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
        windowRoot.SizeChanged += (_, _) => UpdateGroupChrome();
        app.Broadcast.Register(this);
        RefreshBroadcast();
    }

    private void UpdateGroupChrome()
    {
        bool separateCaption = Groups.Skip(1).Any() || windowRoot.ActualWidth is > 0 and < 616;
        groupHost.Margin = new Thickness(0, separateCaption ? 43 : 0, 0, 0);
        foreach (var group in Groups)
        {
            group.Padding = new Thickness(0, 0, separateCaption ? 0 : 176, 0);
            UpdateGroupSplitButtons(group);
        }
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
        group.Loaded += (_, _) =>
        {
            LabelGroups();
            UpdateGroupSplitButtons(group);
        };
        group.SizeChanged += (_, _) => UpdateGroupSplitButtons(group);
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
            AutomationProperties.SetName(group, GroupName(group));
        PlaceBroadcastStatus();
    }

    private string GroupName(TabControl group)
    {
        int index = Groups.TakeWhile(candidate => candidate != group).Count() + 1;
        string name = (group.SelectedItem as WindowTab)?.Caption ?? Text("EmptyTabGroup");
        return string.Format(Text(group == activeGroup ? "ActiveTabGroupName" : "TabGroupName"), index, name);
    }

    private void UpdateGroupSplitButtons(TabControl group)
    {
        if (group.Template?.FindName("splitGroupsStackButton", group) is Button stack)
            stack.IsEnabled = SplitLayout.CanSplit(group, Orientation.Vertical, GroupMinimum);
        if (group.Template?.FindName("splitGroupsSideButton", group) is Button side)
            side.IsEnabled = SplitLayout.CanSplit(group, Orientation.Horizontal, GroupMinimum);
        if (group.Template?.FindName("removeTabGroupButton", group) is Button remove)
            remove.IsEnabled = Groups.Skip(1).Any();
    }

    private void OnSplitGroup(object sender, RoutedEventArgs args)
    {
        if (sender is Button { IsEnabled: true, TemplatedParent: TabControl group, Tag: Orientation orientation })
            SplitGroup(group, orientation);
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { IsEnabled: true, TemplatedParent: TabControl group } button) return;
        var destinations = GroupRemovalDestinations(group);
        if (destinations.Length == 0) return;
        if (DirectGroupRemovalDestination(group, destinations) is { } destination)
        {
            RemoveGroup(group, destination);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var target in destinations) menu.Items.Add(GroupRemovalAction(group, target));
        menu.Closed += (_, _) => button.ClearValue(ContextMenuProperty);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SplitGroup(TabControl group, Orientation orientation)
    {
        if (!SplitLayout.CanSplit(group, orientation, GroupMinimum)) return;
        var added = new TabControl { AllowDrop = true, Template = (ControlTemplate)Resources["TabGroupTemplate"] };
        ConfigureGroup(added);
        try { groups.Split(group, added, orientation); }
        catch (ResourceLimitException exception) { ShowError(this, exception); return; }
        UpdateWelcomePanel();
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
        UpdateWelcomePanel();
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
        var destinations = GroupRemovalDestinations(group);
        bool removeDirectly = DirectGroupRemovalDestination(group, destinations) is not null;
        var remove = removeDirectly
            ? MenuAction("RemoveTabGroup", RemoveDirectly)
            : new MenuItem { Header = Text("RemoveTabGroup"), IsEnabled = destinations.Length > 0 };
        if (!removeDirectly)
            foreach (var target in destinations) remove.Items.Add(GroupRemovalAction(group, target));
        menu.Items.Add(remove);
        var focus = new MenuItem { Header = Text("FocusTabGroup") };
        foreach (var candidate in Groups)
            focus.Items.Add(NamedAction(GroupName(candidate), () => { ActivateGroup(candidate); FocusGroup(candidate); }));
        menu.Items.Add(focus);
        return menu;

        void RemoveDirectly()
        {
            if (DirectGroupRemovalDestination(group, GroupRemovalDestinations(group)) is { } destination)
                RemoveGroup(group, destination);
        }
    }

    private TabControl[] GroupRemovalDestinations(TabControl source) =>
        Groups.Where(destination => destination != source).ToArray();

    private static TabControl? DirectGroupRemovalDestination(TabControl source, TabControl[] destinations) =>
        !source.HasItems || destinations.Length == 1 ? destinations.FirstOrDefault() : null;

    private MenuItem GroupRemovalAction(TabControl source, TabControl destination) =>
        NamedAction(GroupName(destination), () => RemoveGroup(source, destination));

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
        broadcastStatus.ToolTip = status;
        AutomationProperties.SetName(broadcastStatus, status);
        PlaceBroadcastStatus();
    }

    private void PlaceBroadcastStatus()
    {
        string text = broadcastStatus.Text;
        TerminalTab? terminal = text.Length == 0 ? null : SelectedTab ?? Groups
            .Select(group => (group.SelectedItem as WindowTab)?.Content).OfType<TerminalTab>().FirstOrDefault();
        if (broadcastStatusTab != terminal)
        {
            broadcastStatusTab?.SetBroadcastStatus("");
            broadcastStatusTab = terminal;
        }
        terminal?.SetBroadcastStatus(text);
        broadcastBand.Visibility = terminal is null && text.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TabControl? GroupOf(TabItem item) => Groups.FirstOrDefault(group => group.Items.Contains(item));

    private void UpdateWelcomePanel() => welcomePanel.Visibility = !closing && !Groups.Skip(1).Any() && !AllTabs.Any()
        ? Visibility.Visible : Visibility.Collapsed;
}
