using System.IO;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class TerminalTab : UserControl
{
    private string path;
    private readonly SshInteraction? sshInteraction;
    private readonly HighlightSet? fixtureHighlights;
    private readonly Func<Window, long, CancellationToken, Task<TerminalPaste?>> captureClipboard;
    private readonly (int Columns, int Rows)? fixedViewport;
    private CancellationTokenSource stop = new();
    private readonly TextBlock status = new();
    private readonly TextBlock highlightStatus = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock broadcastTabStatus = new() { Visibility = Visibility.Collapsed };
    private readonly DockPanel statusBar = new() { Height = 30, ClipToBounds = true };
    private readonly StackPanel searchBar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly TextBox searchQuery = new() { Width = 220, MaxLength = 1024, Margin = new Thickness(3) };
    private readonly CheckBox matchCase = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
    private readonly Button previousMatch = new() { Margin = new Thickness(3) };
    private readonly Button nextMatch = new() { Margin = new Thickness(3) };
    private readonly Button closeSearch = new() { Margin = new Thickness(3) };
    private readonly TextBlock searchStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
    private TerminalSurface output = new();
    private Guid? highlightSetId;
    private readonly TerminalHighlightState highlightState = new();
    private bool highlightSuspended;
    private readonly DispatcherTimer resizeTimer;
    private readonly DispatcherTimer frameTimer;
    private IReadOnlyList<string>? pendingFrame;
    private int receivedRevision;
    private bool capturingFrame;
    private bool immediateFrameQueued;
    private readonly DispatcherTimer bellTimer;
    private int bellPending;
    private (int Columns, int Rows) appliedGrid = (80, 24);
    private TerminalSession? session;
    private RecordingStatus? lastRecordingStatus;
    private FileTransferWindow? transferWindow;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TerminalSession, FileOperationOwnership> transferOwnership = new();
    private string? connectedName;
    private ConnectionDestination? lastDestination;
    private Window? destinationNotice;
    private string statusKey = "Connecting";
    private string? error;
    private int pendingSends;
    private int pendingMouse;
    private bool HasPendingInput => pendingSends > 0 || pasteStop is not null;
    private CancellationTokenSource? pasteReview;
    private CancellationTokenSource? pasteStop;
    private CancellationTokenSource? searchWork;
    private TerminalSnapshot? snapshot;
    private TerminalMatch[] searchMatches = [];
    private readonly DispatcherTimer searchRefreshTimer;
    private readonly ResourceReservation searchMemory = ApplicationResources.SearchCache.Reserve(0);
    private string? searchStatusKey;
    private readonly List<string> sshMessages = [];
    private Channel<TerminalMouseInput> mouseInput = Channel.CreateBounded<TerminalMouseInput>(256);
    private Task mousePump = Task.CompletedTask;
    private string baseFontFamily = "Consolas";
    private double baseFontSize = DefaultTerminalFontSize;
    private double zoomFontSize = DefaultTerminalFontSize;

    public TerminalTab(string path) : this(path, null) { }

    internal TerminalTab(string path, SshInteraction? sshInteraction, (int Columns, int Rows)? fixedViewport = null,
        HighlightSet? fixtureHighlights = null,
        Func<Window, long, CancellationToken, Task<TerminalPaste?>>? captureClipboard = null)
    {
        this.path = path;
        this.sshInteraction = sshInteraction;
        this.fixedViewport = fixedViewport;
        this.fixtureHighlights = fixtureHighlights;
        this.captureClipboard = captureClipboard ?? ClipboardPaste.CaptureAsync;
        bellTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        bellTimer.Tick += (_, _) =>
        {
            bellTimer.Stop();
            RestoreStatusBarColors();
            Volatile.Write(ref bellPending, 0);
        };
        frameTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(17),
        };
        frameTimer.Tick += async (_, _) => await CaptureAndRenderFrameAsync();
        resizeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        resizeTimer.Tick += async (_, _) => await ResizeTerminalAsync();
        searchRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        searchRefreshTimer.Tick += async (_, _) =>
        {
            searchRefreshTimer.Stop();
            if (searchWork is null) await SearchAsync(backwards: false, navigate: false);
        };
        AttachPane(output);
        var catalog = ((App)Application.Current).Highlights;
        Window? host = null;
        void UpdateWindowPresentation()
        {
            presentationEnabled = host?.WindowState != WindowState.Minimized;
            foreach (var pane in panes.Keys) pane.SetPresentationEnabled(presentationEnabled);
        }
        EventHandler windowStateChanged = (_, _) => UpdateWindowPresentation();
        Loaded += (_, _) =>
        {
            host = Window.GetWindow(this);
            if (host is not null) host.StateChanged += windowStateChanged;
            UpdateWindowPresentation();
            catalog.Changed += ApplyHighlightSet;
            ApplyHighlightSet();
        };
        Unloaded += (_, _) =>
        {
            catalog.Changed -= ApplyHighlightSet;
            if (host is not null) host.StateChanged -= windowStateChanged;
            host = null;
            presentationEnabled = false;
            foreach (var pane in panes.Keys) pane.SetPresentationEnabled(false);
        };
        searchBar.Children.Add(searchQuery);
        searchBar.Children.Add(matchCase);
        searchBar.Children.Add(previousMatch);
        searchBar.Children.Add(nextMatch);
        searchBar.Children.Add(closeSearch);
        searchBar.Children.Add(searchStatus);
        ConfigureStatusIndicator(status);
        terminalArea.Children.Add(output.Scroller);
        InitializeAiControls();
        ConfigureStatusIndicator(highlightStatus);
        ConfigureStatusIndicator(broadcastTabStatus);
        DockPanel.SetDock(highlightStatus, Dock.Right);
        DockPanel.SetDock(broadcastTabStatus, Dock.Right);
        statusBar.Children.Add(highlightStatus);
        statusBar.Children.Add(broadcastTabStatus);
        statusBar.Children.Add(status);
        RestoreStatusBarColors();
        statusBar.SizeChanged += (_, e) =>
        {
            highlightStatus.MaxWidth = e.NewSize.Width / 4;
            aiStatus.MaxWidth = broadcastTabStatus.MaxWidth = e.NewSize.Width * 0.28;
        };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(terminalArea, 1);
        Grid.SetRow(statusBar, 2);
        body.Children.Add(searchBar);
        body.Children.Add(terminalArea);
        body.Children.Add(statusBar);
        panel.Children.Add(body);
        Content = panel;
        searchBar.PreviewKeyDown += OnSearchKeyDown;
        searchQuery.TextChanged += async (_, _) => await RefreshSearchAsync();
        matchCase.Checked += async (_, _) => await RefreshSearchAsync();
        matchCase.Unchecked += async (_, _) => await RefreshSearchAsync();
        previousMatch.Click += async (_, _) => await SearchAsync(backwards: true);
        nextMatch.Click += async (_, _) => await SearchAsync(backwards: false);
        closeSearch.Click += (_, _) => CloseSearch();
        UpdateLabels();
    }

    public event Action<string>? Connected;
    public event Action? CaptionChanged;
    public event Action? Disconnected;
    public bool IsRunning { get; private set; } = true;
    private enum ConnectionEnd { Pending, Normal, InitialCancelled, FailedBeforeConnect, FailedAfterConnect }
    private ConnectionEnd connectionEnd;
    public bool IsDisconnected => !IsRunning && (Macro is null
        ? connectionEnd != ConnectionEnd.Pending : statusKey == "Disconnected" || connectedName is not null);
    internal bool CloseAfterInitialCancellation => connectionEnd == ConnectionEnd.InitialCancelled;
    internal bool CanAutoCloseOnDisconnect => connectionEnd == ConnectionEnd.Normal;
    internal string ConnectionStatus => status.Text;
    public bool ShowDisconnectedIndicator => Macro is null ? !IsRunning && connectionEnd == ConnectionEnd.Normal
        : statusKey == "Disconnected" || IsDisconnected;
    internal string? ConnectionWarning => Macro is not null || IsRunning ? null : connectionEnd switch
    {
        ConnectionEnd.FailedBeforeConnect => "ConnectionAttemptFailed",
        ConnectionEnd.FailedAfterConnect => "ConnectionLost",
        _ => null,
    };
    public string TabCaption => connectedName ?? Path.GetFileNameWithoutExtension(path);
    public string ProfilePath => path;
    internal void UpdateProfilePath(string previous, string destination)
    {
        if (path == previous) path = destination;
    }
    private Task connectionCompletion = Task.CompletedTask;
    public Task Completion => Macro is null ? connectionCompletion : Task.WhenAll(connectionCompletion, Macro.Completion);
    public bool CanTransfer => !ClosePending && IsRunning && !stop.IsCancellationRequested && session?.CanTransfer == true;
    public bool HasActiveTransfer => transferWindow?.HasActiveOperation == true;
    internal object ConnectionGeneration => stop;
    internal FileTransferWindow? TransferWindow => transferWindow;
    internal bool ClosePending { get; set; }
    private bool removed;

    public async void OpenFileTransfer()
    {
        if (!CanTransfer || session is not { } active) return;
        if (transferWindow is { } previous && !previous.OwnsSession(active))
        {
            if (!(await previous.CloseWithTerminalAsync()).Dismissed || !CanTransfer || session != active) return;
        }
        if (transferWindow is { } existing && existing.OwnsSession(active))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new FileTransferWindow(active, transferOwnership.GetOrCreateValue(active));
        transferWindow = window;
        window.Closed += (_, _) => { if (transferWindow == window) transferWindow = null; };
        window.Show();
    }

    public Task<TransferWindowCloseResult> CloseFileTransferAsync() => transferWindow?.CloseWithTerminalAsync() ??
        Task.FromResult(new TransferWindowCloseResult(TransferWindowCloseStatus.Dismissed, false));

    public void Start(Action<ConnectionProfile>? opened = null)
    {
        if (removed || ClosePending || Macro is not null || !Completion.IsCompleted) return;
        AiCollaboration.EndSharing(this);
        bool reconnect = !IsRunning;
        if (reconnect)
        {
            stop = new CancellationTokenSource();
            mouseInput = Channel.CreateBounded<TerminalMouseInput>(256);
            mousePump = Task.CompletedTask;
            pendingMouse = 0;
            pendingSends = 0;
            pasteReview = null;
            pasteStop = null;
            IsRunning = true;
            appliedGrid = (80, 24);
            sshMessages.Clear();
            connectionEnd = ConnectionEnd.Pending;
            SetStatus("Connecting");
        }
        connectionCompletion = ConnectAsync(opened, reconnect: reconnect);
    }

    private async Task ConnectAsync(Action<ConnectionProfile>? opened, TaskCompletionSource<TerminalSession>? ready = null,
        CancellationToken attempt = default, RecordingOverrides recordingOverrides = default, bool reconnect = false)
    {
        var generation = stop;
        bool established = false;
        lastRecordingStatus = null;
        try
        {
            if (retainedAiSession is { } previous)
            {
                retainedAiSession = null;
                await previous.ReleaseRetainedOutputAsync();
            }
            stop.Token.ThrowIfCancellationRequested();
            var app = (App)Application.Current;
            var interaction = sshInteraction ?? new SshInteraction(ApproveHostAsync, RequestSshPasswordAsync,
                message => Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(stop, generation)) sshMessages.Add(message);
                }), RequestSshChallengeAsync,
                RequestSshPrivateKeyAsync, app.HostTrust.ApprovedFormats,
                (request, token) => OnSshPromptAsync((application, owner) =>
                    application.Vault.CheckSshCredentialsAsync(owner, request, token), token, serialize: false),
                message => Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(stop, generation) || stop.IsCancellationRequested) return;
                    sshMessages.Add(message);
                    if (statusKey == "Connecting") SetStatus("Connecting", message);
                }));
            string fontFamily = app.TerminalFontFamily;
            double fontSize = app.TerminalFontSize;
            int retention = app.RetainedRows;
            RecordingDefaults logDefaults = app.LogDefaults;
            int sessionRetention = retention;
            void Opened(ConnectionProfile profile) => Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(stop, generation)) return;
                    sessionRetention = profile.RetainedRows ?? retention;
                    baseFontFamily = profile.FontFamily ?? fontFamily;
                    baseFontSize = zoomFontSize = profile.FontSizePoints ?? fontSize;
                    ConfigurePaneFonts();
                    opened?.Invoke(profile);
                });
            Task<(int Columns, int Rows)> Viewport(CancellationToken token) => Dispatcher.InvokeAsync(() =>
                {
                    if (output.IsPresentationActive && GridHost.ActualWidth > 0 && GridHost.ActualHeight > 0)
                        appliedGrid = EffectiveGrid(sessionRetention);
                    return appliedGrid;
                }, DispatcherPriority.Loaded, token).Task;
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, attempt))
            {
                Window? owner = MacroWindow ?? Window.GetWindow(this);
                string attemptPath = path;
                var profile = await app.Profiles.AdmitAsync(owner, attemptPath, Macro?.FullPath, startup.Token);
                RecordingSettings? logging = null;
                if (profile.RecordsOutput)
                {
                    try
                    {
                        logging = RecordingSettings.Resolve(attemptPath, profile, Macro?.BaseName, logDefaults, recordingOverrides);
                        await app.Profiles.ApproveRecordingDestinationAsync(MacroWindow ?? Window.GetWindow(this), logging.Destination, startup.Token);
                    }
                    catch (Exception failure) when (failure is not RecordingStartException &&
                        failure is (IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException or NotSupportedException))
                    { throw new RecordingStartException(failure.Message, failure); }
                }
                session = await TerminalSession.ConnectReviewedAsync(attemptPath, profile, Macro, startup.Token, interaction,
                    Opened, retention, Viewport, logging);
            }
            session.Bell += () =>
            {
                if (Interlocked.Exchange(ref bellPending, 1) != 0) return;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!ReferenceEquals(stop, generation) || generation.IsCancellationRequested ||
                        app.BellChoice != "visual" || !output.IsPresentationActive)
                    {
                        Volatile.Write(ref bellPending, 0);
                        return;
                    }
                    statusBar.Background = Brushes.White;
                    statusBar.SetValue(TextElement.ForegroundProperty, Brushes.Black);
                    bellTimer.Start();
                }));
            };
            stop.Token.ThrowIfCancellationRequested();
            var destination = ConnectionDestination.From(session);
            if (lastDestination is { } previousDestination && previousDestination != destination)
                NotifyDestinationChange(session, previousDestination, destination, generation.Token);
            lastDestination = destination;
            connectedName = session.DisplayName;
            established = true;
            SetStatus("Connected");
            var recordingOwner = session;
            recordingOwner.RecordingPartFailed += ShowRecordingFailure;
            ShowRecordingFailure();
            Connected?.Invoke(connectedName);
            highlightState.Resume();
            highlightSetId = session.HighlightSetId;
            ApplyHighlightSet();
            foreach (var pane in panes.Keys) pane.SetConnected(!HasActiveMacro);
            mousePump = SendMouseAsync(session);
            Interlocked.Exchange(ref receivedRevision, 1);
            ScheduleFrame(immediately: true);
            ScheduleResize();
            ready?.TrySetResult(session);
            try
            {
                await ReceiveOutputAsync(recordingOwner);
                await recordingOwner.WaitForTerminalPeerCloseAsync(stop.Token);
            }
            finally { recordingOwner.RecordingPartFailed -= ShowRecordingFailure; }
            connectionEnd = session.RecordingStartFailure is null ? ConnectionEnd.Normal : ConnectionEnd.FailedAfterConnect;
            if (session.RecordingStartFailure is { } logFailure) SetStatus("Failed", LogStartError(logFailure));
            else SetStatus("Disconnected");

            void ShowRecordingFailure() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!removed && ReferenceEquals(session, recordingOwner) &&
                    recordingOwner.RecordingStatus is { Disposition: RecordingDisposition.Incomplete, Error: { } problem })
                    SetStatus(statusKey, problem);
            }));
        }
        catch (OperationCanceledException exception) when (stop.IsCancellationRequested || exception is SshInteractionCancelledException)
        {
            ready?.TrySetException(exception);
            connectionEnd = established || reconnect ? ConnectionEnd.Normal : ConnectionEnd.InitialCancelled;
            SetStatus("Disconnected");
        }
        catch (Exception exception) when (exception is IOException or SocketException or NotSupportedException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException
            or InvalidDataException or CryptographicException or DllNotFoundException or EntryPointNotFoundException
            or FormatException or System.Text.Json.JsonException or KeyNotFoundException or MacroError)
        {
            ready?.TrySetException(exception);
            if (stop.IsCancellationRequested && session?.RecordingStartFailure is null)
            {
                connectionEnd = established || reconnect ? ConnectionEnd.Normal : ConnectionEnd.InitialCancelled;
                SetStatus("Disconnected");
            }
            else
            {
                connectionEnd = established ? ConnectionEnd.FailedAfterConnect : ConnectionEnd.FailedBeforeConnect;
                string? credentialError = exception switch
                {
                    SshCredentialFailureException => exception.Message,
                    CredentialLookupException { Code: MacroErrorCode.CredentialKindMismatch } => Text("VaultCredentialKindMismatch"),
                    CredentialLookupException { Code: MacroErrorCode.CredentialUnavailable } => Text("SshCredentialMissing"),
                    _ => null,
                };
                SetStatus("Failed", session?.RecordingStartFailure is { } failure ? LogStartError(failure) : credentialError ?? (exception switch
                {
                    RecordingStartException recording => LogStartError(recording.Message),
                    OperationCanceledException => Text("TimedOut"),
                    DllNotFoundException or EntryPointNotFoundException => Text("VaultCryptoUnavailable"),
                    _ => Macro is null ? exception.Message : Text("MacroErrorConnectionFailed"),
                }));
                if (credentialError is not null && (MacroWindow ?? Window.GetWindow(this)) is { IsVisible: true } owner)
                    ((App)Application.Current).Modal(() => ThemedMessageBox.ShowVault(owner, credentialError,
                        Text("Vault"), icon: MessageBoxImage.Error));
            }
        }
        finally
        {
            destinationNotice?.Close();
            AiCollaboration.EndControl(this);
            transferWindow?.ConnectionEnded();
            stop.Cancel();
            mouseInput.Writer.TryComplete();
            await mousePump;
            if (session is { } closing)
            {
                if (ClosePending) closing.ProtectRecordingClose();
                try { await closing.DisposeAsync(); }
                finally
                {
                    lastRecordingStatus = closing.RecordingStatus;
                    session = null;
                    if (removed) await closing.ReleaseRetainedOutputAsync();
                    else retainedAiSession = closing;
                }
                if (!removed)
                {
                    try
                    {
                        var rows = await closing.GetOutputAsync();
                        if (!removed) Display(rows);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException)
                    { if (!removed) SetStatus("Failed", exception.Message); }
                }
                if (closing.RecordingStartFailure is { } failure)
                {
                    connectionEnd = established ? ConnectionEnd.FailedAfterConnect : ConnectionEnd.FailedBeforeConnect;
                    SetStatus("Failed", LogStartError(failure));
                }
                else if (closing.RecordingError is { } problem) SetStatus(statusKey, problem);
            }
            resizeTimer.Stop();
            foreach (var pane in panes.Keys) pane.SetConnected(false);
            IsRunning = false;
            stop.Dispose();
            CaptionChanged?.Invoke();
            if (IsDisconnected) Disconnected?.Invoke();
        }
    }

    internal Task ReleaseRemovedOutputAsync()
    {
        removed = true;
        frameTimer.Stop();
        searchRefreshTimer.Stop();
        searchWork?.Cancel();
        searchMatches = [];
        searchMemory.Dispose();
        paneLayout?.ReleaseReservations();
        foreach (var pane in panes.Values) pane.Highlight.Detach();
        pendingFrame = null;
        var owner = session ?? retainedAiSession;
        retainedAiSession = null;
        return owner?.ReleaseRetainedOutputAsync() ?? Task.CompletedTask;
    }

    private async Task ReceiveOutputAsync(TerminalSession active)
    {
        // SSH owns its receive cutoff and drains its admitted generation through
        // peer CLOSE/grace even after the UI stops admitting new input.
        CancellationToken readLifetime = active.IsSsh ? CancellationToken.None : stop.Token;
        while (await active.ProcessOutputAsync(readLifetime).ConfigureAwait(false))
        {
            if (Interlocked.Exchange(ref receivedRevision, 1) == 0)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!ReferenceEquals(session, active) || removed) return;
                    ScheduleFrame();
                    if (active.RecordingStartFailure is { } failure) SetStatus("Failed", LogStartError(failure));
                }));
        }
    }

    private async Task SendMouseAsync(TerminalSession active)
    {
        try
        {
            await foreach (TerminalMouseInput input in mouseInput.Reader.ReadAllAsync(stop.Token))
            {
                try { await active.SendMouseAsync(input, stop.Token); }
                finally { Interlocked.Decrement(ref pendingMouse); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException
            or CryptographicException or ObjectDisposedException)
        {
            if (!stop.IsCancellationRequested)
            {
                SetStatus("Failed", exception.Message);
                Disconnect();
            }
        }
    }

    private void ScheduleResize()
    {
        if (session is null || !output.IsPresentationActive || GridHost.ActualWidth <= 0 ||
            GridHost.ActualHeight <= 0 || EffectiveGrid(session.RetainedRows) == appliedGrid) return;
        resizeTimer.Stop();
        resizeTimer.Start();
    }

    private async Task ResizeTerminalAsync()
    {
        resizeTimer.Stop();
        if (session is not { } active || !output.IsPresentationActive || stop.IsCancellationRequested ||
            GridHost.ActualWidth <= 0 || GridHost.ActualHeight <= 0) return;
        var grid = EffectiveGrid(active.RetainedRows);
        if (grid == appliedGrid) return;
        appliedGrid = grid;
        try
        {
            IReadOnlyList<string> frame = await active.ResizeAsync(grid.Columns, grid.Rows, stop.Token);
            if (session == active) Display(frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException
            or CryptographicException)
        {
            if (IsRunning) SetStatus("Connected", exception.Message);
        }
    }

    private (int Columns, int Rows) EffectiveGrid(int retention)
    {
        if (fixedViewport is { } size) return size;
        var (columns, rows) = output.ViewportGrid(GridHost.ActualWidth, GridHost.ActualHeight);
        return (columns, Math.Min(rows, retention));
    }

    private Task<bool> ApproveHostAsync(SshHostKey key, CancellationToken token) =>
        OnSshPromptAsync((app, owner) => app.HostTrust.ApproveAsync(owner, key, token), token);

    private Task<VaultSecret?> RequestSshPrivateKeyAsync(SshPrivateKeyRequest request, CancellationToken token) =>
        OnSshPromptAsync((app, owner) => app.Vault.ReadPrivateKeyAsync(owner, request, token),
            token, serialize: false);

    private Task<VaultSecret?> RequestSshPasswordAsync(SshPasswordRequest request, CancellationToken token)
    {
        if (request.Attempt == 1 && request.SavedPassword is { } reference)
            return OnSshPromptAsync((app, owner) => app.Vault.ReadSavedPasswordAsync(owner, reference, token),
                token, serialize: false);
        return OnSshPromptAsync<VaultSecret?>((app, owner) =>
        {
            var form = new MainWindow.FormWindow(owner, Text("SshPasswordSource"));
            var password = new PasswordBox { MaxLength = 16 * 1024 };
            form.AddField("SshDestination", new TextBox { Text = request.Destination.ToString(), IsReadOnly = true });
            form.AddField("SshUsername", new TextBox { Text = PermissionDisplay.Literal(request.Username), IsReadOnly = true });
            form.AddField("VaultPassword", password);
            form.Loaded += (_, _) => password.Focus();
            form.AddButtons(() => { }, acceptKey: "Authenticate");
            try
            {
                if (SshHostTrust.ShowCancellable(app, form, token) != true) return Task.FromResult<VaultSecret?>(null);
                return Task.FromResult<VaultSecret?>(EncodeSecret(VaultController.ReadPassword(password), token));
            }
            finally { password.Clear(); }
        }, token);
    }

    internal static (MainWindow.FormWindow Form, List<Control> Inputs) CreateSshChallengeForm(Window owner, SshChallenge challenge)
    {
        var form = new MainWindow.FormWindow(owner, Text("KeyboardInteractive")) { Width = 540 };
        form.AddPinnedNotice(string.Format(CultureInfo.CurrentCulture, Text("SshServerQuestions"), challenge.Destination));
        if (challenge.Name.Length > 0 || challenge.Instruction.Length > 0)
            form.AddField("SshInstructions", new TextBox
            {
                Text = string.Join(Environment.NewLine, new[] { challenge.Name, challenge.Instruction }
                    .Where(value => value.Length > 0)),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        var inputs = new List<Control>();
        foreach (SshPrompt prompt in challenge.Prompts)
        {
            Control input = prompt.Echo
                ? new TextBox { MaxLength = 8192, IsUndoEnabled = false }
                : new PasswordBox { MaxLength = 8192 };
            form.AddPrompt(prompt.Text, input);
            inputs.Add(input);
        }
        form.Loaded += (_, _) => inputs.FirstOrDefault()?.Focus();
        form.AddButtons(() => { }, acceptKey: "SendSshAnswers");
        return (form, inputs);
    }

    private Task<VaultSecret[]?> RequestSshChallengeAsync(SshChallenge challenge, CancellationToken token) =>
        OnSshPromptAsync<VaultSecret[]?>((app, owner) =>
        {
            var (form, inputs) = CreateSshChallengeForm(owner, challenge);
            var answers = new List<VaultSecret>();
            try
            {
                if (SshHostTrust.ShowCancellable(app, form, token) != true)
                    return Task.FromResult<VaultSecret[]?>(null);
                foreach (Control input in inputs)
                    answers.Add(EncodeSecret(input is PasswordBox password ? VaultController.ReadPassword(password) :
                        ((TextBox)input).Text.ToCharArray(), token));
                return Task.FromResult<VaultSecret[]?>(answers.ToArray());
            }
            catch
            {
                foreach (VaultSecret answer in answers) answer.Dispose();
                throw;
            }
            finally
            {
                foreach (Control input in inputs)
                {
                    if (input is PasswordBox password) password.Clear();
                    else ((TextBox)input).Clear();
                }
            }
        }, token);

    private static VaultSecret EncodeSecret(char[] characters, CancellationToken token)
    {
        try
        {
            var encoding = new UTF8Encoding(false, true);
            byte[] bytes = new byte[encoding.GetByteCount(characters)];
            try
            {
                encoding.GetBytes(characters, bytes);
                token.ThrowIfCancellationRequested();
                return new VaultSecret(bytes);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }
        finally { Array.Clear(characters); }
    }

    private async Task<T> OnSshPromptAsync<T>(Func<App, Window, Task<T>> prompt, CancellationToken token,
        bool serialize = true)
    {
        var app = (App)Application.Current;
        if (serialize) await app.SshPrompts.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                Window? owner = Macro is null ? Window.GetWindow(this) : MacroWindow;
                if (owner is null || token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (Macro is not null && owner is MainWindow main) main.RestoreMacroHost();
                return prompt(app, owner);
            }).Task.Unwrap().ConfigureAwait(false);
        }
        finally { if (serialize) app.SshPrompts.Release(); }
    }

    public void Disconnect()
    {
        Macro?.Stop("Disconnected");
        AiCollaboration.EndControl(this);
        if (!IsRunning) return;
        transferWindow?.ConnectionEnded();
        stop.Cancel();
        session?.Disconnect();
        Broadcast.Refresh();
    }

    public void StopPaste() => pasteReview?.Cancel();
    internal RecordingStatus? RecordingCloseStatus => session?.RecordingStatus ?? lastRecordingStatus;
    public bool RecordingReleasePending => session?.RecordingReleasePending == true;
    public void ProtectRecordingClose() => session?.ProtectRecordingClose();
    public bool AbandonCleanup() => session?.AbandonCleanup() == true;

    public void ShowConnectionCommands()
    {
        if (session is not { } active || stop.IsCancellationRequested) return;
        if (HasPendingInput)
        {
            SetStatus("Connected", Text("InputBusy"));
            return;
        }
        var menu = new ContextMenu();
        if (InputSession is not null && (active.IsSerial || active.IsTelnet)) Add("SendBreak", () => active.SendBreakAsync(stop.Token));
        if (InputSession is not null && active.IsSerial)
        {
            if (active.CanSetDtr)
            {
                Add("DtrOn", () => SetLine(true, true));
                Add("DtrOff", () => SetLine(true, false));
            }

            if (active.CanSetRts)
            {
                Add("RtsOn", () => SetLine(false, true));
                Add("RtsOff", () => SetLine(false, false));
            }
        }
        else if (InputSession is not null && active.IsTelnet)
        {
            Add("AreYouThere", () => active.SendAreYouThereAsync(stop.Token));
        }
        if (active.SupportsEchoChange)
        {
            var echo = new MenuItem { Header = Text("LocalEcho"), ToolTip = Text("LocalEchoExplanation") };
            foreach (string policy in active.IsSerial ? new[] { "off", "on" } : new[] { "off", "followRemote" })
            {
                var item = new MenuItem { Header = Text(policy == "off" ? "Off" : policy == "on" ? "On" : "FollowRemote"),
                    IsCheckable = true, IsChecked = active.EchoPolicy == policy };
                item.Click += async (_, _) =>
                {
                    try { await active.SetLocalEchoAsync(policy, stop.Token); UpdateStatusText(); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (IOException exception) { SetStatus(statusKey, exception.Message); }
                };
                echo.Items.Add(item);
            }
            menu.Items.Add(echo);
        }
        var details = new MenuItem { Header = Text("ConnectionDetails") };
        details.Click += (_, _) =>
        {
            if (Window.GetWindow(this) is not { } owner) return;
            var form = new MainWindow.FormWindow(owner, Text("ConnectionDetails")) { Width = 650 };
            form.AddField("ConnectionDetails", new TextBox { Text = active.ConnectionDetails, IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            form.AddButtons(() => { }, acceptKey: "CloseDialog");
            ((App)Application.Current).Modal(form.ShowDialog);
        };
        menu.Items.Add(details);

        menu.IsOpen = true;

        Task SetLine(bool dtr, bool enabled)
        {
            active.SetSerialLine(dtr, enabled);
            return Task.CompletedTask;
        }

        void Add(string key, Func<Task> command)
        {
            var item = new MenuItem { Header = Text(key) };
            item.Click += async (_, _) =>
            {
                if (InputSession != active) return;
                var generation = stop;
                pendingSends++;
                try { await command(); }
                catch (OperationCanceledException) when (generation.IsCancellationRequested) { }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    if (ReferenceEquals(stop, generation) && !generation.IsCancellationRequested)
                        SetStatus("Connected", exception.Message);
                }
                finally { if (ReferenceEquals(stop, generation)) pendingSends--; }
            };
            menu.Items.Add(item);
        }
    }

    public async Task ChangeRecordingAsync(bool resume)
    {
        if (session is not { RecordsOutput: true } active || stop.IsCancellationRequested) return;
        try
        {
            var outcome = await active.ChangeRecordingAsync(resume, stop.Token);
            if (!ReferenceEquals(session, active)) return;
            SetStatus("Connected", outcome.Status.Error ?? Text(resume ? "RecordingResumed" : "RecordingPaused"));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            if (IsRunning && ReferenceEquals(session, active))
            {
                if (active.RecordingStartFailure is { } failure) SetStatus("Failed", LogStartError(failure));
                else SetStatus("Connected", exception.Message);
            }
        }
    }

    public void UpdateLabels()
    {
        UpdateMacroControls();
        UpdateAiControls();
        UpdateStatusText();
        CaptionChanged?.Invoke();
        UpdateHighlightStatus();
        AutomationProperties.SetName(searchQuery, Text("Search"));
        matchCase.Content = Text("MatchCase");
        previousMatch.Content = Text("PreviousMatch");
        nextMatch.Content = Text("NextMatch");
        closeSearch.Content = Text("CloseSearch");
        UpdateSearchStatusText();
        paneLayout?.UpdateLabels();
        foreach (var pane in panes.Keys) UpdatePaneMenu(pane);
    }

    private void UpdatePaneMenu(TerminalSurface pane)
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = Text("Copy") };
        copy.Click += (_, _) => pane.Copy();
        var paste = new MenuItem { Header = Text("Paste") };
        paste.Click += async (_, _) => { ActivatePane(pane); await PasteClipboardAsync(); };
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        var search = new MenuItem { Header = Text("Search"), InputGestureText = "Ctrl+Shift+F" };
        search.Click += (_, _) => { ActivatePane(pane); OpenSearch(); };
        menu.Items.Add(search);
        var messages = new MenuItem { Header = Text("SshMessages") };
        messages.Click += (_, _) => ShowSshMessages();
        menu.Items.Add(messages);
        var resumeHighlight = new MenuItem { Header = Text("ResumeHighlighting") };
        resumeHighlight.Click += (_, _) => panes[pane].Highlight.Resume();
        menu.Items.Add(resumeHighlight);
        menu.Opened += (_, _) =>
        {
            while (menu.Items.Count > 5) menu.Items.RemoveAt(5);
            var splits = new MenuItem { Header = Text("TerminalPanes") };
            FillPaneCommands(splits, pane);
            menu.Items.Add(splits);
        };
        pane.ContextMenu = menu;
    }

    public void Zoom(int direction)
    {
        zoomFontSize = direction == 0 ? baseFontSize : Math.Clamp(zoomFontSize + direction, 6, 72);
        ConfigurePaneFonts();
        ScheduleResize();
        UpdateStatusText();
    }

    public void ShowSearch() => OpenSearch();
    public void CopySelection() => output.Copy();
    public Task PasteAsync() => PasteClipboardAsync();

    private void ShowSshMessages()
    {
        if (Window.GetWindow(this) is not { } owner) return;
        var form = new MainWindow.FormWindow(owner, Text("SshMessages")) { Width = 650 };
        form.AddField("SshMessages", new TextBox
        {
            Text = string.Join(Environment.NewLine, sshMessages), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        form.AddButtons(() => { }, acceptKey: "CloseDialog");
        ((App)Application.Current).Modal(form.ShowDialog);
    }

    private void SetStatus(string key, string? detail = null)
    {
        bool changed = statusKey != key;
        statusKey = key;
        error = detail;
        UpdateStatusText();
        if (changed) CaptionChanged?.Invoke();
    }

    private static string LogStartError(string message) => string.Format(Text("RecordingStartFailed"),
        message == RecordingDestinationChangedException.Description ? Text("RecordingDestinationChanged") : message);

    private void UpdateStatusText()
    {
        string? detail = error == RecordingDestinationChangedException.Description
            ? Text("RecordingDestinationChanged") : error;
        string text = detail is null ? Text(statusKey) : $"{Text(statusKey)} — {detail}";
        if (session is { } active)
        {
            if (active.IsTelnet) text = $"{Text("TelnetUnencrypted")}   {text}";
            text += $"   {Text("LocalEcho")} {Text(active.LocalEchoEnabled ? "On" : "Off")}";
        }
        if (output.UsesFontFallback) text += "   " + string.Format(Text("FontFallback"), output.AutomationFontFamily);
        if (Equals(status.ToolTip, text)) return;
        status.Text = text.ReplaceLineEndings(" ");
        status.ToolTip = text;
        AutomationProperties.SetName(status, text);
    }

    internal void SetBroadcastStatus(string text)
    {
        broadcastTabStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (broadcastTabStatus.Text == text) return;
        broadcastTabStatus.Text = text;
        broadcastTabStatus.ToolTip = text;
        AutomationProperties.SetName(broadcastTabStatus, text);
    }

    private void RestoreStatusBarColors()
    {
        statusBar.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        statusBar.SetResourceReference(TextElement.ForegroundProperty, "WindowForeground");
    }

    private static void ConfigureStatusIndicator(TextBlock indicator)
    {
        indicator.Padding = new Thickness(6, 0, 6, 0);
        indicator.VerticalAlignment = VerticalAlignment.Center;
        indicator.TextTrimming = TextTrimming.CharacterEllipsis;
        indicator.Focusable = true;
        ToolTipService.SetShowsToolTipOnKeyboardFocus(indicator, true);
    }

    private void NotifyDestinationChange(TerminalSession connected, ConnectionDestination previous,
        ConnectionDestination current, CancellationToken token)
    {
        // Start receiving before displaying the notice, including for a background tab.
        Dispatcher.BeginInvoke(() =>
        {
            if (session != connected || token.IsCancellationRequested || removed || ClosePending) return;
            Window? owner = Window.GetWindow(this) ?? AiHost;
            if (owner is null) return;
            string body = string.Format(Text("ReconnectDestinationDetails"), PermissionDisplay.Literal(connected.DisplayName),
                previous, current);
            var dialog = ThemedMessageBox.Create(owner, body, Text("ReconnectDestinationChanged"),
                MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, _ => { }, notification: true);
            destinationNotice = dialog;
            dialog.Closed += (_, _) => { if (destinationNotice == dialog) destinationNotice = null; };
            dialog.Show();
        });
    }

    private void ApplyHighlightSet()
    {
        var rules = fixtureHighlights ?? ((App)Application.Current).Highlights.Find(highlightSetId);
        highlightState.SetRules(rules);
        UpdateHighlightStatus();
    }

    private void UpdateHighlightStatus()
    {
        bool missing = highlightSetId is not null && ((App)Application.Current).Highlights.Find(highlightSetId) is null;
        highlightStatus.Visibility = highlightSuspended || missing ? Visibility.Visible : Visibility.Collapsed;
        highlightStatus.Text = Text(missing ? "HighlightSetMissing" : "HighlightRuleSuspended");
        highlightStatus.ToolTip = highlightStatus.Text;
        AutomationProperties.SetName(highlightStatus, highlightStatus.Text);
    }

    private void Display(IReadOnlyList<string> rows)
    {
        pendingFrame = rows;
        ScheduleFrame();
    }

    private async Task CaptureAndRenderFrameAsync()
    {
        frameTimer.Stop();
        if (!output.IsPresentationActive || capturingFrame) return;
        if (session is { } active && Interlocked.Exchange(ref receivedRevision, 0) != 0)
        {
            capturingFrame = true;
            try
            {
                IReadOnlyList<string> frame = await active.GetViewportOutputAsync();
                if (!removed && session == active) pendingFrame = frame;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
            {
                if (session == active) SetStatus(statusKey, exception.Message);
            }
            finally { capturingFrame = false; }
        }
        if (output.IsPresentationActive && pendingFrame is { } ready)
        {
            pendingFrame = null;
            Render(ready);
        }
        ScheduleFrame();
    }

    internal bool HasPendingFrame => pendingFrame is not null || capturingFrame || Volatile.Read(ref receivedRevision) != 0;

    private void ScheduleFrame(bool immediately = false)
    {
        if (removed || !output.IsPresentationActive || capturingFrame || immediateFrameQueued ||
            pendingFrame is null && (session is null || Volatile.Read(ref receivedRevision) == 0)) return;
        if (immediately)
        {
            frameTimer.Stop();
            immediateFrameQueued = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
            {
                immediateFrameQueued = false;
                await CaptureAndRenderFrameAsync();
            }));
        }
        else if (!frameTimer.IsEnabled)
        {
            frameTimer.Start();
        }
    }

    private void Render(IReadOnlyList<string> rows)
    {
        UpdateStatusText();
        bool wasAlternate = snapshot?.IsAlternateScreen == true;
        TerminalSnapshot? updated = rows as TerminalSnapshot;
        foreach (var pane in panes.Values)
            pane.CurrentMatch = snapshot is { } old && pane.CurrentMatch is { } match ? updated?.MapMatchFrom(old, match) : null;
        if (snapshot is { } previous && updated is not null)
            SetSearchMatches(searchMatches.Select(match => updated.MapMatchFrom(previous, match))
                .OfType<TerminalMatch>().ToArray());
        if (snapshot is { } prior && updated is not null && searchBar.Visibility == Visibility.Visible &&
            !updated.HasSameHighlightText(prior))
            ScheduleSearchRefresh();
        snapshot = updated;
        foreach (var (surface, pane) in panes)
        {
            if (updated is not null)
            {
                surface.UpdateSnapshotPosition(updated);
                pane.Highlight.SetSnapshot(updated);
            }
            surface.SetSearch(pane.CurrentMatch, searchMatches, scroll: false);
        }
        if (searchBar.Visibility == Visibility.Visible)
        {
            if (snapshot?.IsAlternateScreen == true)
            {
                searchWork?.Cancel();
                SetSearchStatus("SearchNormalScreen");
            }
            else if (wasAlternate)
            {
                SetSearchStatus(null);
            }
            UpdateSearchButtons();
        }
    }

    private void OpenSearch()
    {
        searchBar.Visibility = Visibility.Visible;
        SetSearchStatus(snapshot?.IsAlternateScreen == true ? "SearchNormalScreen" : null);
        UpdateSearchButtons();
        searchQuery.Focus();
        searchQuery.SelectAll();
    }

    private void CloseSearch()
    {
        searchRefreshTimer.Stop();
        searchWork?.Cancel();
        searchBar.Visibility = Visibility.Collapsed;
        foreach (var pane in panes.Values) pane.CurrentMatch = null;
        SetSearchMatches([]);
        foreach (var pane in panes.Keys) pane.SetSearch(null, [], scroll: false);
        output.Focus();
    }

    private void UpdateSearchButtons()
    {
        previousMatch.IsEnabled = nextMatch.IsEnabled =
            snapshot is { IsAlternateScreen: false } && searchQuery.Text.Length > 0;
    }

    private int? searchMatchCount;

    private void SetSearchStatus(string? key, int? count = null)
    {
        searchStatusKey = key;
        searchMatchCount = count;
        UpdateSearchStatusText();
    }

    private void UpdateSearchStatusText() => searchStatus.Text = searchStatusKey is { } key ? Text(key) :
        searchMatchCount is { } count ? Math.Min(count, TerminalSearch.Limit).ToString("N0") +
            (count > TerminalSearch.Limit ? "+" : "") : "";

    private async Task RefreshSearchAsync()
    {
        foreach (var pane in panes.Values) pane.CurrentMatch = null;
        SetSearchMatches([]);
        foreach (var pane in panes.Keys) pane.SetSearch(null, [], scroll: false);
        UpdateSearchButtons();
        if (searchBar.Visibility == Visibility.Visible) await SearchAsync(backwards: false);
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
        else if (e.Key == Key.Enter && searchQuery.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            await SearchAsync(backwards: Keyboard.Modifiers == ModifierKeys.Shift);
        }
    }

    private void ScheduleSearchRefresh()
    {
        if (removed || searchBar.Visibility != Visibility.Visible || searchQuery.Text.Length == 0) return;
        searchRefreshTimer.Stop();
        searchRefreshTimer.Start();
    }

    private async Task SearchAsync(bool backwards, bool navigate = true)
    {
        searchRefreshTimer.Stop();
        if (removed || searchBar.Visibility != Visibility.Visible) return;
        searchWork?.Cancel();
        if (snapshot is not { IsAlternateScreen: false } captured || searchQuery.Text.Length == 0)
        {
            SetSearchStatus(snapshot?.IsAlternateScreen == true ? "SearchNormalScreen" : null);
            return;
        }
        using var work = new CancellationTokenSource();
        searchWork = work;
        string query = searchQuery.Text;
        bool caseSensitive = matchCase.IsChecked == true;
        TerminalSurface operated = output;
        TerminalMatch? anchor = panes[operated].CurrentMatch;
        if (navigate && anchor is null && captured.Count != 0)
        {
            var (first, end) = operated.VisibleRows();
            int row = Math.Min(backwards ? Math.Max(first, end - 1) : first, captured.Count - 1);
            int offset = backwards ? int.MaxValue : -1;
            anchor = new TerminalMatch(row, offset, row, offset);
        }
        SetSearchStatus("Searching");
        try
        {
            await TerminalHighlightView.Workers.WaitAsync(work.Token);
            var viewports = panes.Keys.Select(surface => surface.VisibleRows()).ToArray();
            try
            {
                using var ranges = ApplicationResources.SearchCache.Reserve(TerminalSearch.Limit * 64);
                Func<TerminalMatch?, Task>? announce = navigate
                    ? match => Dispatcher.InvokeAsync(() => Navigate(match), DispatcherPriority.Normal, work.Token).Task
                    : null;
                var result = await Task.Run(() => TerminalSearch.ScanAsync(captured, query, caseSensitive,
                    anchor, backwards, viewports, announce, work.Token), work.Token);
                if (!IsCurrent())
                {
                    if (!work.IsCancellationRequested) ScheduleSearchRefresh();
                    return;
                }
                if (!viewports.SequenceEqual(panes.Keys.Select(surface => surface.VisibleRows())))
                    ScheduleSearchRefresh();
                SetSearchMatches(result.Matches);
                SetSearchStatus(result.Count == 0 ? "NoSearchMatch" : null, result.Count);
                foreach (var (surface, pane) in panes) surface.SetSearch(pane.CurrentMatch, searchMatches, scroll: false);
            }
            finally { TerminalHighlightView.Workers.Release(); }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (ResourceLimitException) { if (IsCurrent()) SetSearchStatus(null); }
        finally
        {
            if (searchWork == work) searchWork = null;
        }
        bool IsCurrent() => !removed && !work.IsCancellationRequested && ReferenceEquals(searchWork, work) &&
            snapshot is { IsAlternateScreen: false } current && current.HasSameHighlightText(captured);

        void Navigate(TerminalMatch? found)
        {
            if (!IsCurrent() || !panes.TryGetValue(operated, out var pane)) return;
            pane.CurrentMatch = found;
            operated.SetSearch(found, searchMatches, scroll: found.HasValue);
        }
    }

    private void SetSearchMatches(TerminalMatch[] matches)
    {
        if (removed) return;
        try { searchMemory.Resize(matches.LongLength * 128); }
        catch (ResourceLimitException) { matches = []; searchMemory.Resize(0); }
        searchMatches = matches;
    }

    private async void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Handled || InputSession is null || e.Text.Length == 0) return;
        ActivatePane((TerminalSurface)sender);
        // Let the WPF text store finish its IME transaction and deliver any queued updates.
        if (e.TextComposition.AutoComplete != TextCompositionAutoComplete.Off) e.Handled = true;
        string text = e.Text;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        await SendAsync(new(TerminalInputKind.Text, text, Alt: alt));
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        ActivatePane((TerminalSurface)sender);
        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F4 && modifiers.HasFlag(ModifierKeys.Alt)) return;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.F)
        {
            e.Handled = true;
            OpenSearch();
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C)
        {
            e.Handled = true;
            output.Copy();
            return;
        }

        if (InputSession is not { } active) return;
        if ((active.ApplicationKeypad || IsBroadcastActive) && modifiers == ModifierKeys.None)
        {
            TerminalKey? keypad = key switch
            {
                >= Key.NumPad0 and <= Key.NumPad9 => TerminalKey.Keypad0 + (key - Key.NumPad0),
                Key.Decimal => TerminalKey.KeypadDecimal,
                Key.Enter when NativeKeyState.KeypadEnter => TerminalKey.KeypadEnter,
                Key.Multiply => TerminalKey.KeypadMultiply,
                Key.Add => TerminalKey.KeypadAdd,
                Key.Subtract => TerminalKey.KeypadSubtract,
                Key.Divide => TerminalKey.KeypadDivide,
                _ => null,
            };
            if (keypad is { } mappedKeypad)
            {
                e.Handled = true;
                await SendAsync(new(TerminalInputKind.Key, Key: mappedKeypad));
                return;
            }
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.V)
        {
            e.Handled = true;
            await PasteClipboardAsync();
            return;
        }

        TerminalInput? send = (key, modifiers) switch
        {
            (Key.Enter, ModifierKeys.None) => new(TerminalInputKind.Enter),
            (Key.Back, ModifierKeys.None) => new(TerminalInputKind.Backspace),
            (Key.Tab, ModifierKeys.None) => new(TerminalInputKind.Tab),
            (>= Key.A and <= Key.Z, ModifierKeys.Control) =>
                new(TerminalInputKind.Control, ((char)('A' + key - Key.A)).ToString()),
            _ => null,
        };
        TerminalKey? navigation = key switch
        {
            Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down, Key.Left => TerminalKey.Left,
            Key.Right => TerminalKey.Right, Key.Home => TerminalKey.Home, Key.End => TerminalKey.End,
            Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete, Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown, Key.Escape => TerminalKey.Escape,
            Key.Tab when modifiers == ModifierKeys.Shift => TerminalKey.BackTab,
            >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
            _ => null,
        };
        if (send is null && navigation is { } mapped)
        {
            TerminalModifiers flags = TerminalModifiers.None;
            if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= TerminalModifiers.Shift;
            if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= TerminalModifiers.Alt;
            if (modifiers.HasFlag(ModifierKeys.Control)) flags |= TerminalModifiers.Control;
            send = new(TerminalInputKind.Key, Key: mapped, Modifiers: flags);
        }
        if (send is null) return;
        e.Handled = true;
        await SendAsync(send.Value);
    }

    private async Task PasteClipboardAsync()
    {
        if (InputSession is not { } active || HasPendingInput || Window.GetWindow(this) is not { } owner) return;
        var app = (App)Application.Current;
        var operated = output;
        CancellationToken sourceLifetime = stop.Token;
        if (pasteReview is not null) return;
        using var cancellation = new CancellationTokenSource();
        using var review = CancellationTokenSource.CreateLinkedTokenSource(sourceLifetime, cancellation.Token);
        pasteReview = cancellation;
        long maximum = app.MaxPasteTextBytes;
        try
        {
            if (owner is MainWindow host)
            {
                var broadcast = await app.Broadcast.PasteFromClipboardAsync(host, this, CaptureAsync,
                    (text, preview, token) => Dispatcher.InvokeAsync(() =>
                        PresentPaste(owner, text, preview.Pacing, preview.Targets, token),
                        DispatcherPriority.Normal, token).Task, review.Token);
                if (broadcast != BroadcastInput.PasteOutcome.Inactive)
                {
                    if (broadcast == BroadcastInput.PasteOutcome.Submitted && session == active && !sourceLifetime.IsCancellationRequested)
                        operated.RevealCursor();
                    return;
                }
            }
            using var paste = await CaptureAsync(review.Token);
            if (paste is null || !SingleTargetCurrent()) return;
            var (characterDelay, lineDelay, remember) = app.PasteDefaults;
            if (paste.RequiresConfirmation)
            {
                var choice = PresentPaste(owner, paste, (characterDelay, lineDelay, remember), null, review.Token);
                if (choice is null || !SingleTargetCurrent()) return;
                (characterDelay, lineDelay, remember) = choice;
                app.PasteDefaults = remember ? (characterDelay, lineDelay, true) : default;
            }

            pasteStop = cancellation;
            try
            {
                SetStatus("Connected", Text("Pasting"));
                int characters = paste.CharacterCount;
                var progress = new Progress<int>(sent =>
                {
                    if (ReferenceEquals(pasteStop, cancellation) && session == active && !sourceLifetime.IsCancellationRequested)
                        SetStatus("Connected", string.Format(CultureInfo.CurrentCulture, Text("PasteProgress"), sent,
                            characters));
                });
                operated.RevealCursor();
                bool completed = await active.SendPasteAsync(paste, characterDelay, lineDelay,
                    cancellation.Token, sourceLifetime, progress);
                if (session == active && !sourceLifetime.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref receivedRevision, 1);
                    ScheduleFrame();
                    SetStatus("Connected", completed ? active.RecordingError : Text("PasteStopped"));
                }
            }
            finally
            {
                if (ReferenceEquals(pasteStop, cancellation)) pasteStop = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ArgumentException
            or InvalidOperationException or ExternalException)
        {
            if (session == active && !sourceLifetime.IsCancellationRequested)
                SetStatus("Connected", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(pasteReview, cancellation)) pasteReview = null;
        }

        bool SingleTargetCurrent() => Window.GetWindow(this) == owner &&
            (owner is not MainWindow window || !app.Broadcast.IsActive(window)) &&
            active == InputSession && !HasPendingInput && !review.IsCancellationRequested;

        async Task<TerminalPaste?> CaptureAsync(CancellationToken token)
        {
            var value = await captureClipboard(owner, maximum, token);
            if (active == InputSession && !HasPendingInput && !token.IsCancellationRequested) return value;
            value?.Dispose();
            return null;
        }
    }

    private static BroadcastInput.PasteDecision? PresentPaste(Window owner, TerminalPaste paste,
        (int Character, int Line, bool Remember) pacing, IReadOnlyList<string>? targets, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        var app = (App)Application.Current;
        var (characterDelay, lineDelay, remember) = pacing;
        var form = new MainWindow.FormWindow(owner, Text("PastePreview")) { Width = 680 };
        string summary = string.Format(CultureInfo.CurrentCulture, Text("PasteCounts"),
            paste.CharacterCount, paste.LineCount,
            Text(paste.EndsWithNewline ? "TrailingNewline" : "NoTrailingNewline"));
        const int pageUnits = 8192;
        int page = 0;
        int pages = 1 + (paste.Text.Length - 1) / pageUnits;
        // Each source page can expand into visible Unicode markers and line breaks.
        // Include escaped captions, their joined text and the read-only controls.
        long captionMemory = targets?.Sum(name => 128L + 64L * name.Length) ?? 0;
        using var previewMemory = ApplicationResources.TerminalState.Reserve(1024 * 1024 + captionMemory);
        var previewText = new TextBox
        {
            IsReadOnly = true,
            IsUndoEnabled = false,
            AcceptsReturn = true,
            MaxHeight = 260,
            MinHeight = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var pageLabel = new TextBox { IsReadOnly = true };
        form.AddField("PastePreview", previewText);
        if (pages > 1)
        {
            form.AddField("PastePreviewPageLabel", pageLabel);
            form.AddAction("PastePreviousPage", () => { if (page > 0) { page--; ShowPage(); } });
            form.AddAction("PasteNextPage", () => { if (page < pages - 1) { page++; ShowPage(); } });
        }
        ShowPage();
        void ShowPage()
        {
            int start = PageBoundary(page);
            int end = page == pages - 1 ? paste.Text.Length : PageBoundary(page + 1);
            previewText.Text = PermissionDisplay.PastePage(paste.Text.AsSpan(start, end - start));
            var position = string.Format(CultureInfo.CurrentCulture, Text("PastePreviewPage"), page + 1, pages);
            pageLabel.Text = position;
            AutomationProperties.SetName(previewText, Text("PastePreview") + " " + position);
        }

        int PageBoundary(int value)
        {
            int offset = value * pageUnits;
            if (offset != 0 && (char.IsLowSurrogate(paste.Text[offset]) ||
                paste.Text[offset] == '\n' && paste.Text[offset - 1] == '\r')) offset--;
            return offset;
        }

        form.AddField("PasteCountsLabel", new TextBox { Text = summary, IsReadOnly = true });
        if (targets is not null)
            form.AddField("BroadcastTargets", new TextBox
            {
                Text = string.Join(Environment.NewLine, targets), TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        var character = new TextBox { Text = characterDelay.ToString(CultureInfo.InvariantCulture) };
        var line = new TextBox { Text = lineDelay.ToString(CultureInfo.InvariantCulture) };
        var rememberDelays = new CheckBox { IsChecked = remember };
        form.AddField("CharacterDelay", character);
        form.AddField("LineDelay", line);
        form.AddField("RememberDelays", rememberDelays);
        form.AddButtons(() =>
        {
            if (!int.TryParse(character.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedCharacter) ||
                !int.TryParse(line.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedLine) ||
                parsedCharacter is < 0 or > 10000 || parsedLine is < 0 or > 10000)
                throw new ArgumentException(Text("BadDelay"));
            characterDelay = parsedCharacter;
            lineDelay = parsedLine;
            remember = rememberDelays.IsChecked == true;
        }, acceptKey: "SendPaste", cancelDefault: true);
        using var close = token.Register(() => form.Dispatcher.BeginInvoke(() =>
        {
            if (form.IsVisible) form.Close();
        }));
        return app.Modal(form.ShowDialog) == true && !token.IsCancellationRequested
            ? new(characterDelay, lineDelay, remember) : null;
    }

    private async Task SendAsync(TerminalInput input)
    {
        if (InputSession is not { } active) return;
        if (pasteStop is not null)
        {
            SetStatus("Connected", Text("InputBusy"));
            return;
        }
        var generation = stop;
        var lifetime = generation.Token;
        pendingSends++;
        try
        {
            TerminalSurface operated = output;
            if (Window.GetWindow(this) is MainWindow host && Broadcast.Send(host, input, lifetime) is { } send)
            {
                if (await send && session == active && !lifetime.IsCancellationRequested) operated.RevealCursor();
                return;
            }
            await active.DeliverInputAsync(input, lifetime);
            if (session == active && !lifetime.IsCancellationRequested)
            {
                operated.RevealCursor();
                Interlocked.Exchange(ref receivedRevision, 1);
                ScheduleFrame();
                SetStatus("Connected", session?.RecordingError);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ArgumentException or InvalidOperationException)
        {
            if (session == active && !lifetime.IsCancellationRequested) SetStatus("Connected", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(stop, generation)) pendingSends--;
        }
    }
}
