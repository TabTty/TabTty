using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class HighlightCatalog
{
    private Guid? deleting;

    public void Show(MainWindow owner)
    {
        try { owner.OpenForm("HighlightSets", BuildManagement); }
        catch (Exception exception) when (InvalidStore(exception))
        {
            owner.ReportCommandFailure("HighlightSets", new InvalidDataException(Text("HighlightStoreInvalid")));
        }
    }

    private void BuildManagement(FormView form)
    {
        var list = new ListView { Background = Brushes.Transparent };
        var columns = new DetailsColumns(list);
        var checkbox = new FrameworkElementFactory(typeof(HighlightSetCheckBox));
        checkbox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        checkbox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(HighlightSet.Enabled)) { Mode = BindingMode.OneWay });
        AutomationProperties.SetName(list, Text("HighlightSetList"));
        checkbox.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(HighlightSet.Name)));
        columns.Add("Enabled", nameof(HighlightSet.Enabled), 88, new DataTemplate { VisualTree = checkbox });
        columns.Add("HighlightName", nameof(HighlightSet.Name), 340);
        var frame = new Border
        {
            Child = list, Margin = new Thickness(24, 0, 24, 8), BorderThickness = new Thickness(1),
            Padding = new Thickness(8), UseLayoutRounding = true,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");

        var menu = new ContextMenu();
        list.ContextMenu = menu;
        HighlightSet? target = null;
        MenuItem edit = AddMenu("EditHighlightSet", () => OpenSelected());
        MenuItem duplicate = AddMenu("DuplicateHighlightSet", () => OpenSelected(duplicate: true));
        MenuItem delete = AddMenu("RemoveHighlightSet", async () =>
        {
            if (target is { } selected) await form.RunAsync(() => DeleteAsync(form, selected.Id));
        });
        MenuItem create = AddMenu("NewListItem", Create);
        Button createButton = null!;
        form.AddActionRow(() => createButton = form.AddAction("AddHighlightSet", Create));
        form.AddFillingDisplay("HighlightSetList", frame);
        form.AddLocalizedUpdate(() =>
        {
            AutomationProperties.SetName(list, Text("HighlightSetList"));
            columns.UpdateHeaders();
        });

        bool observing = false;
        form.Loaded += (_, _) =>
        {
            if (observing) return;
            observing = true;
            Changed += Refresh;
            Refresh();
        };
        form.Unloaded += (_, _) =>
        {
            if (!observing) return;
            observing = false;
            Changed -= Refresh;
        };
        Refresh();

        list.ContextMenuOpening += (_, args) =>
        {
            bool keyboard = args.CursorLeft == -1;
            var origin = keyboard ? Keyboard.FocusedElement as DependencyObject : args.OriginalSource as DependencyObject;
            if (!keyboard && (MainWindow.Ancestor<GridViewColumnHeader>(origin) is not null ||
                              MainWindow.Ancestor<ScrollBar>(origin) is not null))
            {
                args.Handled = true;
                return;
            }
            var row = origin is null ? null : columns.RowAt(origin);
            target = row?.Content as HighlightSet;
            if (keyboard) target ??= list.SelectedItem as HighlightSet;
            if (target is { } selected)
            {
                list.SelectedItem = selected;
                row?.Focus();
            }
            edit.Visibility = duplicate.Visibility = delete.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
            create.Visibility = target is null ? Visibility.Visible : Visibility.Collapsed;
            create.IsEnabled = duplicate.IsEnabled = CanCreate();
            delete.IsEnabled = !temporaryDefaults;
        };
        list.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2 || columns.RowAt(args.OriginalSource)?.Content is not HighlightSet selected ||
                MainWindow.Ancestor<CheckBox>(args.OriginalSource as DependencyObject) is not null) return;
            args.Handled = true;
            OpenEditor(selected, creating: false);
        };
        list.AddHandler(HighlightSetCheckBox.ToggleRequestedEvent, new RoutedEventHandler(async (_, args) =>
        {
            if (args.OriginalSource is not HighlightSetCheckBox { DataContext: HighlightSet selected }) return;
            list.SelectedItem = selected;
            await form.RunAsync(() => SetEnabledAsync(form, selected.Id, !selected.Enabled));
        }));

        MenuItem AddMenu(string key, Action action)
        {
            var item = new MenuItem();
            form.AddLocalizedUpdate(() => item.Header = Text(key));
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        bool CanCreate() => !temporaryDefaults && GetSets().Count < MaximumSets;

        void Create()
        {
            if (CanCreate()) OpenEditor(new(Guid.NewGuid(), "", true, []), creating: true);
        }

        void OpenSelected(bool duplicate = false)
        {
            if (Find(target?.Id) is not { } set) return;
            if (!duplicate) OpenEditor(set, creating: false);
            else if (CanCreate())
                OpenEditor(set with { Id = Guid.NewGuid(),
                    Rules = set.Rules.Select(rule => rule with { Id = Guid.NewGuid() }).ToArray() }, creating: true);
        }

        void Refresh()
        {
            Guid? selection = (list.SelectedItem as HighlightSet)?.Id;
            list.ItemsSource = GetSets();
            columns.ApplySort();
            list.SelectedItem = Find(selection);
            createButton.IsEnabled = CanCreate();
        }
    }

    private void OpenEditor(HighlightSet set, bool creating)
    {
        if (deleting == set.Id) return;
        if (EditorFor(set.Id) is { } editor)
        {
            if (editor.WindowState == WindowState.Minimized) editor.WindowState = WindowState.Normal;
            editor.Activate();
            return;
        }
        new HighlightSetEditorWindow(app, this, set, creating).Show();
    }

    private HighlightSetEditorWindow? EditorFor(Guid id) =>
        app.Windows.OfType<HighlightSetEditorWindow>().FirstOrDefault(window => window.SetId == id);

    internal string AffectedTabs(Guid setId)
    {
        string[] names = app.Windows.OfType<MainWindow>().SelectMany(window => window.TerminalTabs)
            .Where(tab => tab.HighlightSetId == setId).Distinct()
            .Select(tab => PermissionDisplay.Literal(tab.TabCaption)).ToArray();
        return names.Length == 0 ? Text("NoAffectedHighlightTabs") : string.Join(Environment.NewLine, names);
    }

    private async Task SetEnabledAsync(FormView form, Guid id, bool enabled)
    {
        using var progress = form.BeginProgress("FileOperationPending");
        await writes.WaitAsync();
        try
        {
            var current = GetSets().ToArray();
            int index = Array.FindIndex(current, set => set.Id == id);
            if (index < 0) throw new InvalidOperationException(Text("ManagementTabOutdated"));
            if (current[index].Enabled == enabled) return;
            current[index] = current[index] with { Enabled = enabled };
            await SaveAsync(current);
        }
        finally { writes.Release(); }
    }

    private async Task DeleteAsync(FormView form, Guid id)
    {
        if (Find(id) is not { } set) return;
        RequireSavedEditor();
        var context = new StackPanel();
        context.Children.Add(new DialogField(Text("HighlightSet"), new DialogText(PermissionDisplay.Literal(set.Name))));
        context.Children.Add(new DialogField(Text("AffectedTabs"), new DialogText(AffectedTabs(id), 150)));
        if (app.Modal(() => ThemedMessageBox.Show(form.OwnerWindow, Text("DeleteHighlightSetConfirm"),
            Text("DeleteHighlightSet"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No, acceptKey: "DeleteHighlightSet", context: context)) != MessageBoxResult.Yes) return;

        using var progress = form.BeginProgress("FileOperationPending");
        await writes.WaitAsync();
        try
        {
            var current = GetSets().ToList();
            if (current.RemoveAll(set => set.Id == id) == 0) return;
            var editor = RequireSavedEditor();
            deleting = id;
            editor?.SetDeleting(true);
            try { await SaveAsync(current); }
            finally
            {
                deleting = null;
                editor?.SetDeleting(false);
            }
            editor?.Close();
        }
        finally { writes.Release(); }

        HighlightSetEditorWindow? RequireSavedEditor()
        {
            var editor = EditorFor(id);
            if (editor?.HasPendingEdits == true)
                throw new InvalidOperationException(Text("ResolveHighlightEditorBeforeDelete"));
            return editor;
        }
    }
}

internal sealed class HighlightSetCheckBox : CheckBox
{
    internal static readonly RoutedEvent ToggleRequestedEvent = EventManager.RegisterRoutedEvent(
        "ToggleRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(HighlightSetCheckBox));

    protected override void OnToggle() => RaiseEvent(new RoutedEventArgs(ToggleRequestedEvent, this));
}
