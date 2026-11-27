using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static TabTty.App.App;

namespace TabTty.App;

internal static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None,
        string? acceptKey = null) =>
        Show(null, message, title, buttons, icon, defaultResult, acceptKey: acceptKey);

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, ImageSource? windowIcon = null,
        string? acceptKey = null, FrameworkElement? context = null)
    {
        MessageBoxResult result = DismissalResult(buttons);
        Window dialog = Create(owner, message, title, buttons, icon, defaultResult, selected => result = selected,
            label: acceptKey is null ? null : DecisionLabels(acceptKey), windowIcon: windowIcon, context: context);
        dialog.ShowDialog();
        return result;
    }

    public static MessageBoxResult ShowVault(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        string? acceptKey = null, FrameworkElement? context = null) =>
        Show(owner, message, title, buttons, icon, defaultResult, WindowIcons.Vault, acceptKey, context);

    internal static Func<MessageBoxResult, string> DecisionLabels(string acceptKey, string dismissKey = "Cancel") =>
        choice => Text(choice is MessageBoxResult.Yes or MessageBoxResult.OK ? acceptKey : dismissKey);

    internal static Window Create(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon, MessageBoxResult defaultResult, Action<MessageBoxResult> select,
        Func<MessageBoxResult, string>? label = null, ImageSource? windowIcon = null,
        string? details = null, FrameworkElement? context = null)
    {
        owner = owner is { IsVisible: true } ? owner : null;
        var dialog = new Window
        {
            Owner = owner, Title = title, Icon = windowIcon ?? WindowIcons.App,
            Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        DialogLayout.SetWindowBounds(dialog);
#pragma warning disable WPF0001 // WPF Fluent theme integration.
        ThemeMode mode = Application.Current?.ThemeMode ?? ThemeMode.System;
        dialog.ThemeMode = mode == ThemeMode.None ? ThemeMode.System : mode;
#pragma warning restore WPF0001
        dialog.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        dialog.SetResourceReference(Control.ForegroundProperty, "WindowForeground");

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
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (context is not null)
        {
            content.Children.Add(new Border { Child = context, Margin = new Thickness(0, 8, 0, 0) });
        }
        if (!string.IsNullOrEmpty(details))
            content.Children.Add(new Expander
            {
                Header = Text("TransferAllUnresolved"),
                Content = new DialogField(Text("TransferResults"), new DialogText(details, 250)),
            });
        body.Children.Add(content);
        var layout = new DialogLayout(body, new Thickness(20));
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
            var button = DialogLayout.StyleAction(new Button
            {
                Content = label?.Invoke(choice) ?? Text(choice.ToString()), MinWidth = 84,
                IsDefault = choice == initial, IsCancel = choice == DismissalResult(buttons),
            });
            button.Click += (_, _) =>
            {
                if (!dialog.IsVisible) return;
                select(choice);
                dialog.DialogResult = true;
            };
            if (button.IsDefault) dialog.Loaded += (_, _) => { if (dialog.ShowActivated) button.Focus(); };
            layout.Actions.Children.Add(button);
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
