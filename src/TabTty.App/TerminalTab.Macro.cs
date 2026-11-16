using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private readonly TextBlock macroStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, Button> macroButtons = [];
    private MacroDialogWindow? macroDialog;
    private int macroRefreshPending;
    internal MacroRun? Macro { get; private set; }
    internal MainWindow? MacroWindow { get; set; }
    internal bool HasActiveMacro => Macro?.IsActive == true;

    internal void PrepareMacro(MacroProgram program, IReadOnlyList<string> arguments)
    {
        Macro = new MacroRun(program, arguments, new MacroHost(this));
        IsRunning = false;
        status.Visibility = Visibility.Collapsed;
        var controls = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        controls.Children.Add(macroStatus);
        var actions = new WrapPanel();
        controls.Children.Add(actions);
        Add("MacroPause", () => { Macro.Pause(); return Task.CompletedTask; });
        Add("MacroResume", () => { Macro.Resume(); return Task.CompletedTask; });
        Add("MacroStop", () => { Macro.Stop(); return Task.CompletedTask; });
        Add("MacroRetry", RetryMacroRecordingAsync);
        Add("MacroContinue", () => { Macro.ContinueAfterHistoryLoss(); return Task.CompletedTask; });
        DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Insert(0, controls);
        Macro.Changed += QueueMacroRefresh;
        UpdateMacroControls();

        void Add(string label, Func<Task> action)
        {
            var button = new Button { Margin = new Thickness(0, 4, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += async (_, _) => await action();
            actions.Children.Add(button);
            macroButtons.Add(label, button);
        }
    }

    internal void DisposeMacro()
    {
        if (Macro is not { } run) return;
        run.Changed -= QueueMacroRefresh;
        run.Dispose();
    }

    private void QueueMacroRefresh()
    {
        if (Interlocked.Exchange(ref macroRefreshPending, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref macroRefreshPending, 0);
            UpdateMacroControls();
            foreach (var pane in panes.Keys) pane.SetConnected(InputSession is not null);
            Broadcast.Refresh();
        });
    }

    private void UpdateMacroControls()
    {
        if (Macro is not { } run) return;
        MacroRunStatus state = run.Status;
        string label = Text("MacroState" + state.State);
        if (state.Operation.Length != 0) label += " · " + MacroOperationLabel(state.Operation);
        if (state.Result is { } result)
            label += "\n" + (state.Position.Line > 0
                ? string.Format(Text("MacroLocation"), state.Position.Line, state.Position.Column, MacroErrorLabel(result))
                : MacroErrorLabel(result));
        if (state.Holds.HasFlag(MacroHold.HistoryLoss)) label += "\n" + Text("MacroHistoryLost");
        if (state.Holds.HasFlag(MacroHold.RecordingFailure)) label += "\n" + Text("MacroRecordingFailed");
        if (state.Holds.HasFlag(MacroHold.RecordingReady)) label += "\n" + Text("MacroRecordingReady");
        if (state.PlaintextNotice) label += "\n" + Text("MacroPlaintextNotice");
        macroStatus.Text = label;
        foreach (var (key, button) in macroButtons) button.Content = Text(key);
        bool active = run.IsActive && state.State != MacroRunState.Stopping;
        macroButtons["MacroPause"].IsEnabled = active && !state.Holds.HasFlag(MacroHold.UserPause);
        macroButtons["MacroResume"].IsEnabled = active && (state.Holds & (MacroHold.UserPause | MacroHold.RecordingReady)) != 0;
        macroButtons["MacroStop"].IsEnabled = active;
        ShowWhen("MacroRetry", active && state.Holds.HasFlag(MacroHold.RecordingFailure));
        ShowWhen("MacroContinue", active && state.Holds.HasFlag(MacroHold.HistoryLoss));
        macroDialog?.UpdateLabels();

        void ShowWhen(string key, bool visible) => macroButtons[key].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RetryMacroRecordingAsync()
    {
        if (session is not { } active) { Macro?.RequestRecordingRetry(); return; }
        try { await active.ResumeRecordingAsync(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { SetStatus(statusKey, Text("MacroRecordingFailed")); }
    }

    internal static string MacroErrorLabel(string code) =>
        LocalizedText("MacroError" + code) ?? string.Format(Text("MacroErrorCode"), code);

    private static string MacroOperationLabel(string operation) => Text(operation switch
    {
        "Connect" => "Connecting",
        "Wait" => "MacroReceiving",
        "Sleep" => "MacroSleeping",
        "Dialog" => "MacroPrompting",
        "MinimizeWindow" or "RestoreWindow" => "MacroWindowAction",
        _ => "MacroSending",
    });

    private sealed class MacroHost(TerminalTab tab) : IMacroHost
    {
        public async Task<TerminalSession> ConnectAsync(MacroRun run, string profilePath, CancellationToken token)
        {
            var ready = new TaskCompletionSource<TerminalSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            await tab.Dispatcher.InvokeAsync(() =>
            {
                if (tab.MacroWindow is not { IsClosing: false }) throw new MacroError(MacroErrorCode.UserCancelled);
                tab.path = profilePath;
                tab.IsRunning = true;
                tab.status.Visibility = Visibility.Visible;
                tab.SetStatus("Connecting");
                tab.connectionCompletion = tab.ConnectAsync(null, ready, token);
            }, DispatcherPriority.Normal, token);
            return await ready.Task.ConfigureAwait(false);
        }

        public Task<VaultSecret?> ReadPasswordAsync(CredentialReference reference, CancellationToken token) =>
            tab.Dispatcher.InvokeAsync(async () =>
            {
                var owner = tab.MacroWindow ?? throw new MacroError(MacroErrorCode.OperationUnavailable);
                owner.RestoreMacroHost();
                return await ((App)Application.Current).Vault.ReadMacroPasswordAsync(owner, reference, token);
            }, DispatcherPriority.Normal, token).Task.Unwrap();

        public Task<MacroDialogResult> DialogAsync(MacroDialogRequest request, CancellationToken token) =>
            tab.Dispatcher.InvokeAsync(async () =>
            {
                var owner = tab.MacroWindow ?? throw new MacroError(MacroErrorCode.OperationUnavailable);
                owner.RestoreMacroHost();
                var dialog = new MacroDialogWindow(owner, tab.Macro!.BaseName, request);
                tab.macroDialog = dialog;
                try { return await dialog.ShowAsync(token); }
                finally { tab.macroDialog = null; }
            }, DispatcherPriority.Normal, token).Task.Unwrap();

        public async Task SetMinimizedAsync(bool minimized, CancellationToken token)
        {
            while (!await tab.Dispatcher.InvokeAsync(() =>
            {
                if (tab.MacroWindow is not { } owner)
                    throw new MacroError(MacroErrorCode.OperationUnavailable);
                if (minimized)
                {
                    if (owner.OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return false;
                    owner.WindowState = WindowState.Minimized;
                }
                else owner.RestoreMacroHost();
                return true;
            }, DispatcherPriority.Normal, token))
            {
                await Task.Delay(50, token).ConfigureAwait(false);
                await tab.Macro!.WaitForContinuationAsync(token).ConfigureAwait(false);
            }
        }
        public string ErrorMessage(MacroErrorCode code) => MacroErrorLabel(code.ToString());
    }
}
