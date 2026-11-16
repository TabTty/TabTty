using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class FormView : DockPanel
{
    private readonly StackPanel fields = new() { Margin = new Thickness(14) };
    private readonly ScrollViewer scroller;
    private Panel? section;
    private readonly bool modal;
    private readonly List<Action> labels = [];
    private TextBlock? pageTitle;
    private string? pageTitleKey;
    private readonly List<EditField> edits = [];
    private Func<bool>? savesSection;
    private bool tracksEdits;

    public FormView(bool modal = false)
    {
        this.modal = modal;
        scroller = new ScrollViewer
        {
            Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = false,
        };
        scroller.SetResourceReference(StyleProperty, typeof(ScrollViewer));
        Focusable = false;
        if (!modal)
        {
            fields.MaxWidth = 760;
            fields.MinWidth = 280;
        }
        Children.Add(scroller);
    }

    public MainWindow? TabOwner { get; set; }
    public Window OwnerWindow => (Window?)TabOwner ?? Window.GetWindow(this) ??
        throw new InvalidOperationException("The form is not hosted.");
    public bool IsBusy { get; private set; }
    public bool HasUnsavedChanges => tracksEdits && edits.Any(edit => edit.IsChanged());
    public event Action<bool>? Completed;

    public void SetPageTitle(string key)
    {
        pageTitleKey = key;
        if (pageTitle is null)
        {
            fields.Margin = new Thickness(24, 20, 24, 24);
            fields.MaxWidth = 1000;
            pageTitle = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
            AutomationProperties.SetHeadingLevel(pageTitle, AutomationHeadingLevel.Level1);
            fields.Children.Insert(0, pageTitle);
            AddLocalizedUpdate(() => pageTitle.Text = Text(pageTitleKey!));
        }
        else pageTitle.Text = Text(key);
    }

    public void RequestClose(Func<bool> confirmDiscard)
    {
        if (CanClose(confirmDiscard)) Cancel();
    }

    public bool CanClose(Func<bool> confirmDiscard) => !IsBusy && (!HasUnsavedChanges || confirmDiscard());

    private void Cancel()
    {
        if (!IsBusy) Completed?.Invoke(false);
    }

    public StackPanel AddSection(Action<StackPanel> build, Func<bool>? savesFields = null)
    {
        var panel = new StackPanel();
        (section ?? fields).Children.Add(panel);
        Panel? previous = section;
        Func<bool>? previousSavesSection = savesSection;
        section = panel;
        savesSection = savesFields ?? previousSavesSection;
        try { build(panel); }
        finally { section = previous; savesSection = previousSavesSection; }
        return panel;
    }

    public void AddGroup(string key, Action build)
    {
        var card = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        var outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Opacity = 0.18, IsHitTestVisible = false };
        outline.SetResourceReference(Border.BorderBrushProperty, "WindowForeground");
        card.Children.Add(outline);
        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        card.Children.Add(panel);
        (section ?? fields).Children.Add(card);
        var heading = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        SetText(key, value => heading.Text = value);
        panel.Children.Add(heading);
        BuildIn(panel, build);
    }

    public void AddColumns(Action first, Action second)
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetRow(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        (section ?? fields).Children.Add(columns);
        BuildIn(left, first);
        BuildIn(right, second);
        columns.SizeChanged += (_, e) =>
        {
            bool wide = e.NewSize.Width >= 640;
            if (wide == (Grid.GetColumn(right) == 1)) return;
            columns.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(right, wide ? 1 : 0);
            Grid.SetRow(right, wide ? 0 : 1);
            right.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 10, 0, 0);
        };
    }

    public void AddNavigation(params (string Title, Action Build)[] sections)
    {
        var navigation = new ListBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Margin = new Thickness(8, 20, 8, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        SetText("SettingsCategories", value => AutomationProperties.SetName(navigation, value));
        var pages = new List<StackPanel>();
        foreach (var (key, build) in sections)
        {
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
            SetText(key, value => title.Text = value);
            navigation.Items.Add(new ListBoxItem { Content = title, MinHeight = 36, Padding = new Thickness(10, 6, 10, 6) });
            var page = new StackPanel { Visibility = Visibility.Collapsed, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
            page.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = fields });
            fields.Children.Add(page);
            var heading = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            SetText(key, value => heading.Text = value);
            page.Children.Add(heading);
            BuildIn(page, build);
            pages.Add(page);
        }
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.Source != navigation || navigation.SelectedIndex < 0) return;
            foreach (StackPanel page in pages) page.Visibility = Visibility.Collapsed;
            pages[navigation.SelectedIndex].Visibility = Visibility.Visible;
            scroller.ScrollToTop();
        };
        navigation.SelectedIndex = 0;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var separator = new Border { Width = 1, Opacity = 0.16, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 12), IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "WindowForeground");
        Children.Remove(scroller);
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(navigation);
        layout.Children.Add(separator);
        layout.Children.Add(scroller);
        Children.Add(layout);
        layout.SizeChanged += (_, args) =>
            layout.ColumnDefinitions[0].Width = new GridLength(args.NewSize.Width < 650 ? 146 : 190);
    }

    public void AddActionRow(Action build)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        (section ?? fields).Children.Add(row);
        BuildIn(row, build);
    }

    public void AddDisplay(string key, FrameworkElement element)
    {
        SetText(key, value => AutomationProperties.SetName(element, value));
        (section ?? fields).Children.Add(element);
    }

    private void BuildIn(Panel panel, Action build)
    {
        Panel? previous = section;
        section = panel;
        try { build(); }
        finally { section = previous; }
    }

    public void AddChoicePanel(string key, Panel choices)
    {
        var label = new TextBlock { Margin = new Thickness(0, 8, 0, 4) };
        SetText(key, value => label.Text = value);
        (section ?? fields).Children.Add(label);
        (section ?? fields).Children.Add(choices);
    }

    public void UpdateLabels()
    {
        foreach (Action label in labels) label();
    }

    public void AddLocalizedUpdate(Action update)
    {
        labels.Add(update);
        update();
    }

    public Button AddAction(string key, Action action)
    {
        var button = new Button { Margin = section is WrapPanel ? new Thickness(0, 0, 8, 6) : new Thickness(0, 4, 0, 0) };
        SetText(key, value => button.Content = value);
        button.Click += (_, _) => action();
        (section ?? fields).Children.Add(button);
        return button;
    }

    public Button AddAsyncAction(string key, Func<Task> action) => AddAction(key, async () => await RunAsync(action));

    public void AddField(string key, Control control, bool trackEdit = true)
    {
        if (!modal && section is not null && control is CheckBox { Content: null } check)
        {
            check.Margin = new Thickness(0, 6, 0, 3);
            SetText(key, value => check.Content = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
            section.Children.Add(check);
        }
        else
        {
            var label = AddLabel(control);
            SetText(key, value => label.Text = value);
        }
        if (modal || !trackEdit) return;
        switch (control)
        {
            case TextBox { IsReadOnly: false } text:
                TrackValue(() => text.Text, value => text.Text = value);
                break;
            case ComboBox { IsEditable: true } editable:
                TrackValue(() => editable.Text, value => editable.Text = value);
                break;
            case ComboBox choice:
                TrackValue(() => choice.SelectedIndex, value => choice.SelectedIndex = value);
                break;
            case ToggleButton toggle:
                TrackValue(() => toggle.IsChecked, value => toggle.IsChecked = value);
                break;
        }
    }

    public void TrackValue<T>(Func<T> read, Action<T> restore, Func<T, T, bool>? equals = null)
    {
        T saved = default!;
        Func<bool>? savesField = savesSection;
        bool IsChanged() => !(equals ?? EqualityComparer<T>.Default.Equals)(saved, read());
        edits.Add(new EditField(IsChanged, () =>
        {
            if (IsChanged()) restore(saved);
        }, () => saved = read(), savesField));
    }

    public void AddPrompt(string caption, Control control) => AddLabel(control).Text = caption;

    private TextBlock AddLabel(Control control)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var label = new Label { Content = text, Target = control };
        if (pageTitle is not null && section is not null) label.Margin = new Thickness(0, 5, 0, 0);
        label.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = control });
        AutomationProperties.SetLabeledBy(control, label);
        (section ?? fields).Children.Add(label);
        (section ?? fields).Children.Add(control);
        return text;
    }

    public void AddButtons(Action save, string acceptKey = "Save", bool cancelDefault = false, string cancelKey = "Cancel") =>
        AddAsyncButtons(() => { save(); return Task.FromResult(true); }, acceptKey, cancelDefault, cancelKey);

    public void AddAsyncButtons(Func<Task<bool>> save, string acceptKey = "Save", bool cancelDefault = false,
        string cancelKey = "Cancel") => AddButtonRow(save, acceptKey, cancelDefault, cancelKey, () => Cancel());

    public void AddEditorButtons(Action save) => AddEditorButtons(() =>
    {
        save();
        return Task.FromResult(true);
    });

    public void AddEditorButtons(Func<Task<bool>> save)
    {
        foreach (EditField edit in edits) edit.Capture();
        tracksEdits = true;
        AddButtonRow(save, "Save", false, "DiscardChanges", () =>
        {
            if (IsBusy) return;
            foreach (EditField edit in edits) edit.Restore();
        });
    }

    private void AddButtonRow(Func<Task<bool>> save, string acceptKey, bool cancelDefault, string cancelKey,
        Action cancelAction)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var apply = new Button { IsDefault = !cancelDefault, Margin = new Thickness(4) };
        var cancel = new Button { IsCancel = modal, IsDefault = cancelDefault, Margin = new Thickness(4) };
        SetText(acceptKey, value => apply.Content = value);
        SetText(cancelKey, value => cancel.Content = value);
        apply.Click += async (_, _) =>
        {
            bool committed = false;
            await RunAsync(async () => committed = await save());
            if (!committed) return;
            if (tracksEdits)
            {
                foreach (EditField edit in edits)
                    if (edit.IsSaved?.Invoke() != false) edit.Capture();
            }
            else Completed?.Invoke(true);
        };
        if (!modal) cancel.Click += (_, _) => cancelAction();
        if (cancelDefault) Loaded += (_, _) => cancel.Focus();
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        if (!modal && pageTitle is not null && tracksEdits)
        {
            var footer = new Grid { MaxWidth = 1000, Margin = new Thickness(24, 0, 24, 8) };
            var divider = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Opacity = 0.18,
                IsHitTestVisible = false };
            divider.SetResourceReference(Border.BorderBrushProperty, "WindowForeground");
            buttons.Margin = new Thickness(0, 5, 0, 0);
            footer.Children.Add(divider);
            footer.Children.Add(buttons);
            SetDock(footer, Dock.Bottom);
            Children.Insert(0, footer);
        }
        else fields.Children.Add(buttons);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsEnabled = false;
        try { await action(); }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception) ||
            exception is OperationCanceledException or CryptographicException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (((Window?)TabOwner ?? Window.GetWindow(this)) is { } owner) MainWindow.ShowError(owner, exception);
        }
        finally
        {
            IsBusy = false;
            IsEnabled = true;
        }
    }

    private void SetText(string key, Action<string> apply) => AddLocalizedUpdate(() => apply(Text(key)));

    private sealed record EditField(Func<bool> IsChanged, Action Restore, Action Capture, Func<bool>? IsSaved);
}
