using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal enum TransferWindowCloseStatus { Dismissed, ReportFailed, DismissalRejected }

internal readonly record struct TransferWindowCloseResult(TransferWindowCloseStatus Status, bool ProviderPending)
{
    public bool Dismissed => Status == TransferWindowCloseStatus.Dismissed;
}

internal sealed class FileTransferWindow : Window
{
    private readonly TerminalSession session;
    private readonly CancellationTokenSource connection = new();
    private readonly Pane local = new(false), remote = new(true);
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
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
    private DragSelection? activeDrag;
    private ListViewItem? highlighted;
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
        upload = DirectionButton("TransferUpload", "＞", () => StartCopy(Selected(local), remote.Directory));
        download = DirectionButton("TransferDownload", "＜", () => StartCopy(Selected(remote), local.Directory));
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
            files = new TransferFiles(client, session.FileTransferWait, ownership,
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
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
        actions.Children.Add(Button("TransferParent", () => _ = BrowseAsync(pane, token =>
            pane.Directory is { } path ? files!.ParentAsync(path, token) : Task.FromResult<TransferPath>(null!))));
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
        AddColumn("TransferName", 220, nameof(Row.Name));
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
        pane.List.PreviewMouseLeftButtonDown += (_, args) =>
        {
            dragStart = args.GetPosition(pane.List);
            dragRow = RowAt(pane, args.OriginalSource);
        };
        pane.List.PreviewMouseMove += (_, args) => Drag(pane, args);
        pane.List.DragOver += (_, args) => HandleDrop(pane, args, commit: false);
        pane.List.Drop += (_, args) => HandleDrop(pane, args, commit: true);
        pane.List.DragLeave += (_, _) =>
        {
            if (activeDrag is { } drag) drag.PreviewTarget = null;
            Highlight(null);
        };
        layout.Children.Add(pane.List);
        return layout;

        void AddColumn(string key, double width, string property)
        {
            var column = new GridViewColumn { Header = Text(key), Width = width, DisplayMemberBinding = new Binding(property) };
            view.Columns.Add(column);
            columns.Add((column, key));
        }
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
            var origin = keyboard ? Keyboard.FocusedElement : args.OriginalSource;
            var row = origin is null ? null : RowContainerAt(pane, origin);
            var clicked = row?.Content as Row;
            if (keyboard) clicked ??= pane.List.SelectedItem as Row;
            onRow = clicked is not null;
            if (!pane.IsListed || !onRow && !CanPaste(pane))
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
        : Task.FromResult(false);

    private async Task<bool> BrowseAsync(Pane pane, Func<CancellationToken, Task<TransferPath>> target, bool preserveDraft = false)
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
            TransferPath path = await target(load.Token);
            if (path is null) return false;
            long? refreshRevision = ownership.RefreshRevision;
            TransferItem[] items = await files.ListAsync(path, load.Token);
            if (pane.Load != load || load.IsCancellationRequested) return false;
            Row[] rows = items.OrderByDescending(item => item.Attributes.Kind == TransferKind.Directory)
                .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).Select(item => new Row(item)).ToArray();
            pane.List.ItemsSource = rows;
            pane.Revision++;
            pane.Directory = path;
            pane.ListedPath = files.Display(path);
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
        if (row?.Item is { Name: not null, Attributes.Kind: TransferKind.Directory } item)
            _ = BrowseAsync(pane, _ => Task.FromResult(item.Path));
    }

    private static ListViewItem? RowContainerAt(Pane pane, object origin) => origin is DependencyObject element
        ? ItemsControl.ContainerFromElement(pane.List, element) as ListViewItem : null;

    private static Row? RowAt(Pane pane, object origin) => RowContainerAt(pane, origin)?.Content as Row;

    private static TransferItem[] Selected(Pane pane) => pane.List.SelectedItems.Cast<Row>().Select(row => row.Item).ToArray();

    private void Drag(Pane pane, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || dragRow is null) return;
        Point now = args.GetPosition(pane.List);
        if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        IDataObject? data = CreateDragData(pane.List, dragRow);
        dragRow = null;
        if (data is null) return;
        try { DragDrop.DoDragDrop(pane.List, data, DragDropEffects.Copy); }
        finally { activeDrag = null; Highlight(null); }
    }

    internal IDataObject? CreateDragData(ListView list, object clickedRow)
    {
        Pane pane = list == local.List ? local : remote;
        if (!CanCopy(1) || pane.List != list ||
            clickedRow is not Row row || !list.Items.Contains(row)) return null;
        TransferItem[] selection = Selected(pane);
        activeDrag = new DragSelection(pane, pane.Revision, selection.Contains(row.Item) ? selection : [row.Item]);
        return new DataObject(typeof(DragSelection), activeDrag);
    }

    private void HandleDrop(Pane pane, DragEventArgs args, bool commit)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (activeDrag is not { } drag || !CanCopy(drag.Items.Length) || pane.Directory is null ||
            !ReferenceEquals(args.Data.GetData(typeof(DragSelection)), drag) || drag.Source == pane || drag.Revision != drag.Source.Revision)
        {
            ClearDropPreview();
            return;
        }
        ListViewItem? container = RowContainerAt(pane, args.OriginalSource);
        var folder = container?.Content is Row { Item.Attributes.Kind: TransferKind.Directory } row ? row.Item : null;
        TransferPath target = folder?.Path ?? pane.Directory;
        if (commit && (drag.PreviewPane != pane || drag.PreviewRevision != pane.Revision || !ReferenceEquals(drag.PreviewTarget, target)))
        {
            ClearDropPreview();
            return;
        }
        args.Effects = DragDropEffects.Copy;
        if (commit) StartCopy(drag.Items, target);
        else
        {
            drag.PreviewTarget = target;
            drag.PreviewPane = pane;
            drag.PreviewRevision = pane.Revision;
            Highlight(folder is null ? null : container);
            SetOperationStatus(Format("TransferDropTarget", files!.Display(target)));
        }
    }

    private void ClearDropPreview()
    {
        if (activeDrag is { } drag) drag.PreviewTarget = null;
        Highlight(null);
    }

    private void Highlight(ListViewItem? item)
    {
        if (highlighted == item) return;
        highlighted?.ClearValue(BorderBrushProperty);
        highlighted = item;
        highlighted?.SetResourceReference(BorderBrushProperty, "ListViewItemPillFillBrush");
    }

    private void StartCopy(TransferItem[] selection, TransferPath? target)
    {
        if (target is null || !CanCopy(selection.Length)) return;
        Observe(operations?.Copy(selection, target, PresentQuestion));
    }

    private bool IsConnected => files is not null && client?.IsOpen == true && !disconnected && !closing;
    private bool CanStart => IsConnected && operations?.CanStart == true;
    private bool CanCopy(int selected) => CanStart && local.IsListed && remote.IsListed && selected > 0;
    private bool CanMutate(Pane pane) => CanStart && pane.IsListed;
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
        if (!CanCopy(activeDrag?.Items.Length ?? 0)) ClearDropPreview();
        upload.IsEnabled = CanCopy(local.List.SelectedItems.Count);
        download.IsEnabled = CanCopy(remote.List.SelectedItems.Count);
        retry.IsEnabled = CanStart && operations?.CanRetry == true;
        cancel.IsEnabled = operations?.IsRunning == true && !closing;
        local.NewFolder.IsEnabled = CanMutate(local);
        remote.NewFolder.IsEnabled = CanMutate(remote);
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
        public TextBox Path { get; } = new() { Margin = new Thickness(0, 5, 0, 0) };
        public ListView List { get; } = new();
        public Button NewFolder { get; set; } = null!;
        public TransferPath? Directory;
        public string? ListedPath;
        public bool IsListed => Directory is not null && Load is null && Path.Text == ListedPath;
        public CancellationTokenSource? Load;
        public int Revision;
        public long? RefreshRevision;
    }

    private sealed record Row(TransferItem Item)
    {
        public string Name => Item.Attributes.Kind == TransferKind.Directory ? Item.Label + "/" : Item.Label;
        public string Size => Item.Attributes.Kind == TransferKind.File ? FileTransferWindow.Size(Item.Attributes) : "";
        public string Modified => FileTransferWindow.Modified(Item.Attributes);
    }

    private sealed record DragSelection(Pane Source, int Revision, TransferItem[] Items)
    {
        public TransferPath? PreviewTarget { get; set; }
        public Pane? PreviewPane { get; set; }
        public int PreviewRevision { get; set; }
    }

    private sealed record FileClipboard(Pane Source, TransferItem[] Items, bool Cut);
}
