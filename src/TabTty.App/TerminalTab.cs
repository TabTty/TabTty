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
    private readonly TextBlock recordingStatus = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock highlightStatus = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock pasteProgressStatus = new() { Visibility = Visibility.Collapsed };
    private readonly DockPanel statusBar;
    private const double SearchInset = 4;
    private readonly Canvas terminalOverlays = new();
    internal NoticeStack Notices { get; } = new();
    private readonly WrapPanel searchBar = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox searchQuery = new() { Width = 220, MaxLength = 1024, Margin = new Thickness(3) };
    private readonly CheckBox matchCase = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
    private readonly Button previousMatch = new() { Margin = new Thickness(3) };
    private readonly Button nextMatch = new() { Margin = new Thickness(3) };
    private readonly Button closeSearch = new() { Margin = new Thickness(3) };
    private readonly TextBlock searchStatus = new()
    {
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3), TextWrapping = TextWrapping.Wrap,
    };
    private TerminalSurface output = new();
    private Guid? highlightSetId;
    private readonly TerminalHighlightState highlightState = new();
    private readonly TerminalHighlightView visibleHighlights;
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
    private readonly HashSet<int> reportedRecordingParts = [];
    private FileTransferWindow? transferWindow;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TerminalSession, FileOperationOwnership> transferOwnership = new();
    private string? connectedName;
    private ConnectionDestination? lastDestination;
    private Func<MainWindow?>? tabHost;
    internal void ResolveTabHost(Func<MainWindow?> resolve) => tabHost = resolve;

    internal void RefreshNoticeOwner()
    {
        reports?.HostChanged();
        PendingDecisionHostChanged();
    }
    private string statusKey = "Connecting";
    private string? error;
    private int pendingSends;
    private int pendingMouse;
    private bool HasPendingInput => pendingSends > 0 || pasteStop is not null;
    private CancellationTokenSource? pasteReview;
    private CancellationTokenSource? pasteStop;
    private CancellationTokenSource? searchWork;
    private CancellationTokenSource? searchMoves;
    private readonly Dictionary<TerminalSurface, Queue<bool>> pendingSearchMoves = [];
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
        statusBar = new LocalStatusBar(status, recordingStatus) { Height = 30, ClipToBounds = true, LastChildFill = false };
        statusBar.SetResourceReference(MarginProperty, "WindowStatusInset");
        visibleHighlights = new(Dispatcher, highlightState);
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
            if (searchWork is null) await UpdateSearchResultsAsync();
        };
        highlightState.Changed += _ => UpdateHighlightStatus();
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
        searchBar.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        Canvas.SetLeft(searchBar, SearchInset);
        terminalOverlays.Children.Add(searchBar);
        terminalOverlays.Children.Add(Notices);
        terminalArea.LayoutUpdated += (_, _) => { if (IsVisible && (Notices.HasItems || searchBar.IsVisible)) PositionSearchBar(); };
        terminalArea.Children.Add(output.Scroller);
        terminalArea.Children.Add(terminalOverlays);
        foreach (var indicator in new[] { status, recordingStatus, pasteProgressStatus, highlightStatus, macroStatus, mouseStatus, aiStatus })
        {
            ConfigureStatusIndicator(indicator);
            statusBar.Children.Add(indicator);
        }
        AutomationProperties.SetLiveSetting(aiStatus, AutomationLiveSetting.Polite);
        UpdateAiControls();
        RestoreStatusBarColors();
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(statusBar, 1);
        body.Children.Add(terminalArea);
        body.Children.Add(statusBar);
        panel.Children.Add(body);
        Content = panel;
        searchBar.PreviewKeyDown += OnSearchKeyDown;
        searchQuery.TextChanged += async (_, _) => await RefreshSearchAsync();
        matchCase.Checked += async (_, _) => await RefreshSearchAsync();
        matchCase.Unchecked += async (_, _) => await RefreshSearchAsync();
        previousMatch.Click += (_, _) => QueueSearchMove(backwards: true);
        nextMatch.Click += (_, _) => QueueSearchMove(backwards: false);
        closeSearch.Click += (_, _) => CloseSearch();
        UpdateLabels();
    }

    public event Action<string>? Connected;
    public event Action? CaptionChanged;
    public event Action? Disconnected;
    public bool IsRunning { get; private set; } = true;
    private enum ConnectionEnd { Pending, Normal, InitialCancelled, FailedBeforeConnect, FailedAfterConnect }
    private ConnectionEnd connectionEnd;
    public bool IsDisconnected => !IsRunning && (!IsExternalMacro
        ? connectionEnd != ConnectionEnd.Pending : statusKey == "Disconnected" || connectedName is not null);
    internal bool CanReconnect => IsDisconnected && CanStartConnection;
    internal bool CloseAfterInitialCancellation => connectionEnd == ConnectionEnd.InitialCancelled;
    internal bool CanAutoCloseOnDisconnect => connectionEnd == ConnectionEnd.Normal && reports?.HasReports != true;
    internal string ConnectionStatus => status.Text;
    public bool ShowDisconnectedIndicator => !IsExternalMacro ? !IsRunning && connectionEnd == ConnectionEnd.Normal
        : statusKey == "Disconnected" || IsDisconnected;
    internal string? ConnectionWarning => IsExternalMacro || IsRunning ? null : connectionEnd switch
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
    private bool hasAdmission;
    private ProfileCatalog.OnceApproval? previousOnce;
    private Task connectionCompletion = Task.CompletedTask;
    public Task Completion => Macro is null ? connectionCompletion : Task.WhenAll(connectionCompletion, Macro.Completion);
    public bool CanTransfer => !ClosePending && IsRunning && !stop.IsCancellationRequested && session?.CanTransfer == true;
    public bool HasActiveTransfer => transferWindow?.HasActiveOperation == true;
    internal object ConnectionGeneration => stop;
    internal FileTransferWindow? TransferWindow => transferWindow;
    internal Task<bool>? ClosureDecision { private get; set; }
    internal bool ClosePending => ClosureDecision is { IsCompletedSuccessfully: true, Result: true };
    private bool removed;
    private bool CanStartConnection => !removed && !ClosePending && !IsExternalMacro && connectionCompletion.IsCompleted;

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
        if (!CanStartConnection) return;
        ClearPostConnectionMacro();
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

    private async Task ConnectAsync(Action<ConnectionProfile>? opened, TaskCompletionSource<MacroConnection>? ready = null,
        CancellationToken attempt = default, RecordingOverrides recordingOverrides = default, bool reconnect = false)
    {
        var generation = stop;
        pendingFrame = null;
        ResetMouseRouting();
        bool established = false;
        MacroRun? postConnection = null;
        MacroProgram? reviewedPostConnection = null;
        bool postConnectionStarted = false;
        lastRecordingStatus = null;
        reportedRecordingParts.Clear();
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
                    application.Vault.CheckSshCredentialsAsync(owner, request, token, this), token, serialize: false),
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
                using var admission = await app.Profiles.AdmitAsync(owner, attemptPath, Macro?.FullPath, startup.Token,
                    reconnect: hasAdmission, previousOnce, Macro, this);
                var profile = admission.Profile;
                hasAdmission = true;
                previousOnce = admission.Once;
                attemptPath = path = profile.OpenedPath;
                if (!IsExternalMacro && admission.Program is { } program)
                {
                    PrepareMacro(program, []);
                    postConnection = Macro!;
                }
                using var embeddedStartup = postConnection is null ? null :
                    CancellationTokenSource.CreateLinkedTokenSource(startup.Token, postConnection.AttemptToken);
                CancellationToken startupToken = embeddedStartup?.Token ?? startup.Token;
                RecordingSettings? logging = null;
                if (profile.RecordsOutput)
                {
                    try
                    {
                        logging = RecordingSettings.Resolve(attemptPath, profile, Macro?.RecordingMacroName,
                            logDefaults, recordingOverrides);
                        await app.Profiles.ApproveRecordingDestinationAsync(DecisionHost, logging.Destination, startupToken, this);
                    }
                    catch (Exception failure) when (failure is not RecordingStartException &&
                        failure is (IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException or NotSupportedException))
                    { throw new RecordingStartException(failure.Message, failure); }
                }
                session = await TerminalSession.ConnectReviewedAsync(attemptPath, profile, Macro, startupToken, interaction,
                    Opened, retention, Viewport, logging);
                if (ready is not null) reviewedPostConnection = admission.TakeProgram();
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
                Reports.Report(new object(), string.Format(Text("ReconnectDestinationDetails"),
                    PermissionDisplay.Literal(session.DisplayName), previousDestination, destination));
            lastDestination = destination;
            connectedName = session.DisplayName;
            established = true;
            SetStatus("Connected");
            var recordingOwner = session;
            recordingOwner.RecordingPartFailed += ShowRecordingFailure;
            ShowRecordingFailure();
            visibleHighlights.ResetForSession();
            highlightState.Resume();
            highlightSetId = session.HighlightSetId;
            ApplyHighlightSet();
            Connected?.Invoke(connectedName);
            foreach (var pane in panes.Keys) pane.SetConnected(!HasActiveMacro);
            mousePump = SendMouseAsync(session);
            Interlocked.Exchange(ref receivedRevision, 1);
            ScheduleFrame(immediately: true);
            ScheduleResize();
            if (postConnection is not null)
            {
                postConnection.Start(session);
                postConnectionStarted = true;
            }
            if (ready?.TrySetResult(new(session, reviewedPostConnection)) == true) reviewedPostConnection = null;
            try
            {
                await ReceiveOutputAsync(recordingOwner);
                await recordingOwner.WaitForTerminalPeerCloseAsync(stop.Token);
            }
            finally { recordingOwner.RecordingPartFailed -= ShowRecordingFailure; }
            connectionEnd = session.RecordingStartFailure is null ? ConnectionEnd.Normal : ConnectionEnd.FailedAfterConnect;
            if (session.RecordingStartFailure is { } logFailure) SetStatus("Failed", LogStartError(logFailure));
            else SetStatus("Disconnected");

            void ShowRecordingFailure()
            {
                var snapshot = recordingOwner.RecordingStatus;
                if (snapshot.Disposition == RecordingDisposition.Incomplete)
                    _ = Dispatcher.BeginInvoke(() => ObserveRecordingFailure(recordingOwner, snapshot, generation));
            }
        }
        catch (OperationCanceledException exception) when (stop.IsCancellationRequested ||
            postConnection?.AttemptToken.IsCancellationRequested == true || exception is SshInteractionCancelledException)
        {
            ready?.TrySetException(exception);
            connectionEnd = established || reconnect ? ConnectionEnd.Normal : ConnectionEnd.InitialCancelled;
            SetStatus("Disconnected");
        }
        catch (Exception exception) when (exception is IOException or SocketException or NotSupportedException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException
            or InvalidDataException or CryptographicException or DllNotFoundException or EntryPointNotFoundException
            or FormatException or System.Text.Json.JsonException or KeyNotFoundException or MacroError or MacroSourceException)
        {
            ready?.TrySetException(exception);
            if (stop.IsCancellationRequested && session?.RecordingStartFailure is null)
            {
                connectionEnd = established || reconnect ? ConnectionEnd.Normal : ConnectionEnd.InitialCancelled;
                SetStatus("Disconnected");
            }
            else ShowConnectionFailure(exception, established);
        }
        finally
        {
            reviewedPostConnection?.Dispose();
            if (postConnectionStarted) postConnection!.Stop("Disconnected");
            else if (postConnection is not null) ClearPostConnectionMacro();
            AiCollaboration.EndControl(this);
            transferWindow?.ConnectionEnded();
            stop.Cancel();
            UpdateMouseRouting();
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
                else ObserveRecordingFailure(closing, closing.RecordingStatus, generation);
                UpdateStatusText();
            }
            resizeTimer.Stop();
            if (postConnectionStarted) await postConnection!.Completion;
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
        reports?.Dispose();
        CancelPendingDecisions();
        frameTimer.Stop();
        searchRefreshTimer.Stop();
        HideMouseNotice();
        searchWork?.Cancel();
        CancelSearchMoves();
        searchMatches = [];
        searchMemory.Dispose();
        paneLayout?.ReleaseReservations();
        visibleHighlights.Detach();
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
        OnSshPromptAsync((app, owner) => app.HostTrust.ApproveAsync(owner, key, token, this), token);

    private Task<VaultSecret?> RequestSshPrivateKeyAsync(SshPrivateKeyRequest request, CancellationToken token) =>
        OnSshPromptAsync((app, owner) => app.Vault.ReadPrivateKeyAsync(owner, request, token, this),
            token, serialize: false);

    private Task<VaultSecret?> RequestSshPasswordAsync(SshPasswordRequest request, CancellationToken token)
    {
        if (request.Attempt == 1 && request.SavedPassword is { } reference)
            return OnSshPromptAsync((app, owner) => app.Vault.ReadSavedPasswordAsync(owner, reference, token, this),
                token, serialize: false);
        return OnSshPromptAsync<VaultSecret?>(async (app, owner) =>
        {
            var form = new MainWindow.FormWindow(owner, Text("SshPasswordSource"));
            var password = new PasswordBox { MaxLength = 16 * 1024 };
            form.AddField("SshDestination", new TextBox { Text = request.Destination.ToString(), IsReadOnly = true });
            form.AddField("SshUsername", new TextBox { Text = PermissionDisplay.Literal(request.Username), IsReadOnly = true });
            form.AddField("VaultPassword", password);
            form.Loaded += (_, _) => { if (form.ShowActivated) password.Focus(); };
            form.AddButtons(() => { }, acceptKey: "Authenticate");
            try
            {
                await PresentDecisionAsync(form, token);
                return form.Accepted ? EncodeSecret(VaultController.ReadPassword(password), token) : null;
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
        form.Loaded += (_, _) => { if (form.ShowActivated) inputs.FirstOrDefault()?.Focus(); };
        form.AddButtons(() => { }, acceptKey: "SendSshAnswers");
        return (form, inputs);
    }

    internal Task<VaultSecret[]?> RequestSshChallengeAsync(SshChallenge challenge, CancellationToken token) =>
        OnSshPromptAsync<VaultSecret[]?>(async (app, owner) =>
        {
            var (form, inputs) = CreateSshChallengeForm(owner, challenge);
            var answers = new List<VaultSecret>();
            try
            {
                await PresentDecisionAsync(form, token);
                if (!form.Accepted) return null;
                foreach (Control input in inputs)
                    answers.Add(EncodeSecret(input is PasswordBox password ? VaultController.ReadPassword(password) :
                        ((TextBox)input).Text.ToCharArray(), token));
                return answers.ToArray();
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
                Window? owner = DecisionHost;
                if (owner is null || token.IsCancellationRequested) throw new OperationCanceledException(token);
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

        ApplyContextSubmenuTemplates(menu);
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
            var previous = active.RecordingStatus;
            var outcome = await active.ChangeRecordingAsync(resume, stop.Token);
            if (!ReferenceEquals(session, active)) return;
            if (resume && previous.Disposition == RecordingDisposition.Incomplete && outcome.Status.Error is null)
            {
                ShowRecordingRecovery(active, previous.Part);
                return;
            }
            if (outcome.Status.Error is not null) ObserveRecordingFailure(active, outcome.Status, ConnectionGeneration);
            UpdateStatusText();
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
        reports?.UpdateLabels();
        Notices.UpdateLabels();
        Notices.UpdateHintText(mouseHintKey, Text("MouseSentToHostNotice"));
        UpdateMacroControls();
        UpdateAiControls();
        UpdateStatusText();
        UpdateMouseRouting();
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
        var shortcuts = ((App)Application.Current).Shortcuts;
        var copy = new MenuItem { Header = Text("Copy"), InputGestureText = shortcuts[ShortcutAction.Copy] ?? "" };
        copy.Click += (_, _) => pane.Copy();
        var paste = new MenuItem { Header = Text("Paste"), InputGestureText = shortcuts[ShortcutAction.Paste] ?? "" };
        paste.Click += async (_, _) => { ActivatePane(pane); await PasteClipboardAsync(); };
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        var search = new MenuItem { Header = Text("Search"), InputGestureText = shortcuts[ShortcutAction.Search] ?? "" };
        search.Click += (_, _) => { ActivatePane(pane); OpenSearch(); };
        menu.Items.Add(search);
        var messages = new MenuItem { Header = Text("SshMessages") };
        messages.Click += (_, _) => ShowSshMessages();
        menu.Items.Add(messages);
        var resumeHighlight = new MenuItem { Header = Text("ResumeHighlighting") };
        resumeHighlight.Click += (_, _) => highlightState.Resume();
        menu.Items.Add(resumeHighlight);
        int fixedItemCount = menu.Items.Count;
        menu.Opened += (_, _) =>
        {
            while (menu.Items.Count > fixedItemCount) menu.Items.RemoveAt(fixedItemCount);
            AddMouseRoutingCommand(menu, pane);
            var splits = new MenuItem { Header = Text("TerminalPanes") };
            FillPaneCommands(splits, pane);
            menu.Items.Add(splits);
            ApplyContextSubmenuTemplates(menu);
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

    internal void ShowConnectionFailure(Exception exception, bool established)
    {
        connectionEnd = established ? ConnectionEnd.FailedAfterConnect : ConnectionEnd.FailedBeforeConnect;
        string? credentialError = exception switch
        {
            SshCredentialFailureException => exception.Message,
            CredentialLookupException { Code: MacroErrorCode.CredentialKindMismatch } => Text("VaultCredentialKindMismatch"),
            CredentialLookupException { Code: MacroErrorCode.CredentialUnavailable } => Text("SshCredentialMissing"),
            _ => null,
        };
        SetStatus("Failed", session?.RecordingStartFailure is { } failure ? LogStartError(failure) : exception switch
        {
            _ when credentialError is not null => null,
            RecordingStartException recording => LogStartError(recording.Message),
            OperationCanceledException => Text("TimedOut"),
            DllNotFoundException or EntryPointNotFoundException => Text("VaultCryptoUnavailable"),
            MacroSourceException macroError => PostConnectionSourceError(macroError),
            MacroError macroError when macroError.DisplayDetail is { } detail => detail,
            _ => !IsExternalMacro ? exception.Message : Text("MacroErrorConnectionFailed"),
        });
        if (credentialError is not null && !removed)
            Reports.Report(ConnectionGeneration, $"{PermissionDisplay.Literal(TabCaption)} — {credentialError}");
    }

    internal void ReportTransferCloseFailure() => SetStatus(statusKey, Text("SftpCloseReportFailed"));

    private void SetStatus(string key, string? detail = null)
    {
        bool changed = statusKey != key;
        statusKey = key;
        error = detail;
        UpdateStatusText();
        if (changed) CaptionChanged?.Invoke();
    }

    private void ShowRecordingRecovery(TerminalSession active, int recoveredPart)
    {
        if (ReferenceEquals(session, active) && active.RecordingError is null)
        {
            reports?.UpdateIfPending((ConnectionGeneration, recoveredPart),
                string.Format(Text("NoticeRecordingRecovered"), recoveredPart, active.RecordingStatus.Part));
            UpdateStatusText();
        }
    }

    private void ObserveRecordingFailure(TerminalSession source, RecordingStatus snapshot, object generation)
    {
        if (removed || generation != ConnectionGeneration || source.RecordingStartFailure is not null ||
            snapshot is not { Disposition: RecordingDisposition.Incomplete, Error: { } reason }) return;
        if (reportedRecordingParts.Add(snapshot.Part))
        {
            Reports.Report((generation, snapshot.Part), string.Format(Text("NoticeRecordingFailed"), snapshot.Part,
                reason == RecordingDestinationChangedException.Description ? Text("RecordingDestinationChanged") : reason));
            if (source.RecordingStatus.Part > snapshot.Part) ShowRecordingRecovery(source, snapshot.Part);
        }
        UpdateStatusText();
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
        string recordingLabel = "";
        if (RecordingCloseStatus is { Part: > 0 } recording)
        {
            string key = recording.Disposition switch
            {
                RecordingDisposition.Active => "NoticeRecordingActive",
                RecordingDisposition.Incomplete => "NoticeRecordingStopped",
                RecordingDisposition.Saving => "NoticeRecordingSaving",
                RecordingDisposition.Saved when session is not null && IsRunning && statusKey == "Connected" => "NoticeRecordingPaused",
                _ => "NoticeRecordingSaved"
            };
            recordingLabel = string.Format(Text(key), recording.Part);
        }
        if (recordingStatus.Text != recordingLabel)
        {
            recordingStatus.Text = recordingLabel;
            recordingStatus.ToolTip = recordingLabel;
            AutomationProperties.SetName(recordingStatus, recordingLabel);
            recordingStatus.Visibility = recordingLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (output.UsesFontFallback) text += "   " + string.Format(Text("FontFallback"), output.AutomationFontFamily);
        if (Equals(status.ToolTip, text)) return;
        status.Text = text.ReplaceLineEndings(" ");
        status.ToolTip = text;
        AutomationProperties.SetName(status, text);
    }

    internal void SetBroadcastPasteProgress(string? text)
    {
        if (Equals(pasteProgressStatus.ToolTip, text)) return;
        pasteProgressStatus.Text = text?.ReplaceLineEndings(" ") ?? "";
        pasteProgressStatus.ToolTip = text;
        AutomationProperties.SetName(pasteProgressStatus, text ?? "");
        pasteProgressStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class LocalStatusBar(TextBlock connection, TextBlock recording) : DockPanel
    {
        protected override Size MeasureOverride(Size available)
        {
            var labels = InternalChildren.Cast<TextBlock>().Where(label => label.Visibility == Visibility.Visible).ToArray();
            foreach (var label in labels) label.Measure(new Size(double.PositiveInfinity, available.Height));
            double width = labels.Sum(label => label.DesiredSize.Width);
            if (width > available.Width)
            {
                // Short labels take only their natural width. Share the remainder among the long labels,
                // giving connection and recording more room while keeping every active label reachable.
                double remaining = available.Width;
                int weight = labels.Sum(Weight);
                foreach (var label in labels.OrderBy(label => label.DesiredSize.Width / Weight(label)))
                {
                    double share = remaining * Weight(label) / weight;
                    if (label.DesiredSize.Width > share) label.Measure(new Size(share, available.Height));
                    remaining = Math.Max(0, remaining - label.DesiredSize.Width);
                    weight -= Weight(label);
                }
            }
            return new Size(Math.Min(width, available.Width), Height);
        }

        private int Weight(TextBlock label) => label == connection ? 5 : label == recording ? 3 : 1;
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

    private ViewNotifications? reports;
    internal ViewNotifications Reports
    {
        get
        {
            if (reports is null)
            {
                reports = new(Notices, () => tabHost?.Invoke(), () => tabHost?.Invoke()?.IsCurrentView(this) ?? IsVisible,
                    () => TabCaption, SelectNoticeTab);
                reports.Changed += () => CaptionChanged?.Invoke();
            }
            return reports;
        }
    }

    internal void SelectNoticeTab()
    {
        tabHost?.Invoke()?.SelectNotificationTab(this);
    }

    private void ApplyHighlightSet()
    {
        var rules = fixtureHighlights ?? ((App)Application.Current).Highlights.Find(highlightSetId);
        highlightState.SetRules(rules, highlightSetId is not null && rules is null);
    }

    private void UpdateHighlightStatus()
    {
        bool missing = highlightState.MissingSet;
        highlightStatus.Visibility = highlightState.Suspended.Count != 0 || missing ? Visibility.Visible : Visibility.Collapsed;
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
            if (updated is not null) surface.UpdateSnapshotPosition(updated);
            surface.SetSearch(pane.CurrentMatch, searchMatches, scroll: false);
        }
        if (updated is not null) visibleHighlights.SetSnapshot(updated);
        UpdateMouseRouting();
        if (searchBar.Visibility == Visibility.Visible)
        {
            if (snapshot?.IsAlternateScreen == true)
            {
                searchWork?.Cancel();
                CancelSearchMoves();
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
        bool opening = searchBar.Visibility != Visibility.Visible;
        searchBar.Visibility = Visibility.Visible;
        PositionSearchBar();
        UpdateSearchButtons();
        searchQuery.Focus();
        searchQuery.SelectAll();
        if (opening) _ = UpdateSearchResultsAsync();
    }

    private void PositionSearchBar()
    {
        Rect anchor = NoticeOverlayBounds();
        Canvas.SetRight(Notices, terminalArea.ActualWidth - anchor.Right + 2);
        Canvas.SetTop(Notices, anchor.Top + 2);
        Notices.MaxWidth = Math.Clamp(anchor.Width - 4, 0, 440);
        double availableHeight = anchor.Height - 4;
        if (searchBar.Visibility == Visibility.Visible)
        {
            bool beside = Notices.HasItems && anchor.Left >= searchQuery.Width + 2 * SearchInset;
            double searchWidth = beside ? anchor.Left : terminalArea.ActualWidth;
            searchBar.MaxWidth = Math.Max(0, searchWidth - 2 * SearchInset);
            if (!beside)
                availableHeight = Math.Min(availableHeight, terminalArea.ActualHeight - anchor.Top -
                    searchBar.ActualHeight - 2 * SearchInset);
            double occupiedTop = Notices.HasItems && !beside ? Canvas.GetTop(Notices) + Notices.ActualHeight : 0;
            Canvas.SetTop(searchBar, occupiedTop + SearchInset);
        }
        Notices.MaxHeight = Math.Max(0, availableHeight);
    }

    private void CloseSearch()
    {
        searchRefreshTimer.Stop();
        searchWork?.Cancel();
        CancelSearchMoves();
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

    private void CancelSearchMoves()
    {
        searchMoves?.Cancel();
        searchMoves?.Dispose();
        searchMoves = null;
        pendingSearchMoves.Clear();
    }

    private void QueueSearchMove(bool backwards)
    {
        if (removed || searchBar.Visibility != Visibility.Visible || !nextMatch.IsEnabled) return;
        var pane = output;
        if (pendingSearchMoves.TryGetValue(pane, out var pending))
        {
            pending.Enqueue(backwards);
            return;
        }

        pending = new Queue<bool>();
        pending.Enqueue(backwards);
        pendingSearchMoves.Add(pane, pending);
        searchMoves ??= new CancellationTokenSource();
        _ = MoveThroughSearchAsync(pane, pending, searchMoves.Token);
    }

    private async Task MoveThroughSearchAsync(TerminalSurface surface, Queue<bool> pending, CancellationToken token)
    {
        string query = searchQuery.Text;
        bool caseSensitive = matchCase.IsChecked == true;
        try
        {
            while (pending.TryPeek(out bool backwards))
            {
                token.ThrowIfCancellationRequested();
                if (snapshot is not { IsAlternateScreen: false } captured || !panes.TryGetValue(surface, out var pane)) return;
                TerminalMatch? anchor = pane.CurrentMatch;
                if (anchor is null && captured.Count != 0)
                {
                    var (first, end) = surface.VisibleRows();
                    int row = Math.Min(backwards ? Math.Max(first, end - 1) : first, captured.Count - 1);
                    var (left, right) = surface.VisibleColumns();
                    var (start, finish) = captured.VisibleText(row, left, right);
                    int offset = backwards ? finish : start - 1;
                    anchor = new TerminalMatch(row, offset, row, offset);
                }
                await TerminalHighlightView.Workers.WaitAsync(token);
                TerminalMatch? found;
                try
                {
                    found = await Task.Run(() => TerminalSearch.FindNext(captured, query, caseSensitive,
                        anchor, backwards, token), token);
                }
                finally { TerminalHighlightView.Workers.Release(); }

                token.ThrowIfCancellationRequested();
                if (!panes.ContainsKey(surface) || snapshot is not { IsAlternateScreen: false } current) return;
                if (!current.HasSameHighlightText(captured)) continue;
                pending.Dequeue();
                pane.CurrentMatch = found;
                surface.SetSearch(found, searchMatches, scroll: found.HasValue, backwards: backwards);
                ScheduleSearchRefresh();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ResourceLimitException) { }
        finally
        {
            if (pendingSearchMoves.TryGetValue(surface, out var current) && ReferenceEquals(current, pending))
                pendingSearchMoves.Remove(surface);
        }
    }

    private async Task RefreshSearchAsync()
    {
        CancelSearchMoves();
        foreach (var pane in panes.Values) pane.CurrentMatch = null;
        SetSearchMatches([]);
        foreach (var pane in panes.Keys) pane.SetSearch(null, [], scroll: false);
        UpdateSearchButtons();
        if (searchBar.Visibility == Visibility.Visible) await UpdateSearchResultsAsync();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
        else if (e.Key == Key.Enter && searchQuery.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            QueueSearchMove(backwards: Keyboard.Modifiers == ModifierKeys.Shift);
        }
    }

    private void ScheduleSearchRefresh()
    {
        if (removed || searchBar.Visibility != Visibility.Visible || searchQuery.Text.Length == 0) return;
        searchRefreshTimer.Stop();
        searchRefreshTimer.Start();
    }

    private async Task UpdateSearchResultsAsync()
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
        SetSearchStatus("Searching");
        try
        {
            await TerminalHighlightView.Workers.WaitAsync(work.Token);
            var viewports = panes.Keys.Select(surface => surface.VisibleRows()).ToArray();
            try
            {
                using var ranges = ApplicationResources.SearchCache.Reserve(TerminalSearch.Limit * 64);
                var result = await Task.Run(() => TerminalSearch.Aggregate(captured, query, caseSensitive,
                    viewports, work.Token), work.Token);
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
        switch (((App)Application.Current).Shortcuts.Match(e))
        {
            case ShortcutAction.Search:
                e.Handled = true;
                OpenSearch();
                return;
            case ShortcutAction.Copy:
                e.Handled = true;
                output.Copy();
                return;
            case ShortcutAction.Paste:
                e.Handled = true;
                await PasteClipboardAsync();
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
                        PresentPasteAsync(DecisionHost ?? owner, text, preview.Pacing, preview.Targets, token),
                        DispatcherPriority.Normal, token).Task.Unwrap(), review.Token);
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
                var choice = await PresentPasteAsync(DecisionHost ?? owner, paste, (characterDelay, lineDelay, remember),
                    [BroadcastTargetLabel(active)], review.Token);
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
                    SetStatus("Connected", completed ? null : Text("PasteStopped"));
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

        bool SingleTargetCurrent() => DecisionHost is { } current &&
            (current is not MainWindow window || !app.Broadcast.IsActive(window)) &&
            active == InputSession && !HasPendingInput && !review.IsCancellationRequested;

        async Task<TerminalPaste?> CaptureAsync(CancellationToken token)
        {
            var value = await captureClipboard(owner, maximum, token);
            if (active == InputSession && !HasPendingInput && !token.IsCancellationRequested) return value;
            value?.Dispose();
            return null;
        }
    }

    private async Task<BroadcastInput.PasteDecision?> PresentPasteAsync(Window owner, TerminalPaste paste,
        (int Character, int Line, bool Remember) pacing, IReadOnlyList<string> targets, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        var (characterDelay, lineDelay, remember) = pacing;
        var form = new MainWindow.FormWindow(owner, Text("PastePreview")) { Width = 680 };
        string summary = string.Format(CultureInfo.CurrentCulture, Text("PasteCounts"),
            paste.CharacterCount, paste.LineCount,
            Text(paste.EndsWithNewline ? "TrailingNewline" : "NoTrailingNewline"));
        // Each source page can expand into visible Unicode markers and line breaks.
        // Include escaped captions, their joined text and the read-only controls.
        long captionMemory = targets.Sum(name => 128L + 64L * name.Length);
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
        ReadOnlyPreview.Add(form, "PastePreview", previewText, paste.Text);

        form.AddField("PasteCountsLabel", new TextBox { Text = summary, IsReadOnly = true });
        form.AddField("PasteTargets", new TextBox
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
        await PresentDecisionAsync(form, token);
        return form.Accepted ? new(characterDelay, lineDelay, remember) : null;
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
                SetStatus("Connected");
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
