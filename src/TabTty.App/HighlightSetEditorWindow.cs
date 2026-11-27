using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class HighlightSetEditorWindow : Window
{
    private readonly FormView form = new();
    private readonly TextBox name;
    private readonly CheckBox enabled;
    private readonly Action<bool> setEnabledBaseline;
    private readonly ObservableCollection<TerminalHighlightRule> rules;
    private readonly DialogText affected = new("", 70);
    private readonly HighlightCatalog catalog;
    private HighlightSet? baseline;
    private bool committedEnabled;
    private bool deleting;

    internal HighlightSetEditorWindow(App app, HighlightCatalog catalog, HighlightSet set, bool creating)
    {
        this.catalog = catalog;
        SetId = set.Id;
        baseline = creating ? null : set;
        committedEnabled = set.Enabled;
        Icon = WindowIcons.App;
        Width = 820;
        Height = 650;
        MinWidth = 520;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");
        Content = form;
        form.SetPageTitle("HighlightSetEditor", useTitleBand: true);

        name = new TextBox { Text = set.Name, MaxLength = 512 };
        enabled = new CheckBox { IsChecked = set.Enabled };
        rules = new(set.Rules);
        form.AddField("HighlightName", name);
        form.AddField("Enabled", enabled, trackEdit: false, inlineCheckBox: true);
        setEnabledBaseline = form.TrackValue(() => enabled.IsChecked == true, value => enabled.IsChecked = value);
        form.TrackValue(rules.ToArray, saved =>
        {
            rules.Clear();
            foreach (var rule in saved) rules.Add(rule);
        }, (saved, current) => saved.SequenceEqual(current));
        BuildRuleList();
        form.AddStatusBar();
        form.AddEditorButtons(async () =>
        {
            RefreshAffectedTabs();
            using var progress = form.BeginProgress("FileOperationPending");
            baseline = await catalog.SaveEditorAsync(baseline,
                () => new HighlightSet(SetId, name.Text, enabled.IsChecked == true, rules.ToArray()));
            committedEnabled = baseline.Enabled;
            return true;
        }, canSaveInitialValues: () => baseline is null && !string.IsNullOrWhiteSpace(name.Text));
        rules.CollectionChanged += (_, _) => form.RefreshEditorButtons();

        var affectedRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        affectedRefresh.Tick += (_, _) => RefreshAffectedTabs();
        Activated += (_, _) =>
        {
            RefreshAffectedTabs();
            affectedRefresh.Start();
        };
        Deactivated += (_, _) => affectedRefresh.Stop();
        catalog.Changed += CatalogChanged;
        Closed += (_, _) =>
        {
            affectedRefresh.Stop();
            catalog.Changed -= CatalogChanged;
            form.Reports.Dispose();
            app.EditorClosed(this);
        };
        UpdateLabels();
    }

    internal Guid SetId { get; }
    internal bool HasPendingEdits => form.HasUnsavedChanges || form.IsBusy;

    internal void UpdateLabels()
    {
        Title = Text("HighlightSetEditor");
        form.UpdateLabels();
        RefreshAffectedTabs();
    }

    internal void SetDeleting(bool value)
    {
        deleting = value;
        form.IsEnabled = !value;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) e.Cancel = deleting || !form.CanClose(form.ConfirmDiscardChanges);
    }

    private void CatalogChanged()
    {
        if (baseline is null) return;
        if (catalog.Find(SetId) is not { } current)
        {
            if (!deleting && !HasPendingEdits) Close();
        }
        else if (current.Enabled != committedEnabled)
        {
            committedEnabled = current.Enabled;
            enabled.IsChecked = current.Enabled;
            setEnabledBaseline(current.Enabled);
        }
    }

    private void RefreshAffectedTabs()
    {
        string value = catalog.AffectedTabs(SetId);
        if (affected.Text != value) affected.Text = value;
    }

    private void BuildRuleList()
    {
        var list = new ListView { ItemsSource = rules, MinHeight = 68, Background = Brushes.Transparent };
        var columns = new DetailsColumns(list, sortable: false);
        columns.Add("HighlightName", nameof(TerminalHighlightRule.Name), 300);
        var blank = new Border { Height = 24, Background = Brushes.Transparent };
        var rows = new DockPanel();
        DockPanel.SetDock(blank, Dock.Bottom);
        rows.Children.Add(blank);
        rows.Children.Add(list);
        var area = new Grid { AllowDrop = true, ClipToBounds = true };
        area.Children.Add(rows);
        var frame = new Border
        {
            Child = area, Padding = new Thickness(8), BorderThickness = new Thickness(1), UseLayoutRounding = true,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");

        Button up = DirectionButton("▲", "MoveRuleUp", -1);
        Button down = DirectionButton("▼", "MoveRuleDown", 1);
        var high = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        var priority = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        var low = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        var side = new Grid { Margin = new Thickness(10, 0, 0, 0), UseLayoutRounding = true };
        FrameworkElement[] priorityColumn =
            [up, high, PriorityArrow(pointsUp: true), priority, PriorityArrow(pointsUp: false), low, down];
        for (int row = 0; row < priorityColumn.Length; row++)
        {
            FrameworkElement element = priorityColumn[row];
            side.RowDefinitions.Add(new RowDefinition
            {
                Height = row is 2 or 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
                MinHeight = element.MinHeight,
            });
            Grid.SetRow(element, row);
            side.Children.Add(element);
        }
        var display = new DockPanel { Margin = new Thickness(24, 0, 24, 0) };
        DockPanel.SetDock(side, Dock.Right);
        display.Children.Add(side);
        display.Children.Add(frame);

        var impactField = new DialogField(Text("AffectedTabs"), affected);
        Button createButton = null!;
        form.AddActionRow(() => createButton = form.AddAction("AddHighlightRule", CreateRule));
        form.AddFillingDisplay("HighlightRules", display, () => form.AddDisplay("AffectedTabs", impactField));
        form.AddLocalizedUpdate(() =>
        {
            columns.UpdateHeaders();
            high.Text = Text("HighlightRulePriorityHigh");
            priority.Text = Text("HighlightRulePriority");
            low.Text = Text("HighlightRulePriorityLow");
            impactField.SetCaption(Text("AffectedTabs"));
            AutomationProperties.SetName(list, Text("HighlightRules"));
        });

        var menu = new ContextMenu();
        list.ContextMenu = blank.ContextMenu = menu;
        TerminalHighlightRule? target = null;
        MenuItem edit = MenuAction("EditHighlightRule", () =>
        {
            if (target is { } rule) EditRule(rule);
        });
        MenuItem copy = MenuAction("Copy", () =>
        {
            int index = target is null ? -1 : rules.IndexOf(target);
            if (index >= 0) CopyRule(rules[index], index + 1);
        });
        MenuItem delete = MenuAction("RemoveHighlightRule", () =>
        {
            if (target is { } rule) rules.Remove(rule);
        });
        MenuItem create = MenuAction("NewListItem", CreateRule);
        list.ContextMenuOpening += SetMenu;
        blank.ContextMenuOpening += SetMenu;
        list.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2 || columns.RowAt(args.OriginalSource)?.Content is not TerminalHighlightRule rule) return;
            args.Handled = true;
            EditRule(rule);
        };
        list.SelectionChanged += (_, _) => UpdateActions();
        rules.CollectionChanged += (_, _) => UpdateActions();
        UpdateActions();
        EnableRuleDrag(list, columns, area, blank, CopyRule);

        void SetMenu(object sender, ContextMenuEventArgs args)
        {
            bool keyboard = args.CursorLeft == -1;
            var origin = keyboard ? Keyboard.FocusedElement as DependencyObject : args.OriginalSource as DependencyObject;
            if (sender == list && !keyboard && (MainWindow.Ancestor<GridViewColumnHeader>(origin) is not null ||
                                               MainWindow.Ancestor<ScrollBar>(origin) is not null))
            {
                args.Handled = true;
                return;
            }
            var row = origin is null ? null : columns.RowAt(origin);
            target = sender == list ? row?.Content as TerminalHighlightRule : null;
            if (keyboard && sender == list) target ??= list.SelectedItem as TerminalHighlightRule;
            if (target is not null)
            {
                list.SelectedItem = target;
                row?.Focus();
            }
            edit.Visibility = copy.Visibility = delete.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
            create.Visibility = target is null ? Visibility.Visible : Visibility.Collapsed;
            create.IsEnabled = copy.IsEnabled = rules.Count < HighlightCatalog.MaximumRules;
        }

        MenuItem MenuAction(string key, Action action)
        {
            var item = new MenuItem();
            form.AddLocalizedUpdate(() => item.Header = Text(key));
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Button DirectionButton(string arrow, string key, int offset)
        {
            var button = new Button { Content = arrow, MinWidth = 36, HorizontalAlignment = HorizontalAlignment.Center };
            form.AddLocalizedUpdate(() =>
            {
                button.ToolTip = Text(key);
                AutomationProperties.SetName(button, Text(key));
            });
            button.Click += (_, _) =>
            {
                int from = list.SelectedIndex;
                int to = from + offset;
                if (from < 0 || to < 0 || to >= rules.Count) return;
                var selected = rules[from];
                rules.Move(from, to);
                list.ScrollIntoView(selected);
            };
            return button;
        }

        void UpdateActions()
        {
            createButton.IsEnabled = rules.Count < HighlightCatalog.MaximumRules;
            up.IsEnabled = list.SelectedIndex > 0;
            down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex + 1 < rules.Count;
        }

        void CreateRule()
        {
            if (rules.Count >= HighlightCatalog.MaximumRules) return;
            var rule = new TerminalHighlightRule(Guid.NewGuid(), "", "", true, false, default);
            if (catalog.EditRule(this, rule) is not { } added) return;
            rules.Add(added);
            Select(added);
        }

        void EditRule(TerminalHighlightRule rule)
        {
            int index = rules.IndexOf(rule);
            if (index < 0 || catalog.EditRule(this, rule) is not { } updated) return;
            rules[index] = updated;
            Select(updated);
        }

        void CopyRule(TerminalHighlightRule rule, int index)
        {
            if (rules.Count >= HighlightCatalog.MaximumRules) return;
            var copied = rule with { Id = Guid.NewGuid() };
            rules.Insert(index, copied);
            Select(copied);
        }

        void Select(TerminalHighlightRule rule)
        {
            list.SelectedItem = rule;
            list.ScrollIntoView(rule);
        }
    }

    private static Grid PriorityArrow(bool pointsUp)
    {
        var arrow = new Grid { MinHeight = 5, IsHitTestVisible = false };
        var shaft = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
        shaft.SetResourceReference(Border.BackgroundProperty, "WindowForeground");
        var head = new Polygon
        {
            Points = pointsUp ? [new Point(4, 0), new Point(8, 5), new Point(0, 5)]
                : [new Point(0, 0), new Point(8, 0), new Point(4, 5)],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = pointsUp ? VerticalAlignment.Top : VerticalAlignment.Bottom,
        };
        head.SetResourceReference(Shape.FillProperty, "WindowForeground");
        arrow.Children.Add(shaft);
        arrow.Children.Add(head);
        return arrow;
    }

    private void EnableRuleDrag(ListView list, DetailsColumns columns, Grid area, Border blank,
        Action<TerminalHighlightRule, int> copyRule)
    {
        const string format = "TabTty.HighlightRule";
        var line = new Border
        {
            Height = 2, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        line.SetResourceReference(Border.BackgroundProperty, SystemColors.HighlightBrushKey);
        area.Children.Add(line);
        TerminalHighlightRule? pressed = null, dragging = null;
        Point start = default;
        long nextScroll = 0;
        list.PreviewMouseLeftButtonDown += (_, args) =>
        {
            pressed = !args.Handled && args.ClickCount == 1 ? columns.RowAt(args.OriginalSource)?.Content as TerminalHighlightRule : null;
            start = args.GetPosition(list);
        };
        list.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        list.PreviewMouseMove += (_, args) =>
        {
            if (pressed is not { } rule || dragging is not null || args.LeftButton != MouseButtonState.Pressed) return;
            Point point = args.GetPosition(list);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            pressed = null;
            dragging = rule;
            try { DragDrop.DoDragDrop(list, new DataObject(format, rule.Id.ToString("D")), DragDropEffects.Move | DragDropEffects.Copy); }
            finally
            {
                dragging = null;
                line.Visibility = Visibility.Collapsed;
            }
        };
        area.PreviewDragEnter += PreviewDrop;
        area.PreviewDragOver += PreviewDrop;
        area.DragLeave += (_, _) => line.Visibility = Visibility.Collapsed;
        area.PreviewDrop += (_, args) =>
        {
            line.Visibility = Visibility.Collapsed;
            if (dragging is not { } rule) return;
            args.Handled = true;
            var target = FindDrop(args, rule);
            args.Effects = target.Effect;
            if (target.Effect == DragDropEffects.Copy) copyRule(rule, target.Index);
            if (target.Effect != DragDropEffects.Move) return;
            int source = rules.IndexOf(rule);
            rules.Move(source, target.Index > source ? target.Index - 1 : target.Index);
            list.SelectedItem = rule;
            list.ScrollIntoView(rule);
        };

        void PreviewDrop(object sender, DragEventArgs args)
        {
            if (dragging is not { } rule) return;
            args.Handled = true;
            var target = FindDrop(args, rule);
            args.Effects = target.Effect;
            line.Visibility = target.Effect == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible;
            if (target.Effect != DragDropEffects.None)
                line.Margin = new Thickness(0, Math.Clamp(target.Y - 1, 0, Math.Max(0, area.ActualHeight - 2)), 0, 0);
            if (args.OriginalSource is DependencyObject origin &&
                MainWindow.Ancestor<ScrollContentPresenter>(origin) is { } viewport &&
                MainWindow.Ancestor<ScrollViewer>(viewport) is { } scroll && Environment.TickCount64 >= nextScroll)
            {
                double y = args.GetPosition(viewport).Y;
                if (y < 18 && scroll.VerticalOffset > 0) scroll.LineUp();
                else if (y > viewport.ActualHeight - 18 && scroll.VerticalOffset < scroll.ScrollableHeight) scroll.LineDown();
                else return;
                nextScroll = Environment.TickCount64 + 100;
            }
        }

        (DragDropEffects Effect, int Index, double Y) FindDrop(DragEventArgs args, TerminalHighlightRule dragged)
        {
            const DragDropKeyStates modifiers = DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey | DragDropKeyStates.AltKey;
            if (args.OriginalSource is not DependencyObject origin || !args.Data.GetDataPresent(format, autoConvert: false) ||
                MainWindow.Ancestor<GridViewColumnHeader>(origin) is not null || MainWindow.Ancestor<ScrollBar>(origin) is not null)
                return default;
            DragDropEffects effect = (args.KeyStates & modifiers) switch
            {
                DragDropKeyStates.ControlKey when rules.Count < HighlightCatalog.MaximumRules => DragDropEffects.Copy,
                DragDropKeyStates.ControlKey or DragDropKeyStates.AltKey or (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey)
                    => DragDropEffects.None,
                _ => DragDropEffects.Move,
            };
            if (effect == DragDropEffects.None || (args.AllowedEffects & effect) == 0) return default;

            int index = rules.Count;
            double y = blank.TranslatePoint(new Point(), area).Y;
            if (columns.RowAt(origin) is { Content: TerminalHighlightRule hit } row)
            {
                bool after = args.GetPosition(row).Y >= row.ActualHeight / 2;
                index = rules.IndexOf(hit) + (after ? 1 : 0);
                y = row.TranslatePoint(new Point(0, after ? row.ActualHeight : 0), area).Y;
            }
            else if (MainWindow.Ancestor<ListView>(origin) == list &&
                     list.ItemContainerGenerator.ContainerFromIndex(rules.Count - 1) is ListViewItem last)
                y = last.TranslatePoint(new Point(0, last.ActualHeight), area).Y;
            int source = rules.IndexOf(dragged);
            if (source < 0 || index < 0 || (effect == DragDropEffects.Move && (index == source || index == source + 1)))
                return default;
            return (effect, index, y);
        }
    }
}
