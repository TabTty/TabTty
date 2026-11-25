using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckRecordingNoticesAsync(App app, string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string path = Path.Combine(directory, "recording-notices.ttty");
        await ConnectionProfile.CreateTelnet("Recording", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            recordRaw: true).SaveNewAsync(path, timeout.Token);
        var tab = new TerminalTab(path);
        var host = new MainWindow(app);
        host.Show();
        host.AddTerminalTab(tab);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Connected += _ => connected.TrySetResult();
        try
        {
            tab.Start();
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await peer.GetStream().ReadExactlyAsync(new byte[15], timeout.Token);
            await connected.Task.WaitAsync(timeout.Token);
            var pane = FindControls<TerminalSurface>(tab).Single();
            await tab.ChangeRecordingAsync(false);
            SendText(pane, "x");
            await peer.GetStream().ReadExactlyAsync(new byte[1], timeout.Token);
            await Idle();
            CheckStatus("NoticeRecordingPaused", 1);
            await tab.ChangeRecordingAsync(true);

            // Inject a writer failure on the real session while the local terminal and peer are idle.
            var lifecycle = (RecordingLifecycle)typeof(TerminalSession).GetField("recordings",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab.InputSession)!;
            lifecycle.MarkIncomplete("Writer failed.");
            while (!tab.Reports.HasReports) await Idle();
            CheckStatus("NoticeRecordingStopped", 2);
            await tab.ChangeRecordingAsync(true);
            host.UpdateLayout();
            if (!FindControls<TextBlock>(tab.Notices).Any(t => t.Text == string.Format(App.Text("NoticeRecordingRecovered"), 2, 3)))
                throw new InvalidOperationException("Recovery did not update the original report.");
            Acknowledge();
            CheckStatus("NoticeRecordingActive", 3);
            lifecycle.MarkIncomplete("Second writer failed.");
            while (!tab.Reports.HasReports) await Idle();
            Acknowledge();
            SendText(pane, "y");
            await peer.GetStream().ReadExactlyAsync(new byte[1], timeout.Token);
            await Idle();
            CheckStatus("NoticeRecordingStopped", 3);
            if (tab.Reports.HasReports) throw new InvalidOperationException("A failed part was announced twice.");
        }
        finally
        {
            tab.Disconnect();
            await tab.Completion;
            host.Close();
        }

        Task Idle() => host.Dispatcher.InvokeAsync(() => host.UpdateLayout(), DispatcherPriority.ApplicationIdle, timeout.Token).Task;
        void CheckStatus(string key, int part)
        {
            host.UpdateLayout();
            string label = string.Format(App.Text(key), part);
            var row = TerminalStatusBar(tab);
            var current = row.Children.OfType<TextBlock>().Single(text => text.Text == tab.ConnectionStatus);
            if (!row.Children.OfType<TextBlock>().Any(text => text != current && text.Text == label &&
                text.IsVisible && text.ActualWidth > 0 && text.Focusable && Equals(text.ToolTip, label) &&
                AutomationProperties.GetName(text) == label) || current.ActualWidth < row.ActualWidth * 0.34)
                throw new InvalidOperationException($"Recording status did not reflect {key}, part {part}.");
        }
        void Acknowledge() => FindControls<Button>(tab.Notices).Single(b => Equals(b.Content, App.Text("OK")))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
