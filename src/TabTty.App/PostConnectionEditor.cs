using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class PostConnectionEditor : Grid
{
    internal TextBox Source { get; } = new()
    {
        AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly ListBox completions = new()
    {
        Focusable = false, IsTabStop = false, Visibility = Visibility.Collapsed,
        DisplayMemberPath = nameof(PostConnectionCompletion.Label),
    };
    private readonly TextBlock signature = Hint("PostConnectionSignature");
    private readonly TextBlock diagnostic = Hint("PostConnectionDiagnostic");
    private readonly PostConnectionUnderline underline;
    private readonly DispatcherTimer timer;
    private PostConnectionEditorAnalysis? analysis;
    private int revision;
    private bool analyzing;
    private bool automaticCompletion;
    private bool explicitCompletion;

    internal PostConnectionEditor(FormView form, string source)
    {
        Source.Text = source;
        AutomationProperties.SetAutomationId(Source, "PostConnectionSource");
        AutomationProperties.SetAutomationId(completions, "PostConnectionCompletions");
        completions.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        underline = new(Source);
        var overlay = new Canvas { ClipToBounds = true };
        overlay.Children.Add(completions);
        var field = new Grid { ClipToBounds = true };
        field.Children.Add(Source);
        field.Children.Add(underline);
        field.Children.Add(overlay);
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(signature, 1);
        SetRow(diagnostic, 2);
        Children.Add(field);
        Children.Add(signature);
        Children.Add(diagnostic);

        form.AddLocalizedUpdate(() =>
        {
            AutomationProperties.SetName(Source, Text("PostConnectionMacro"));
            AutomationProperties.SetHelpText(Source, Text("PostConnectionCompletionKeys"));
            AutomationProperties.SetName(completions, Text("PostConnectionCompletions"));
            Source.ToolTip = Text("PostConnectionCompletionKeys");
            ShowDiagnostic();
            if (signature.IsVisible) ShowSignature(analysis?.Signature);
        });

        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += Analyze;
        Loaded += (_, _) => Schedule();
        Unloaded += (_, _) => Schedule();
        IsVisibleChanged += (_, _) => Schedule();
        Source.TextChanged += (_, _) =>
        {
            automaticCompletion = true;
            explicitCompletion = false;
            SetDiagnostic("");
            underline.Clear();
            Schedule();
        };
        Source.SelectionChanged += (_, _) => Schedule();
        Source.LostKeyboardFocus += (_, _) =>
        {
            Dismiss();
            signature.Visibility = Visibility.Collapsed;
        };
        Source.GotKeyboardFocus += (_, _) => Schedule();
        Source.PreviewKeyDown += OnKeyDown;
        Source.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            underline.InvalidateVisual();
            PlaceCompletions();
        }));
        Source.SizeChanged += (_, _) => PlaceCompletions();
        completions.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(completions, element) is ListBoxItem { Content: PostConnectionCompletion choice })
            {
                Insert(choice);
                args.Handled = true;
            }
        };
    }

    private static TextBlock Hint(string id)
    {
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(hint, id);
        AutomationProperties.SetLiveSetting(hint, AutomationLiveSetting.Polite);
        return hint;
    }

    private void Schedule()
    {
        revision++;
        timer.Stop();
        analysis = null;
        completions.Visibility = signature.Visibility = Visibility.Collapsed;
        if (IsLoaded && IsVisible) timer.Start();
    }

    private async void Analyze(object? sender, EventArgs args)
    {
        timer.Stop();
        if (analyzing) return;
        int snapshot = revision;
        int caret = Source.CaretIndex;
        string source = Source.Text;
        analyzing = true;
        PostConnectionEditorAnalysis result;
        try { result = await Task.Run(() => PostConnectionEditorAnalysis.Analyze(source, caret)); }
        finally { analyzing = false; }
        if (snapshot != revision)
        {
            if (IsLoaded && IsVisible) timer.Start();
            return;
        }
        analysis = result;
        ShowDiagnostic();
        if (analysis.Diagnostic is not null) underline.Mark(analysis.DiagnosticStart, analysis.DiagnosticLength);
        else underline.Clear();
        if (!Source.IsKeyboardFocused || Source.SelectionLength != 0) return;
        ShowSignature(analysis.Signature);
        bool followsIdentifier = caret > 0 && (char.IsAsciiLetterOrDigit(source[caret - 1]) || source[caret - 1] is '_' or '.');
        if ((explicitCompletion || automaticCompletion && followsIdentifier) && analysis.Completions.Count > 0)
        {
            completions.ItemsSource = analysis.Completions;
            completions.SelectedIndex = 0;
            completions.ScrollIntoView(completions.SelectedItem);
            completions.Visibility = Visibility.Visible;
            PlaceCompletions();
        }
    }

    private void ShowDiagnostic() => SetDiagnostic(analysis?.Diagnostic is { } error
        ? string.Format(Text("MacroLocation"), error.Position.Line, error.Position.Column, TerminalTab.MacroErrorLabel(error.Code))
        : "");

    private void SetDiagnostic(string message)
    {
        if (diagnostic.Text == message) return;
        diagnostic.Text = message;
        diagnostic.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UIElementAutomationPeer.FromElement(diagnostic)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ShowSignature(PostConnectionSignature? call)
    {
        if (call is null) return;
        signature.Inlines.Clear();
        signature.Inlines.Add(new Run(call.Name + "("));
        for (int i = 0; i < call.Parameters.Length; i++)
        {
            if (i > 0) signature.Inlines.Add(new Run(", "));
            signature.Inlines.Add(new Run(call.Parameters[i])
            {
                FontWeight = i == call.ActiveParameter ? FontWeights.Bold : FontWeights.Normal,
            });
        }
        signature.Inlines.Add(new Run(")"));
        signature.Visibility = Visibility.Visible;
        AutomationProperties.SetName(signature, call.ActiveParameter >= 0 && call.ActiveParameter < call.Parameters.Length
            ? string.Format(Text("PostConnectionCurrentArgument"), signature.Text, call.Parameters[call.ActiveParameter])
            : signature.Text);
        UIElementAutomationPeer.FromElement(signature)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void PlaceCompletions()
    {
        if (completions.Visibility != Visibility.Visible || Source.ActualHeight <= 0) return;
        Rect caret = Source.GetRectFromCharacterIndex(Source.CaretIndex);
        if (caret.IsEmpty || caret.Bottom <= 0 || caret.Top >= Source.ActualHeight)
        {
            completions.Visibility = Visibility.Collapsed;
            return;
        }
        double below = Math.Max(0, Source.ActualHeight - caret.Bottom);
        double above = Math.Max(0, caret.Top);
        bool placeAbove = below < 100 && above > below;
        completions.MaxWidth = Source.ActualWidth;
        completions.MaxHeight = Math.Min(144, placeAbove ? above : below);
        completions.Measure(new Size(completions.MaxWidth, completions.MaxHeight));
        Canvas.SetLeft(completions, Math.Clamp(caret.Left, 0, Math.Max(0, Source.ActualWidth - completions.DesiredSize.Width)));
        Canvas.SetTop(completions, placeAbove ? caret.Top - completions.DesiredSize.Height : caret.Bottom);
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            explicitCompletion = true;
            Schedule();
            args.Handled = true;
            return;
        }
        if (completions.Visibility != Visibility.Visible || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (args.Key)
        {
            case Key.Up:
            case Key.Down:
                completions.SelectedIndex = Math.Clamp(completions.SelectedIndex + (args.Key == Key.Up ? -1 : 1), 0, completions.Items.Count - 1);
                completions.ScrollIntoView(completions.SelectedItem);
                break;
            case Key.Tab:
            case Key.Enter:
                if (completions.SelectedItem is PostConnectionCompletion choice) Insert(choice);
                break;
            case Key.Escape:
                Dismiss();
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    private void Insert(PostConnectionCompletion choice)
    {
        if (analysis is not { } result) return;
        Source.Select(result.ReplacementStart, result.ReplacementLength);
        Source.SelectedText = choice.InsertText;
        Source.Select(result.ReplacementStart + choice.InsertText.Length, 0);
        Dismiss();
    }

    private void Dismiss()
    {
        explicitCompletion = automaticCompletion = false;
        completions.Visibility = Visibility.Collapsed;
    }
}
