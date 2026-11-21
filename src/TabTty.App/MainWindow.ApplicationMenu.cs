using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TabTty.App;

internal sealed partial class MainWindow
{
    private IInputElement? focusBeforeMenu;
    private MenuItem? hoveredMenuItem;
    private readonly DispatcherTimer menuHover = new(DispatcherPriority.Input);

    private bool IsApplicationMenuOpen => mainMenu.Visibility == Visibility.Visible;

    private void InitializeApplicationMenu()
    {
        menuButton.Click += (_, _) =>
        {
            if (IsApplicationMenuOpen) CloseApplicationMenu();
            else OpenApplicationMenu();
        };
        menuButton.ContextMenuOpening += (_, args) =>
        {
            OpenApplicationMenu();
            args.Handled = true;
        };
        PreviewGotKeyboardFocus += (_, args) =>
        {
            bool clickingMenuButton = ReferenceEquals(args.NewFocus, menuButton) && menuButton.IsMouseOver;
            if (IsApplicationMenuOpen && !clickingMenuButton && IsApplicationMenuElement(args.OldFocus as DependencyObject) &&
                !IsApplicationMenuElement(args.NewFocus as DependencyObject))
                CloseApplicationMenu(restoreFocus: false);
        };
        AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((_, args) =>
        {
            if (IsApplicationMenuOpen && IsApplicationMenuElement(args.OriginalSource as DependencyObject))
                CloseApplicationMenu();
        }), handledEventsToo: true);
        AddHandler(MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((_, args) =>
        {
            if (IsApplicationMenuOpen && args.OriginalSource is MenuItem parent && IsApplicationMenuElement(parent))
                ApplyApplicationMenuStyle(parent.Items);
        }));
        Deactivated += (_, _) => CloseApplicationMenu(restoreFocus: false);
        PreviewMouseDown += (_, args) =>
        {
            if (!IsApplicationMenuOpen) return;
            var hit = InputHitTest(args.GetPosition(this)) as DependencyObject;
            if (IsApplicationMenuElement(hit))
            {
                // WPF exits menu mode when an open top-level header is pressed.
                if (args.ChangedButton == MouseButton.Left &&
                    Ancestor<MenuItem>(hit) is { Role: MenuItemRole.TopLevelHeader, IsSubmenuOpen: true })
                    args.Handled = true;
                return;
            }
            if (Ancestor<Button>(hit) != menuButton) CloseApplicationMenu();
            else if (Mouse.Captured == mainMenu && args.ChangedButton == MouseButton.Left)
            {
                CloseApplicationMenu();
                args.Handled = true;
            }
        };
        PreviewKeyDown += HandleApplicationMenuKey;
        menuHover.Tick += (_, _) =>
        {
            menuHover.Stop();
            var item = hoveredMenuItem;
            hoveredMenuItem = null;
            if (IsApplicationMenuOpen && item is { IsEnabled: true } &&
                Ancestor<MenuItem>(Mouse.DirectlyOver as DependencyObject) == item)
            {
                if (IsApplicationMenuElement(Keyboard.FocusedElement as DependencyObject)) item.Focus();
                SelectMenuBranch(item);
            }
        };
    }

    private void OnApplicationMenuItemEntered(object sender, MouseEventArgs args)
    {
        if (Ancestor<MenuItem>(Mouse.DirectlyOver as DependencyObject) != sender) return;
        hoveredMenuItem = (MenuItem)sender;
        menuHover.Stop();
        menuHover.Interval = TimeSpan.FromMilliseconds(SystemParameters.MenuShowDelay);
        menuHover.Start();
    }

    private bool IsApplicationMenuElement(DependencyObject? element) =>
        element is Visual visual && (visual == mainMenu || mainMenu.IsAncestorOf(visual));

    private void OpenApplicationMenu(MenuItem? category = null)
    {
        if (!IsApplicationMenuOpen)
        {
            focusBeforeMenu = Keyboard.FocusedElement;
            mainMenu.Visibility = Visibility.Visible;
        }
        var target = category ?? mainMenu.Items.OfType<MenuItem>().First();
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsApplicationMenuOpen || !IsActive || !target.Focus()) return;
            if (category is not null) OpenMenuBranch(target);
        }, DispatcherPriority.Loaded);
    }

    private void CloseApplicationMenu(bool restoreFocus = true)
    {
        if (!IsApplicationMenuOpen) return;
        var previous = focusBeforeMenu;
        bool returnFocus = restoreFocus && IsApplicationMenuElement(Keyboard.FocusedElement as DependencyObject);
        focusBeforeMenu = null;
        menuHover.Stop();
        hoveredMenuItem = null;
        mainMenu.Visibility = Visibility.Collapsed;
        foreach (MenuItem item in mainMenu.Items.OfType<MenuItem>()) CloseMenuBranch(item);
        if (returnFocus && previous is not null) Keyboard.Focus(previous);
    }

    private void HandleApplicationMenuKey(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt &&
            mainMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag is Key access && access == key) is { } category)
        {
            OpenApplicationMenu(category);
            args.Handled = true;
            return;
        }
        if (!IsApplicationMenuOpen || !IsApplicationMenuElement(args.OriginalSource as DependencyObject) ||
            Ancestor<MenuItem>(args.OriginalSource as DependencyObject) is not { } item) return;

        menuHover.Stop();
        switch (key)
        {
            case Key.Left when item.HasItems:
            case Key.Enter when item.HasItems:
                OpenMenuBranch(item);
                break;
            case Key.Left:
                break;
            case Key.Right:
            case Key.Escape:
                if (item.IsSubmenuOpen) CloseMenuBranch(item);
                else if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem parent)
                {
                    parent.Focus();
                    CloseMenuBranch(parent);
                }
                else if (key == Key.Escape) CloseApplicationMenu();
                break;
            case Key.Up:
            case Key.Down:
                if (ItemsControl.ItemsControlFromItemContainer(item) is { } owner)
                {
                    var available = MenuItems(owner).ToArray();
                    int index = Array.IndexOf(available, item);
                    if (index >= 0)
                    {
                        int step = key == Key.Up ? -1 : 1;
                        var next = available[(index + step + available.Length) % available.Length];
                        next.Focus();
                        next.BringIntoView();
                        if (next != item) CloseMenuBranch(item);
                    }
                }
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    private void OpenMenuBranch(MenuItem item)
    {
        SelectMenuBranch(item);
        Dispatcher.BeginInvoke(() =>
        {
            if (IsApplicationMenuOpen && item.IsSubmenuOpen && MenuItems(item).FirstOrDefault() is { } first)
                first.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static IEnumerable<MenuItem> MenuItems(ItemsControl owner) =>
        owner.Items.OfType<MenuItem>().Where(item => item.IsEnabled && item.Visibility == Visibility.Visible);

    private static void SelectMenuBranch(MenuItem item)
    {
        if (ItemsControl.ItemsControlFromItemContainer(item) is { } owner)
            foreach (var sibling in owner.Items.OfType<MenuItem>())
                if (sibling != item) CloseMenuBranch(sibling);
        if (item.HasItems) item.IsSubmenuOpen = true;
    }

    private static void CloseMenuBranch(MenuItem item)
    {
        if (!item.IsSubmenuOpen) return;
        foreach (var child in item.Items.OfType<MenuItem>()) CloseMenuBranch(child);
        item.IsSubmenuOpen = false;
    }

    private void ApplyApplicationMenuStyle(ItemCollection items)
    {
        var style = (Style)mainMenu.Resources["ApplicationMenuItem"];
        foreach (MenuItem item in items.OfType<MenuItem>())
        {
            item.Style = style;
            ApplyApplicationMenuStyle(item.Items);
        }
    }
}
