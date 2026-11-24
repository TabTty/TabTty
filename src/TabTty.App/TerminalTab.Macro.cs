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
    private StackPanel? macroControls;
    private int macroRefreshPending;
    internal MacroRun? Macro { get; private set; }
    internal MainWindow? MacroWindow => tabHost?.Invoke();
    internal bool HasActiveMacro => Macro?.IsActive == true;
    internal bool IsExternalMacro => Macro is { IsPostConnection: false };

    internal void PrepareMacro(MacroProgram program, IReadOnlyList<string> arguments)
    {
        Macro = new MacroRun(program, arguments, new MacroHost(this));
        if (IsExternalMacro)
        {
            IsRunning = false;
            status.Visibility = Visibility.Collapsed;
        }
        var controls = macroControls = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
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

    private void ClearPostConnectionMacro()
    {
        if (Macro is not { IsPostConnection: true }) return;
        DisposeMacro();
        if (macroControls is { } controls)
        {
            controls.Children.Clear();
            panel.Children.Remove(controls);
        }
        macroControls = null;
        macroButtons.Clear();
        Macro = null;
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
        if (state.PostConnection) label = Text("PostConnectionMacro") + " · " + label;
        if (state.Operation.Length != 0) label += " · " + MacroOperationLabel(state.Operation);
        if (state.Result is { } result)
            label += "\n" + (state.Position.Line > 0
                ? string.Format(Text("MacroLocation"), state.Position.Line, state.Position.Column, state.DisplayDetail ?? MacroErrorLabel(result))
                : state.DisplayDetail ?? MacroErrorLabel(result));
        if (state.Holds.HasFlag(MacroHold.HistoryLoss)) label += "\n" + Text("MacroHistoryLost");
        if (state.Holds.HasFlag(MacroHold.RecordingFailure)) label += "\n" + Text("MacroRecordingFailed");
        if (state.Holds.HasFlag(MacroHold.RecordingReady)) label += "\n" + Text("MacroRecordingReady");
        if (state.PlaintextNotice) label += "\n" + Text("MacroPlaintextNotice");
        macroStatus.Text = label;
        foreach (var (key, button) in macroButtons) button.Content = Text(key);
        macroButtons["MacroPause"].IsEnabled = state.Actions.HasFlag(MacroAction.Pause);
        macroButtons["MacroResume"].IsEnabled = state.Actions.HasFlag(MacroAction.Resume);
        macroButtons["MacroStop"].IsEnabled = state.Actions.HasFlag(MacroAction.Stop);
        ShowWhen("MacroRetry", state.Holds.HasFlag(MacroHold.RecordingFailure) && state.Actions.HasFlag(MacroAction.Stop));
        macroButtons["MacroRetry"].IsEnabled = state.Actions.HasFlag(MacroAction.RetryRecording);
        ShowWhen("MacroContinue", state.Actions.HasFlag(MacroAction.ContinueAfterHistoryLoss));
        macroDialog?.UpdateLabels();

        void ShowWhen(string key, bool visible) => macroButtons[key].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RetryMacroRecordingAsync()
    {
        if (session is not { } active || Macro is not { } run) return;
        try { await run.RetryRecordingAsync(() => active.RetryRecordingAsync(stop.Token)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or InvalidDataException or NotSupportedException or OperationCanceledException)
        {
            if (active.RecordingStartFailure is { } failure) SetStatus("Failed", LogStartError(failure));
            else SetStatus(statusKey, Text("MacroRecordingFailed"));
        }
    }

    internal static string MacroErrorLabel(string code) =>
        LocalizedText("MacroError" + code) ?? string.Format(Text("MacroErrorCode"), code);

    internal static string PostConnectionSourceError(MacroSourceException error) =>
        Text("PostConnectionMacro") + " · " +
        string.Format(Text("MacroLocation"), error.Position.Line, error.Position.Column, MacroErrorLabel(error.Code));

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
        public async Task<TerminalSession> ConnectAsync(MacroRun run, string profilePath, RecordingOverrides recording, CancellationToken token)
        {
            var ready = new TaskCompletionSource<TerminalSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            await tab.Dispatcher.InvokeAsync(() =>
            {
                if (tab.MacroWindow is not { IsClosing: false }) throw new MacroError(MacroErrorCode.UserCancelled);
                tab.path = profilePath;
                tab.IsRunning = true;
                tab.status.Visibility = Visibility.Visible;
                tab.SetStatus("Connecting");
                tab.connectionCompletion = tab.ConnectAsync(null, ready, token, recording);
            }, DispatcherPriority.Normal, token);
            try { return await ready.Task.ConfigureAwait(false); }
            catch (InvalidDataException error) when (error.InnerException is MacroSourceException)
            {
                throw new MacroError(MacroErrorCode.ConnectionFailed, error.Message);
            }
            catch (MacroSourceException error)
            {
                throw new MacroError(MacroErrorCode.ConnectionFailed, PostConnectionSourceError(error));
            }
        }

        public Task<VaultSecret?> ReadPasswordAsync(CredentialReference reference, CancellationToken token) =>
            tab.Dispatcher.InvokeAsync(async () =>
            {
                var owner = (Window?)tab.MacroWindow ?? Window.GetWindow(tab)
                    ?? throw new MacroError(MacroErrorCode.OperationUnavailable);
                if (owner is MainWindow main) main.RestoreMacroHost();
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
