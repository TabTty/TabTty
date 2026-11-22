using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class MainWindow : Window
{
    private readonly App app;
    private readonly TerminalClosure terminalClosure;
    private MenuItem? currentMenu;
    private bool closing;
    private bool readyToClose;
    private bool closed;
    private int tabWheelDelta;
    private Color? headerAccent;

    public MainWindow(App app)
    {
        this.app = app;
        terminalClosure = new(this);
        Closed += (_, _) =>
        {
            closed = true;
            groups.ReleaseReservations();
            app.CommitWindowClose(this);
        };
        // Register before WindowChrome so right-clicking the caption can open the tab menu.
        SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)
                ?.AddHook(HandleWindowMessage);
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
        NativeKeyState.Attach(this);
        InitializeGroups();
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
        var group = Groups.FirstOrDefault(group =>
            group.Items.OfType<WindowTab>().Any(tab => ReferenceEquals(tab.Content, source)));
        if (group is not null) OpenProfiles(paths, group);
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
            first ??= item;
        }
        group.SelectedItem = first;
        UpdateWelcomePanel();
        app.Broadcast.Refresh();
        foreach (var terminal in terminals)
            terminal.Start(profile => _ = app.Profiles.RememberAsync(terminal.ProfilePath, profile, used: true));
    }

    internal void AddTerminalTab(TerminalTab terminal) => SelectNewTab(CreateTerminalTab(terminal));

    private WindowTab CreateTerminalTab(TerminalTab terminal)
    {
        terminal.MacroWindow = this;
        var item = new WindowTab(terminal) { Host = this };
        item.UpdateTerminalCaption(terminal);
        terminal.CaptionChanged += () => { item.UpdateTerminalCaption(terminal); app.Broadcast.Refresh(); };
        terminal.Disconnected += () => item.Host?.OnTerminalDisconnected(item);
        return item;
    }

    private void OnTerminalDisconnected(WindowTab item)
    {
        if (!closing && item.Content is TerminalTab { Macro: null } terminal &&
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
            form.TabOwner?.RemoveFormTab(item, form);
        };
        build(form);
        SelectNewTab(item);
    }

    private void SelectNewTab(WindowTab item)
    {
        item.Host = this;
        if (item.Content is FormView form) form.TabOwner = this;
        activeGroup.Items.Add(item);
        activeGroup.SelectedItem = item;
        UpdateWelcomePanel();
        app.Broadcast.Refresh();
    }

    internal bool CanAcceptTabs => IsLoaded && !closing;
    internal bool IsClosing => closing;

    internal void UpdateLabels()
    {
        groups.UpdateLabels();
        LabelGroups();
        RefreshBroadcast();
        welcomeTitle.Text = Text("WelcomeTitle");
        profileHint.Text = Text("OpenHint");
        vaultHint.Text = Text("VaultHint");
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
            if (SelectedTab is { IsRunning: false } terminal)
                terminal.Start(profile => _ = app.Profiles.RememberAsync(terminal.ProfilePath, profile, used: true));
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
        vaultMenu.Items.Add(MenuAction("UnlockVault", async () => await app.Vault.UnlockFromMenuAsync(this)));
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

    private bool MoveTab(TabItem item, MainWindow target, int? destination = null, TabControl? destinationGroup = null)
    {
        if (GroupOf(item) is not { } source || !target.CanAcceptTabs || closing ||
            item is WindowTab { IsClosing: true } || item.Content is FormView { IsBusy: true }) return false;
        if (!app.Broadcast.CanMove(this, target))
        {
            ShowError(this, new InvalidOperationException(Text("BroadcastMoveBusy")));
            return false;
        }

        destinationGroup ??= target.activeGroup;
        if (!target.Groups.Contains(destinationGroup)) return false;
        var terminal = item.Content as TerminalTab;
        bool moveCapturedPane = terminal?.HasMouseCapture == true;
        InvalidOperationException? placementError = null;
        var outcome = app.Broadcast.MoveTerminalTab(this, target, terminal, Place);
        if (placementError is not null) ShowError(this, placementError);
        if (outcome.Placed && this != target && !closed && !closing && !AllTabs.Any()) Close();
        return outcome.Placed;

        bool Place()
        {
            int index = source.Items.IndexOf(item);
            bool wasSelected = item.IsSelected;
            source.Items.RemoveAt(index);
            try
            {
                int insertion = destination ?? destinationGroup.Items.Count;
                if (source == destinationGroup && destination is not null && index < insertion) insertion--;
                destinationGroup.Items.Insert(Math.Clamp(insertion, 0, destinationGroup.Items.Count), item);
            }
            catch (InvalidOperationException exception)
            {
                source.Items.Insert(index, item);
                if (wasSelected) source.SelectedItem = item;
                placementError = exception;
                return false;
            }

            target.UpdateWelcomePanel();
            if (item is WindowTab movedTab) movedTab.Host = target;
            if (terminal is not null)
            {
                terminal.MacroWindow = target;
                if (this != target && terminal.Macro is null) terminal.BroadcastChecked = true;
            }
            if (item.Content is FormView moved) moved.TabOwner = target;
            target.ActivateGroup(destinationGroup);
            if (moveCapturedPane || destinationGroup.SelectedContent is not TerminalTab { HasMouseCapture: true })
                destinationGroup.SelectedItem = item;
            if (app.Broadcast.ShouldActivateDestination(this, terminal))
                target.Activate();
            target.UpdateLayout();
            return true;
        }
    }
    private void DetachTab(TabItem item, Point? screenPoint = null)
    {
        if (closing || GroupOf(item) is null) return;
        if (!app.Broadcast.CanMoveToNewWindow(this))
        {
            ShowError(this, new InvalidOperationException(Text("BroadcastMoveBusy")));
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
                    tab.BringIntoView();
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
        if (wheel) MoveTabSelectionByWheel(unchecked((short)((long)wParam >> 16)));
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

    private void MoveTabSelectionByWheel(int delta)
    {
        int count = activeGroup.Items.Count;
        if (count < 2)
        {
            tabWheelDelta = 0;
            return;
        }
        if (Math.Sign(tabWheelDelta) != Math.Sign(delta)) tabWheelDelta = 0;
        tabWheelDelta += delta;
        int steps = tabWheelDelta / Mouse.MouseWheelDeltaForOneLine;
        tabWheelDelta %= Mouse.MouseWheelDeltaForOneLine;
        if (steps != 0)
            activeGroup.SelectedIndex = (Math.Max(0, activeGroup.SelectedIndex) - steps % count + count) % count;
    }

    private void OnScrollTabsLeft(object sender, RoutedEventArgs args) => ScrollTabHeaders((Button)sender, left: true);

    private void OnScrollTabsRight(object sender, RoutedEventArgs args) => ScrollTabHeaders((Button)sender, left: false);

    private static void ScrollTabHeaders(Button button, bool left)
    {
        if (!button.IsEnabled || button.TemplatedParent is not TabControl group ||
            group.Template.FindName("tabStripScroller", group) is not ScrollViewer strip) return;

        var tabs = group.Items.OfType<TabItem>();
        foreach (TabItem tab in left ? tabs.Reverse() : tabs)
        {
            double start = tab.TranslatePoint(new Point(), (UIElement)strip.Content).X - strip.HorizontalOffset;
            double hidden = left ? -start : start + tab.ActualWidth - strip.ViewportWidth;
            if (hidden <= TabScrollTolerance) continue;
            double distance = Math.Min(hidden, strip.ViewportWidth);
            strip.ScrollToHorizontalOffset(strip.HorizontalOffset + (left ? -distance : distance));
            return;
        }
        if (left) strip.ScrollToLeftEnd();
        else strip.ScrollToRightEnd();
    }

    private const double TabScrollTolerance = 0.01;

    private void OnTabStripScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (sender != args.OriginalSource) return;
        var strip = (ScrollViewer)sender;
        var group = (TabControl)strip.TemplatedParent;
        var leftButton = (Button)group.Template.FindName("scrollTabsLeftButton", group);
        var rightButton = (Button)group.Template.FindName("scrollTabsRightButton", group);
        // Use the width available with the arrows hidden so they cannot sustain overflow themselves.
        double available = strip.ViewportWidth +
            (leftButton.Visibility == Visibility.Visible ? leftButton.ActualWidth + rightButton.ActualWidth : 0);
        var headers = (TabHeaderPanel)strip.Content;
        double headerWidth = strip.ExtentWidth - headers.AddedPreviewWidth;
        leftButton.Visibility = rightButton.Visibility =
            headerWidth - available > TabScrollTolerance ? Visibility.Visible : Visibility.Collapsed;
        bool left = strip.HorizontalOffset > TabScrollTolerance;
        bool right = strip.ScrollableWidth - strip.HorizontalOffset > TabScrollTolerance;
        leftButton.IsEnabled = left;
        rightButton.IsEnabled = right;
        if ((!left && !right) || strip.ViewportWidth <= 0)
        {
            strip.OpacityMask = null;
            return;
        }
        double fade = Math.Min(18 / strip.ViewportWidth, 0.5);
        strip.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
            GradientStops =
            [
                new GradientStop(left ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fade),
                new GradientStop(Colors.Black, 1 - fade),
                new GradientStop(right ? Colors.Transparent : Colors.Black, 1),
            ],
        };
    }

    private async void OpenNewTabMenu(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var group = Ancestor<TabControl>(button) ?? activeGroup;
        ActivateGroup(group);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        IReadOnlyList<ProfileCatalog.Registration> profiles;
        try { profiles = app.Profiles.ReadRegistrations(); }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception)) { profiles = []; }
        var entries = new List<(string Path, MenuItem Item)>();
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
                entries.Add((profile.Path, item));
                parent.Items.Add(item);
            }
        }
        if (entries.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("ProfileCatalog", () => app.Profiles.Show(this)));
        menu.Closed += (_, _) => button.ClearValue(ContextMenuProperty);
        using var lifetime = new CancellationTokenSource();
        RoutedEventHandler closed = (_, _) => lifetime.Cancel();
        menu.Closed += closed;
        try
        {
            button.ContextMenu = menu;
            menu.IsOpen = true;
            await app.Profiles.ReadRegistrationDetailsAsync(profiles, lifetime.Token, legacyOnly: true);
            if (lifetime.IsCancellationRequested) return;
            var names = app.Profiles.ReadRegistrations().ToDictionary(profile => profile.Path, profile => profile.Name,
                StringComparer.Ordinal);
            foreach (var (path, item) in entries)
                if (names.TryGetValue(path, out string? name)) SetMenuItemName(item, name);
        }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception)) { }
        finally { menu.Closed -= closed; }
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
            ShowError(this, exception);
        }
    }

    private void EditSettings() => OpenForm("Settings", form =>
    {
        form.ShowFileOperationStatus();
        int revision = app.SettingsRevision;
        string[] languages = ["en", "ja", "system"];
        string[] themes = ["system", "light", "dark"];
        var language = LocalizedChoice(Array.IndexOf(languages, app.LanguageChoice), "English", "Japanese", "System");
        var theme = LocalizedChoice(Array.IndexOf(themes, app.ThemeChoice), "System", "Light", "Dark");
        string[] bells = ["visual", "off"];
        var bell = LocalizedChoice(Array.IndexOf(bells, app.BellChoice), "VisualBell", "Off");
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
                form.AddActionField("ProfileTrustReset", "Run", () => app.Profiles.ResetTrust(form.OwnerWindow));
                form.AddActionField("MacroManageTrust", "Run", () => form.TabOwner?.ManageMacroTrust());
                form.AddActionField("MacroTrustReset", "Run", () => ResetMacroTrust(form.OwnerWindow));
                form.AddActionField("RecoverStores", "Run", () =>
                {
                    if (form.TabOwner is { } host) app.ShowStoreRecovery(host);
                });
            }));
        form.AddEditorButtons(async () =>
        {
            if (string.IsNullOrWhiteSpace(fontFamily.Text) ||
                !double.TryParse(fontSize.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double size) ||
                !double.IsFinite(size) || size is < 6 or > 72 ||
                !int.TryParse(retained.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int rows) ||
                rows is < 100 or > 100_000)
                throw new ArgumentException(Text("InvalidTerminalDefaults"));
            if (!long.TryParse(maxPaste.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long maxPasteBytes) ||
                maxPasteBytes <= 0)
                throw new ArgumentException(Text("InvalidPasteSourceMaximum"));
            await app.SaveSettingsAsync(languages[language.SelectedIndex], themes[theme.SelectedIndex], bells[bell.SelectedIndex],
                fontFamily.Text, size, rows, autoClose.IsChecked == true, vaultPath.Text, revision,
                broadcastScope.IsChecked == true, maxPasteBytes,
                profileDirectory.Text == app.ProfileSaveDirectory ? null : profileDirectory.Text,
                new RecordingDefaults(logDirectory.Text, logTemplate.Text, macroLogTemplate.Text),
                new KeyboardShortcuts(shortcutChoices.Select(choice => choice.SelectedItem as string)));
            revision = app.SettingsRevision;
            vaultPath.Text = app.VaultPath;
            profileDirectory.Text = app.ProfileSaveDirectory;
            return true;
        });

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
                app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, Text("AssociationRegistered"), "TabTty"));
            }
            catch (Exception exception) when (ProfileFileAssociation.IsRegistrationFailure(exception)) { ShowError(form.OwnerWindow, exception); }
        }

        ComboBox LocalizedChoice(int index, params string[] keys)
        {
            var choice = new ComboBox();
            form.AddLocalizedUpdate(() =>
            {
                int selected = choice.ItemsSource is null ? index : choice.SelectedIndex;
                choice.ItemsSource = keys.Select(Text).ToArray();
                choice.SelectedIndex = selected;
            });
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
        if (GroupOf(item) is not null) return (false, Task.CompletedTask);
        item.Host = null;
        Task release = terminal.ReleaseRemovedOutputAsync();
        app.Ai.EndSharing(terminal);
        terminal.DisposeMacro();
        app.Broadcast.Refresh();
        UpdateWelcomePanel();
        return (true, release);
    }

    private void RemoveFormTab(WindowTab item, FormView form)
    {
        if (GroupOf(item) is not { } group) return;
        group.Items.Remove(item);
        item.Host = null;
        form.TabOwner = null;
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
            string[] unresolved = AllTabs.Select(tab => (tab.Content as FormView)?.UnresolvedPublication)
                .OfType<string>().ToArray();
            if (unresolved.Length > 0 && app.Modal(() => ThemedMessageBox.Show(this,
                $"{Text("DiscardChangesAndCloseWindow")}\n\n{string.Join("\n\n", unresolved)}", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes)
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
        choice.AddField("CleanupPending", new TextBox
        {
            Text = Text("CleanupPendingExplanation"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        });
        choice.AddButtons(() => { }, acceptKey: "CloseWithLoss", cancelDefault: true, cancelKey: "ContinueWaiting");
        return app.Modal(choice.ShowDialog) == true;
    }

    private bool ConfirmClose(TerminalTab[] terminals)
    {
        foreach (var terminal in terminals) terminal.Macro?.SetCloseReview(true);
        bool confirmed = false;
        try
        {
            string transfers = string.Concat(terminals.Where(terminal => terminal.HasActiveTransfer)
                .Select(terminal => $"\n{Text("FileTransfer")} — {terminal.TabCaption}"));
            string macros = string.Concat(terminals.Where(terminal => terminal.HasActiveMacro)
                .Select(terminal => $"\n{Text("MacroRunningOnClose")} — {terminal.TabCaption}"));
            string prompt = Text(terminals.Any(terminal => terminal.IsRunning) ? "CloseConnections" : "CloseTabWork");
            confirmed = app.Modal(() => ThemedMessageBox.Show(this, prompt + transfers + macros,
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes;
            return confirmed;
        }
        finally
        {
            if (!confirmed)
                foreach (var terminal in terminals) terminal.Macro?.SetCloseReview(false);
        }
    }

    internal static void ShowError(Window owner, Exception exception) =>
        ((App)Application.Current).Modal(() =>
            ThemedMessageBox.Show(owner, exception is FilePublicationException publication ?
                JsonStoreRecovery.DescribeFailure(publication.Result) : exception.Message,
                Text("Error"), MessageBoxButton.OK, MessageBoxImage.Error));

    private sealed class WindowTab : TabItem
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
            header.Children.Add(aiIcon);
            header.Children.Add(label);
            Header = header;
            Content = content;
            if (content is TerminalTab terminal)
            {
                var menu = new ContextMenu();
                header.ContextMenu = menu;
                menu.Opened += (_, _) =>
                {
                    menu.Items.Clear();
                    if (terminal.CanTransfer) menu.Items.Add(MenuAction("FileTransfer", terminal.OpenFileTransfer));
                    if (Host is not { } host || host.GroupOf(this) is not { } group) return;
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
        public MainWindow? Host { get; set; }
        public bool IsClosing { get; set; }
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
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "WindowForeground");
            Content = form;
            form.Completed += result => DialogResult = result;
            Closing += (_, args) => { if (form.IsBusy) args.Cancel = true; };
        }

        public Button AddAction(string key, Action action) => form.AddAction(key, action);
        public void AddField(string key, Control control, bool inlineCheckBox = false) =>
            form.AddField(key, control, inlineCheckBox: inlineCheckBox);
        public void AddPrompt(string caption, Control control) => form.AddPrompt(caption, control);
        public void AddNotice(string key) => form.AddNotice(key);
        internal void AddStatusBar() => form.AddStatusBar();
        internal IDisposable BeginProgress(string key) => form.BeginProgress(key);
        public void AddPinnedNotice(string message)
        {
            var notice = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 10, 14, 4) };
            DockPanel.SetDock(notice, Dock.Top);
            form.Children.Insert(0, notice);
        }
        public void AddButtons(Action save, string acceptKey = "Save", bool cancelDefault = false, string cancelKey = "Cancel") =>
            form.AddButtons(save, acceptKey, cancelDefault, cancelKey);
        public void AddAsyncButtons(Func<Task<bool>> save, string acceptKey = "Save", bool cancelDefault = false,
            bool acceptOnEnter = true) => form.AddAsyncButtons(save, acceptKey, cancelDefault, acceptOnEnter: acceptOnEnter);
    }
}
