using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab
{
    private readonly TextBlock macroStatus = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, Button> macroButtons = [];
    private MacroDialogWindow? macroDialog;
    private WrapPanel? macroControls;
    private Button? compactMacroStop;
    private int macroRefreshPending;
    internal MacroRun? Macro { get; private set; }
    internal MainWindow? MacroWindow => tabHost?.Invoke();
    internal bool HasActiveMacro => Macro?.IsActive == true;
    internal bool IsExternalMacro => Macro is { IsPostConnection: false };

    internal void PrepareMacro(MacroProgram program, IReadOnlyList<string> arguments)
    {
        var run = Macro = new MacroRun(program, arguments, new MacroHost(this));
        if (IsExternalMacro)
        {
            IsRunning = false;
            status.Visibility = Visibility.Collapsed;
        }
        macroStatus.Visibility = Visibility.Visible;
        macroControls = new WrapPanel();
        Add("MacroPause", MacroAction.Pause, () => { run.Pause(); return Task.CompletedTask; });
        Add("MacroResume", MacroAction.Resume, () => { run.Resume(); return Task.CompletedTask; });
        Add("MacroStop", MacroAction.Stop, () => { run.Stop(); return Task.CompletedTask; });
        Add("MacroRetry", MacroAction.RetryRecording, RetryMacroRecordingAsync);
        Add("MacroContinue", MacroAction.ContinueAfterHistoryLoss, () => { run.ContinueAfterHistoryLoss(); return Task.CompletedTask; });
        compactMacroStop = new Button { Margin = new Thickness(2) };
        compactMacroStop.Click += (_, _) => { if (Macro == run && run.Status.Actions.HasFlag(MacroAction.Stop)) run.Stop(); };
        run.Changed += QueueMacroRefresh;
        UpdateMacroControls();

        void Add(string label, MacroAction accepted, Func<Task> action)
        {
            var button = new Button { Margin = new Thickness(2), Tag = accepted };
            button.Click += async (_, _) => { if (Macro == run && run.Status.Actions.HasFlag(accepted)) await action(); };
            macroControls.Children.Add(button);
            macroButtons.Add(label, button);
        }
    }

    internal void DisposeMacro()
    {
        if (Macro is not { } run) return;
        Notices.Remove(run);
        run.Changed -= QueueMacroRefresh;
        macroControls = null;
        compactMacroStop = null;
        macroButtons.Clear();
        run.Dispose();
    }

    private void ClearPostConnectionMacro()
    {
        if (Macro is not { IsPostConnection: true }) return;
        DisposeMacro();
        macroStatus.Visibility = Visibility.Collapsed;
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
        if (Macro is not { } run || macroControls is null || compactMacroStop is null) return;
        MacroRunStatus state = run.Status;
        string label = Text("MacroState" + state.State);
        if (state.PostConnection) label = Text("PostConnectionMacro") + " · " + label;
        if (state.Operation.Length != 0) label += " · " + MacroOperationLabel(state.Operation);
        if (state.Result is { } result)
            label += "\n" + (state.Position.Line > 0
                ? string.Format(Text("MacroLocation"), state.Position.Line, state.Position.Column, state.DisplayDetail ?? MacroErrorLabel(result))
                : state.DisplayDetail ?? MacroErrorLabel(result));
        bool active = state.Actions.HasFlag(MacroAction.Stop);
        if (state.Holds.HasFlag(MacroHold.HistoryLoss)) label += "\n" + Text(active ? "MacroHistoryLost" : "MacroHistoryLossFact");
        if (state.Holds.HasFlag(MacroHold.RecordingFailure)) label += "\n" + Text(active ? "MacroRecordingFailed" : "MacroRecordingFailureFact");
        if (state.Holds.HasFlag(MacroHold.RecordingReady)) label += "\n" + Text(active ? "MacroRecordingReady" : "MacroRecordingRecoveryFact");
        if (state.PlaintextNotice) label += "\n" + Text("MacroPlaintextNotice");
        macroStatus.Text = label.ReplaceLineEndings(" ");
        macroStatus.ToolTip = label;
        AutomationProperties.SetName(macroStatus, label);
        foreach (var (key, button) in macroButtons)
        {
            string text = Text(key);
            button.Content = text;
            AutomationProperties.SetName(button, text);
            button.Visibility = state.Actions.HasFlag((MacroAction)button.Tag) ? Visibility.Visible : Visibility.Collapsed;
        }
        compactMacroStop.Content = Text("MacroStop");
        AutomationProperties.SetName(compactMacroStop, Text("MacroStop"));
        if (active) Notices.ShowContent(run, macroControls, keepVisible: true, compactActions: compactMacroStop);
        else Notices.Remove(run);
        macroDialog?.UpdateLabels();
    }

    private async Task RetryMacroRecordingAsync()
    {
        if (session is not { } active || Macro is not { } run) return;
        object generation = ConnectionGeneration;
        try
        {
            int recoveringPart = active.RecordingStatus.Part;
            await run.RetryRecordingAsync(() => active.RetryRecordingAsync(stop.Token));
            ShowRecordingRecovery(active, recoveringPart);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or InvalidDataException or NotSupportedException or OperationCanceledException)
        {
            if (Macro != run || session != active || ConnectionGeneration != generation) return;
            if (active.RecordingStartFailure is { } failure) SetStatus("Failed", LogStartError(failure));
            else if (run.Status.Actions.HasFlag(MacroAction.Stop))
            {
                if (active.RecordingStatus.Disposition == RecordingDisposition.Incomplete)
                    ObserveRecordingFailure(active, active.RecordingStatus, generation);
                else SetStatus(statusKey, Text("MacroRecordingFailed"));
            }
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
        public async Task<MacroConnection> ConnectAsync(MacroRun run, string profilePath, RecordingOverrides recording, CancellationToken token)
        {
            var ready = new TaskCompletionSource<MacroConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                return await ((App)Application.Current).Vault.ReadMacroPasswordAsync(owner, reference, token, tab);
            }, DispatcherPriority.Normal, token).Task.Unwrap();

        public Task<MacroDialogResult> DialogAsync(MacroDialogRequest request, CancellationToken token) =>
            tab.Dispatcher.InvokeAsync(async () =>
            {
                var owner = tab.MacroWindow ?? throw new MacroError(MacroErrorCode.OperationUnavailable);
                var run = tab.Macro ?? throw new MacroError(MacroErrorCode.OperationUnavailable);
                var dialog = new MacroDialogWindow(owner, run.BaseName, request, tab.TabCaption);
                tab.macroDialog = dialog;
                try
                {
                    await tab.PresentDecisionAsync(dialog, token);
                    if (tab.Macro != run) throw new OperationCanceledException(token);
                    return await dialog.Answered;
                }
                finally { if (tab.macroDialog == dialog) tab.macroDialog = null; }
            }, DispatcherPriority.Normal, token).Task.Unwrap();

        public async Task SetMinimizedAsync(bool minimized, CancellationToken token)
        {
            while (!await tab.Dispatcher.InvokeAsync(() =>
            {
                if (tab.MacroWindow is not { } owner)
                    throw new MacroError(MacroErrorCode.OperationUnavailable);
                if (minimized)
                {
                    if (owner.TerminalTabs.Any(terminal => terminal.HasPendingDecision) ||
                        owner.OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return false;
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
