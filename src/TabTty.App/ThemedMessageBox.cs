using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static TabTty.App.App;

namespace TabTty.App;

internal static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, message, title, buttons, icon, defaultResult);

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        MessageBoxResult result = DismissalResult(buttons);
        Window dialog = Create(owner, message, title, buttons, icon, defaultResult, selected => result = selected);
        dialog.ShowDialog();
        return result;
    }

    internal static Window Create(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon, MessageBoxResult defaultResult, Action<MessageBoxResult> select,
        Func<MessageBoxResult, string>? label = null)
    {
        owner = owner is { IsVisible: true } ? owner : null;
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            MaxWidth = Math.Max(300, SystemParameters.WorkArea.Width - 32),
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 32),
        };
#pragma warning disable WPF0001 // WPF Fluent theme integration.
        ThemeMode mode = Application.Current?.ThemeMode ?? ThemeMode.System;
        dialog.ThemeMode = mode == ThemeMode.None ? ThemeMode.System : mode;
#pragma warning restore WPF0001
        dialog.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        dialog.SetResourceReference(Control.ForegroundProperty, "WindowForeground");

        var layout = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        var body = new DockPanel();
        string? glyph = icon switch
        {
            MessageBoxImage.Error => "\uEA39",
            MessageBoxImage.Warning => "\uE7BA",
            MessageBoxImage.Question => "\uE897",
            MessageBoxImage.Information => "\uE946",
            _ => null,
        };
        if (glyph is not null)
        {
            var symbol = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 21,
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(symbol, Dock.Left);
            body.Children.Add(symbol);
        }
        body.Children.Add(new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(100, SystemParameters.WorkArea.Height - 180),
        });
        layout.Children.Add(body);
        dialog.Content = layout;

        MessageBoxResult[] choices = buttons switch
        {
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };
        MessageBoxResult initial = choices.Contains(defaultResult) ? defaultResult : choices[0];
        foreach (MessageBoxResult choice in choices)
        {
            var button = new Button
            {
                Content = label?.Invoke(choice) ?? Text(choice.ToString()), MinWidth = 84, Margin = new Thickness(8, 0, 0, 0),
                IsDefault = choice == initial, IsCancel = choice == DismissalResult(buttons),
            };
            button.Click += (_, _) =>
            {
                select(choice);
                dialog.DialogResult = true;
            };
            if (button.IsDefault) dialog.Loaded += (_, _) => button.Focus();
            actions.Children.Add(button);
        }
        return dialog;
    }

    private static MessageBoxResult DismissalResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK,
    };
}
