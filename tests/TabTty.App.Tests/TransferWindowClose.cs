using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        FileTransferWindow OpenDisconnected(FileOperationOwnership ownership)
        {
            var window = new FileTransferWindow(session, ownership);
            window.ConnectionEnded();
            window.Show();
            return window;
        }
    }
}
