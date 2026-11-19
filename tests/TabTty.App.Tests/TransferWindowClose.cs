using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
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
        interruptTransferReportFor = unresolvedWindow;
        try
        {
            TransferWindowCloseResult result = await unresolvedWindow.CloseWithTerminalAsync().WaitAsync(deadline.Token);
            if (result.Status != TransferWindowCloseStatus.ReportFailed || !unresolvedWindow.IsVisible)
                throw new InvalidOperationException("A report closed before rendering must leave the transfer results visible.");
        }
        finally
        {
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
            var localList = FindControls<ListView>(transfer).First();
            var upload = FindControls<Button>(transfer).Single(button => Equals(button.Content, App.Text("TransferUpload")));
            var create = FindControls<Button>(transfer).Where(button => Equals(button.Content, App.Text("TransferNewFolder"))).ToArray();
            var delete = FindControls<Button>(transfer).First(button => Equals(button.Content, App.Text("TransferDelete")));
            while (!create[1].IsEnabled) await Task.Delay(10, deadline.Token);
            editors[0].Text = directory;
            Press(editors[0], Key.Enter);
            while (!localList.IsEnabled || localList.Items.Count == 0) await Task.Delay(10, deadline.Token);
            localList.SelectedIndex = 0;
            if (!upload.IsEnabled || !delete.IsEnabled)
                throw new InvalidOperationException("The listed directory and its selection did not enable file actions.");
            string listedRemote = editors[1].Text;
            editors[1].Text = listedRemote + "/another-folder";
            if (upload.IsEnabled || create[1].IsEnabled || !delete.IsEnabled)
                throw new InvalidOperationException("A pending destination still allowed an upload or remote folder creation.");
            Press(editors[1], Key.Escape);
            if (editors[1].Text != listedRemote || !upload.IsEnabled)
                throw new InvalidOperationException("Restoring the listed destination did not restore the transfer action.");
            string missing = Path.Combine(directory, "missing-folder");
            editors[0].Text = missing;
            Press(editors[0], Key.Enter);
            while (!FindControls<TextBlock>(transfer).Any(text => text.Text == App.Text("SftpServerError")))
                await Task.Delay(10, deadline.Token);
            if (editors[0].Text != missing || localList.IsEnabled || upload.IsEnabled || delete.IsEnabled)
                throw new InvalidOperationException("Failed navigation still exposed operations on the previous directory.");
            Press(editors[0], Key.Escape);
            if (editors[0].Text != directory || !upload.IsEnabled || !delete.IsEnabled)
                throw new InvalidOperationException("The former listing did not resume after cancelling failed navigation.");
            Console.WriteLine("PASS SftpOperationsRequireListedPaths");

            string dropSource = Directory.CreateDirectory(Path.Combine(directory, "drop-source")).FullName;
            Directory.CreateDirectory(Path.Combine(dropSource, "inbox"));
            await File.WriteAllTextAsync(Path.Combine(dropSource, "note.txt"), "SFTP drop fixture", deadline.Token);
            editors[0].Text = dropSource;
            Press(editors[0], Key.Enter);
            while (!localList.IsEnabled || localList.Items.Count != 2) await Task.Delay(10, deadline.Token);
            var remoteList = FindControls<ListView>(transfer).Last();
            localList.SelectedIndex = 0; // Folders precede files in both panes.
            upload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!remoteList.IsEnabled || remoteList.Items.Count != 1 || !create[1].IsEnabled)
                await Task.Delay(10, deadline.Token);
            transfer.UpdateLayout();
            var folderRow = (ListViewItem)remoteList.ItemContainerGenerator.ContainerFromIndex(0);
            localList.SelectedIndex = 1;
            var drag = StartFileDrag();
            long revision = transfer.OperationRevision;
            if (RouteDrag(remoteList, DragDrop.DragOverEvent, drag) != DragDropEffects.Copy ||
                RouteDrag(folderRow, DragDrop.DropEvent, drag) != DragDropEffects.None || transfer.OperationRevision != revision)
                throw new InvalidOperationException("A drop on a folder absent from the last preview was accepted.");
            drag = StartFileDrag();
            if (RouteDrag(folderRow, DragDrop.DragOverEvent, drag) != DragDropEffects.Copy ||
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

        Task SetAutoClose(bool autoClose) => app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, autoClose, app.VaultPath, app.SettingsRevision);

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
}
