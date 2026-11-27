using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TabTty.App;

/// <summary>A scrolling dialog body with a separate, wrapping action area.</summary>
internal sealed class DialogLayout : Grid
{
    internal WrapPanel Actions { get; }

    internal DialogLayout(FrameworkElement body, Thickness padding)
    {
        Margin = padding;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(new ScrollViewer
        {
            Content = body, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Actions = CreateActions(new Thickness(0, 8, 0, 0));
        SetRow(Actions, 1);
        Children.Add(Actions);
    }

    internal static WrapPanel CreateActions(Thickness margin) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, Margin = margin,
    };

    internal static Button Action(string label) => StyleAction(new Button { Content = label });

    internal static Button StyleAction(Button button)
    {
        button.SetResourceReference(ContentControl.ContentTemplateProperty, "DialogActionLabel");
        button.Margin = new Thickness(4);
        return button;
    }

    internal static void SetWindowBounds(Window window)
    {
        Rect available = SystemParameters.WorkArea;
        window.MaxWidth = Math.Max(280, available.Width - 32);
        window.MaxHeight = Math.Max(220, available.Height - 32);
        KeyboardNavigation.SetTabNavigation(window, KeyboardNavigationMode.Cycle);
    }
}
