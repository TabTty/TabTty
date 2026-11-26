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
    private readonly List<(Button Button, Pane Pane, string Key)> mutations = [];
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
        var commands = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(commands);
        upload = Button("TransferUpload", () => StartCopy(Selected(local), remote.Directory));
        download = Button("TransferDownload", () => StartCopy(Selected(remote), local.Directory));
        retry = Button("TransferRetry", () => Observe(operations?.Retry(PresentQuestion)));
        cancel = Button("Cancel", () => operations?.Cancel());
        foreach (Button control in new[] { upload, download, retry, cancel }) commands.Children.Add(control);

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement left = BuildPane(local, Text("TransferLocal"));
        FrameworkElement right = BuildPane(remote, session.FileTransferEndpoint);
        var divider = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(right, 2);
        panes.Children.Add(left);
        panes.Children.Add(divider);
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
        Button create = Button("TransferNewFolder", () => StartCreate(pane));
        Button rename = Button("TransferRename", () => StartRename(pane));
        Button delete = Button("TransferDelete", () => StartDelete(pane));
        mutations.AddRange([(create, pane, "TransferNewFolder"), (rename, pane, "TransferRename"), (delete, pane, "TransferDelete")]);
        actions.Children.Add(create);
        actions.Children.Add(rename);
        actions.Children.Add(delete);

        pane.List.SelectionMode = SelectionMode.Extended;
        pane.List.AllowDrop = true;
        var view = new GridView();
        AddColumn("TransferName", 220, nameof(Row.Name));
        AddColumn("TransferSize", 85, nameof(Row.Size));
        AddColumn("TransferModified", 145, nameof(Row.Modified));
        pane.List.View = view;
        AutomationProperties.SetName(pane.List, title);
        pane.List.SelectionChanged += (_, _) =>
        {
            if (activeDrag?.Source == pane) activeDrag = null;
            UpdateActions();
        };
        pane.List.MouseDoubleClick += (_, args) => OpenFolder(pane, RowAt(pane, args.OriginalSource));
        pane.List.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) OpenFolder(pane, pane.List.SelectedItem as Row);
            else if (args.Key == Key.F2) StartRename(pane);
            else if (args.Key == Key.Delete) StartDelete(pane);
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

    private Button Button(string key, Action action, bool track = true)
    {
        var button = new Button { Content = Text(key), Margin = new Thickness(0, 4, 6, 4), Padding = new Thickness(8, 3, 8, 3) };
        button.Click += (_, _) => action();
        if (track) labels.Add((button, key));
        return button;
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

    private static Row? RowAt(Pane pane, object origin) => origin is DependencyObject element &&
        ItemsControl.ContainerFromElement(pane.List, element) is ListViewItem row ? row.Content as Row : null;

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
        Row? row = RowAt(pane, args.OriginalSource);
        TransferPath target = row?.Item.Attributes.Kind == TransferKind.Directory ? row.Item.Path : pane.Directory;
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
            Highlight(args.OriginalSource is DependencyObject element ? ItemsControl.ContainerFromElement(pane.List, element) as ListViewItem : null);
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
        highlighted?.ClearValue(BackgroundProperty);
        highlighted = item;
        highlighted?.SetResourceReference(BackgroundProperty, SystemColors.ControlLightBrushKey);
    }

    private void StartCopy(TransferItem[] selection, TransferPath? target)
    {
        if (target is null || !CanCopy(selection.Length)) return;
        Observe(operations?.Copy(selection, target, PresentQuestion));
    }

    private bool IsConnected => files is not null && client?.IsOpen == true && !disconnected && !closing;
    private bool CanStart => IsConnected && operations?.CanStart == true;
    private bool CanCopy(int selected) => CanStart && local.IsListed && remote.IsListed && selected > 0;
    private bool CanMutate(Pane pane, string action) => CanStart && pane.IsListed && action switch
    {
        "TransferRename" => pane.List.SelectedItems.Count == 1 && pane.List.SelectedItem is Row { Item.Name: not null },
        "TransferDelete" => pane.List.SelectedItems.Count != 0,
        _ => true,
    };

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
        foreach (var (button, pane, key) in mutations)
            button.IsEnabled = CanMutate(pane, key);
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
            return ValueTask.FromResult(Confirm(Format("TransferDeletePermanent", files!.Display(question.Path)), question.Lifetime) ? TransferChoice.Overwrite : TransferChoice.Skip);
        if (question.Kind == TransferQuestionKind.Delete)
        {
            var items = question.Selection!;
            string names = string.Join('\n', items.Take(40).Select(item => item.Label));
            if (items.Count > 40) names += "\n" + Format("TransferMore", items.Count - 40);
            string key = question.Path.Remote is null ? "TransferDeleteLocal" : "TransferDeleteRemote";
            return ValueTask.FromResult(Confirm(Format(key, files!.Display(question.Path), names), question.Lifetime) ? TransferChoice.Overwrite : TransferChoice.Cancel);
        }
        TransferItem source = question.Source!;
        TransferPath target = question.Path;
        TransferAttributes existing = question.Existing!;
        string message = Format("TransferConflict", files!.Display(source.Path), Size(source.Attributes), Modified(source.Attributes),
            files.Display(target), Size(existing), Modified(existing));
        MessageBoxResult selected = MessageBoxResult.Cancel;
        Window dialog = ThemedMessageBox.Create(this, message, Text("FileTransfer"), MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel, value => selected = value,
            value => Text(value == MessageBoxResult.Yes ? "TransferOverwrite" : value == MessageBoxResult.No ? "TransferSkip" : "Cancel"));
        using CancellationTokenRegistration registration = question.Lifetime.Register(() => Dispatcher.BeginInvoke(() =>
        { if (dialog.IsVisible) dialog.Close(); }));
        dialog.ShowDialog();
        return ValueTask.FromResult(selected == MessageBoxResult.Yes ? TransferChoice.Overwrite : selected == MessageBoxResult.No ? TransferChoice.Skip : TransferChoice.Cancel);
    }

    private void StartCreate(Pane pane)
    {
        if (!CanMutate(pane, "TransferNewFolder") || pane.Directory is not { } parent) return;
        int revision = pane.Revision;
        string? name = PromptName("TransferNewFolder", "");
        if (name is null || !pane.IsListed || pane.Revision != revision) return;
        Observe(operations?.Create(parent, name));
    }

    private void StartRename(Pane pane)
    {
        if (!CanMutate(pane, "TransferRename") || pane.Directory is not { } parent ||
            Selected(pane) is not [{ Name: { } oldName } item]) return;
        int revision = pane.Revision;
        string? name = PromptName("TransferRename", oldName);
        if (name is null || name == oldName || !pane.IsListed || pane.Revision != revision) return;
        Observe(operations?.Rename(parent, item, name));
    }

    private void StartDelete(Pane pane)
    {
        if (!CanMutate(pane, "TransferDelete") || pane.Directory is not { } parent) return;
        TransferItem[] items = Selected(pane);
        Observe(operations?.Delete(parent, items, PresentQuestion));
    }

    private string? PromptName(string title, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        AutomationProperties.SetName(box, Text("TransferName"));
        var layout = new StackPanel { Margin = new Thickness(12) };
        var dialog = new Window { Owner = this, Title = Text(title), Icon = WindowIcons.App,
            Content = layout, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        layout.Children.Add(box);
        var commands = new WrapPanel();
        layout.Children.Add(commands);
        Button ok = Button("OK", () => { if (box.Text.Length != 0) dialog.DialogResult = true; }, track: false);
        ok.IsDefault = true;
        Button dismiss = Button("Cancel", () => dialog.Close(), track: false);
        dismiss.IsCancel = true;
        commands.Children.Add(ok);
        commands.Children.Add(dismiss);
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    private bool Confirm(string message, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return false;
        var answer = MessageBoxResult.No;
        Window dialog = ThemedMessageBox.Create(this, message, Title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, value => answer = value);
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
                string[] summary = [Text("SftpUnknownOutcome"), counts, .. entries.Take(10)];
                try
                {
                    Window report = ThemedMessageBox.Create(this,
                        string.Join("\n", summary.Where(line => line.Length != 0)),
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, _ => { },
                        details: entries.Length > 10 ? string.Join("\n", entries) : null);
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
        if (!HasActiveOperation || Confirm(Text("TransferCloseBusy"))) _ = CloseWithTerminalAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = closed = true;
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
}
