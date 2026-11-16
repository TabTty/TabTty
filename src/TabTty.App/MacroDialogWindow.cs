using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class MacroDialogWindow : Window
{
    private readonly TaskCompletionSource<MacroDialogResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<MacroDialogButton, Button> buttons = [];
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox? input;
    private bool invalid;

    public MacroDialogWindow(Window owner, string macro, MacroDialogRequest request)
    {
        Owner = owner;
        Title = request.Title.Length == 0 ? macro : $"{request.Title} ({macro})";
        Width = 480;
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap });
        if (request.InputEnabled)
        {
            input = new TextBox { Text = request.InitialText, MaxLength = 4096, Margin = new Thickness(0, 8, 0, 0) };
            input.TextChanged += (_, _) => { invalid = false; UpdateLabels(); };
            DataObject.AddPastingHandler(input, (_, e) =>
            {
                string? content = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                if (content is null || content.Any(c => c < 32) ||
                    content.Length + input.Text.Length - input.SelectionLength > input.MaxLength || !IsUnicode(content))
                {
                    e.CancelCommand();
                    invalid = true;
                    UpdateLabels();
                }
            });
            panel.Children.Add(input);
            panel.Children.Add(validation);
            Loaded += (_, _) => input.Focus();
        }
        var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(bar);
        MacroDialogButton[] choices = request.Buttons switch
        {
            MacroDialogButtons.OK => [MacroDialogButton.OK],
            MacroDialogButtons.OKCancel => [MacroDialogButton.OK, MacroDialogButton.Cancel],
            MacroDialogButtons.YesNo => [MacroDialogButton.Yes, MacroDialogButton.No],
            _ => [MacroDialogButton.Yes, MacroDialogButton.No, MacroDialogButton.Cancel],
        };
        foreach (MacroDialogButton choice in choices)
        {
            var button = new Button { IsDefault = choice == choices[^1], MinWidth = 75, Margin = new Thickness(8, 8, 0, 0) };
            button.Click += (_, _) => Answer(choice);
            buttons.Add(choice, button);
            bar.Children.Add(button);
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) => result.TrySetResult(new(MacroDialogButton.Cancel, false, ""));
        UpdateLabels();
    }

    internal void UpdateLabels()
    {
        foreach (var (choice, button) in buttons) button.Content = Text(choice.ToString());
        if (input is not null) AutomationProperties.SetName(input, Text("MacroInput"));
        validation.Text = invalid ? Text("MacroInvalidInput") : "";
    }

    private void Answer(MacroDialogButton choice)
    {
        bool hasText = choice != MacroDialogButton.Cancel && input is not null;
        string text = hasText ? input!.Text : "";
        if (text.Any(c => c < 32) || !IsUnicode(text))
        {
            invalid = true;
            UpdateLabels();
            return;
        }
        result.TrySetResult(new(choice, hasText, text));
        Close();
    }

    public async Task<MacroDialogResult> ShowAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Show();
        using var cancellation = token.Register(() => Dispatcher.BeginInvoke(() =>
        {
            if (result.TrySetCanceled(token)) Close();
        }));
        return await result.Task;
    }

    private static bool IsUnicode(string text)
    {
        try { MacroLimits.Unicode(text); return true; }
        catch (MacroError) { return false; }
    }
}
