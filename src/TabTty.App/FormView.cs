using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class FormView : Grid
{
    private const double EditorActionInset = 24;
    private const double EditorDividerOpacity = 0.18;
    private readonly StackPanel fields = new() { Margin = new Thickness(14) };
    private readonly ScrollViewer scroller;
    private Panel? section;
    private bool hasNavigation;
    private double AdditionalFieldSpacing => hasNavigation ? 8 : 0;
    private readonly bool modal;
    private readonly List<Action> labels = [];
    private TextBlock? pageTitle;
    private string? pageTitleKey;
    private string? pageTitleArgument;
    private readonly List<EditField> edits = [];
    private Func<bool>? savesSection;
    private bool tracksEdits;
    private Action? updateEditorButtons;
    private Grid? editorFooter;
    private WrapPanel? dialogActions;
    internal DockPanel Body { get; } = new();
    private readonly NoticeStack notices = new();
    internal NoticeStack Notices => notices;
    internal ViewNotifications Reports { get; }
    private TextBlock? statusBarText;
    private string statusMessage = "";
    private string? statusResource;
    private bool fileWorkAttempted;
    private string? progressKey;

    public FormView(bool modal = false)
    {
        this.modal = modal;
        // Keep content, fixed actions and status in that order regardless of when a view adds them.
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(Body);
        var overlay = new Canvas { ClipToBounds = true };
        Canvas.SetRight(notices, 0);
        Canvas.SetTop(notices, 0);
        overlay.Children.Add(notices);
        Children.Add(overlay);
        overlay.SizeChanged += (_, args) =>
        {
            notices.MaxWidth = Math.Min(440, args.NewSize.Width);
            notices.MaxHeight = args.NewSize.Height;
        };
        Reports = new ViewNotifications(notices,
            () => (Window?)TabOwner ?? Window.GetWindow(this),
            () => Tab is null || TabOwner?.IsCurrentView(this) == true,
            () => Tab?.Caption ?? Window.GetWindow(this)?.Title ?? "",
            () => { if (Tab is { Host: { } host } tab) host.SelectNotificationTab(tab); });
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
        Body.Children.Add(scroller);
    }

    internal MainWindow.WindowTab? Tab { get; set; }
    public MainWindow? TabOwner => Tab?.Host;
    public Window OwnerWindow => (Window?)TabOwner ?? Window.GetWindow(this) ??
        throw new InvalidOperationException("The form is not hosted.");
    public bool IsBusy { get; private set; }
    internal Func<bool>? CanDismissBusyView { private get; set; }
    internal bool BlocksClose => IsBusy && CanDismissBusyView?.Invoke() != true;
    internal Func<ReportField[]?>? DescribeUnresolvedPublication { private get; set; }
    internal ReportField[]? UnresolvedPublication => DescribeUnresolvedPublication?.Invoke();
    public bool HasUnsavedChanges => tracksEdits && edits.Any(edit => edit.IsChanged());
    public event Action<bool>? Completed;

    internal void AddStatusBar()
    {
        if (statusBarText is not null) return;
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        var bar = new Border
        {
            Child = text, Height = MainWindow.StatusBarHeight, Padding = new Thickness(14, 0, 14, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        bar.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        text.SetResourceReference(MarginProperty, "WindowStatusInset");
        statusBarText = text;
        SetRow(bar, 2);
        Children.Add(bar);
        AddLocalizedUpdate(UpdateStatusBar);
    }

    internal void SetStatusMessage(string message)
    {
        statusResource = null;
        statusMessage = message;
        UpdateStatusBar();
    }

    internal void SetStatusResource(string key)
    {
        statusResource = key;
        UpdateStatusBar();
    }

    internal IDisposable BeginProgress(string key)
    {
        fileWorkAttempted = true;
        return new DelayedProgress(this, key);
    }

    private void UpdateStatusBar()
    {
        if (statusBarText is not { } text) return;
        string message = (progressKey ?? statusResource) is { } key ? Text(key) : statusMessage;
        if (Equals(text.ToolTip, message.Length == 0 ? null : message)) return;
        text.Text = message.ReplaceLineEndings(" ");
        text.ToolTip = message.Length == 0 ? null : message;
        AutomationProperties.SetName(text, message);
        UIElementAutomationPeer.FromElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private sealed class DelayedProgress : IDisposable
    {
        private readonly FormView form;
        private readonly string key;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private bool disposed;

        public DelayedProgress(FormView form, string key)
        {
            this.form = form;
            this.key = key;
            timer.Tick += Show;
            timer.Start();
        }

        private void Show(object? sender, EventArgs args)
        {
            timer.Stop();
            form.progressKey = key;
            form.UpdateStatusBar();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Tick -= Show;
            form.progressKey = null;
            form.UpdateStatusBar();
        }
    }

    public void SetPageTitle(string key, string? argument = null, bool useTitleBand = false)
    {
        pageTitleKey = key;
        pageTitleArgument = argument;
        if (pageTitle is null)
        {
            fields.Margin = new Thickness(24, useTitleBand ? 0 : 20, 24, 24);
            fields.MaxWidth = 1000;
            pageTitle = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
            AutomationProperties.SetHeadingLevel(pageTitle, AutomationHeadingLevel.Level1);
            if (useTitleBand)
            {
                pageTitle.Margin = new Thickness(24, 20, 24, 18);
                // Cover the gap under the tab without shifting the title or body.
                double tabOverlap = Tab is null ? 0 : 1;
                var header = new Border
                {
                    Child = pageTitle,
                    Margin = new Thickness(0, -tabOverlap, 0, 0),
                    Padding = new Thickness(0, tabOverlap, 0, 0),
                };
                header.SetResourceReference(Border.BackgroundProperty, "TabViewItemHeaderBackgroundSelected");
                DockPanel.SetDock(header, Dock.Top);
                Body.Children.Insert(0, header);
            }
            else fields.Children.Insert(0, pageTitle);
            AddLocalizedUpdate(() => pageTitle.Text = FormatPageTitle());
        }
        else pageTitle.Text = FormatPageTitle();

        string FormatPageTitle()
        {
            string text = Text(pageTitleKey!);
            return pageTitleArgument is null ? text : string.Format(text, pageTitleArgument);
        }
    }

    public void RequestClose(Func<bool> confirmDiscard)
    {
        if (CanClose(confirmDiscard)) Cancel();
    }

    public bool CanClose(Func<bool> confirmDiscard) =>
        !BlocksClose && (!HasUnsavedChanges && UnresolvedPublication is null || confirmDiscard());

    internal bool ConfirmDiscardChanges()
    {
        Window owner = OwnerWindow;
        bool isTab = TabOwner is not null;
        string key = isTab ? "DiscardChangesAndClose" : "DiscardChangesAndCloseWindow";
        return ((App)Application.Current).Modal(() => ThemedMessageBox.Show(owner, Text(key), Text("DiscardAndClose"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel,
            acceptKey: "DiscardAndClose", context: DiscardContext(UnresolvedPublication))) == MessageBoxResult.OK;
    }

    internal StackPanel DiscardContext(ReportField[]? unresolved)
    {
        var context = new StackPanel();
        context.Children.Add(new DialogField(Text(Tab is null ? "AffectedWindow" : "AffectedTabs"),
            new DialogText(Tab?.Caption ?? OwnerWindow.Title)));
        if (unresolved is not null)
            foreach (var field in unresolved) context.Children.Add(field.Create());
        return context;
    }

    private void Cancel()
    {
        if (!BlocksClose) Completed?.Invoke(false);
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
        var card = new Grid { Margin = new Thickness(0, AdditionalFieldSpacing, 0, 12) };
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

    public ListBox AddNavigation(params (string Title, Action Build)[] sections) =>
        AddNavigation(null, sections);

    internal ListBox AddNavigation(string? fillingPage, params (string Title, Action Build)[] sections)
    {
        hasNavigation = true;
        var navigation = new ListBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 8, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        SetText("SettingsCategories", value => AutomationProperties.SetName(navigation, value));
        var pages = new List<Panel>();
        DockPanel? fill = null;
        foreach (var (key, build) in sections)
        {
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
            SetText(key, value => title.Text = value);
            navigation.Items.Add(new ListBoxItem
            {
                Tag = key, Content = title, MinHeight = 36, Padding = new Thickness(10, 6, 10, 6),
            });
            Panel page;
            if (key == fillingPage)
                page = fill = new DockPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(14, 0, 14, 14) };
            else
            {
                page = new StackPanel { Visibility = Visibility.Collapsed, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
                page.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = fields });
                fields.Children.Add(page);
            }
            var heading = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            SetText(key, value => heading.Text = value);
            DockPanel.SetDock(heading, Dock.Top);
            page.Children.Add(heading);
            BuildIn(page, build);
            pages.Add(page);
        }
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.Source != navigation || navigation.SelectedIndex < 0) return;
            foreach (Panel page in pages) page.Visibility = Visibility.Collapsed;
            Panel selected = pages[navigation.SelectedIndex];
            selected.Visibility = Visibility.Visible;
            scroller.Visibility = selected == fill ? Visibility.Collapsed : Visibility.Visible;
            scroller.ScrollToTop();
        };
        navigation.SelectedIndex = 0;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        if (pageTitle is not null)
        {
            pageTitle.Margin = new Thickness(24, 20, 24, 12);
            if (fields.Children.Contains(pageTitle))
            {
                fields.Children.Remove(pageTitle);
                Grid.SetColumnSpan(pageTitle, 2);
                layout.Children.Add(pageTitle);
            }
            var margin = fields.Margin;
            fields.Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
        }
        var separator = new Border { Width = 1, Opacity = EditorDividerOpacity,
            HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "WindowForeground");
        Body.Children.Remove(scroller);
        Grid.SetRow(navigation, 1);
        Grid.SetRow(separator, 1);
        Grid.SetRow(scroller, 1);
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(navigation);
        layout.Children.Add(separator);
        layout.Children.Add(scroller);
        if (fill is not null)
        {
            Grid.SetRow(fill, 1);
            Grid.SetColumn(fill, 1);
            layout.Children.Add(fill);
        }
        Body.Children.Add(layout);
        layout.SizeChanged += (_, args) =>
            layout.ColumnDefinitions[0].Width = new GridLength(args.NewSize.Width < 650 ? 146 : 190);
        return navigation;
    }

    public void AddActionRow(Action build)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        (section ?? fields).Children.Add(row);
        BuildIn(row, build);
    }

    public void AddDisplay(string key, FrameworkElement element)
    {
        if (modal)
        {
            AddDialogField(key, element);
            return;
        }
        SetText(key, value => AutomationProperties.SetName(element, value));
        (section ?? fields).Children.Add(element);
    }

    private void AddDialogField(string key, FrameworkElement value, FrameworkElement? target = null)
    {
        var field = new DialogField(Text(key), value, target);
        (section ?? fields).Children.Add(field);
        AddLocalizedUpdate(() => field.SetCaption(Text(key)));
    }

    internal void AddPageDescription(string key)
    {
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        SetText(key, value => description.Text = value);
        DockPanel.SetDock(description, Dock.Top);
        (section ?? fields).Children.Add(description);
    }

    internal void AddFillingDisplay(string key, FrameworkElement element, Action? buildFooter = null)
    {
        SetText(key, value => AutomationProperties.SetName(element, value));
        Body.Children.Remove(scroller);
        scroller.Content = null;
        fields.MaxWidth = double.PositiveInfinity;
        Thickness margin = fields.Margin;
        fields.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 8);
        DockPanel.SetDock(fields, Dock.Top);
        Body.Children.Add(fields);
        if (buildFooter is not null)
        {
            Thickness displayMargin = element.Margin;
            var footer = new StackPanel { Margin = new Thickness(displayMargin.Left, 8, displayMargin.Right, 0) };
            var footerScroll = new ScrollViewer
            {
                Content = footer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = false,
            };
            footerScroll.SetResourceReference(StyleProperty, typeof(ScrollViewer));
            SizeChanged += (_, args) => footerScroll.MaxHeight = args.NewSize.Height * 0.45;
            DockPanel.SetDock(footerScroll, Dock.Bottom);
            Body.Children.Add(footerScroll);
            BuildIn(footer, buildFooter);
        }
        Body.Children.Add(element);
    }

    private void BuildIn(Panel panel, Action build)
    {
        Panel? previous = section;
        section = panel;
        try { build(); }
        finally { section = previous; }
    }

    public TextBlock AddChoicePanel(string key, Panel choices, string descriptionKey)
    {
        AddChoiceLabel(key);
        var description = AddFieldDescription(descriptionKey);
        (section ?? fields).Children.Add(choices);
        return description;
    }

    public void AddCheckBoxGroup(string key, Action build)
    {
        AddChoiceLabel(key);
        var choices = new WrapPanel();
        (section ?? fields).Children.Add(choices);
        BuildIn(choices, build);
    }

    private void AddChoiceLabel(string key)
    {
        var label = new TextBlock { FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8 + AdditionalFieldSpacing, 0, 4) };
        SetText(key, value => label.Text = value);
        (section ?? fields).Children.Add(label);
    }

    private TextBlock AddFieldDescription(string key)
    {
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 4) };
        SetText(key, value => description.Text = value);
        (section ?? fields).Children.Add(description);
        return description;
    }

    public void AddNotice(string key, bool heading = false)
    {
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        if (heading) notice.FontWeight = FontWeights.SemiBold;
        SetText(key, value => notice.Text = value);
        (section ?? fields).Children.Add(notice);
    }

    public void UpdateLabels()
    {
        Reports.UpdateLabels();
        notices.UpdateLabels();
        foreach (Action label in labels) label();
        updateEditorButtons?.Invoke();
    }

    public void AddLocalizedUpdate(Action update)
    {
        labels.Add(update);
        update();
    }

    public Button AddAction(string key, Action action)
    {
        var button = CreateAction(key, action);
        (section ?? fields).Children.Add(button);
        return button;
    }

    public void AddActionField(string key, string actionKey, Action action)
    {
        var button = CreateAction(actionKey, action);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        AddField(key, button, trackEdit: false);
    }

    private WrapPanel DialogActions()
    {
        if (dialogActions is not null) return dialogActions;
        dialogActions = DialogLayout.CreateActions(new Thickness(10, 2, 10, 8));
        AddFixedActions(dialogActions);
        return dialogActions;
    }

    private void AddFixedActions(Panel actions)
    {
        actions.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = Body });
        SetRow(actions, 1);
        Children.Add(actions);
    }

    internal Button AddDialogAction(string key, Action action, bool isDefault = false)
    {
        var button = DialogLayout.StyleAction(CreateAction(key, action));
        button.IsDefault = isDefault;
        if (isDefault) Loaded += (_, _) =>
        {
            if (Window.GetWindow(this)?.ShowActivated != false) button.Focus();
        };
        DialogActions().Children.Add(button);
        return button;
    }

    private Button CreateAction(string key, Action action)
    {
        var button = new Button { Margin = section is WrapPanel ? new Thickness(0, 0, 8, 6) : new Thickness(0, 4, 0, 0) };
        SetText(key, value => button.Content = value);
        button.Click += (_, _) =>
        {
            action();
            updateEditorButtons?.Invoke();
        };
        return button;
    }

    public Button AddAsyncAction(string key, Func<Task> action) => AddAction(key, async () => await RunAsync(action));

    public void AddField(string key, Control control, bool trackEdit = true, string? actionKey = null,
        Func<Task>? action = null, string? descriptionKey = null, bool inlineCheckBox = false, Action? reset = null)
    {
        if (modal && control is DialogText)
        {
            FrameworkElement content = action is null ? control : FieldWithAction(control, actionKey!, action, reset);
            AddDialogField(key, content, control);
            return;
        }
        if (control is CheckBox { Content: null } check && descriptionKey is null &&
            (inlineCheckBox || !modal && section is not null && (!hasNavigation || section is WrapPanel)))
        {
            if (check.ReadLocalValue(MarginProperty) == DependencyProperty.UnsetValue)
                check.Margin = hasNavigation ? new Thickness(0, 0, 18, 6) : new Thickness(0, 6, 0, 3);
            SetCheckBoxContent(check, key);
            (section ?? fields).Children.Add(check);
        }
        else
        {
            var label = AddLabel(control, addControl: action is null, descriptionKey: descriptionKey);
            SetText(key, value => label.Text = value);
            if (hasNavigation && control is CheckBox { Content: null } standalone)
                SetCheckBoxContent(standalone, "Enabled");
            if (action is not null)
                (section ?? fields).Children.Add(FieldWithAction(control, actionKey!, action, reset));
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
            case FixedChoiceBox fixedChoice:
                TrackValue(() => fixedChoice.SelectedId, value => fixedChoice.SelectedId = value);
                break;
            case ComboBox choice:
                TrackValue(() => choice.SelectedIndex, value => choice.SelectedIndex = value);
                break;
            case ToggleButton toggle:
                TrackValue(() => toggle.IsChecked, value => toggle.IsChecked = value);
                break;
        }
    }

    private Grid FieldWithAction(Control control, string actionKey, Func<Task> action, Action? reset)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(control);
        BuildIn(row, () =>
        {
            PlaceButton(AddAsyncAction(actionKey, action));
            if (reset is not null) PlaceButton(AddAction("Reset", reset));
        });
        return row;

        void PlaceButton(Button button)
        {
            Grid.SetColumn(button, row.ColumnDefinitions.Count);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            button.Margin = new Thickness(8, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private void SetCheckBoxContent(CheckBox check, string key)
    {
        var content = new TextBlock { TextWrapping = TextWrapping.Wrap };
        SetText(key, value => content.Text = value);
        check.Content = content;
    }

    public Action<T> TrackValue<T>(Func<T> read, Action<T> restore, Func<T, T, bool>? equals = null)
    {
        T saved = default!;
        Func<bool>? savesField = savesSection;
        bool IsChanged() => !(equals ?? EqualityComparer<T>.Default.Equals)(saved, read());
        edits.Add(new EditField(IsChanged, () =>
        {
            if (IsChanged()) restore(saved);
        }, () =>
        {
            T snapshot = read();
            return () => saved = snapshot;
        }, savesField));
        return value =>
        {
            saved = value;
            RefreshEditorButtons();
        };
    }

    internal void RefreshEditorButtons() => updateEditorButtons?.Invoke();

    internal Action CaptureSaveBaseline()
    {
        Action[] capture = edits.Where(edit => edit.IsSaved?.Invoke() != false)
            .Select(edit => edit.Snapshot()).ToArray();
        return () =>
        {
            foreach (Action setBaseline in capture) setBaseline();
            updateEditorButtons?.Invoke();
        };
    }

    public void AddPrompt(string caption, Control control) => AddLabel(control).Text = caption;

    internal Action<string> AddValidation(Control field)
    {
        var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        (section ?? fields).Children.Add(error);
        return message =>
        {
            error.Text = message;
            error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(field, message);
            UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
    }

    private TextBlock AddLabel(Control control, bool addControl = true, string? descriptionKey = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (hasNavigation) text.FontWeight = FontWeights.Bold;
        var label = new Label { Content = text, Target = control };
        Panel parent = section ?? fields;
        double topMargin = (pageTitle is not null && section is not null ? 5 : 0) + AdditionalFieldSpacing;
        if (modal)
        {
            label.Padding = new Thickness(0);
            if (parent.Children.Count > 0 && parent.Children[^1] is not DialogField)
                topMargin = DialogField.Spacing;
        }
        if (topMargin > 0) label.Margin = new Thickness(0, topMargin, 0, 0);
        label.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = control });
        AutomationProperties.SetLabeledBy(control, label);
        parent.Children.Add(label);
        bool descriptionFollowsControl = addControl && control is CheckBox;
        if (descriptionFollowsControl) (section ?? fields).Children.Add(control);
        if (descriptionKey is not null)
        {
            var description = AddFieldDescription(descriptionKey);
            description.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = control });
            control.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(TextBlock.Text)) { Source = description });
        }
        if (addControl && !descriptionFollowsControl) (section ?? fields).Children.Add(control);
        return text;
    }

    public Button AddButtons(Action save, string acceptKey = "Save", bool cancelDefault = false, string cancelKey = "Cancel") =>
        AddAsyncButtons(() => { save(); return Task.FromResult(true); }, acceptKey, cancelDefault, cancelKey);

    public Button AddAsyncButtons(Func<Task<bool>> save, string acceptKey = "Save", bool cancelDefault = false,
        string cancelKey = "Cancel", bool acceptOnEnter = true) =>
        AddButtonRow(save, acceptKey, cancelDefault, cancelKey, () => Cancel(), acceptOnEnter: acceptOnEnter);

    public void AddEditorButtons(Action save) => AddEditorButtons(() =>
    {
        save();
        return Task.FromResult(true);
    });

    public void AddEditorButtons(Func<Task<bool>> save, Func<bool>? isCreating = null, Func<bool>? canSaveInitialValues = null)
    {
        foreach (EditField edit in edits) edit.Snapshot()();
        tracksEdits = true;
        AddButtonRow(save, "Save", false, "DiscardChanges", () =>
        {
            if (IsBusy || !HasUnsavedChanges) return;
            foreach (EditField edit in edits) edit.Restore();
            updateEditorButtons?.Invoke();
        }, isCreating, canSaveInitialValues: canSaveInitialValues);
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnEditorInputChanged), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnEditorInputChanged), true);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnEditorInputChanged), true);
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnEditorInputChanged), true);
        AddHandler(ToggleButton.IndeterminateEvent, new RoutedEventHandler(OnEditorInputChanged), true);
    }

    private void OnEditorInputChanged(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is TextBox { IsReadOnly: false } or ComboBox or ToggleButton)
            updateEditorButtons?.Invoke();
    }

    private Button AddButtonRow(Func<Task<bool>> save, string acceptKey, bool cancelDefault, string cancelKey,
        Action cancelAction, Func<bool>? isCreating = null, bool acceptOnEnter = true, Func<bool>? canSaveInitialValues = null)
    {
        Panel buttons = modal ? DialogActions() : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        bool acceptsReturn = acceptOnEnter && acceptKey != "Save";
        var apply = new Button { IsDefault = acceptsReturn && !cancelDefault, Margin = new Thickness(4) };
        if (!acceptsReturn) KeyboardNavigation.SetAcceptsReturn(apply, false);
        var cancel = new Button { IsCancel = modal, IsDefault = cancelDefault, Margin = new Thickness(4) };
        if (modal)
        {
            DialogLayout.StyleAction(apply);
            DialogLayout.StyleAction(cancel);
        }
        if (!tracksEdits) SetText(acceptKey, value => apply.Content = value);
        SetText(cancelKey, value => cancel.Content = value);
        apply.Click += async (_, _) =>
        {
            if (tracksEdits && !CanSave()) return;
            bool committed = false;
            await RunAsync(async () => committed = await save());
            if (!committed) return;
            if (tracksEdits)
            {
                CaptureSaveBaseline()();
            }
            else Completed?.Invoke(true);
        };
        cancel.Click += (_, _) =>
        {
            if (!modal || Window.GetWindow(this) is MainWindow.FormWindow { IsDecision: true }) cancelAction();
        };
        if (cancelDefault) Loaded += (_, _) =>
        {
            if (Window.GetWindow(this)?.ShowActivated != false) cancel.Focus();
        };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        if (tracksEdits)
        {
            var close = new Button { Margin = hasNavigation ? new Thickness(4, 4, 0, 4) : new Thickness(4) };
            SetText("CloseDialog", value => close.Content = value);
            close.Click += (_, _) =>
            {
                if (TabOwner is not null) RequestClose(ConfirmDiscardChanges);
                else Window.GetWindow(this)?.Close();
            };
            buttons.Children.Add(close);
            updateEditorButtons = () =>
            {
                apply.Content = Text(isCreating?.Invoke() == true ? "Create" : acceptKey);
                apply.IsEnabled = CanSave();
                cancel.IsEnabled = HasUnsavedChanges;
            };
            updateEditorButtons();
        }
        if (!modal && tracksEdits && (pageTitle is not null || hasNavigation))
        {
            var footer = new Grid
            {
                MaxWidth = hasNavigation ? double.PositiveInfinity : 1000,
                Margin = hasNavigation ? new Thickness(0, 0, 0, 8) : new Thickness(24, 0, 24, 8),
            };
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var divider = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Opacity = EditorDividerOpacity,
                IsHitTestVisible = false };
            divider.SetResourceReference(Border.BorderBrushProperty, "WindowForeground");
            buttons.Margin = new Thickness(0, 5, hasNavigation ? EditorActionInset : 0, 0);
            Grid.SetRow(buttons, 2);
            footer.Children.Add(divider);
            footer.Children.Add(buttons);
            AddFixedActions(footer);
            editorFooter = footer;
        }
        else if (!modal) fields.Children.Add(buttons);
        return apply;

        bool CanSave() => HasUnsavedChanges || canSaveInitialValues?.Invoke() == true;
    }

    internal Action AddEditorSaveRecovery(Func<(string Message, string Action)?> recovery, Func<Task> retry)
    {
        double sideInset = hasNavigation ? EditorActionInset : 0;
        var row = new Grid { Margin = new Thickness(sideInset, 8, sideInset, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        var button = new Button { Margin = new Thickness(8, 0, hasNavigation ? 0 : 4, 0),
            VerticalAlignment = VerticalAlignment.Center };
        KeyboardNavigation.SetAcceptsReturn(button, false);
        button.Click += async (_, _) => await RunAsync(retry);
        Grid.SetColumn(button, 1);
        row.Children.Add(message);
        row.Children.Add(button);
        Grid.SetRow(row, 1);
        editorFooter!.Children.Insert(1, row);
        AddLocalizedUpdate(Refresh);
        return Refresh;

        void Refresh()
        {
            var state = recovery();
            row.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
            message.Text = state is { } pending ? Text(pending.Message) : "";
            button.Content = state is { } action ? Text(action.Action) : null;
            UIElementAutomationPeer.FromElement(message)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    internal async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        fileWorkAttempted = false;
        Body.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception) ||
            exception is OperationCanceledException or CryptographicException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (fileWorkAttempted || exception is IOException or UnauthorizedAccessException or CryptographicException or DllNotFoundException or EntryPointNotFoundException)
                ReportFailure(exception);
            else if (((Window?)TabOwner ?? Window.GetWindow(this)) is { } owner)
                MainWindow.ShowError(owner, exception);
        }
        finally
        {
            IsBusy = false;
            Body.IsEnabled = true;
            updateEditorButtons?.Invoke();
        }
    }

    internal void ReportFailure(Exception exception)
    {
        if (Application.Current is App app) app.ReportUnresolvedStore(exception);
        ReportFailure(Reports.Report, exception);
    }

    internal static void ReportFailure(Action<object, string, ReportField[]> report, Exception exception)
    {
        if (exception is FilePublicationException publication)
        {
            var fields = JsonStoreRecovery.FailureFields(publication.Result);
            report(publication.Result.OperationId, ReportField.Describe(fields), fields);
        }
        else report(exception, exception.Message, [new(Text("DialogDetails"), exception.Message, Framed: true)]);
    }

    private void SetText(string key, Action<string> apply) => AddLocalizedUpdate(() => apply(Text(key)));

    private sealed record EditField(Func<bool> IsChanged, Action Restore, Func<Action> Snapshot, Func<bool>? IsSaved);
}
