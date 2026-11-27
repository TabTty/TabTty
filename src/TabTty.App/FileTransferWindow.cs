using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TabTty.Core;
using IconPath = System.Windows.Shapes.Path;
using static TabTty.App.App;

namespace TabTty.App;

internal enum TransferWindowCloseStatus { Dismissed, ReportFailed, DismissalRejected }

internal readonly record struct TransferWindowCloseResult(TransferWindowCloseStatus Status, bool ProviderPending)
{
    public bool Dismissed => Status == TransferWindowCloseStatus.Dismissed;
}

internal sealed class FileTransferWindow : Window
{
    private const string DragDataFormat = "TabTty.FileTransfer";

    private readonly TerminalSession session;
    private readonly CancellationTokenSource connection = new();
    private readonly Pane local = new(false), remote = new(true);
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private string operationStatus = "";
    private readonly ProgressBar progress = new() { Height = 5, Minimum = 0, Maximum = 100 };
    private readonly ListBox results = new() { MaxHeight = 130 };
    private readonly Button upload, download, cancel, retry;
    private readonly List<(ContentControl Control, string Key)> labels = [];
    private readonly List<(GridViewColumn Column, string Key)> columns = [];
    private TextBlock? localHeading;
    private readonly FileOperationOwnership ownership;
    private SftpClient? client;
    private TransferFiles? files;
    private FileOperations? operations;
    private Task work = Task.CompletedTask;
    private bool disconnected, closing, closed;
    private bool dismissalApproved, closeReportFailed;
    private Task<TransferWindowCloseResult>? closeRequest;
    private long lastProgress;
    private Point dragStart;
    private Row? dragRow;
    private MouseButton dragButton;
    private bool suppressContextMenu;
    private DragSelection? activeDrag;
    private ListViewItem? highlighted;
    private ContextMenu? dropMenu;
    private FileClipboard? clipboard;

    public FileTransferWindow(TerminalSession session, FileOperationOwnership ownership)
    {
        this.session = session;
        this.ownership = ownership;
        ownership.Changed += OwnershipChanged;
        Title = $"{Text("FileTransfer")} — {session.FileTransferEndpoint}";
        Icon = WindowIcons.App;
        Width = 1060;
        Height = 660;
        MinWidth = 550;
        MinHeight = 370;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");

        var layout = new DockPanel { Margin = new Thickness(12) };
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        footer.Children.Add(status);
        footer.Children.Add(progress);
        AutomationProperties.SetName(results, Text("TransferResults"));
        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new Binding { Converter = new ResultTextConverter() });
        results.ItemTemplate = new DataTemplate { VisualTree = row };
        footer.Children.Add(results);
        var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(commands);
        retry = Button("TransferRetry", () => Observe(operations?.Retry(PresentQuestion)));
        cancel = Button("Cancel", () => operations?.Cancel());
        cancel.Margin = new Thickness(0, 4, 0, 4);
        commands.Children.Add(retry);
        commands.Children.Add(cancel);

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement left = BuildPane(local, Text("TransferLocal"));
        FrameworkElement right = BuildPane(remote, session.FileTransferEndpoint);
        var divider = new GridSplitter
        {
            Width = 6, HorizontalAlignment = HorizontalAlignment.Center,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        upload = DirectionButton("TransferUpload", "＞", () => StartTransfer(Selected(local), remote.Directory));
        download = DirectionButton("TransferDownload", "＜", () => StartTransfer(Selected(remote), local.Directory));
        var direction = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { upload, download } };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(direction, 1);
        Grid.SetColumn(right, 2);
        panes.Children.Add(left);
        panes.Children.Add(divider);
        panes.Children.Add(direction);
        panes.Children.Add(right);
        layout.Children.Add(panes);
        Content = layout;
        Loaded += (_, _) => { if (client is null && !closing && !disconnected) _ = InitializeAsync(); };
        UpdateActions();
    }

    public bool HasActiveOperation => ownership.IsBusy;
    internal long OperationRevision => ownership.Revision;

    public bool OwnsSession(TerminalSession value) => ReferenceEquals(session, value);

    public void UpdateLabels()
    {
        Title = $"{Text("FileTransfer")} — {session.FileTransferEndpoint}";
        if (localHeading is { } heading) heading.Text = Text("TransferLocal");
        AutomationProperties.SetName(local.List, Text("TransferLocal"));
        AutomationProperties.SetName(results, Text("TransferResults"));
        AutomationProperties.SetName(local.Path, Text("TransferPath"));
        AutomationProperties.SetName(remote.Path, Text("TransferPath"));
        LabelDirectionButton(upload, "TransferUpload");
        LabelDirectionButton(download, "TransferDownload");
        foreach (var (control, key) in labels) control.Content = Text(key);
        foreach (var (column, key) in columns) column.Header = Text(key);
        if (results.ItemsSource is not null) results.Items.Refresh();
        if (closeReportFailed) status.Text = Text("SftpCloseReportFailed");
    }

    private async Task InitializeAsync()
    {
        SetOperationStatus(Text("Connecting"));
        try
        {
            SftpClient connected = await session.OpenFileTransferAsync(connection.Token);
            if (closing || disconnected) { connected.Dispose(); return; }
            client = connected;
            files = new TransferFiles(client, session.FileTransferWait, ownership, new WindowInteropHelper(this).Handle);
            operations = new FileOperations(files, ownership, connection.Token);
            results.ItemsSource = operations.Results;
            operations.Changed += OperationChanged;
            SetOperationStatus("");
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            local.Path.Text = documents;
            remote.Path.Text = ".";
            refreshingAfterOperation = true;
            long? initialRevision = ownership.RefreshRevision;
            try { await Task.WhenAll(NavigateAsync(local, documents), NavigateAsync(remote, ".")); }
            finally { refreshingAfterOperation = false; }
            if (initialRevision is null && ownership.RefreshRevision is not null && local.IsListed && remote.IsListed)
                await RefreshAfterOperationAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (TransferBatch.IsFileError(error)) { SetOperationStatus(Text(TransferBatch.ErrorKey(error))); }
        finally { UpdateActions(); }
    }

    private FrameworkElement BuildPane(Pane pane, string title)
    {
        var layout = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        if (!pane.Remote) localHeading = heading;
        header.Children.Add(heading);
        AutomationProperties.SetName(pane.Path, Text("TransferPath"));
        DataObject.AddCopyingHandler(pane.Path, (_, args) =>
        {
            if (args.IsDragDrop) args.CancelCommand();
        });
        pane.Path.TextChanged += (_, _) =>
        {
            pane.Load?.Cancel();
            activeDrag = null;
            UpdateActions();
        };
        pane.Path.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && pane.ListedPath is { } previous)
            {
                args.Handled = true;
                pane.Path.Text = previous;
                return;
            }
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            _ = pane.IsListed ? RefreshAsync(pane) : NavigateAsync(pane, pane.Path.Text);
        };
        header.Children.Add(pane.Path);
        var actions = new WrapPanel();
        header.Children.Add(actions);
        pane.Parent = Button("TransferParent", () => OpenParent(pane));
        actions.Children.Add(pane.Parent);
        actions.Children.Add(Button("TransferRefresh", () =>
        {
            if (ownership.NeedsRefresh) _ = RefreshAfterOperationAsync();
            else _ = RefreshAsync(pane);
        }));
        pane.NewFolder = Button("TransferNewFolder", () => StartCreate(pane));
        actions.Children.Add(pane.NewFolder);

        pane.List.SelectionMode = SelectionMode.Extended;
        pane.List.AllowDrop = true;
        var view = new CompactGridView();
        AddColumn("TransferName", 220, nameof(Row.Name), CreateNameCellTemplate());
        AddColumn("TransferSize", 85, nameof(Row.Size));
        AddColumn("TransferModified", 145, nameof(Row.Modified));
        pane.List.View = view;
        AutomationProperties.SetName(pane.List, title);
        AddFileMenu(pane);
        pane.List.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut,
            (_, _) => CaptureClipboard(pane, Selected(pane), cut: true), (_, args) => args.CanExecute = CanCapture(pane)));
        pane.List.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, _) => CaptureClipboard(pane, Selected(pane), cut: false), (_, args) => args.CanExecute = CanCapture(pane)));
        pane.List.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
            (_, _) => PasteFiles(pane), (_, args) => args.CanExecute = CanPaste(pane)));
        pane.List.SelectionChanged += (_, _) =>
        {
            if (activeDrag?.Source == pane) activeDrag = null;
            UpdateActions();
        };
        pane.List.MouseDoubleClick += (_, args) => OpenFolder(pane, RowAt(pane, args.OriginalSource));
        pane.List.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) OpenFolder(pane, pane.List.SelectedItem as Row);
            else if (args.Key == Key.F2) StartRename(pane, Selected(pane));
            else if (args.Key == Key.Delete) StartDelete(pane, Selected(pane));
            else return;
            args.Handled = true;
        };
        pane.List.PreviewMouseDown += (_, args) =>
        {
            if (args.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            if (args.ChangedButton == MouseButton.Right) suppressContextMenu = false;
            dragButton = args.ChangedButton;
            dragStart = args.GetPosition(pane.List);
            dragRow = RowAt(pane, args.OriginalSource);
        };
        pane.List.PreviewMouseUp += (_, args) =>
        {
            if (args.ChangedButton == dragButton) dragRow = null;
        };
        pane.List.PreviewMouseMove += (_, args) => Drag(pane, args);
        pane.List.DragEnter += (_, args) => HandleDrop(pane, args, commit: false);
        pane.List.DragOver += (_, args) => HandleDrop(pane, args, commit: false);
        pane.List.Drop += (_, args) => HandleDrop(pane, args, commit: true);
        pane.List.DragLeave += (_, _) => ClearDropPreview();
        pane.List.PreviewQueryContinueDrag += (_, args) => ContinueDrag(args);
        pane.List.PreviewGiveFeedback += (_, args) =>
        {
            if (activeDrag?.PreviewTarget is not null || args.Effects == DragDropEffects.None) return;
            Mouse.SetCursor(Cursors.No);
            args.UseDefaultCursors = false;
            args.Handled = true;
        };
        layout.Children.Add(pane.List);
        return layout;

        void AddColumn(string key, double width, string property, DataTemplate? cell = null)
        {
            var column = new GridViewColumn { Header = Text(key), Width = width };
            if (cell is null) column.DisplayMemberBinding = new Binding(property);
            else column.CellTemplate = cell;
            view.Columns.Add(column);
            columns.Add((column, key));
        }
    }

    private static DataTemplate CreateNameCellTemplate()
    {
        var cell = new FrameworkElementFactory(typeof(DockPanel));

        var icon = new FrameworkElementFactory(typeof(IconPath));
        icon.SetValue(FrameworkElement.WidthProperty, 14d);
        icon.SetValue(FrameworkElement.HeightProperty, 14d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(IconPath.StretchProperty, Stretch.Uniform);
        icon.SetValue(IconPath.StrokeThicknessProperty, 1.2d);
        icon.SetBinding(IconPath.StrokeProperty, new Binding(nameof(Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListViewItem), 1),
        });
        icon.SetBinding(IconPath.DataProperty, new Binding(nameof(Row.Icon)));
        cell.AppendChild(icon);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Name)));
        cell.AppendChild(name);

        return new DataTemplate { VisualTree = cell };
    }

    private void AddFileMenu(Pane pane)
    {
        var cut = new MenuItem { InputGestureText = "Ctrl+X" };
        var copy = new MenuItem { InputGestureText = "Ctrl+C" };
        var paste = new MenuItem { InputGestureText = "Ctrl+V" };
        var separator = new Separator();
        var rename = new MenuItem();
        var delete = new MenuItem();
        var menu = new ContextMenu { Items = { cut, copy, paste, separator, rename, delete } };
        pane.List.ContextMenu = menu;
        TransferItem[] targets = [];
        int revision = -1;
        bool onRow = false;
        pane.List.ContextMenuOpening += (_, args) =>
        {
            bool keyboard = args.CursorLeft == -1;
            if (!keyboard && suppressContextMenu)
            {
                args.Handled = true;
                return;
            }
            var origin = keyboard ? Keyboard.FocusedElement : args.OriginalSource;
            var row = origin is null ? null : RowContainerAt(pane, origin);
            var clicked = row?.Content as Row;
            if (keyboard) clicked ??= pane.List.SelectedItem as Row;
            onRow = clicked is not null;
            if (!pane.HasDirectory || !onRow && !CanPaste(pane))
            {
                args.Handled = true;
                return;
            }
            if (clicked is not null && !pane.List.SelectedItems.Contains(clicked))
            {
                pane.List.UnselectAll();
                pane.List.SelectedItem = clicked;
                row?.Focus();
            }
        };
        menu.Opened += (_, _) =>
        {
            targets = onRow ? Selected(pane) : [];
            revision = pane.Revision;
            cut.Header = Text("TransferCut");
            copy.Header = Text("Copy");
            paste.Header = Text("Paste");
            rename.Header = Text("TransferRename");
            delete.Header = Text("TransferDelete");
            cut.Visibility = copy.Visibility = separator.Visibility = rename.Visibility = delete.Visibility =
                onRow ? Visibility.Visible : Visibility.Collapsed;
            cut.IsEnabled = copy.IsEnabled = CanMutate(pane) && targets.Length != 0;
            paste.IsEnabled = CanPaste(pane);
            rename.IsEnabled = CanMutate(pane) && targets is [{ Name: not null }];
            delete.IsEnabled = CanMutate(pane) && targets.Length != 0;
        };
        cut.Click += (_, _) => { if (revision == pane.Revision) CaptureClipboard(pane, targets, cut: true); };
        copy.Click += (_, _) => { if (revision == pane.Revision) CaptureClipboard(pane, targets, cut: false); };
        paste.Click += (_, _) => { if (revision == pane.Revision) PasteFiles(pane); };
        rename.Click += (_, _) => { if (revision == pane.Revision) StartRename(pane, targets); };
        delete.Click += (_, _) => { if (revision == pane.Revision) StartDelete(pane, targets); };
    }

    private Button Button(string key, Action action, bool track = true)
    {
        var button = new Button { Content = Text(key), Margin = new Thickness(0, 4, 6, 4), Padding = new Thickness(8, 3, 8, 3) };
        button.Click += (_, _) => action();
        if (track) labels.Add((button, key));
        return button;
    }

    private Button DirectionButton(string key, string glyph, Action action)
    {
        const double size = 36;
        var button = Button(key, action, track: false);
        button.Content = glyph;
        button.MinWidth = size;
        button.Width = size;
        button.Height = size;
        button.Margin = new Thickness(2, 4, 2, 4);
        LabelDirectionButton(button, key);
        return button;
    }

    private static void LabelDirectionButton(Button button, string key)
    {
        string label = Text(key);
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private Task NavigateAsync(Pane pane, string path) => BrowseAsync(pane, token => files!.ResolveAsync(pane.Remote, path, pane.Directory, token));

    private Task<bool> RefreshAsync(Pane pane) => pane.Directory is { } path
        ? BrowseAsync(pane, _ => Task.FromResult(path), preserveDraft: pane.Path.Text != pane.ListedPath)
        : pane.ListedPath == "" ? BrowseAsync(pane, null, preserveDraft: pane.Path.Text.Length != 0) : Task.FromResult(false);

    private bool CanOpenParent(Pane pane) => IsConnected && pane.HasDirectory && !pane.NoRemoteParent &&
        (pane.Directory?.Local is not { } path || IsDriveRoot(path) || GetLocalParent(path) is not null);

    private static string? GetLocalParent(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));

    private static bool IsDriveRoot(string path) => path is [_, ':', '\\'];

    private void OpenParent(Pane pane)
    {
        if (!CanOpenParent(pane) || pane.Directory is not { } directory) return;
        if (directory.Local is { } localPath)
        {
            string? parent = GetLocalParent(localPath);
            _ = BrowseAsync(pane, parent is null ? null : _ => Task.FromResult(new TransferPath(parent)));
        }
        else
            _ = BrowseAsync(pane, token => files!.ResolveAsync(true, "..", directory, token), remoteParent: true);
    }

    private async Task<bool> BrowseAsync(Pane pane, Func<CancellationToken, Task<TransferPath>>? resolveDirectory,
        bool preserveDraft = false, bool remoteParent = false)
    {
        if (files is null || client?.IsOpen != true || disconnected || closing) return false;
        pane.Load?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        pane.Load = load;
        pane.RefreshRevision = null;
        activeDrag = null;
        dragRow = null;
        UpdateActions();
        try
        {
            TransferPath? path = resolveDirectory is null ? null : await resolveDirectory(load.Token);
            if (pane.Load != load || load.IsCancellationRequested) return false;
            if (remoteParent && path?.Remote is { } resolved && resolved.AsSpan().SequenceEqual(pane.Directory?.Remote))
            {
                pane.NoRemoteParent = true;
                return false;
            }
            long? refreshRevision = ownership.RefreshRevision;
            Row[] rows = path is null ? (await files.ListDrivesAsync(load.Token))
                .Order(StringComparer.OrdinalIgnoreCase).Select(drive => new Row(null, drive)).ToArray()
                : (await files.ListAsync(path, load.Token)).OrderByDescending(item => item.Attributes.Kind == TransferKind.Directory)
                    .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).Select(item => new Row(item)).ToArray();
            if (pane.Load != load || load.IsCancellationRequested) return false;
            pane.List.ItemsSource = rows;
            pane.Revision++;
            if (!ReferenceEquals(pane.Directory, path))
                pane.NoRemoteParent = path?.Remote is { } wire && wire.AsSpan().SequenceEqual("/"u8);
            pane.Directory = path;
            pane.ListedPath = path is null ? "" : files.Display(path);
            pane.Load = null;
            if (!preserveDraft) pane.Path.Text = pane.ListedPath;
            pane.RefreshRevision = refreshRevision;
            if (refreshRevision is { } observed && local.RefreshRevision == observed && remote.RefreshRevision == observed)
                ownership.Refreshed(observed);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (TransferBatch.IsFileError(error))
        {
            if (pane.Load == load) SetOperationStatus(Text(TransferBatch.ErrorKey(error)));
            return false;
        }
        finally { if (pane.Load == load) pane.Load = null; UpdateActions(); }
    }

    private void OpenFolder(Pane pane, Row? row)
    {
        if (!pane.IsListed) return;
        if (row?.Drive is { } drive)
            _ = NavigateAsync(pane, drive);
        else if (row?.Item is { Name: not null, Attributes.Kind: TransferKind.Directory } item)
            _ = BrowseAsync(pane, _ => Task.FromResult(item.Path));
    }

    private static ListViewItem? RowContainerAt(Pane pane, object origin) => origin is DependencyObject element
        ? ItemsControl.ContainerFromElement(pane.List, element) as ListViewItem : null;

    private static Row? RowAt(Pane pane, object origin) => RowContainerAt(pane, origin)?.Content as Row;

    private static TransferItem[] Selected(Pane pane) => pane.List.SelectedItems.Cast<Row>().Select(row => row.Item).OfType<TransferItem>().ToArray();

    private void Drag(Pane pane, MouseEventArgs args)
    {
        bool showMenu = dragButton == MouseButton.Right;
        if (dragRow is null || (showMenu ? args.RightButton : args.LeftButton) != MouseButtonState.Pressed) return;
        Point now = args.GetPosition(pane.List);
        if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        IDataObject? data = CreateDragData(pane.List, dragRow, showMenu);
        dragRow = null;
        if (data is null) return;
        if (showMenu) suppressContextMenu = true;
        try { DragDrop.DoDragDrop(pane.List, data, DragDropEffects.Copy | DragDropEffects.Move); }
        finally { ClearDropPreview(); activeDrag = null; }
    }

    private void ContinueDrag(QueryContinueDragEventArgs args)
    {
        const DragDropKeyStates buttons = DragDropKeyStates.LeftMouseButton | DragDropKeyStates.RightMouseButton |
            DragDropKeyStates.MiddleMouseButton;
        if (args.EscapePressed || (args.KeyStates & buttons) != 0) return;
        if (activeDrag?.PreviewTarget is not null && GetCursorPos(out var point) &&
            WindowFromPoint(point) == new WindowInteropHelper(this).Handle) return;
        args.Action = DragAction.Cancel;
        args.Handled = true;
    }

    internal IDataObject? CreateDragData(ListView list, object clickedRow, bool showMenu = false)
    {
        Pane pane = list == local.List ? local : remote;
        if (!CanMutate(pane) || (showMenu && !CanTransfer(1)) || pane.List != list ||
            clickedRow is not Row { Item: { } item } row || !list.Items.Contains(row)) return null;
        TransferItem[] selection = Selected(pane);
        activeDrag = new DragSelection(pane, pane.Revision, selection.Contains(item) ? selection : [item], showMenu);
        return new DataObject(DragDataFormat, activeDrag.Token);
    }

    private void HandleDrop(Pane pane, DragEventArgs args, bool commit)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (activeDrag is not { } drag || !CanDrop(drag, pane) || pane.Directory is not { } current ||
            !Equals(args.Data.GetData(DragDataFormat, autoConvert: false), drag.Token))
        {
            ClearDropPreview();
            return;
        }
        bool samePane = drag.Source == pane;
        DragDropEffects effect = drag.ShowMenu ? args.AllowedEffects & DragDropEffects.Copy : GetDropEffect(args, samePane);
        ListViewItem? container = RowContainerAt(pane, args.OriginalSource);
        var folder = container?.Content is Row { Item: { Attributes.Kind: TransferKind.Directory } folderItem } ? folderItem : null;
        TransferPath target = folder?.Path ?? current;
        if (effect == DragDropEffects.None || (samePane && (folder is null || drag.Items.Contains(folder))) ||
            (commit && (drag.PreviewPane != pane || drag.PreviewRevision != pane.Revision || !ReferenceEquals(drag.PreviewTarget, target))))
        {
            ClearDropPreview();
            return;
        }
        args.Effects = effect;
        if (commit)
        {
            ClearDropPreview();
            if (samePane) Observe(operations?.MoveToFolder(drag.Items, target));
            else if (drag.ShowMenu) ShowDropMenu(pane, drag, target, args.GetPosition(pane.List));
            else StartTransfer(drag.Items, target, move: effect == DragDropEffects.Move);
        }
        else
        {
            drag.PreviewTarget = target;
            drag.PreviewPane = pane;
            drag.PreviewRevision = pane.Revision;
            Highlight(folder is null ? null : container);
            if (!closeReportFailed)
            {
                string key = drag.ShowMenu ? "TransferChooseDropTarget" : effect == DragDropEffects.Move
                    ? "TransferMoveDropTarget" : "TransferDropTarget";
                status.Text = Format(key, files!.Display(target));
            }
        }
    }

    private bool CanDrop(DragSelection drag, Pane pane) => drag.Revision == drag.Source.Revision &&
        (drag.Source == pane ? !drag.ShowMenu && CanMutate(pane) : CanTransfer(drag.Items.Length));

    private static DragDropEffects GetDropEffect(DragEventArgs args, bool samePane)
    {
        const DragDropKeyStates shift = DragDropKeyStates.ShiftKey;
        const DragDropKeyStates ctrl = DragDropKeyStates.ControlKey;
        const DragDropKeyStates alt = DragDropKeyStates.AltKey;
        DragDropEffects effect = (args.KeyStates & (shift | ctrl | alt)) switch
        {
            0 => samePane ? DragDropEffects.Move : DragDropEffects.Copy,
            ctrl => samePane ? DragDropEffects.None : DragDropEffects.Copy,
            alt or (ctrl | shift) => DragDropEffects.None,
            _ => DragDropEffects.Move,
        };
        return args.AllowedEffects & effect;
    }

    private void ShowDropMenu(Pane pane, DragSelection drag, TransferPath target, Point position)
    {
        CloseDropMenu();
        int revision = pane.Revision;
        var copy = new MenuItem { Header = Text("TransferCopyHere") };
        var move = new MenuItem { Header = Text("TransferMoveHere") };
        var cancelDrop = new MenuItem { Header = Text("Cancel") };
        var menu = new ContextMenu
        {
            PlacementTarget = pane.List,
            Placement = PlacementMode.RelativePoint,
            PlacementRectangle = new Rect(position, new Size()),
            Items = { copy, move, cancelDrop },
        };
        dropMenu = menu;
        copy.Click += (_, _) => Accept(moveSource: false);
        move.Click += (_, _) => Accept(moveSource: true);
        cancelDrop.Click += (_, _) => CloseDropMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(dropMenu, menu)) dropMenu = null; };
        // Open after the native drag loop releases mouse capture.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!ReferenceEquals(dropMenu, menu)) return;
            if (IsCurrent()) menu.IsOpen = true;
            else CloseDropMenu();
        });

        bool IsCurrent() => CanTransfer(drag.Items.Length) && drag.Revision == drag.Source.Revision && revision == pane.Revision;

        void Accept(bool moveSource)
        {
            menu.IsOpen = false;
            if (IsCurrent()) StartTransfer(drag.Items, target, move: moveSource);
        }
    }

    private void CloseDropMenu()
    {
        ContextMenu? menu = dropMenu;
        dropMenu = null;
        if (menu is not null) menu.IsOpen = false;
    }

    private void ClearDropPreview()
    {
        if (activeDrag is { } drag) drag.PreviewTarget = null;
        Highlight(null);
        if (!closeReportFailed) status.Text = operationStatus;
    }

    private void Highlight(ListViewItem? item)
    {
        if (highlighted == item) return;
        highlighted?.ClearValue(BorderBrushProperty);
        highlighted = item;
        highlighted?.SetResourceReference(BorderBrushProperty, "ListViewItemPillFillBrush");
    }

    private void StartTransfer(TransferItem[] selection, TransferPath? target, bool move = false)
    {
        if (target is null || !CanTransfer(selection.Length)) return;
        Observe(move ? operations?.Move(selection, target, PresentQuestion)
            : operations?.Copy(selection, target, PresentQuestion));
    }

    private bool IsConnected => files is not null && client?.IsOpen == true && !disconnected && !closing;
    private bool CanStart => IsConnected && operations?.CanStart == true;
    private bool CanTransfer(int selected) => CanStart && local.HasDirectory && remote.HasDirectory && selected > 0;
    private bool CanMutate(Pane pane) => CanStart && pane.HasDirectory;
    private bool CanCapture(Pane pane) => CanMutate(pane) && pane.List.SelectedItems.Count != 0;
    private bool CanPaste(Pane pane) => CanMutate(pane) && clipboard is { } captured && captured.Source != pane;

    private void CaptureClipboard(Pane pane, TransferItem[] selection, bool cut)
    {
        if (!CanMutate(pane) || selection.Length == 0) return;
        clipboard = new(pane, selection, cut);
        CommandManager.InvalidateRequerySuggested();
    }

    private void PasteFiles(Pane pane)
    {
        if (!CanPaste(pane) || clipboard is not { } captured || pane.Directory is not { } target) return;
        var operation = captured.Cut
            ? operations?.Move(captured.Items, target, PresentQuestion)
            : operations?.Copy(captured.Items, target, PresentQuestion);
        if (operation is null) return;
        if (captured.Cut && !(operation.IsCompletedSuccessfully && operation.Result == "SftpCapacity"))
            clipboard = null;
        Observe(operation);
    }

    private void Observe(Task<string>? result)
    {
        if (result is not null) work = ObserveAsync(result);
    }

    private async Task ObserveAsync(Task<string> result)
    {
        string key = await result;
        if (closed || closing) return;
        SetOperationStatus(Text(disconnected ? "SftpDisconnected" : key));
        progress.IsIndeterminate = false;
        UpdateActions();
        if (key != "SftpCapacity") await RefreshAfterOperationAsync();
    }

    private void OperationChanged(FileOperationUpdate update)
    {
        if (closed || closing) return;
        switch (update)
        {
            case FileOperationUpdate.Started:
                SetOperationStatus(Text("TransferWorking"));
                progress.IsIndeterminate = true;
                UpdateActions();
                break;
            case FileOperationUpdate.Progress report:
                ReportProgress(report.Path, report.Bytes, report.Total);
                break;
        }
    }

    private void OwnershipChanged() => Dispatcher.BeginInvoke(async () =>
    {
        if (closed || closing) return;
        UpdateActions();
        if (ownership.NeedsRefresh) await RefreshAfterOperationAsync();
    });

    private bool refreshingAfterOperation;

    private async Task RefreshAfterOperationAsync()
    {
        if (closed || closing || disconnected || ownership.IsBusy || refreshingAfterOperation) return;
        refreshingAfterOperation = true;
        try
        {
            await Task.WhenAll(RefreshAsync(local), RefreshAsync(remote));
        }
        finally { refreshingAfterOperation = false; UpdateActions(); }
    }

    private void UpdateActions()
    {
        if (activeDrag is not { PreviewPane: { } target } drag || !CanDrop(drag, target)) ClearDropPreview();
        if (dropMenu is not null && !CanTransfer(1)) CloseDropMenu();
        upload.IsEnabled = CanTransfer(local.List.SelectedItems.Count);
        download.IsEnabled = CanTransfer(remote.List.SelectedItems.Count);
        retry.IsEnabled = CanStart && operations?.CanRetry == true;
        cancel.IsEnabled = operations?.IsRunning == true && !closing;
        local.NewFolder.IsEnabled = CanMutate(local);
        remote.NewFolder.IsEnabled = CanMutate(remote);
        local.Parent.IsEnabled = CanOpenParent(local);
        remote.Parent.IsEnabled = CanOpenParent(remote);
        local.Path.IsEnabled = remote.Path.IsEnabled = IsConnected;
        local.List.IsEnabled = local.IsListed;
        remote.List.IsEnabled = remote.IsListed;
    }

    private sealed class ResultTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is FileOperationUpdate.Entry entry
                ? ResultDetails(entry)
                : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    private void ReportProgress(string label, long bytes, long? total)
    {
        long now = Environment.TickCount64;
        if (bytes != 0 && bytes != total && now - lastProgress < 60) return;
        lastProgress = now;
        SetOperationStatus(Format("TransferProgress", label, bytes.ToString("N0"), total?.ToString("N0") ?? Text("TransferUnavailable")));
        progress.IsIndeterminate = total is null;
        progress.Value = total > 0 ? Math.Min(100, 100d * bytes / total.Value) : 0;
    }

    private ValueTask<TransferChoice> PresentQuestion(TransferQuestion question)
    {
        if (question.Kind == TransferQuestionKind.PermanentDelete)
            return ValueTask.FromResult(Confirm(Text("TransferDeletePermanent"), "TransferPermanentlyDelete", question.Lifetime,
                new DialogField(Text("TransferFilePath"), new DialogText(files!.Display(question.Path))), "TransferSkip")
                ? TransferChoice.Overwrite : TransferChoice.Skip);
        if (question.Kind == TransferQuestionKind.Delete)
        {
            var items = question.Selection!;
            string names = string.Join('\n', items.Take(40).Select(item => item.Label));
            if (items.Count > 40) names += "\n" + Format("TransferMore", items.Count - 40);
            string key = question.Path.Remote is null ? "TransferDeleteLocal" : "TransferDeleteRemote";
            string action = question.Path.Remote is null ? "TransferDelete" : "TransferPermanentlyDelete";
            var context = new StackPanel();
            context.Children.Add(new DialogField(Text("TransferPath"), new DialogText(files!.Display(question.Path))));
            context.Children.Add(new DialogField(Text("TransferSelectedItems"), new DialogText(names, 200)));
            return ValueTask.FromResult(Confirm(Text(key), action, question.Lifetime, context) ? TransferChoice.Overwrite : TransferChoice.Cancel);
        }
        TransferItem source = question.Source!;
        TransferPath target = question.Path;
        TransferAttributes existing = question.Existing!;
        TransferChoice selected = TransferChoice.Cancel;
        var dialog = new MainWindow.FormWindow(this, Text("TransferOverwrite")) { Width = 510 };
        dialog.AddNotice("TransferConflict");
        AddFile("TransferIncoming", source.Path, source.Attributes);
        AddFile("TransferExisting", target, existing);
        dialog.AddDialogAction("TransferOverwrite", () => Finish(TransferChoice.Overwrite));
        dialog.AddDialogAction("TransferSkip", () => Finish(TransferChoice.Skip));
        dialog.AddDialogAction("Cancel", () => dialog.Finish(false), isDefault: true).IsCancel = true;
        using CancellationTokenRegistration registration = question.Lifetime.Register(() => Dispatcher.BeginInvoke(() =>
        { if (dialog.IsVisible) dialog.Close(); }));
        if (!question.Lifetime.IsCancellationRequested) dialog.ShowDialog();
        return ValueTask.FromResult(question.Lifetime.IsCancellationRequested ? TransferChoice.Cancel : selected);

        void AddFile(string heading, TransferPath path, TransferAttributes attributes)
        {
            dialog.AddHeading(heading);
            dialog.AddField("TransferFilePath", new DialogText(files!.Display(path)));
            dialog.AddField("TransferSize", new DialogText(Size(attributes)) { IsTabStop = false });
            dialog.AddField("TransferModified", new DialogText(Modified(attributes)) { IsTabStop = false });
        }

        void Finish(TransferChoice choice)
        {
            selected = choice;
            dialog.Finish(true);
        }
    }

    private void StartCreate(Pane pane)
    {
        if (!CanMutate(pane) || pane.Directory is not { } parent) return;
        int revision = pane.Revision;
        string? name = PromptName("TransferNewFolder", "");
        if (name is null || !pane.IsListed || pane.Revision != revision) return;
        Observe(operations?.Create(parent, name));
    }

    private void StartRename(Pane pane, TransferItem[] selection)
    {
        if (!CanMutate(pane) || pane.Directory is not { } parent ||
            selection is not [{ Name: { } oldName } item]) return;
        int revision = pane.Revision;
        string? name = PromptName("TransferRename", oldName);
        if (name is null || name == oldName || !pane.IsListed || pane.Revision != revision) return;
        Observe(operations?.Rename(parent, item, name));
    }

    private void StartDelete(Pane pane, TransferItem[] selection)
    {
        if (!CanMutate(pane) || pane.Directory is not { } parent || selection.Length == 0) return;
        Observe(operations?.Delete(parent, selection, PresentQuestion));
    }

    private string? PromptName(string title, string initial)
    {
        var box = new TextBox { Text = initial };
        var dialog = new MainWindow.FormWindow(this, Text(title));
        dialog.AddField("TransferName", box);
        var accept = dialog.AddButtons(() => { }, acceptKey: title == "TransferNewFolder" ? "CreateTransferFolder" : title);
        accept.IsEnabled = box.Text.Length != 0;
        box.TextChanged += (_, _) => accept.IsEnabled = box.Text.Length != 0;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    private bool Confirm(string message, string action, CancellationToken token = default, FrameworkElement? context = null,
        string dismissKey = "Cancel")
    {
        if (token.IsCancellationRequested) return false;
        var answer = MessageBoxResult.No;
        Window dialog = ThemedMessageBox.Create(this, message, Text(action),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, value => answer = value,
            ThemedMessageBox.DecisionLabels(action, dismissKey), context: context);
        using var registration = token.Register(() => Dispatcher.BeginInvoke(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        }));
        dialog.ShowDialog();
        return !token.IsCancellationRequested && answer == MessageBoxResult.Yes;
    }

    public void ConnectionEnded()
    {
        if (disconnected) return;
        disconnected = true;
        clipboard = null;
        connection.Cancel();
        SetOperationStatus(Text("SftpDisconnected"));
        UpdateActions();
    }

    public Task<TransferWindowCloseResult> CloseWithTerminalAsync() =>
        closeRequest ??= CloseAfterReviewAsync();

    private async Task<TransferWindowCloseResult> CloseAfterReviewAsync()
    {
        if (closed) return new(TransferWindowCloseStatus.Dismissed, ownership.IsBusy);
        closing = true;
        // Publish the shared request before cancellation, modal reporting, or Close can reenter.
        await System.Windows.Threading.Dispatcher.Yield();
        try
        {
            ConnectionEnded();
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CheckOwnership() { if (!ownership.IsBusy) idle.TrySetResult(); }
            ownership.Changed += CheckOwnership;
            Task cleanup = Task.WhenAll(work, idle.Task);
            bool uncertain = false;
            try
            {
                CheckOwnership();
                await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException) { uncertain = !work.IsCompletedSuccessfully; }
            catch (Exception) when (cleanup.IsFaulted || cleanup.IsCanceled) { uncertain = true; }
            finally
            {
                ownership.Changed -= CheckOwnership;
                // After the visual deadline the original provider still owns its work.
                idle.TrySetResult();
                _ = cleanup.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            var unresolved = operations?.UnresolvedForClose() ?? [];
            if (uncertain || ownership.IsBusy || ownership.NeedsRefresh || unresolved.Length != 0)
            {
                string counts = string.Join("\n", unresolved.GroupBy(entry => (entry.Mutation, entry.Reason))
                    .Select(group => $"{ResultLabel(group.First())}  ({group.Count():N0})"));
                var entries = unresolved.Select(ResultDetails).ToArray();
                try
                {
                    StackPanel? context = null;
                    if (entries.Length != 0)
                    {
                        context = new StackPanel();
                        context.Children.Add(new DialogField(Text("TransferOutcomeCounts"), new DialogText(counts, 120)));
                        context.Children.Add(new DialogField(Text("TransferResults"), new DialogText(string.Join("\n", entries.Take(10)), 180)));
                    }
                    Window report = ThemedMessageBox.Create(this, Text("SftpUnknownOutcome"), Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, _ => { },
                        details: entries.Length > 10 ? string.Join("\n", entries) : null, context: context);
                    bool presented = false;
                    report.ContentRendered += (_, _) => presented = true;
                    ((App)Application.Current).Modal(report.ShowDialog);
                    if (!presented) return ReportFailed();
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return ReportFailed();
                }
            }

            dismissalApproved = true;
            try { Close(); }
            catch (InvalidOperationException) when (!closed) { }
            return new(closed ? TransferWindowCloseStatus.Dismissed : TransferWindowCloseStatus.DismissalRejected,
                ownership.IsBusy);
        }
        finally
        {
            if (!closed)
            {
                dismissalApproved = closing = false;
                closeRequest = null;
                UpdateActions();
            }
        }
    }

    private void SetOperationStatus(string message)
    {
        operationStatus = message;
        if (!closeReportFailed) status.Text = message;
    }

    private TransferWindowCloseResult ReportFailed()
    {
        closeReportFailed = true;
        status.Text = Text("SftpCloseReportFailed");
        return new(TransferWindowCloseStatus.ReportFailed, ownership.IsBusy);
    }

    private static string ResultDetails(FileOperationUpdate.Entry entry) =>
        entry.Path.Length == 0 ? Text(entry.Reason) : $"{ResultLabel(entry)}  {entry.DisplayPath}";

    private static string ResultLabel(FileOperationUpdate.Entry entry) => entry.Mutation is { } mutation
        ? $"{Text(mutation switch
        {
            FileMutation.CreateFolder => "TransferNewFolder",
            FileMutation.Rename => "TransferRename",
            FileMutation.Delete => "TransferDelete",
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        })}　{Text(entry.Reason)}"
        : Text(entry.Reason);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || dismissalApproved) return;
        if (closing) { e.Cancel = true; return; }
        if (!HasActiveOperation && !ownership.NeedsRefresh && work.IsCompletedSuccessfully &&
            (operations?.UnresolvedForClose().Length ?? 0) == 0) return;
        e.Cancel = true;
        if (!HasActiveOperation || Confirm(Text("TransferCloseBusy"), "StopWorkAndClose")) _ = CloseWithTerminalAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = closed = true;
        CloseDropMenu();
        clipboard = null;
        ownership.Changed -= OwnershipChanged;
        if (operations is not null) operations.Changed -= OperationChanged;
        connection.Cancel();
        client?.Dispose();
        base.OnClosed(e);
    }

    private static string Format(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);
    private static string Size(TransferAttributes attributes) => attributes.Size?.ToString("N0") ?? Text("TransferUnavailable");
    private static string Modified(TransferAttributes attributes) => attributes.Modified?.LocalDateTime.ToString("G") ?? Text("TransferUnavailable");

    private sealed class Pane(bool remote)
    {
        public bool Remote { get; } = remote;
        public TextBox Path { get; } = new() { Margin = new Thickness(0, 5, 0, 0), AllowDrop = false };
        public ListView List { get; } = new();
        public Button Parent { get; set; } = null!;
        public Button NewFolder { get; set; } = null!;
        public TransferPath? Directory;
        public string? ListedPath;
        public bool IsListed => ListedPath is not null && Load is null && Path.Text == ListedPath;
        public bool HasDirectory => IsListed && Directory is not null;
        public bool NoRemoteParent;
        public CancellationTokenSource? Load;
        public int Revision;
        public long? RefreshRevision;
    }

    private sealed record Row(TransferItem? Item, string? Drive = null)
    {
        private static readonly Geometry FolderIcon = Geometry.Parse("M 1,14 V 3 H 6 L 8,5 H 15 V 14 Z");
        private static readonly Geometry FileIcon = Geometry.Parse("M 2,5 L 6,1 H 14 V 15 H 2 Z M 2,5 H 6 V 1");
        private static readonly Geometry DriveIcon = Geometry.Parse(
            "M 3,4 A 5,2.5 0 0 1 13,4 A 5,2.5 0 0 1 3,4 M 3,4 V 12 A 5,2.5 0 0 0 13,12 V 4");

        public Geometry? Icon => Drive is not null ? DriveIcon : Item?.Attributes.Kind switch
        {
            TransferKind.Directory => FolderIcon,
            TransferKind.File => FileIcon,
            _ => null,
        };
        public string Name => Item switch
        {
            { Attributes.Kind: TransferKind.Directory } => Item.Label + "/",
            not null => Item.Label,
            _ => Drive!,
        };
        public string Size => Item is { Attributes.Kind: TransferKind.File } ? FileTransferWindow.Size(Item.Attributes) : "";
        public string Modified => Item is not null ? FileTransferWindow.Modified(Item.Attributes) : "";
    }

    private sealed record DragSelection(Pane Source, int Revision, TransferItem[] Items, bool ShowMenu)
    {
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public TransferPath? PreviewTarget { get; set; }
        public Pane? PreviewPane { get; set; }
        public int PreviewRevision { get; set; }
    }

    private sealed record FileClipboard(Pane Source, TransferItem[] Items, bool Cut);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(System.Drawing.Point point);
}
