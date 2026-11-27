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
    private readonly DialogField targetField;
    private readonly DialogField messageField;
    private readonly TextBlock? inputLabel;
    private bool invalid;

    public MacroDialogWindow(Window owner, string macro, MacroDialogRequest request, string targetTab)
    {
        Owner = owner;
        Icon = WindowIcons.App;
        Title = request.Title.Length == 0 ? macro : $"{request.Title} ({macro})";
        Width = 480;
        DialogLayout.SetWindowBounds(this);
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");
        var panel = new StackPanel();
        var layout = new DialogLayout(panel, new Thickness(16));
        Content = layout;
        targetField = new DialogField(Text("DecisionTargetTabLabel"), new DialogText(PermissionDisplay.Literal(targetTab)));
        messageField = new DialogField(Text("MacroDialogMessage"), new DialogText(request.Message, 240));
        panel.Children.Add(targetField);
        panel.Children.Add(messageField);
        if (request.InputEnabled)
        {
            inputLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
            input = new TextBox { Text = request.InitialText, MaxLength = 4096 };
            AutomationProperties.SetLabeledBy(input, inputLabel);
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
            panel.Children.Add(inputLabel);
            panel.Children.Add(input);
            panel.Children.Add(validation);
            Loaded += (_, _) => { if (ShowActivated) input.Focus(); };
        }
        MacroDialogButton[] choices = request.Buttons switch
        {
            MacroDialogButtons.OK => [MacroDialogButton.OK],
            MacroDialogButtons.OKCancel => [MacroDialogButton.OK, MacroDialogButton.Cancel],
            MacroDialogButtons.YesNo => [MacroDialogButton.Yes, MacroDialogButton.No],
            _ => [MacroDialogButton.Yes, MacroDialogButton.No, MacroDialogButton.Cancel],
        };
        foreach (MacroDialogButton choice in choices)
        {
            var button = DialogLayout.StyleAction(new Button { IsDefault = choice == choices[^1], MinWidth = 75 });
            button.Click += (_, _) => Answer(choice);
            buttons.Add(choice, button);
            layout.Actions.Children.Add(button);
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
        targetField.SetCaption(Text("DecisionTargetTabLabel"));
        messageField.SetCaption(Text("MacroDialogMessage"));
        if (inputLabel is not null) inputLabel.Text = Text("MacroInput");
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

    internal Task<MacroDialogResult> Answered => result.Task;

    private static bool IsUnicode(string text)
    {
        try { MacroLimits.Unicode(text); return true; }
        catch (MacroError) { return false; }
    }
}
