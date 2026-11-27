using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static Window? interruptTransferReportFor;

    private static async Task CheckTransferWindowClose(string directory, int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string profilePath = Path.Combine(directory, "transfer-close.ttty");
        await ConnectionProfile.CreateSsh("Transfer close", "127.0.0.1", "tabtty-sftp", port).SaveNewAsync(profilePath, deadline.Token);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        await using var session = await TerminalSession.ConnectAsync(profilePath, deadline.Token, interaction);
        using var client = await session.OpenFileTransferAsync(deadline.Token);
        var files = new TransferFiles(client, session.FileTransferWait);
        TransferPath remote = await files.ResolveAsync(true, ".", null, deadline.Token);
        if (remote.Remote is null)
            throw new InvalidOperationException("The transfer fixture did not open a real SFTP directory.");

        var idle = new FileOperationOwnership();
        var quietWindow = OpenDisconnected(idle);
        bool veto = true;
        void CheckDismissal(object? _, CancelEventArgs args) => args.Cancel = veto;
        quietWindow.Closing += CheckDismissal;
        try
        {
            var first = quietWindow.CloseWithTerminalAsync();
            if (!ReferenceEquals(first, quietWindow.CloseWithTerminalAsync()))
                throw new InvalidOperationException("Repeated terminal closes did not join the active transfer close.");
            if ((await first.WaitAsync(deadline.Token)).Status != TransferWindowCloseStatus.DismissalRejected || !quietWindow.IsVisible)
                throw new InvalidOperationException("A cancelled WPF dismissal was reported as a closed transfer window.");
            veto = false;
            if (!(await quietWindow.CloseWithTerminalAsync().WaitAsync(deadline.Token)).Dismissed || quietWindow.IsVisible)
                throw new InvalidOperationException("An idle transfer window did not close after the veto was removed.");
        }
        finally
        {
            quietWindow.Closing -= CheckDismissal;
            if (quietWindow.IsVisible) quietWindow.Close();
        }

        var owner = new FileOperationOwnership();
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Track(provider.Task);
        var pendingWindow = OpenDisconnected(owner);
        bool sawReport = false;
        var acknowledge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        acknowledge.Tick += (_, _) =>
        {
            Window? report = pendingWindow.OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible);
            if (report is null) return;
            sawReport = FindControls<TextBlock>(report).Any(text => text.Text.StartsWith(App.Text("SftpUnknownOutcome"), StringComparison.Ordinal));
            report.Close();
        };
        acknowledge.Start();
        try
        {
            var close = pendingWindow.CloseWithTerminalAsync();
            if (close.IsCompleted || !ReferenceEquals(close, pendingWindow.CloseWithTerminalAsync()))
                throw new InvalidOperationException("Pending transfer cleanup did not have one shared close operation.");
            // A second click on the title bar must not bypass the report.
            pendingWindow.Close();
            if (!pendingWindow.IsVisible)
                throw new InvalidOperationException("A title-bar close bypassed pending transfer cleanup.");
            TransferWindowCloseResult result = await close.WaitAsync(deadline.Token);
            if (!sawReport || !result.Dismissed || !result.ProviderPending || !owner.IsBusy || pendingWindow.IsVisible)
                throw new InvalidOperationException("Dismissal failed to report uncertainty or released the provider too early.");
            provider.SetResult();
            while (owner.IsBusy) await Task.Delay(10, deadline.Token);
        }
        finally
        {
            acknowledge.Stop();
            provider.TrySetResult();
            if (pendingWindow.IsVisible) pendingWindow.Close();
        }

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window report && interruptTransferReportFor is { } ownerWindow && report.Owner == ownerWindow)
                    report.Close();
            }));
        var unresolvedOwner = new FileOperationOwnership();
        unresolvedOwner.RequireRefresh();
        var unresolvedWindow = OpenDisconnected(unresolvedOwner);
        var lateWork = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(FileTransferWindow).GetMethod("Observe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(unresolvedWindow, [lateWork.Task]);
        interruptTransferReportFor = unresolvedWindow;
        try
        {
            unresolvedWindow.Close();
            TransferWindowCloseResult result = await unresolvedWindow.CloseWithTerminalAsync().WaitAsync(deadline.Token);
            if (result.Status != TransferWindowCloseStatus.ReportFailed || !unresolvedWindow.IsVisible)
                throw new InvalidOperationException("A report closed before rendering must leave the transfer results visible.");
            if (!FindControls<TextBlock>(unresolvedWindow).Any(label => label.Text == App.Text("SftpCloseReportFailed")))
                throw new InvalidOperationException("A report presentation failure did not explain why closing stopped.");
            lateWork.TrySetResult("SftpDisconnected");
            await unresolvedWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!FindControls<TextBlock>(unresolvedWindow).Any(label => label.Text == App.Text("SftpCloseReportFailed")))
                throw new InvalidOperationException("Late operation cleanup hid the reason the transfer window stayed open.");
            interruptTransferReportFor = null;
            unresolvedOwner.Refreshed();
            if (!(await unresolvedWindow.CloseWithTerminalAsync().WaitAsync(deadline.Token)).Dismissed)
                throw new InvalidOperationException("The transfer window could not be closed after late cleanup.");
        }
        finally
        {
            lateWork.TrySetResult("SftpDisconnected");
            interruptTransferReportFor = null;
            unresolvedOwner.Refreshed();
            if (unresolvedWindow.IsVisible) unresolvedWindow.Close();
        }

        var app = (App)Application.Current;
        bool previousAutoClose = app.AutoCloseDisconnectedTabs;
        try
        {
            await CheckDisconnectedTransferAsync(autoClose: false);
            await CheckDisconnectedTransferAsync(autoClose: true);
        }
        finally { await SetAutoClose(previousAutoClose); }

        async Task CheckDisconnectedTransferAsync(bool autoClose)
        {
            await SetAutoClose(autoClose);
            var host = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
            host.OpenForm("Settings", _ => { });
            var tab = new TerminalTab(profilePath, interaction);
            host.AddTerminalTab(tab);
            host.Show();
            var tabs = (TabControl)host.FindName("tabs");
            var item = (TabItem)tabs.SelectedItem;
            tab.Start();
            try
            {
                while (!tab.CanTransfer) await Task.Delay(10, deadline.Token);
                tab.OpenFileTransfer();
                var transfer = tab.TransferWindow ?? throw new InvalidOperationException("The SSH tab did not create a transfer window.");
                if (!transfer.IsVisible) throw new InvalidOperationException("The transfer window is not visible.");
                transfer.WindowState = WindowState.Minimized;
                tab.OpenFileTransfer();
                if (!ReferenceEquals(tab.TransferWindow, transfer) || transfer.WindowState == WindowState.Minimized ||
                    Application.Current.Windows.OfType<FileTransferWindow>().Count(window => window.IsVisible) != 1)
                    throw new InvalidOperationException("Reopening the transfer created another window or left the existing one minimized.");
                if (!autoClose) await CheckListedPathsAsync(transfer);
                tab.Disconnect();
                await tab.Completion.WaitAsync(deadline.Token);
                if (!autoClose)
                {
                    if (!tabs.Items.Contains(item) || !transfer.IsVisible)
                        throw new InvalidOperationException("A retained disconnected tab lost its transfer results.");
                    host.UpdateLayout();
                    var strip = (Border)tabs.Template.FindName("tabStripRow", tabs);
                    var bulk = (MenuItem)strip.ContextMenu.Items[0];
                    bool dismissalAttempted = false;
                    void RejectDismissal(object? _, CancelEventArgs args)
                    {
                        dismissalAttempted = true;
                        args.Cancel = true;
                    }
                    transfer.Closing += RejectDismissal;
                    try
                    {
                        bulk.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        while (!dismissalAttempted || tab.ClosePending) await Task.Delay(10, deadline.Token);
                        if (!tabs.Items.Contains(item) || !transfer.IsVisible || tab.AiSession is not { } retained)
                            throw new InvalidOperationException("Failed transfer dismissal removed its terminal or retained results.");
                        _ = await retained.GetOutputAsync(deadline.Token);
                    }
                    finally { transfer.Closing -= RejectDismissal; }
                    bulk.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                while (tabs.Items.Contains(item) || transfer.IsVisible) await Task.Delay(10, deadline.Token);
                if (!host.IsVisible || tabs.Items.Count != 1)
                    throw new InvalidOperationException("Closing a disconnected tab affected its settings tab.");
            }
            finally
            {
                tab.Disconnect();
                await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                if (host.IsVisible) host.Close();
            }
        }

        async Task CheckListedPathsAsync(FileTransferWindow transfer)
        {
            transfer.UpdateLayout();
            var editors = FindControls<TextBox>(transfer).ToArray();
            var lists = FindControls<ListView>(transfer).ToArray();
            var localList = lists[0];
            var remoteList = lists[1];
            var buttons = FindControls<Button>(transfer).ToArray();
            var upload = buttons.Single(button => AutomationProperties.GetName(button) == App.Text("TransferUpload"));
            var download = buttons.Single(button => AutomationProperties.GetName(button) == App.Text("TransferDownload"));
            var create = buttons.Where(button => Equals(button.Content, App.Text("TransferNewFolder"))).ToArray();
            var retry = buttons.Single(button => Equals(button.Content, App.Text("TransferRetry")));
            var cancel = buttons.Single(button => Equals(button.Content, App.Text("Cancel")));
            var layout = (FrameworkElement)transfer.Content;
            if (!Equals(upload.Content, "＞") || !Equals(download.Content, "＜") ||
                upload.TranslatePoint(new Point(), layout).X < localList.TranslatePoint(new Point(localList.ActualWidth, 0), layout).X ||
                download.TranslatePoint(new Point(download.ActualWidth, 0), layout).X > remoteList.TranslatePoint(new Point(), layout).X ||
                !ReferenceEquals(retry.Parent, cancel.Parent) || Math.Abs(cancel.TranslatePoint(new Point(cancel.ActualWidth, 0), layout).X - layout.ActualWidth) > 1 ||
                buttons.Any(button => Equals(button.Content, App.Text("TransferRename")) || Equals(button.Content, App.Text("TransferDelete"))))
                throw new InvalidOperationException("The transfer controls did not use the center arrows, right-aligned footer, and row menus.");
            if (Math.Abs(upload.ActualWidth - upload.ActualHeight) > 1 ||
                Math.Abs(download.ActualWidth - download.ActualHeight) > 1)
                throw new InvalidOperationException("The transfer direction buttons were not square.");
            while (!create[1].IsEnabled) await Task.Delay(10, deadline.Token);
            editors[0].Text = directory;
            Press(editors[0], Key.Enter);
            while (!localList.IsEnabled || localList.Items.Count == 0) await Task.Delay(10, deadline.Token);
            localList.SelectedIndex = 0;
            if (!upload.IsEnabled || !await CanDeleteSelectedAsync(localList))
                throw new InvalidOperationException("The listed directory and its selection did not enable file actions.");
            string listedRemote = editors[1].Text;
            editors[1].Text = listedRemote + "/another-folder";
            if (upload.IsEnabled || create[1].IsEnabled || !await CanDeleteSelectedAsync(localList))
                throw new InvalidOperationException("A pending destination still allowed an upload or remote folder creation.");
            Press(editors[1], Key.Escape);
            if (editors[1].Text != listedRemote || !upload.IsEnabled)
                throw new InvalidOperationException("Restoring the listed destination did not restore the transfer action.");
            string missing = Path.Combine(directory, "missing-folder");
            editors[0].Text = missing;
            Press(editors[0], Key.Enter);
            while (!FindControls<TextBlock>(transfer).Any(text => text.Text == App.Text("SftpServerError")))
                await Task.Delay(10, deadline.Token);
            if (editors[0].Text != missing || localList.IsEnabled || upload.IsEnabled || await CanDeleteSelectedAsync(localList))
                throw new InvalidOperationException("Failed navigation still exposed operations on the previous directory.");
            Press(editors[0], Key.Escape);
            if (editors[0].Text != directory || !upload.IsEnabled || !await CanDeleteSelectedAsync(localList))
                throw new InvalidOperationException("The former listing did not resume after cancelling failed navigation.");
            Console.WriteLine("PASS SftpOperationsRequireListedPaths");

            string dropSource = Directory.CreateDirectory(Path.Combine(directory, "drop-source")).FullName;
            Directory.CreateDirectory(Path.Combine(dropSource, "inbox"));
            await File.WriteAllTextAsync(Path.Combine(dropSource, "note.txt"), "SFTP drop fixture", deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(dropSource, "other.txt"), "SFTP drop onto a file", deadline.Token);
            editors[0].Text = dropSource;
            Press(editors[0], Key.Enter);
            while (!localList.IsEnabled || localList.Items.Count != 3) await Task.Delay(10, deadline.Token);
            localList.SelectedIndex = 0; // Folders precede files in both panes.
            upload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!remoteList.IsEnabled || remoteList.Items.Count != 1 || !create[1].IsEnabled)
                await Task.Delay(10, deadline.Token);
            transfer.UpdateLayout();
            var folderRow = (ListViewItem)remoteList.ItemContainerGenerator.ContainerFromIndex(0);
            double folderHeight = folderRow.ActualHeight;
            if (folderRow.MinHeight != 24)
                throw new InvalidOperationException("File transfer did not use the compact management row height.");
            localList.SelectedIndex = 1;
            var drag = StartFileDrag();
            long revision = transfer.OperationRevision;
            if (RouteDrag(remoteList, DragDrop.DragOverEvent, drag) != DragDropEffects.Copy ||
                RouteDrag(folderRow, DragDrop.DropEvent, drag) != DragDropEffects.None || transfer.OperationRevision != revision)
                throw new InvalidOperationException("A drop on a folder absent from the last preview was accepted.");
            drag = StartFileDrag();
#pragma warning disable WPF0001 // Confirm the drop outline in both Fluent themes.
            var app = (App)Application.Current;
            var previousTheme = app.ThemeMode;
            try
            {
                foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
                {
                    app.ThemeMode = theme;
                    transfer.UpdateLayout();
                    folderRow = (ListViewItem)remoteList.ItemContainerGenerator.ContainerFromIndex(0);
                    drag = StartFileDrag();
                    var background = folderRow.Background;
                    var foreground = folderRow.Foreground;
                    var effect = RouteDrag(folderRow, DragDrop.DragOverEvent, drag);
                    object outline = folderRow.TryFindResource("ListViewItemPillFillBrush");
                    if (effect != DragDropEffects.Copy ||
                        !Equals(folderRow.Background, background) || !Equals(folderRow.Foreground, foreground) ||
                        outline is not System.Windows.Media.Brush ||
                        !Equals(folderRow.BorderBrush, outline))
                        throw new InvalidOperationException($"Drop preview in {theme} mode: effect {effect}, outline {folderRow.BorderBrush} (expected {outline}), " +
                            $"background {background} → {folderRow.Background}, foreground {foreground} → {folderRow.Foreground}.");
                    RouteDrag(folderRow, DragDrop.DragLeaveEvent, drag);
                    if (folderRow.ReadLocalValue(Control.BorderBrushProperty) != DependencyProperty.UnsetValue)
                        throw new InvalidOperationException("Leaving the folder did not clear its drop outline.");
                }
            }
            finally { app.ThemeMode = previousTheme; }
#pragma warning restore WPF0001
            transfer.UpdateLayout();
            folderRow = (ListViewItem)remoteList.ItemContainerGenerator.ContainerFromIndex(0);
            drag = StartFileDrag();
            if (RouteDrag(folderRow, DragDrop.DragOverEvent, drag) != DragDropEffects.Copy ||
                folderRow.ReadLocalValue(Control.BorderBrushProperty) == DependencyProperty.UnsetValue ||
                !FindControls<TextBlock>(transfer).Any(text => text.Text.Contains("/home/inbox", StringComparison.Ordinal)) ||
                RouteDrag(folderRow, DragDrop.DropEvent, drag) != DragDropEffects.Copy || transfer.OperationRevision == revision)
                throw new InvalidOperationException("The folder shown by the drop preview did not accept the source file.");
            while (!remoteList.IsEnabled || !create[1].IsEnabled) await Task.Delay(10, deadline.Token);
            remoteList.SelectedIndex = 0;
            Press(remoteList, Key.Enter);
            while (!remoteList.IsEnabled || editors[1].Text != "/home/inbox") await Task.Delay(10, deadline.Token);
            remoteList.SelectedValuePath = "Name";
            remoteList.SelectedIndex = 0;
            if (remoteList.Items.Count != 1 || !Equals(remoteList.SelectedValue, "note.txt"))
                throw new InvalidOperationException("The SFTP server did not list note.txt in the previewed folder.");
            Console.WriteLine("PASS SftpDropRequiresPreviewedFolder");

            _ = transfer.Dispatcher.BeginInvoke(() => create[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            var nameDialog = await WaitForTransferDialogAsync("TransferNewFolder");
            try
            {
                var nameField = FindControls<TextBox>(nameDialog).Single(field => !field.IsReadOnly);
                if (AutomationProperties.GetLabeledBy(nameField) is not Label { Content: TextBlock nameLabel } ||
                    nameLabel.Text != App.Text("TransferName") ||
                    !FindControls<Button>(nameDialog).Any(button => Equals(button.Content, App.Text("CreateTransferFolder")) && !button.IsEnabled))
                    throw new InvalidOperationException("The name dialog did not expose its field and keep Create unavailable for an empty name.");
                if (!FindControls<Button>(nameDialog).Any(button => Equals(button.Content, App.Text("Cancel")) && button.IsCancel))
                    throw new InvalidOperationException("The name dialog had no cancellation action.");
            }
            finally { nameDialog.Close(); }

            await WaitForListsAsync(() => true);
            localList.SelectedValuePath = "Name";
            localList.SelectedValue = "note.txt";
            if (!upload.IsEnabled || !Equals(localList.SelectedValue, "note.txt"))
                throw new InvalidOperationException("The duplicate upload fixture did not select note.txt.");
            _ = transfer.Dispatcher.BeginInvoke(() => upload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            var overwrite = await WaitForTransferDialogAsync("TransferOverwrite");
            try
            {
                overwrite.Width = 300;
                overwrite.MaxHeight = 250;
                overwrite.UpdateLayout();
                var values = FindControls<DialogText>(overwrite).ToArray();
                var skip = FindControls<Button>(overwrite).Single(button => Equals(button.Content, App.Text("TransferSkip")));
                if (values.Length != 6 || !values[0].Text.EndsWith("note.txt", StringComparison.Ordinal) ||
                    values[3].Text != "/home/inbox/note.txt" || !FindControls<Button>(overwrite).Single(button => button.IsDefault).IsCancel ||
                    ((FormView)overwrite.Content).Body.IsAncestorOf(skip))
                    throw new InvalidOperationException("The overwrite dialog did not keep the comparison and safe action accessible.");
                skip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { if (overwrite.IsVisible) overwrite.Close(); }
            await WaitForListsAsync(() => remoteList.Items.Count == 1);
            Console.WriteLine("PASS SftpNameAndOverwriteUseSharedDialogs");

            transfer.UpdateLayout();
            var fileRow = (ListViewItem)remoteList.ItemContainerGenerator.ContainerFromIndex(0);
            if (fileRow.ActualHeight != folderHeight)
                throw new InvalidOperationException("Files and folders have different transfer row heights.");
            localList.SelectedIndex = 2;
            drag = StartFileDrag();
            if (RouteDrag(fileRow, DragDrop.DragOverEvent, drag) != DragDropEffects.Copy ||
                fileRow.ReadLocalValue(Control.BorderBrushProperty) != DependencyProperty.UnsetValue ||
                RouteDrag(fileRow, DragDrop.DropEvent, drag) != DragDropEffects.Copy)
                throw new InvalidOperationException("Dropping on a file must target the displayed folder without highlighting the file.");
            while (!remoteList.IsEnabled || remoteList.Items.Count != 2 || !create[1].IsEnabled)
                await Task.Delay(10, deadline.Token);
            remoteList.SelectedIndex = 1;
            if (!Equals(remoteList.SelectedValue, "other.txt"))
                throw new InvalidOperationException("Dropping on the file did not copy into its displayed folder.");

            string copySource = Path.Combine(dropSource, "clipboard.txt");
            string moveSource = Directory.CreateDirectory(Path.Combine(dropSource, "clipboard-folder")).FullName;
            await File.WriteAllTextAsync(copySource, "clipboard copy", deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(moveSource, "move.txt"), "clipboard move", deadline.Token);
            Press(editors[0], Key.Enter);
            await WaitForListsAsync(() => localList.Items.Count == 5);
            localList.SelectedValuePath = "Name";
            localList.SelectedValue = "note.txt";
            localList.SelectedItems.Add(localList.Items.Cast<object>().Single(row => RowName(row) == "other.txt"));
            transfer.UpdateLayout();
            var selectedRow = (ListViewItem)localList.ItemContainerGenerator.ContainerFromItem(localList.SelectedItem);
            if (!await OpenFileMenuAsync(localList, selectedRow) || localList.SelectedItems.Count != 2 ||
                MenuCommand(localList, "TransferRename").IsEnabled || !MenuCommand(localList, "TransferDelete").IsEnabled)
                throw new InvalidOperationException("A row menu did not retain a multiple selection and restrict Rename.");
            localList.ContextMenu.IsOpen = false;
            var copyRow = (ListViewItem)localList.ItemContainerGenerator.ContainerFromItem(
                localList.Items.Cast<object>().Single(row => RowName(row) == "clipboard.txt"));
            if (!await OpenFileMenuAsync(localList, copyRow) || localList.SelectedItems.Count != 1 ||
                !Equals(localList.SelectedValue, "clipboard.txt") || !MenuCommand(localList, "TransferRename").IsEnabled)
                throw new InvalidOperationException("The row menu did not select the item under the pointer.");
            InvokeMenu(localList, "Copy");
            if (ApplicationCommands.Paste.CanExecute(null, localList) || !ApplicationCommands.Paste.CanExecute(null, remoteList))
                throw new InvalidOperationException("A captured file was not restricted to cross-pane paste.");
            ApplicationCommands.Paste.Execute(null, remoteList);
            await WaitForListsAsync(() => remoteList.Items.Count == 3);
            if (!File.Exists(copySource) || !HasRow(remoteList, "clipboard.txt"))
                throw new InvalidOperationException("Copy and Paste did not leave the original after transferring.");

            localList.SelectedValue = "clipboard-folder/";
            if (!ApplicationCommands.Cut.CanExecute(null, localList))
                throw new InvalidOperationException("The selected folder did not offer the cut shortcut.");
            ApplicationCommands.Cut.Execute(null, localList);
            if (!Directory.Exists(moveSource) || !await OpenFileMenuAsync(remoteList, remoteList) ||
                !MenuCommand(remoteList, "Paste").IsEnabled || MenuCommand(remoteList, "TransferDelete").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Cut removed its source too soon or blank-space Paste exposed row commands.");
            InvokeMenu(remoteList, "Paste");
            await WaitForListsAsync(() => remoteList.Items.Count == 4);
            if (Directory.Exists(moveSource) || !HasRow(remoteList, "clipboard-folder/"))
                throw new InvalidOperationException("Cross-pane cut did not remove the transferred source folder.");

            remoteList.SelectedValue = "clipboard.txt";
            ApplicationCommands.Copy.Execute(null, remoteList);
            if (!ApplicationCommands.Paste.CanExecute(null, localList))
                throw new InvalidOperationException("The copy shortcut did not capture a remote item.");
            ApplicationCommands.Cut.Execute(null, remoteList);
            string downloadTarget = Directory.CreateDirectory(Path.Combine(directory, "clipboard-download")).FullName;
            editors[0].Text = downloadTarget;
            Press(editors[0], Key.Enter);
            await WaitForListsAsync(() => editors[0].Text == downloadTarget && localList.Items.Count == 0);
            ApplicationCommands.Paste.Execute(null, localList);
            await WaitForListsAsync(() => localList.Items.Count == 1);
            if (HasRow(remoteList, "clipboard.txt") ||
                await File.ReadAllTextAsync(Path.Combine(downloadTarget, "clipboard.txt"), deadline.Token) != "clipboard copy")
                throw new InvalidOperationException("Cut and Paste did not move the remote file to the newly listed local folder.");
            Console.WriteLine("PASS SftpClipboardCopiesAndMovesAcrossPanes");

            async Task WaitForListsAsync(Func<bool> condition)
            {
                while (!localList.IsEnabled || !remoteList.IsEnabled || !create.All(button => button.IsEnabled) || !condition())
                    await Task.Delay(10, deadline.Token);
            }

            async Task<Window> WaitForTransferDialogAsync(string title)
            {
                for (;;)
                {
                    if (transfer.OwnedWindows.Cast<Window>().FirstOrDefault(window =>
                        window.IsVisible && window.Title == App.Text(title)) is { } dialog) return dialog;
                    await Task.Delay(10, deadline.Token);
                }
            }

            static bool HasRow(ListView list, string name) => list.Items.Cast<object>().Any(row => RowName(row) == name);
            static string? RowName(object row) => System.ComponentModel.TypeDescriptor.GetProperties(row)["Name"]?.GetValue(row) as string;

            static void InvokeMenu(ListView list, string key)
            {
                try { MenuCommand(list, key).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
                finally { list.ContextMenu.IsOpen = false; }
            }

            async Task<bool> CanDeleteSelectedAsync(ListView list)
            {
                var menu = list.ContextMenu;
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is not ListViewItem row ||
                    !await OpenFileMenuAsync(list, row)) return false;
                try { return MenuCommand(list, "TransferDelete").IsEnabled; }
                finally { menu.IsOpen = false; }
            }

            async Task<bool> OpenFileMenuAsync(ListView list, UIElement origin)
            {
                var opening = CreateContextMenuEvent(origin, true, 1, 1);
                origin.RaiseEvent(opening);
                if (opening.Handled) return false;
                list.ContextMenu.PlacementTarget = list;
                list.ContextMenu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                return list.ContextMenu.IsOpen;
            }

            static MenuItem MenuCommand(ListView list, string key) => list.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, App.Text(key)));

            IDataObject StartFileDrag() => transfer.CreateDragData(localList, localList.SelectedItem)
                ?? throw new InvalidOperationException("The selected local file could not start a drag.");

            static DragDropEffects RouteDrag(UIElement origin, RoutedEvent routedEvent, IDataObject data)
            {
                var args = CreateDragEvent(data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, origin, new Point());
                args.RoutedEvent = routedEvent;
                origin.RaiseEvent(args);
                return args.Effects;
            }

            void Press(UIElement editor, Key key) => editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(transfer), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }

        Task SetAutoClose(bool autoClose) => app.SaveSettingsAsync(app.SettingsRevision, new(AutoClose: autoClose));

        FileTransferWindow OpenDisconnected(FileOperationOwnership ownership)
        {
            var window = new FileTransferWindow(session, ownership);
            window.ConnectionEnded();
            window.Show();
            return window;
        }
    }

    // WPF constructs these routed event arguments inside its native OLE drag loop.
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern DragEventArgs CreateDragEvent(IDataObject data, DragDropKeyStates keys,
        DragDropEffects allowedEffects, DependencyObject target, Point position);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ContextMenuEventArgs CreateContextMenuEvent(object source, bool opening, double left, double top);
}
