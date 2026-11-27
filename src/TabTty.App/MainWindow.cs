using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow : Window
{
    private const string NonFullscreenWindowProperty = "NonRudeHWND";
    private readonly App app;
    private readonly Func<bool> hasForeground;
    private readonly TerminalClosure terminalClosure;
    private MenuItem? currentMenu;
    private MenuItem? vaultLockMenuItem;
    private string VaultLockMenuKey => app.Vault.IsSelectedUnlocked ? "LockVault" : "UnlockVault";
    private bool closing;
    private bool readyToClose;
    private bool closed;
    private nint taskbarPropertyWindow;
    private static readonly DependencyProperty TabWheelRemainderProperty = DependencyProperty.RegisterAttached(
        "TabWheelRemainder", typeof(int), typeof(MainWindow), new PropertyMetadata(0));
    private Color? headerAccent;

    public MainWindow(App app, Func<bool>? hasForeground = null)
    {
        this.app = app;
        this.hasForeground = hasForeground ?? (() => IsActive);
        terminalClosure = new(this);
        Closed += (_, _) =>
        {
            closed = true;
            app.StatusReports.Changed -= UpdateWindowReports;
            app.StatusReports.WindowClosed(this);
            foreach (var form in AllTabs.Select(tab => tab.Content).OfType<FormView>()) form.Reports.Dispose();
            groups.ReleaseReservations();
            app.CommitWindowClose(this);
        };
        // Register before WindowChrome so right-clicking the caption can open the tab menu.
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(handle) is not { } source) return;
            source.AddHook(HandleWindowMessage);

            // Keep the taskbar available when the maximized client area covers the monitor.
            if (SetPropW(handle, NonFullscreenWindowProperty, 1)) taskbarPropertyWindow = handle;
        };
        InitializeComponent();
        UpdateActiveHeaderShade();
        Icon = WindowIcons.App;
        welcomeIcon.Source = WindowIcons.Welcome;
        MinWidth = WindowPlacement.MinimumWidth;
        InitializeApplicationMenu();
        minimizeButton.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        maximizeButton.Click += (_, _) =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        };
        closeButton.Click += (_, _) => SystemCommands.CloseWindow(this);
        StateChanged += (_, _) => UpdateMaximizeButton();
        app.Vault.ManagementChanged += UpdateVaultStatus;
        Closed += (_, _) => app.Vault.ManagementChanged -= UpdateVaultStatus;
        app.StatusReports.Changed += UpdateWindowReports;
        windowStatusBar.SizeChanged += (_, _) => UpdateStatusInset();
        NativeKeyState.Attach(this);
        InitializeGroups();
        Activated += (_, _) => RefreshNoticeActivity();
        Deactivated += (_, _) => RefreshNoticeActivity();
        Loaded += (_, _) => RefreshNoticeHosts();
        StateChanged += (_, _) => RefreshNoticeHosts();
        PreviewKeyDown += OnTabShortcutKeyDown;
        ConfigureTabDrag();
        UpdateLabels();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty && titleBarBackground is not null)
            UpdateActiveHeaderShade();
    }

    private void UpdateActiveHeaderShade()
    {
        if (SystemParameters.HighContrast || Background is not SolidColorBrush { Color: var background })
        {
            Resources["ActiveHeaderBackground"] = Brushes.Transparent;
            return;
        }

        bool dark = background.R < 128;
        double brightness = dark ? 0.62 : 0.975;
        double tint = 0;
        Color accent = headerAccent.GetValueOrDefault();
        if (headerAccent.HasValue)
        {
            brightness = dark ? 0.32 : 0.985;
            tint = dark ? 0.075 : 0.025;
        }
        double baseWeight = brightness * (1 - tint);
        var brush = new SolidColorBrush(Color.FromRgb(
            Blend(background.R, accent.R), Blend(background.G, accent.G), Blend(background.B, accent.B)));
        brush.Freeze();
        Resources["ActiveHeaderBackground"] = brush;

        byte Blend(byte component, byte accentComponent) =>
            (byte)Math.Round(component * baseWeight + accentComponent * tint);
    }

    internal void SetActiveHeaderAccent(Color? accent)
    {
        headerAccent = accent;
        UpdateActiveHeaderShade();
    }

    internal void OpenProfile(string path) => OpenProfiles([path], activeGroup);

    internal void OpenProfiles(IReadOnlyList<string> paths, FormView source)
    {
        if (source.Tab?.Placement is { } location && location.Window == this)
            OpenProfiles(paths, location.Group);
    }

    private void OpenProfiles(IReadOnlyList<string> paths, TabControl group)
    {
        if (closing || paths.Count == 0 || !Groups.Contains(group)) return;
        ActivateGroup(group);
        var terminals = paths.Select(path => new TerminalTab(path)).ToArray();
        WindowTab? first = null;
        foreach (var terminal in terminals)
        {
            var item = CreateTerminalTab(terminal);
            group.Items.Add(item);
            item.Placement = (this, group);
            first ??= item;
        }
        group.SelectedItem = first;
        UpdateWelcomePanel();
        app.Broadcast.Refresh();
        foreach (var terminal in terminals)
            terminal.Start(profile => _ = app.Profiles.RememberUsageAsync(terminal.ProfilePath, profile));
    }

    internal void AddTerminalTab(TerminalTab terminal) => SelectNewTab(CreateTerminalTab(terminal));

    private static WindowTab CreateTerminalTab(TerminalTab terminal) => new(terminal);

    private void OnTerminalDisconnected(WindowTab item)
    {
        if (!closing && item.Content is TerminalTab { IsExternalMacro: false } terminal &&
            (terminal.CloseAfterInitialCancellation || app.AutoCloseDisconnectedTabs && terminal.CanAutoCloseOnDisconnect))
            terminalClosure.CloseAutomatically(item);
    }

    private bool SelectForm(string titleKey)
    {
        var existing = AllTabs.FirstOrDefault(item => item.FormKey == titleKey);
        if (existing is null) return false;
        var group = GroupOf(existing)!;
        ActivateGroup(group);
        group.SelectedItem = existing;
        return true;
    }

    internal void OpenForm(string titleKey, Action<FormView> build, string? pageTitleKey = null)
    {
        if (closing || SelectForm(titleKey)) return;
        var form = new FormView();
        form.SetPageTitle(pageTitleKey ?? titleKey, useTabBackground: true);
        var item = new WindowTab(form) { FormKey = titleKey, Caption = Text(titleKey) };
        form.Completed += _ =>
        {
            item.Host?.RemoveFormTab(item);
        };
        build(form);
        form.AddStatusBar();
        SelectNewTab(item);
    }

    private void SelectNewTab(WindowTab item)
    {
        var group = activeGroup;
        group.Items.Add(item);
        item.Placement = (this, group);
        group.SelectedItem = item;
        UpdateWelcomePanel();
        app.Broadcast.Refresh();
    }

    internal bool CanAcceptTabs => IsLoaded && !closing;
    internal bool IsClosing => closing;
    internal bool IsClosed => closed;

    internal void UpdateLabels()
    {
        groups.UpdateLabels();
        LabelGroups();
        RefreshBroadcast();
        welcomeTitle.Text = Text("WelcomeTitle");
        profileHint.Text = Text("OpenHint");
        vaultHint.Text = Text("VaultHint");
        UpdateVaultStatus();
        Resources["NewTabLabel"] = Text("NewTab");
        Resources["TabListLabel"] = Text("TabList");
        Resources["ScrollTabsLeftLabel"] = Text("ScrollTabsLeft");
        Resources["ScrollTabsRightLabel"] = Text("ScrollTabsRight");
        Resources["SplitGroupsStackLabel"] = Text("SplitGroupsStack");
        Resources["SplitGroupsSideLabel"] = Text("SplitGroupsSide");
        Resources["RemoveTabGroupLabel"] = Text("RemoveTabGroup");
        SetButtonLabel(menuButton, "ApplicationMenu");
        SetButtonLabel(minimizeButton, "MinimizeWindow");
        SetButtonLabel(closeButton, "CloseWindow");
        UpdateMaximizeButton();
        CloseApplicationMenu();
        mainMenu.Items.Clear();
        BeginMenu("FileMenu", Key.F);
        AddButton("Open", ChooseProfile);
        AddButton("RunMacro", async () => await ChooseMacroAsync());
        AddButton("New", CreateProfile);
        AddButton("CloseTab", CloseSelectedTab);
        AddButton("CloseWithLoss", async () =>
        {
            var pending = AllTabs.Where(item => item.Content is TerminalTab { ClosePending: true, RecordingReleasePending: true });
            var target = pending.FirstOrDefault(item => ReferenceEquals(item.Content, SelectedTab)) ??
                (closing ? pending.FirstOrDefault() : null);
            if (target is not null) await terminalClosure.ReviewRecordingLossAsync(target);
        });
        AddButton("CloseWindow", Close);
        BeginMenu("EditMenu", Key.E);
        AddButton("Copy", () => SelectedTab?.CopySelection(), ShortcutAction.Copy);
        AddButton("Paste", async () => { if (SelectedTab is { } tab) await tab.PasteAsync(); }, ShortcutAction.Paste);
        AddButton("Search", () => SelectedTab?.ShowSearch(), ShortcutAction.Search);
        BeginMenu("ViewMenu", Key.V);
        AddButton("ZoomIn", () => SelectedTab?.Zoom(1));
        AddButton("ZoomOut", () => SelectedTab?.Zoom(-1));
        AddButton("ResetZoom", () => SelectedTab?.Zoom(0));
        windowReportsMenu = MenuAction("WindowReportsTitle", ShowWindowReports);
        currentMenu!.Items.Add(windowReportsMenu);
        UpdateWindowReports();
        BeginMenu("WindowMenu", Key.W);
        AddButton("NewWindow", () => app.CreateWindow().Show());
        var moveTab = new MenuItem { Header = Text("MoveTab") };
        moveTab.Items.Add(new MenuItem());
        moveTab.SubmenuOpened += (_, e) => { if (e.Source == moveTab) FillTabDestinations(moveTab); };
        currentMenu!.Items.Add(moveTab);
        var groupCommands = new MenuItem { Header = Text("TabGroups") };
        groupCommands.Items.Add(new MenuItem());
        groupCommands.SubmenuOpened += (_, e) =>
        {
            if (e.Source != groupCommands) return;
            var menu = GroupCommands(activeGroup);
            var items = menu.Items.Cast<MenuItem>().ToArray();
            menu.Items.Clear();
            groupCommands.Items.Clear();
            foreach (var item in items) groupCommands.Items.Add(item);
        };
        currentMenu!.Items.Add(groupCommands);
        var paneCommands = new MenuItem { Header = Text("TerminalPanes") };
        paneCommands.Items.Add(new MenuItem());
        paneCommands.SubmenuOpened += (_, e) =>
        {
            if (e.Source != paneCommands) return;
            paneCommands.Items.Clear();
            if (SelectedTab is { } terminal) terminal.FillPaneCommands(paneCommands);
        };
        currentMenu.Items.Add(paneCommands);
        var windowMenu = currentMenu;
        windowMenu.SubmenuOpened += (_, e) =>
        {
            if (e.Source != windowMenu) return;
            moveTab.IsEnabled = activeGroup.SelectedItem is WindowTab { IsClosing: false } item &&
                item.Content is not FormView { IsBusy: true };
            paneCommands.IsEnabled = SelectedTab is not null;
            if (moveTab.Items.Count == 0) moveTab.Items.Add(new MenuItem());
            if (paneCommands.Items.Count == 0) paneCommands.Items.Add(new MenuItem());
        };
        BeginMenu("ConnectionMenu", Key.C);
        currentMenu!.Items.Add(BroadcastCommands());
        AddButton("Reconnect", () =>
        {
            if (activeGroup.SelectedItem is WindowTab item) ReconnectTab(item);
        });
        AddButton("Disconnect", () => SelectedTab?.Disconnect());
        AddButton("ConnectionCommands", () => SelectedTab?.ShowConnectionCommands());
        AddButton("PauseRecording", async () => await ChangeRecordingAsync(false));
        AddButton("ResumeRecording", async () => await ChangeRecordingAsync(true));
        AddButton("StopPaste", () =>
        {
            app.Broadcast.StopPaste(this);
            SelectedTab?.StopPaste();
        });
        BeginMenu("ToolsMenu", Key.T);
        AddButton("ProfileCatalog", () => app.Profiles.Show(this));
        var vaultMenu = new MenuItem { Header = Text("Vault") };
        vaultLockMenuItem = MenuAction(VaultLockMenuKey, async () =>
        {
            if (app.Vault.IsSelectedUnlocked) await app.Vault.LockFromMenuAsync(this);
            else await app.Vault.UnlockFromMenuAsync(this);
        });
        vaultMenu.Items.Add(vaultLockMenuItem);
        vaultMenu.Items.Add(MenuAction("ManageVault", async () => await app.Vault.ManageAsync(this)));
        vaultMenu.Items.Add(MenuAction("RestoreVault", async () => await app.Vault.RestoreFromMenuAsync(this)));
        vaultMenu.Items.Add(MenuAction("ArchiveVault", async () => await app.Vault.ArchiveAsync(this)));
        currentMenu!.Items.Add(vaultMenu);
        AddButton("HostKeys", ShowHostKeys);
        AddButton("HighlightSets", () => app.Highlights.Show(this));
        AddButton("Settings", EditSettings);
        ApplyApplicationMenuStyle(mainMenu.Items);
        if (closing) SetWindowCloseControls(pending: true);
        foreach (WindowTab item in AllTabs)
        {
            item.UpdateCloseLabel();
            if (item.Content is TerminalTab terminal) terminal.UpdateLabels();
            else if (item.FormKey is { } key && item.Content is FormView form)
            {
                item.Caption = Text(key);
                form.UpdateLabels();
            }
        }
    }

    private void UpdateMaximizeButton()
    {
        bool restore = WindowState == WindowState.Maximized;
        maximizeGlyph.Text = restore ? "\uE923" : "\uE922";
        SetButtonLabel(maximizeButton, restore ? "RestoreWindow" : "MaximizeWindow");
    }

    private static void SetButtonLabel(Button button, string key)
    {
        string label = Text(key);
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private void FillTabDestinations(MenuItem menu)
    {
        menu.Items.Clear();
        if (activeGroup.SelectedItem is not WindowTab { IsClosing: false } item || item.Content is FormView { IsBusy: true }) return;
        var fresh = MenuAction("NewWindow", () => DetachTab(item));
        fresh.IsEnabled = app.Broadcast.CanMoveToNewWindow(this);
        menu.Items.Add(fresh);
        var targets = app.Windows.OfType<MainWindow>().Where(window => window.CanAcceptTabs).ToArray();
        for (int index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            foreach (var group in target.Groups.Where(group => target != this || group != activeGroup))
            {
                string name = target == this ? GroupName(group) :
                    string.Format(Text("WindowGroupName"), index + 1, target.GroupName(group));
                var destination = NamedAction(name, () => MoveTab(item, target, destinationGroup: group));
                destination.IsEnabled = app.Broadcast.CanMove(this, target);
                menu.Items.Add(destination);
            }
        }
    }

    private bool MoveTab(WindowTab item, MainWindow target, int? destination = null, TabControl? destinationGroup = null,
        Action<Exception>? reportFailure = null)
    {
        if (item.IsClosing || GroupOf(item) is not { } source || closing || item.Content is FormView { IsBusy: true })
            return false;
        reportFailure ??= item.ReportMoveFailure;
        destinationGroup ??= target.activeGroup;
        if (!target.CanAcceptTabs || !target.Groups.Contains(destinationGroup))
        {
            reportFailure(new InvalidOperationException(Text("TabMoveDestinationUnavailable")));
            return false;
        }
        if (!app.Broadcast.CanMove(this, target))
        {
            reportFailure(new InvalidOperationException(Text("BroadcastMoveBusy")));
            return false;
        }

        var terminal = item.Content as TerminalTab;
        bool moveCapturedPane = terminal?.HasMouseCapture == true;
        InvalidOperationException? placementError = null;
        var outcome = app.Broadcast.MoveTerminalTab(this, target, terminal, Place);
        if (placementError is not null) reportFailure(placementError);
        if (outcome.Placed && this != target && !closed && !closing && !AllTabs.Any()) Close();
        return outcome.Placed;

        bool Place()
        {
            int index = source.Items.IndexOf(item);
            var sourceSelection = source.SelectedItem;
            var targetSelection = destinationGroup.SelectedItem;
            source.Items.RemoveAt(index);
            try
            {
                int insertion = destination ?? destinationGroup.Items.Count;
                if (source == destinationGroup && destination is not null && index < insertion) insertion--;
                destinationGroup.Items.Insert(Math.Clamp(insertion, 0, destinationGroup.Items.Count), item);
            }
            catch (InvalidOperationException exception)
            {
                if (destinationGroup.Items.Contains(item)) destinationGroup.Items.Remove(item);
                source.Items.Insert(index, item);
                source.SelectedItem = sourceSelection;
                if (destinationGroup != source) destinationGroup.SelectedItem = targetSelection;
                placementError = exception;
                return false;
            }

            item.Placement = (target, destinationGroup);
            target.UpdateWelcomePanel();
            if (terminal is not null)
            {
                if (this != target && !terminal.IsExternalMacro) terminal.BroadcastChecked = true;
            }
            target.ActivateGroup(destinationGroup);
            if (moveCapturedPane || destinationGroup.SelectedContent is not TerminalTab { HasMouseCapture: true })
                destinationGroup.SelectedItem = item;
            if (app.Broadcast.ShouldActivateDestination(this, terminal))
                target.Activate();
            target.UpdateLayout();
            return true;
        }
    }
    private void DetachTab(WindowTab item, Point? screenPoint = null)
    {
        if (closing || GroupOf(item) is null) return;
        if (!app.Broadcast.CanMoveToNewWindow(this))
        {
            item.ReportMoveFailure(new InvalidOperationException(Text("BroadcastMoveBusy")));
            return;
        }
        var target = app.CreateWindow(restorePlacement: false);
        WindowPlacement.PlaceDetached(target, this, screenPoint ?? PointToScreen(new Point(24, 24)));
        target.Loaded += (_, _) =>
        {
            WindowPlacement.KeepOnScreen(target);
            if (!MoveTab(item, target)) target.Close();
        };
        target.Show();
    }

    internal static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private TerminalTab? SelectedTab => (activeGroup.SelectedItem as TabItem)?.Content as TerminalTab;

    private async Task ChangeRecordingAsync(bool resume)
    {
        if (SelectedTab is { } selected)
            await selected.ChangeRecordingAsync(resume);
    }

    private void AddButton(string key, Action action, ShortcutAction? shortcut = null)
    {
        var item = MenuAction(key, action);
        if (shortcut is { } assigned) item.InputGestureText = app.Shortcuts[assigned] ?? "";
        currentMenu!.Items.Add(item);
    }

    private static MenuItem MenuAction(string key, Action action)
    {
        var button = new MenuItem { Header = Text(key), Tag = key };
        button.Click += (_, _) => action();
        return button;
    }

    private bool CanReconnectTab(WindowTab item, TerminalTab terminal) =>
        !closing && !item.IsClosing && terminal.CanReconnect;

    private void ReconnectTab(WindowTab item)
    {
        if (item.Content is not TerminalTab terminal || !CanReconnectTab(item, terminal) || GroupOf(item) is null)
            return;
        terminal.Start(profile => _ = app.Profiles.RememberUsageAsync(terminal.ProfilePath, profile));
    }

    private void OpenTabListMenu(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var group = Ancestor<TabControl>(button) ?? activeGroup;
        ActivateGroup(group);
        foreach (WindowTab tab in group.Items.OfType<WindowTab>())
        {
            var item = new MenuItem { IsCheckable = true, IsChecked = tab.IsSelected, ToolTip = tab.ToolTip };
            SetMenuItemName(item, tab.Caption);
            AutomationProperties.SetName(item, AutomationProperties.GetName(tab));
            item.Click += (_, _) =>
            {
                if (!group.Items.Contains(tab)) return;
                if (tab.IsSelected)
                {
                    RevealTabHeader(group, tab);
                    ScheduleSelectedTerminalFocus(group);
                }
                else group.SelectedItem = tab;
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        button.ContextMenu = menu;
        menu.Closed += (_, _) => button.ClearValue(ContextMenuProperty);
        menu.IsOpen = true;
    }

    internal nint HandleWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0002 && taskbarPropertyWindow == hwnd) // WM_DESTROY
        {
            RemovePropW(hwnd, NonFullscreenWindowProperty);
            taskbarPropertyWindow = 0;
        }
        if (message == 0x0046) // WM_WINDOWPOSCHANGING
            WindowPlacement.FitMaximizedWindowToWorkArea(hwnd, lParam);
        if (message is 0x007e or 0x02e0) // WM_DISPLAYCHANGE, WM_DPICHANGED
            Dispatcher.BeginInvoke(() => { if (IsLoaded) WindowPlacement.KeepOnScreen(this); });
        bool wheel = message == 0x020a; // WM_MOUSEWHEEL
        bool captionRightClick = message == 0x00a5 && wParam == 2; // WM_NCRBUTTONUP, HTCAPTION
        if ((!wheel && !captionRightClick) || closing || !IsEnabled) return 0;
        var screenPoint = new Point(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));
        var group = Groups.FirstOrDefault(candidate =>
            candidate.Template?.FindName("tabStripRow", candidate) is FrameworkElement row && row.IsVisible &&
            PresentationSource.FromVisual(row) is not null && new Rect(row.RenderSize).Contains(row.PointFromScreen(screenPoint)));
        if (group?.Template?.FindName("tabStripRow", group) is not FrameworkElement strip) return 0;
        ActivateGroup(group);
        handled = true;
        if (wheel) MoveTabSelectionByWheel(group, unchecked((short)((long)wParam >> 16)));
        else
        {
            FrameworkElement? header = group.Items.OfType<WindowTab>().Select(tab => tab.Header).OfType<FrameworkElement>()
                .FirstOrDefault(element => element.ContextMenu is not null && PresentationSource.FromVisual(element) is not null &&
                    new Rect(element.RenderSize).Contains(element.PointFromScreen(screenPoint)));
            ContextMenu menu = header?.ContextMenu ?? strip.ContextMenu;
            menu.PlacementTarget = header ?? strip;
            menu.IsOpen = true;
        }
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPropW(nint window, string property, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint RemovePropW(nint window, string property);

    private void OnTabStripMenuOpened(object sender, RoutedEventArgs args)
    {
        var menu = (ContextMenu)sender;
        if (menu.PlacementTarget is DependencyObject anchor && Ancestor<TabControl>(anchor) is { } group)
            ActivateGroup(group);
        var item = (MenuItem)menu.Items[0];
        while (menu.Items.Count > 1) menu.Items.RemoveAt(1);
        menu.Items.Add(GroupCommands(activeGroup));
        item.Header = Text("CloseDisconnectedTabs");
        item.IsEnabled = !closing && AllTabs.Any(tab => tab.CanCloseAsDisconnected);
    }

    private async void CloseDisconnectedTabs(object sender, RoutedEventArgs args) =>
        await terminalClosure.CloseDisconnectedTabsAsync();

    internal static void MoveTabSelectionByWheel(TabControl group, int delta)
    {
        int count = group.Items.Count;
        if (count < 2)
        {
            group.ClearValue(TabWheelRemainderProperty);
            return;
        }
        int remainder = (int)group.GetValue(TabWheelRemainderProperty);
        if (Math.Sign(remainder) != Math.Sign(delta)) remainder = 0;
        remainder += delta;
        int steps = remainder / Mouse.MouseWheelDeltaForOneLine;
        group.SetValue(TabWheelRemainderProperty, remainder % Mouse.MouseWheelDeltaForOneLine);
        if (steps != 0)
            group.SelectedIndex = (Math.Max(0, group.SelectedIndex) - steps % count + count) % count;
    }

    private void OnScrollTabsLeft(object sender, RoutedEventArgs args) => ScrollTabHeaders((Button)sender, left: true);

    private void OnScrollTabsRight(object sender, RoutedEventArgs args) => ScrollTabHeaders((Button)sender, left: false);

    private static TabStripScroller? HeaderStrip(TabControl group) =>
        group.Template?.FindName("tabStripScroller", group) as TabStripScroller;

    private static void RevealTabHeader(TabControl group, TabItem tab)
    {
        if (HeaderStrip(group) is { } strip) strip.RevealSelection(group, tab);
        else tab.BringIntoView();
    }

    private static void ScrollTabHeaders(Button button, bool left)
    {
        if (button.IsEnabled && button.TemplatedParent is TabControl group)
            HeaderStrip(group)?.ScrollHeaders(left);
    }

    private void OnTabStripScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (sender != args.OriginalSource || sender is not TabStripScroller { TemplatedParent: TabControl group } strip)
            return;
        var left = (Button)group.Template.FindName("scrollTabsLeftButton", group);
        var right = (Button)group.Template.FindName("scrollTabsRightButton", group);
        strip.UpdateEdges(left, right);
    }

    private void OpenNewTabMenu(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var group = Ancestor<TabControl>(button) ?? activeGroup;
        ActivateGroup(group);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        IReadOnlyList<ProfileCatalog.Registration> profiles;
        try { profiles = app.Profiles.ReadRegistrations(); }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception)) { profiles = []; }
        foreach (var profileGroup in profiles.GroupBy(profile => profile.GroupName, StringComparer.OrdinalIgnoreCase))
        {
            ItemsControl parent = menu;
            if (profileGroup.Key is { } groupName)
            {
                var displayed = profileGroup.ToArray();
                string label = string.Format(Text("ProfileGroupCount"), PermissionDisplay.Literal(groupName), displayed.Length);
                var header = new TextBlock { Text = label, Background = Brushes.Transparent };
                var submenu = new MenuItem { Header = header };
                AutomationProperties.SetName(submenu, label);
                void ConnectGroup()
                {
                    menu.IsOpen = false;
                    try
                    {
                        var current = app.Profiles.ReadRegistrations().ToDictionary(profile => profile.Id);
                        if (displayed.All(profile => current.TryGetValue(profile.Id, out var registered) &&
                            StringComparer.Ordinal.Equals(profile.Path, registered.Path) &&
                            StringComparer.OrdinalIgnoreCase.Equals(groupName, registered.GroupName)))
                        {
                            OpenProfiles(displayed.Select(profile => profile.Path).ToArray(), group);
                            return;
                        }
                    }
                    catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception))
                    {
                        app.Profiles.ReportStateFailure(this);
                    }
                    OpenNewTabMenu(sender, args);
                }
                bool pressedName = false;
                submenu.PreviewMouseDown += (_, input) =>
                {
                    if (input.ChangedButton != MouseButton.Left) return;
                    pressedName = header.IsMouseOver;
                    input.Handled = pressedName;
                };
                submenu.PreviewMouseUp += (_, input) =>
                {
                    if (input.ChangedButton != MouseButton.Left || !pressedName) return;
                    pressedName = false;
                    input.Handled = true;
                    if (header.IsMouseOver) ConnectGroup();
                };
                submenu.PreviewKeyDown += (_, input) =>
                {
                    if (input.Key != Key.Enter || !submenu.IsKeyboardFocused) return;
                    input.Handled = true;
                    ConnectGroup();
                };
                menu.Items.Add(submenu);
                parent = submenu;
            }
            foreach (var profile in profileGroup)
            {
                var item = CreateProfileMenuItem(profile.Name, profile.Path, path => OpenProfiles([path], group));
                parent.Items.Add(item);
            }
        }
        if (profiles.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("ProfileCatalog", () => app.Profiles.Show(this)));
        menu.Closed += (_, _) => button.ClearValue(ContextMenuProperty);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    internal static MenuItem CreateProfileMenuItem(string name, string path, Action<string> open)
    {
        var item = new MenuItem { ToolTip = path };
        SetMenuItemName(item, name);
        item.Click += (_, _) => open(path);
        return item;
    }

    private static void SetMenuItemName(MenuItem item, string name)
    {
        item.Header = name.Replace("_", "__", StringComparison.Ordinal);
        AutomationProperties.SetName(item, name);
    }

    private void BeginMenu(string key, Key access)
    {
        currentMenu = new MenuItem { Header = Text(key), Tag = access };
        mainMenu.Items.Add(currentMenu);
    }

    private string ProfileFilter => ProfileCatalog.Filter;

    private void ChooseProfile()
    {
        var dialog = new OpenFileDialog { Filter = ProfileFilter, Multiselect = false };
        if (app.Modal(() => dialog.ShowDialog(this)) == true) OpenProfile(dialog.FileName);
    }

    private void CreateProfile()
    {
        if (!closing) app.Profiles.CreateProfile();
    }

    private void ShowHostKeys()
    {
        try { app.HostTrust.Show(this); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ReportCommandFailure("HostKeys", exception);
        }
    }

    internal void ReportCommandFailure(string commandKey, Exception error)
    {
        var report = app.StatusReports.CaptureFields(this, Text(commandKey));
        FormView.ReportFailure((key, _, fields) => report(key, fields), error);
    }

    private void EditSettings() => OpenForm("Settings", form =>
    {
        form.AddStatusBar();
        int revision = app.SettingsRevision;
        string[] languages = ["en", "ja", "system"];
        string[] themes = ["system", "light", "dark"];
        var language = LocalizedChoice(languages, app.LanguageChoice, "English", "Japanese", "System");
        var theme = LocalizedChoice(themes, app.ThemeChoice, "System", "Light", "Dark");
        string[] bells = ["visual", "off"];
        var bell = LocalizedChoice(bells, app.BellChoice, "VisualBell", "Off");
        var fontFamily = FontCatalog.Picker(app.TerminalFontFamily);
        var fontSize = new TextBox { Text = app.TerminalFontSize.ToString(CultureInfo.InvariantCulture) };
        var retained = new TextBox { Text = app.RetainedRows.ToString(CultureInfo.InvariantCulture) };
        var autoClose = new CheckBox { IsChecked = app.AutoCloseDisconnectedTabs };
        var maxPaste = new TextBox { Text = app.MaxPasteTextBytes.ToString(CultureInfo.InvariantCulture), MaxLength = 19 };
        var broadcastScope = new CheckBox { IsChecked = app.BroadcastAcrossWindows };
        var shortcutChoices = new List<ComboBox>();
        var naming = app.LogDefaults;
        var logDirectory = new TextBox { Text = naming.Directory, MaxLength = 32760 };
        var logTemplate = new TextBox { Text = naming.ProfileTemplate, MaxLength = RecordingNameTemplate.MaximumLength };
        var macroLogTemplate = new TextBox { Text = naming.MacroTemplate, MaxLength = RecordingNameTemplate.MaximumLength };
        var vaultPath = new TextBox { Text = app.VaultPath, MaxLength = 32760, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var profileDirectory = new TextBox { Text = app.ProfileSaveDirectory, MaxLength = 32760, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.AddNavigation(
            ("SettingsLanguageAndTheme", () =>
            {
                form.AddField("Language", language);
                form.AddField("Theme", theme);
            }),
            ("SettingsTerminalDefaults", () =>
            {
                form.AddField("DefaultTerminalFont", fontFamily);
                form.AddField("FontSizePoints", fontSize);
                form.AddField("DefaultRetainedRows", retained);
                form.AddField("Bell", bell);
            }),
            ("SettingsTabBehavior", () =>
            {
                form.AddField("AutoCloseDisconnectedTabs", autoClose, descriptionKey: "AutoCloseDisconnectedTabsDescription");
                form.AddField("BroadcastAcrossWindows", broadcastScope);
                form.AddField("MaxPasteSourceBytes", maxPaste);
            }),
            ("SettingsKeyboardShortcuts", () =>
            {
                foreach (var command in KeyboardShortcuts.Commands)
                {
                    var choice = new ComboBox();
                    var disabled = new ComboBoxItem();
                    form.AddLocalizedUpdate(() => disabled.Content = Text("DisabledShortcut"));
                    choice.Items.Add(disabled);
                    foreach (string option in KeyboardShortcuts.Options) choice.Items.Add(option);
                    choice.SelectedIndex = app.Shortcuts[command.Action] is { } assigned ? choice.Items.IndexOf(assigned) : 0;
                    shortcutChoices.Add(choice);
                    form.AddField(command.Label, choice);
                }
            }),
            ("SettingsLogNaming", () =>
            {
                form.AddField("DefaultLogDirectory", logDirectory, descriptionKey: "DefaultLogDirectoryDescription");
                form.AddField("DefaultLogFilenameTemplate", logTemplate, descriptionKey: "LogFilenameTemplateDescription");
                form.AddField("DefaultMacroLogFilenameTemplate", macroLogTemplate, descriptionKey: "MacroLogFilenameTemplateDescription");
            }),
            ("SettingsVault", () =>
            {
                form.AddField("VaultPath", vaultPath, actionKey: "BrowsePath", action: BrowseVaultPathAsync);
                form.AddField("ProfileSaveDirectory", profileDirectory, actionKey: "BrowsePath", action: BrowseProfileDirectoryAsync);
            }),
            ("SettingsMaintenance", () =>
            {
                form.AddActionField("RegisterFileType", "Run", RegisterFileType);
                form.AddActionField("ProfileTrustReset", "Run", () => app.Profiles.ResetTrust(form.OwnerWindow, form.ReportFailure));
                form.AddActionField("MacroManageTrust", "Run", () => form.TabOwner?.ManageMacroTrust());
                form.AddActionField("MacroTrustReset", "Run", () => ResetMacroTrust(form.OwnerWindow));
                form.AddActionField("RecoverStores", "Run", () =>
                {
                    if (form.TabOwner is { } host) app.ShowStoreRecovery(host);
                });
            }));
        form.AddEditorButtons(async () =>
        {
            if (revision != app.SettingsRevision)
            {
                form.ReportFailure(new InvalidOperationException(Text("ManagementTabOutdated")));
                return false;
            }
            if (string.IsNullOrWhiteSpace(fontFamily.Text) ||
                !double.TryParse(fontSize.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double size) ||
                !int.TryParse(retained.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int rows))
                throw new ArgumentException(Text("InvalidTerminalDefaults"));
            if (!long.TryParse(maxPaste.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long maxPasteBytes))
                throw new ArgumentException(Text("InvalidPasteSourceMaximum"));
            bool close = autoClose.IsChecked == true;
            bool broadcast = broadcastScope.IsChecked == true;
            var logging = app.LogDefaults;
            var shortcuts = new KeyboardShortcuts(shortcutChoices.Select(choice => choice.SelectedItem as string));
            using var progress = form.BeginProgress("FileOperationPending");
            await app.SaveSettingsAsync(revision, new App.SettingsChanges(
                Language: Change(language.SelectedId!, app.LanguageChoice),
                Theme: Change(theme.SelectedId!, app.ThemeChoice),
                Bell: Change(bell.SelectedId!, app.BellChoice),
                FontFamily: Change(fontFamily.Text, app.TerminalFontFamily),
                FontSize: size == app.TerminalFontSize ? null : size,
                RetainedRows: rows == app.RetainedRows ? null : rows,
                AutoClose: close == app.AutoCloseDisconnectedTabs ? null : close,
                VaultPath: Change(vaultPath.Text, app.VaultPath),
                Broadcast: broadcast == app.BroadcastAcrossWindows ? null : broadcast,
                PasteMaximum: maxPasteBytes == app.MaxPasteTextBytes ? null : maxPasteBytes,
                ProfileDirectory: Change(profileDirectory.Text, app.ProfileSaveDirectory),
                LogDirectory: Change(logDirectory.Text, logging.Directory),
                LogTemplate: Change(logTemplate.Text, logging.ProfileTemplate),
                MacroLogTemplate: Change(macroLogTemplate.Text, logging.MacroTemplate),
                Shortcuts: app.ChangedShortcuts(shortcuts)));
            revision = app.SettingsRevision;
            vaultPath.Text = app.VaultPath;
            profileDirectory.Text = app.ProfileSaveDirectory;
            return true;
        });

        static string? Change(string draft, string saved) => draft == saved ? null : draft;

        Task BrowseVaultPathAsync()
        {
            var picker = new SaveFileDialog
            {
                Filter = $"{Text("VaultFiles")} (*.tttykey)|*.tttykey", DefaultExt = ".tttykey",
                AddExtension = true, OverwritePrompt = false, FileName = Path.GetFileName(vaultPath.Text),
                InitialDirectory = Path.GetDirectoryName(vaultPath.Text) ?? "",
            };
            if (app.Modal(() => picker.ShowDialog(form.OwnerWindow)) == true) vaultPath.Text = picker.FileName;
            return Task.CompletedTask;
        }

        Task BrowseProfileDirectoryAsync()
        {
            var picker = new OpenFolderDialog { Title = Text("ProfileSaveDirectory"), InitialDirectory = profileDirectory.Text };
            if (app.Modal(() => picker.ShowDialog(form.OwnerWindow)) == true) profileDirectory.Text = picker.FolderName;
            return Task.CompletedTask;
        }

        void RegisterFileType()
        {
            try
            {
                ProfileFileAssociation.RegisterOpenWith();
                form.SetStatusResource("AssociationRegistered");
            }
            catch (Exception exception) when (ProfileFileAssociation.IsRegistrationFailure(exception)) { form.ReportFailure(exception); }
        }

        FixedChoiceBox LocalizedChoice(string[] ids, string selected, params string[] keys)
        {
            var choice = new FixedChoiceBox();
            form.AddLocalizedUpdate(() => choice.SetChoices(ids, keys.Select(Text).ToArray(), choice.SelectedId ?? selected));
            return choice;
        }
    });

    private void CloseSelectedTab()
    {
        if (activeGroup.SelectedItem is WindowTab item) CloseTab(item);
    }

    private async void CloseTab(WindowTab item)
    {
        if (item.Content is FormView form)
        {
            if (closing || item.IsClosing || GroupOf(item) is null) return;
            form.RequestClose(form.ConfirmDiscardChanges);
            return;
        }
        await terminalClosure.CloseTabAsync(item);
    }

    private (bool Removed, Task OutputRelease) RemoveTerminalTab(WindowTab item)
    {
        if (GroupOf(item) is not { } group || item.Content is not TerminalTab terminal)
            return (false, Task.CompletedTask);
        group.Items.Remove(item);
        if (group.Items.Contains(item)) return (false, Task.CompletedTask);
        item.Placement = null;
        Task release = terminal.ReleaseRemovedOutputAsync();
        app.Ai.EndSharing(terminal);
        terminal.DisposeMacro();
        app.Broadcast.Refresh();
        UpdateWelcomePanel();
        return (true, release);
    }

    private void RemoveFormTab(WindowTab item)
    {
        if (GroupOf(item) is not { } group) return;
        group.Items.Remove(item);
        if (group.Items.Contains(item)) return;
        if (item.Content is FormView form) form.Reports.Dispose();
        item.Placement = null;
        UpdateWelcomePanel();
    }

    private void SetWindowCloseControls(bool pending)
    {
        groupHost.IsEnabled = !pending;
        if (mainMenu.Items.OfType<MenuItem>().FirstOrDefault() is not { } file) return;
        foreach (var category in mainMenu.Items.OfType<MenuItem>().Skip(1))
            category.IsEnabled = !pending;
        foreach (var action in file.Items.OfType<MenuItem>())
            action.IsEnabled = !pending || action.Tag is "CloseWithLoss";
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (app.Vault.IsInitializingFor(this) || AllTabs.Any(tab => tab.Content is FormView { BlocksClose: true }))
        {
            e.Cancel = true;
            return;
        }
        if (readyToClose) { SavePlacement(); return; }
        if (!closing)
        {
            var context = new StackPanel();
            foreach (var tab in AllTabs)
                if (tab.Content is FormView form && form.UnresolvedPublication is { } unresolved)
                    context.Children.Add(form.DiscardContext(unresolved));
            if (context.Children.Count > 0 && app.Modal(() => ThemedMessageBox.Show(this,
                Text("DiscardChangesAndCloseWindow"), Text("DiscardAndClose"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, acceptKey: "DiscardAndClose",
                context: context)) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        if (!closing && !TerminalTabs.Any())
        {
            closing = readyToClose = true;
            SavePlacement();
            return;
        }
        e.Cancel = true;
        _ = terminalClosure.CloseWindowAsync();
    }

    private void SavePlacement()
    {
        if (!IsLoaded) return;
        try { WindowPlacement.Save(this); }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception)) { app.Profiles.ReportStateFailure(this); }
    }

    private bool ConfirmCleanupLoss()
    {
        var choice = new FormWindow(this, Text("ClosingConnection"));
        choice.AddNotice("CleanupPendingExplanation");
        choice.AddButtons(() => { }, acceptKey: "CloseWithLoss", cancelDefault: true, cancelKey: "ContinueWaiting");
        return app.Modal(choice.ShowDialog) == true;
    }

    private bool ConfirmClose(TerminalTab[] terminals)
    {
        foreach (var terminal in terminals) terminal.Macro?.SetCloseReview(true);
        bool confirmed = false;
        try
        {
            var context = new StackPanel();
            AddTargets("AffectedTabs", terminals);
            AddTargets("FileTransfer", terminals.Where(terminal => terminal.HasActiveTransfer));
            AddTargets("MacroRunningOnClose", terminals.Where(terminal => terminal.HasActiveMacro));
            bool connected = terminals.Any(terminal => terminal.IsRunning);
            string action = connected ? "DisconnectAndClose" : "StopWorkAndClose";
            string prompt = Text(connected ? "CloseConnections" : "CloseTabWork");
            confirmed = app.Modal(() => ThemedMessageBox.Show(this, prompt, Text(action),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, acceptKey: action,
                context: context)) == MessageBoxResult.Yes;
            return confirmed;

            void AddTargets(string key, IEnumerable<TerminalTab> targets)
            {
                string[] names = targets.Select(tab => PermissionDisplay.Literal(tab.TabCaption)).ToArray();
                if (names.Length != 0)
                    context.Children.Add(new DialogField(Text(key), new DialogText(string.Join("\n", names), names.Length == 1 ? null : 180)));
            }
        }
        finally
        {
            if (!confirmed)
                foreach (var terminal in terminals) terminal.Macro?.SetCloseReview(false);
        }
    }

    internal static void ShowError(Window owner, Exception exception)
    {
        string message = exception.Message;
        StackPanel? context = null;
        if (exception is FilePublicationException publication)
        {
            var fields = JsonStoreRecovery.FailureFields(publication.Result);
            message = fields[0].Value;
            context = new StackPanel();
            foreach (var field in fields.Skip(1)) context.Children.Add(field.Create());
        }
        ((App)Application.Current).Modal(() =>
            ThemedMessageBox.Show(owner, message, Text("Error"), icon: MessageBoxImage.Error, context: context));
    }

    internal sealed class WindowTab : TabItem
    {
        private bool updatingBroadcast;
        private readonly CheckBox targetCheck = new()
        {
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
            MinHeight = 0, Height = 24, Padding = new Thickness(0),
        };
        private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly System.Windows.Shapes.Path disconnectedIcon = new()
        {
            Name = "DisconnectedIndicator",
            // Two separated link ends with strokes marking the break.
            Data = Geometry.Parse(
                "M 5,7 L 3,9 A 2.8,2.8 0 0 0 7,13 L 9,11 " +
                "M 7,5 L 9,3 A 2.8,2.8 0 0 1 13,7 L 11,9 " +
                "M 3,3 L 5,5 M 11,11 L 13,13"),
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0),
            Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        private readonly Border warningIcon = new()
        {
            CornerRadius = new CornerRadius(2),
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        private readonly System.Windows.Shapes.Path aiIcon = new()
        {
            Name = "AiCollaborationIndicator",
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0),
            Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        private static readonly Geometry AiEye = Geometry.Parse("M 1,8 Q 8,-1 15,8 Q 8,17 1,8 Z M 10,8 A 2,2 0 1 1 6,8 A 2,2 0 1 1 10,8");
        private static readonly Geometry AiTerminal = Geometry.Parse("M 1,2 H 15 V 14 H 1 Z M 5,5 L 11,8 L 5,11 Z");
        private readonly Button dismiss = new()
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10 },
            Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        };
        private readonly Button attention = new()
        {
            Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 4, 0),
            Visibility = Visibility.Collapsed, FontWeight = FontWeights.Bold,
        };

        public WindowTab(FrameworkElement content)
        {
            SetResourceReference(StyleProperty, typeof(TabItem));
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var header = new DockPanel();
            disconnectedIcon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            var warningGlyph = new TextBlock { Text = "!", FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            warningIcon.SetResourceReference(Border.BackgroundProperty, "WindowForeground");
            warningGlyph.SetResourceReference(TextBlock.ForegroundProperty, "WindowBackground");
            warningIcon.Child = warningGlyph;
            aiIcon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            DockPanel.SetDock(dismiss, Dock.Right);
            header.Children.Add(dismiss);
            header.Children.Add(targetCheck);
            header.Children.Add(disconnectedIcon);
            header.Children.Add(warningIcon);
            header.Children.Add(attention);
            header.Children.Add(aiIcon);
            header.Children.Add(label);
            Header = header;
            Content = content;
            if (content is FormView form)
            {
                form.Tab = this;
                form.Reports.Changed += () => Caption = Caption;
            }
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(attention, true);
            attention.Click += (_, _) =>
            {
                Host?.SelectNotificationTab(this);
                if (Content is TerminalTab { HasPendingDecision: true } pending) pending.ActivatePendingDecision();
            };
            if (content is TerminalTab terminal)
            {
                terminal.ResolveTabHost(() => Host);
                UpdateTerminalCaption(terminal);
                terminal.CaptionChanged += () =>
                {
                    UpdateTerminalCaption(terminal);
                    Host?.app.Broadcast.Refresh();
                };
                terminal.Disconnected += () =>
                {
                    object generation = terminal.ConnectionGeneration;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(terminal.ConnectionGeneration, generation))
                            Host?.OnTerminalDisconnected(this);
                    });
                };
                var menu = new ContextMenu();
                header.ContextMenu = menu;
                menu.Closed += (_, _) => menu.Items.Clear();
                menu.Opened += (_, _) =>
                {
                    menu.Items.Clear();
                    if (terminal.CanTransfer) menu.Items.Add(MenuAction("FileTransfer", terminal.OpenFileTransfer));
                    if (Placement is not { } placement) return;
                    var (host, group) = placement;
                    if (host.CanReconnectTab(this, terminal))
                        menu.Items.Add(MenuAction("Reconnect", () => Host?.ReconnectTab(this)));
                    terminal.AddMouseRoutingCommand(menu);
                    terminal.FillAiMenu(menu);
                    menu.Items.Add(host.BroadcastCommands());
                    menu.Items.Add(host.GroupCommands(group));
                    var panes = new MenuItem { Header = Text("TerminalPanes") };
                    terminal.FillPaneCommands(panes);
                    menu.Items.Add(panes);
                };
                targetCheck.Click += (_, _) =>
                {
                    if (updatingBroadcast || Host is not { } host) return;
                    host.app.Broadcast.Select(host, terminal, targetCheck.IsChecked == true);
                };
                bool enterPressed = false;
                PreviewKeyDown += (_, args) =>
                {
                    if (args.Key != Key.Enter || !ReferenceEquals(args.OriginalSource, this) || !IsSelected ||
                        (!enterPressed && args.KeyboardDevice.Modifiers != ModifierKeys.None)) return;
                    if (!args.IsRepeat) enterPressed = true;
                    args.Handled = true;
                };
                LostKeyboardFocus += (_, args) =>
                {
                    if (ReferenceEquals(args.OldFocus, this)) enterPressed = false;
                };
                PreviewKeyUp += (_, args) =>
                {
                    if (args.Key == Key.Enter && enterPressed && ReferenceEquals(args.OriginalSource, this))
                    {
                        enterPressed = false;
                        args.Handled = true;
                        // Keep the key and its repeats on the header until it is released.
                        if (IsSelected && terminal.IsVisible && args.KeyboardDevice.Modifiers == ModifierKeys.None)
                            terminal.FocusPane();
                        return;
                    }
                    if (args.Key != Key.Apps && (args.Key != Key.F10 || Keyboard.Modifiers != ModifierKeys.Shift)) return;
                    if (args.OriginalSource is not DependencyObject source || !IsHeaderSource(source)) return;
                    menu.PlacementTarget = header;
                    menu.IsOpen = true;
                    args.Handled = true;
                };

                bool IsHeaderSource(DependencyObject source) => ReferenceEquals(source, this) ||
                    source is Visual visual && header.IsAncestorOf(visual);
            }
            // Tab items are logically parented by the TabControl, outside the header scroller.
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(this, true);
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(content, false);
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(dismiss, true);
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(targetCheck, true);
            dismiss.Click += (_, _) => Host?.CloseTab(this);
            UpdateCloseLabel();
        }

        public string? FormKey { get; init; }
        private (MainWindow Window, TabControl Group)? placement;
        public (MainWindow Window, TabControl Group)? Placement
        {
            get => placement;
            set
            {
                if (placement != value && Header is FrameworkElement { ContextMenu: { } menu })
                {
                    menu.IsOpen = false;
                    menu.Items.Clear();
                }
                placement = value;
                if (value is null) return;
                if (Content is TerminalTab terminal) terminal.RefreshNoticeOwner();
                if (Content is FormView form) form.Reports.HostChanged();
            }
        }
        public MainWindow? Host => Placement?.Window;
        internal void ReportMoveFailure(Exception error)
        {
            if (Content is TerminalTab terminal) FormView.ReportFailure(terminal.Reports.Report, error);
            else if (Content is FormView form) form.ReportFailure(error);
        }

        public bool IsClosing => Content is TerminalTab { ClosePending: true };
        public bool CanCloseAsDisconnected => Content is TerminalTab { IsDisconnected: true, HasActiveMacro: false };

        public void UpdateBroadcast(bool visible, bool editable)
        {
            if (Content is not TerminalTab terminal) return;
            updatingBroadcast = true;
            targetCheck.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            targetCheck.IsEnabled = editable && !terminal.IsAiShared;
            targetCheck.IsChecked = terminal.BroadcastChecked;
            string target = visible && terminal.InputSession is { } active
                ? TerminalTab.BroadcastTargetLabel(active) : PermissionDisplay.Literal(Caption);
            targetCheck.ToolTip = terminal.IsAiShared ? Text("AiBroadcastExcluded") : target;
            ToolTipService.SetShowOnDisabled(targetCheck, true);
            AutomationProperties.SetHelpText(targetCheck, terminal.IsAiShared ? Text("AiBroadcastExcluded") : "");
            AutomationProperties.SetName(targetCheck, string.Format(Text("BroadcastTarget"), target));
            updatingBroadcast = false;
        }
        public string Caption
        {
            get => label.Text;
            set
            {
                label.Text = value;
                string? statusKey = Content is TerminalTab terminal ? terminal.ConnectionWarning : null;
                if (statusKey is null && disconnectedIcon.Visibility == Visibility.Visible) statusKey = "Disconnected";
                string description = statusKey is null ? value : $"({Text(statusKey)}) {value}";
                if (Content is TerminalTab { AiStateLabel: { } aiState }) description += $" ({aiState})";
                bool decision = Content is TerminalTab { HasPendingDecision: true };
                bool unread = Content switch { TerminalTab t => t.Reports.HasUnread, FormView f => f.Reports.HasUnread, _ => false };
                attention.Visibility = decision || unread ? Visibility.Visible : Visibility.Collapsed;
                if (decision || unread)
                {
                    string detail = string.Join(" · ", new[] { decision ? Text("NoticeAwaitingAnswer") : null,
                        unread ? Text("NoticeUnread") : null }.OfType<string>());
                    attention.Content = decision ? "?" : "!";
                    attention.ToolTip = detail;
                    AutomationProperties.SetName(attention, detail);
                    description += $" ({detail})";
                }
                ToolTip = description;
                AutomationProperties.SetName(this, description);
            }
        }

        public void UpdateTerminalCaption(TerminalTab terminal)
        {
            disconnectedIcon.Visibility = terminal.ShowDisconnectedIndicator ? Visibility.Visible : Visibility.Collapsed;
            warningIcon.Visibility = terminal.ConnectionWarning is null ? Visibility.Collapsed : Visibility.Visible;
            aiIcon.Visibility = terminal.IsAiShared ? Visibility.Visible : Visibility.Collapsed;
            aiIcon.Data = terminal.IsAiControlling || terminal.IsAiTakingControl ? AiTerminal : AiEye;
            aiIcon.ToolTip = terminal.AiStateLabel;
            AutomationProperties.SetName(aiIcon, terminal.AiStateLabel ?? "");
            Caption = terminal.TabCaption;
        }

        public void UpdateCloseLabel() => SetButtonLabel(dismiss, "CloseTab");
    }

    internal sealed class FormWindow : Window
    {
        private readonly FormView form = new(modal: true);

        public FormWindow(Window? owner, string title, ImageSource? icon = null)
        {
            if (owner is not null) Owner = owner;
            Title = title;
            Icon = icon ?? WindowIcons.App;
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            DialogLayout.SetWindowBounds(this);
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "WindowForeground");
            Content = form;
            form.Completed += Finish;
            Closing += (_, args) => { if (form.IsBusy) args.Cancel = true; };
        }

        internal bool IsDecision { get; set; }
        internal bool Accepted { get; private set; }
        internal void Finish(bool accepted)
        {
            if (IsDecision)
            {
                if (!IsVisible) return;
                Accepted = accepted;
                Close();
            }
            else DialogResult = accepted;
        }

        public Button AddAction(string key, Action action) => form.AddAction(key, action);
        internal Button AddDialogAction(string key, Action action, bool isDefault = false) =>
            form.AddDialogAction(key, action, isDefault);
        internal void AddCloseAction() => form.AddDialogAction("CloseDialog", Close, isDefault: true).IsCancel = true;
        public void AddField(string key, Control control, bool inlineCheckBox = false,
            string? actionKey = null, Func<Task>? action = null) =>
            form.AddField(key, control, actionKey: actionKey, action: action, inlineCheckBox: inlineCheckBox);
        public void AddPrompt(string caption, Control control) => form.AddPrompt(caption, control);
        internal Action<string> AddValidation(Control field) => form.AddValidation(field);
        internal void AddDisplay(string key, FrameworkElement element) => form.AddDisplay(key, element);
        public void AddNotice(string key) => form.AddNotice(key);
        internal void AddHeading(string key) => form.AddNotice(key, heading: true);
        internal void AddStatusBar() => form.AddStatusBar();
        internal IDisposable BeginProgress(string key) => form.BeginProgress(key);
        public TextBlock AddPinnedNotice(string message)
        {
            var notice = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 10, 14, 4) };
            DockPanel.SetDock(notice, Dock.Top);
            form.Body.Children.Insert(0, notice);
            return notice;
        }
        internal DialogField AddPinnedField(string key, FrameworkElement value)
        {
            // The scrollable form body already supplies the remaining 14 DIP above its first field.
            var field = new DialogField(Text(key), value) { Margin = new Thickness(14, 10, 14, 2) };
            form.AddLocalizedUpdate(() => field.SetCaption(Text(key)));
            DockPanel.SetDock(field, Dock.Top);
            form.Body.Children.Insert(0, field);
            return field;
        }
        public Button AddButtons(Action save, string acceptKey = "Save", bool cancelDefault = false, string cancelKey = "Cancel") =>
            form.AddButtons(save, acceptKey, cancelDefault, cancelKey);
        public void AddAsyncButtons(Func<Task<bool>> save, string acceptKey = "Save", bool cancelDefault = false,
            bool acceptOnEnter = true) => form.AddAsyncButtons(save, acceptKey, cancelDefault, acceptOnEnter: acceptOnEnter);
    }
}
