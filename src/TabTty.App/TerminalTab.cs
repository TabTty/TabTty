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
    private readonly (int Columns, int Rows)? fixedViewport;
    private CancellationTokenSource stop = new();
    private readonly TextBlock status = new() { Padding = new Thickness(8) };
    private readonly TextBlock highlightStatus = new() { Padding = new Thickness(8), Visibility = Visibility.Collapsed };
    private readonly StackPanel searchBar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly TextBox searchQuery = new() { Width = 220, MaxLength = 1024, Margin = new Thickness(3) };
    private readonly CheckBox matchCase = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
    private readonly Button previousMatch = new() { Margin = new Thickness(3) };
    private readonly Button nextMatch = new() { Margin = new Thickness(3) };
    private readonly Button closeSearch = new() { Margin = new Thickness(3) };
    private readonly TextBlock searchStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
    private TerminalSurface output = new();
    private Guid? highlightSetId;
    private bool highlightSuspended;
    private readonly DispatcherTimer resizeTimer;
    private readonly DispatcherTimer frameTimer;
    private IReadOnlyList<string>? pendingFrame;
    private int receivedRevision;
    private bool capturingFrame;
    private bool immediateFrameQueued;
    private readonly DispatcherTimer bellTimer;
    private (int Columns, int Rows) appliedGrid = (80, 24);
    private TerminalSession? session;
    private FileTransferWindow? transferWindow;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TerminalSession, FileOperationOwnership> transferOwnership = new();
    private string? connectedName;
    private string statusKey = "Connecting";
    private string? error;
    private int pendingSends;
    private int pendingMouse;
    private bool reviewingPaste;
    private bool HasPendingInput => pendingSends > 0 || pasteStop is not null;
    private CancellationTokenSource? pasteStop;
    private CancellationTokenSource? searchWork;
    private TerminalSnapshot? snapshot;
    private TerminalMatch[] searchMatches = [];
    private readonly ResourceReservation searchMemory = ApplicationResources.SearchCache.Reserve(0);
    private string? searchStatusKey;
    private readonly List<string> sshMessages = [];
    private Channel<TerminalMouseInput> mouseInput = Channel.CreateBounded<TerminalMouseInput>(256);
    private Task mousePump = Task.CompletedTask;
    private string baseFontFamily = "Consolas";
    private double baseFontSize = 12;
    private double zoomFontSize = 12;

    public TerminalTab(string path) : this(path, null) { }

    internal TerminalTab(string path, SshInteraction? sshInteraction, (int Columns, int Rows)? fixedViewport = null,
        HighlightSet? fixtureHighlights = null)
    {
        this.path = path;
        this.sshInteraction = sshInteraction;
        this.fixedViewport = fixedViewport;
        this.fixtureHighlights = fixtureHighlights;
        bellTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        bellTimer.Tick += (_, _) =>
        {
            bellTimer.Stop();
            status.ClearValue(TextBlock.BackgroundProperty);
            status.ClearValue(TextBlock.ForegroundProperty);
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
        DockPanel.SetDock(searchBar, Dock.Top);
        searchBar.Children.Add(searchQuery);
        searchBar.Children.Add(matchCase);
        searchBar.Children.Add(previousMatch);
        searchBar.Children.Add(nextMatch);
        searchBar.Children.Add(closeSearch);
        searchBar.Children.Add(searchStatus);
        panel.Children.Add(searchBar);
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);
        InitializeAiControls();
        DockPanel.SetDock(highlightStatus, Dock.Bottom);
        panel.Children.Add(highlightStatus);
        panel.Children.Add(output.Scroller);
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
    public bool IsDisconnected => !IsRunning && (statusKey == "Disconnected" || connectedName is not null);
    internal string ConnectionStatus => status.Text;
    public bool ShowDisconnectedIndicator => statusKey == "Disconnected" || IsDisconnected;
    public string TabCaption => connectedName ?? Path.GetFileNameWithoutExtension(path);
    public string ProfilePath => path;
    private Task connectionCompletion = Task.CompletedTask;
    public Task Completion => Macro is null ? connectionCompletion : Task.WhenAll(connectionCompletion, Macro.Completion);
    public bool CanTransfer => IsRunning && !stop.IsCancellationRequested && session?.CanTransfer == true;
    public bool HasActiveTransfer => transferWindow?.HasActiveOperation == true;

    public void OpenFileTransfer()
    {
        if (!CanTransfer || session is not { } active) return;
        if (transferWindow is { } previous && !previous.OwnsSession(active))
            _ = previous.CloseWithTerminalAsync();
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

    public Task CloseFileTransferAsync() => transferWindow?.CloseWithTerminalAsync() ?? Task.CompletedTask;

    public void Start(Action<ConnectionProfile>? opened = null)
    {
        if (Macro is not null || !Completion.IsCompleted) return;
        AiCollaboration.EndSharing(this);
        retainedAiSession = null;
        if (!IsRunning)
        {
            stop = new CancellationTokenSource();
            mouseInput = Channel.CreateBounded<TerminalMouseInput>(256);
            mousePump = Task.CompletedTask;
            pendingMouse = 0;
            IsRunning = true;
            appliedGrid = (80, 24);
            sshMessages.Clear();
            SetStatus("Connecting");
        }
        connectionCompletion = ConnectAsync(opened);
    }

    private async Task ConnectAsync(Action<ConnectionProfile>? opened, TaskCompletionSource<TerminalSession>? ready = null,
        CancellationToken attempt = default)
    {
        try
        {
            var app = (App)Application.Current;
            var interaction = sshInteraction ?? new SshInteraction(ApproveHostAsync, RequestSshPasswordAsync,
                message => Dispatcher.BeginInvoke(() => sshMessages.Add(message)), RequestSshChallengeAsync,
                RequestSshPrivateKeyAsync, app.HostTrust.ApprovedFormats);
            string fontFamily = app.TerminalFontFamily;
            double fontSize = app.TerminalFontSize;
            int retention = app.RetainedRows;
            int sessionRetention = retention;
            void Opened(ConnectionProfile profile) => Dispatcher.BeginInvoke(() =>
                {
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
                session = Macro is { } run
                    ? await TerminalSession.ConnectMacroAsync(path, run, startup.Token, interaction, Opened, retention, Viewport)
                    : await TerminalSession.ConnectAsync(path, startup.Token, interaction, Opened, retention, Viewport);
            }
            session.Bell += () => Dispatcher.BeginInvoke(() =>
            {
                if (app.BellChoice != "visual" || !output.IsPresentationActive || bellTimer.IsEnabled) return;
                status.Background = Brushes.White;
                status.Foreground = Brushes.Black;
                bellTimer.Start();
            });
            stop.Token.ThrowIfCancellationRequested();
            connectedName = session.DisplayName;
            SetStatus("Connected");
            Connected?.Invoke(connectedName);
            highlightSetId = session.HighlightSetId;
            ApplyHighlightSet();
            foreach (var pane in panes.Keys) pane.SetConnected(!HasActiveMacro);
            mousePump = SendMouseAsync(session);
            ScheduleResize();
            ready?.TrySetResult(session);
            await ReceiveOutputAsync(session);
            await session.WaitForMacroPeerCloseAsync(stop.Token);
            SetStatus("Disconnected");
        }
        catch (OperationCanceledException exception) when (stop.IsCancellationRequested || exception is SshInteractionCancelledException)
        {
            ready?.TrySetException(exception);
            SetStatus("Disconnected");
        }
        catch (Exception exception) when (exception is IOException or SocketException or NotSupportedException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException
            or InvalidDataException or CryptographicException or DllNotFoundException or EntryPointNotFoundException
            or FormatException or System.Text.Json.JsonException or KeyNotFoundException or MacroError)
        {
            ready?.TrySetException(exception);
            if (stop.IsCancellationRequested) SetStatus("Disconnected");
            else SetStatus("Failed", exception switch
            {
                OperationCanceledException => Text("TimedOut"),
                DllNotFoundException or EntryPointNotFoundException => Text("VaultCryptoUnavailable"),
                _ => Macro is null ? exception.Message : Text("MacroErrorConnectionFailed"),
            });
        }
        finally
        {
            AiCollaboration.EndControl(this);
            transferWindow?.ConnectionEnded();
            stop.Cancel();
            mouseInput.Writer.TryComplete();
            await mousePump;
            if (session is { } closing)
            {
                await closing.DisposeAsync();
                retainedAiSession = closing;
                session = null;
                try { Display(await closing.GetOutputAsync()); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                { SetStatus("Failed", exception.Message); }
                if (closing.RecordingError is { } problem) SetStatus(statusKey, problem);
            }
            resizeTimer.Stop();
            foreach (var pane in panes.Keys) pane.SetConnected(false);
            IsRunning = false;
            stop.Dispose();
            CaptionChanged?.Invoke();
            if (IsDisconnected) Disconnected?.Invoke();
        }
    }

    private async Task ReceiveOutputAsync(TerminalSession active)
    {
        while (await active.ProcessOutputAsync(stop.Token).ConfigureAwait(false))
        {
            if (Interlocked.Exchange(ref receivedRevision, 1) == 0)
                _ = Dispatcher.BeginInvoke(() =>
                {
                    ScheduleFrame();
                    if (active.RecordingError is { } problem) SetStatus("Connected", problem);
                });
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
        OnSshPromptAsync((app, owner) => Task.FromResult(app.HostTrust.Approve(owner, key, token)), token);

    private Task<VaultSecret?> RequestSshPrivateKeyAsync(SshPrivateKeyRequest request, CancellationToken token) =>
        OnSshPromptAsync((app, owner) => app.Vault.ReadPrivateKeyAsync(owner, request.SavedKey, token),
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
            form.AddField("SshUsername", new TextBox { Text = request.Username, IsReadOnly = true });
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

    private Task<VaultSecret[]?> RequestSshChallengeAsync(SshChallenge challenge, CancellationToken token) =>
        OnSshPromptAsync<VaultSecret[]?>((app, owner) =>
        {
            var form = new MainWindow.FormWindow(owner, Text("KeyboardInteractive")) { Width = 540 };
            form.AddField("SshDestination", new TextBox { Text = challenge.Destination.ToString(), IsReadOnly = true });
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
                Control input = prompt.Echo ? new TextBox { MaxLength = 8192 } : new PasswordBox { MaxLength = 8192 };
                form.AddPrompt(prompt.Text, input);
                inputs.Add(input);
            }
            form.Loaded += (_, _) => inputs.FirstOrDefault()?.Focus();
            form.AddButtons(() => { }, acceptKey: "Authenticate");
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

    public void StopPaste() => pasteStop?.Cancel();
    public void AbandonCleanup() => session?.AbandonCleanup();

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
                pendingSends++;
                try { await command(); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    if (IsRunning) SetStatus("Connected", exception.Message);
                }
                finally { pendingSends--; }
            };
            menu.Items.Add(item);
        }
    }

    public async Task ChangeRecordingAsync(bool resume)
    {
        if (session is not { RecordsOutput: true } active || stop.IsCancellationRequested) return;
        try
        {
            if (resume) await active.ResumeRecordingAsync(stop.Token);
            else await active.PauseRecordingAsync(stop.Token);
            SetStatus("Connected", active.RecordingError ?? Text(resume ? "RecordingResumed" : "RecordingPaused"));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            if (IsRunning) SetStatus("Connected", exception.Message);
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
        SetSearchStatus(searchStatusKey);
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

    private void UpdateStatusText()
    {
        string text = error is null ? Text(statusKey) : $"{Text(statusKey)} — {error}";
        if (session is { } active) text += $"   {Text("LocalEcho")} {Text(active.LocalEchoEnabled ? "On" : "Off")}";
        if (output.UsesFontFallback) text += "   " + string.Format(Text("FontFallback"), output.AutomationFontFamily);
        status.Text = text;
    }

    private void ApplyHighlightSet()
    {
        var rules = fixtureHighlights ?? ((App)Application.Current).Highlights.Find(highlightSetId);
        foreach (var pane in panes.Values) pane.Highlight.SetRules(rules);
        UpdateHighlightStatus();
    }

    private void UpdateHighlightStatus()
    {
        bool missing = highlightSetId is not null && ((App)Application.Current).Highlights.Find(highlightSetId) is null;
        highlightStatus.Visibility = highlightSuspended || missing ? Visibility.Visible : Visibility.Collapsed;
        highlightStatus.Text = Text(missing ? "HighlightSetMissing" : "HighlightRuleSuspended");
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
                IReadOnlyList<string> frame = await active.GetOutputAsync();
                if (session == active) pendingFrame = frame;
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

    private void ScheduleFrame(bool immediately = false)
    {
        if (!output.IsPresentationActive || capturingFrame || immediateFrameQueued ||
            pendingFrame is null && (session is null || Volatile.Read(ref receivedRevision) == 0)) return;
        if (immediately)
        {
            frameTimer.Stop();
            immediateFrameQueued = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(async () =>
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

    private void SetSearchStatus(string? key)
    {
        searchStatusKey = key;
        searchStatus.Text = key is null ? "" : Text(key);
    }

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

    private async Task SearchAsync(bool backwards)
    {
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
        SetSearchStatus("Searching");
        try
        {
            await TerminalHighlightView.Workers.WaitAsync(work.Token);
            (TerminalMatch? Current, TerminalMatch[] Matches) result;
            try
            {
                ResourceReservation? ranges = null;
                try { ranges = ApplicationResources.SearchCache.Reserve(10_000 * 64); }
                catch (ResourceLimitException) { }
                using (ranges)
                    result = await Task.Run(() => (captured.Find(query, caseSensitive, anchor, backwards, work.Token),
                        ranges is null ? [] : captured.FindMatches(query, caseSensitive, work.Token).Take(10_000).ToArray()), work.Token);
            }
            finally { TerminalHighlightView.Workers.Release(); }
            if (work.IsCancellationRequested || snapshot is not { IsAlternateScreen: false } latest ||
                !panes.TryGetValue(operated, out var pane)) return;
            pane.CurrentMatch = result.Current is { } found ? latest.MapMatchFrom(captured, found) : null;
            SetSearchMatches(ReferenceEquals(captured, latest) ? result.Matches : result.Matches
                .Select(match => latest.MapMatchFrom(captured, match)).OfType<TerminalMatch>().ToArray());
            SetSearchStatus(result.Current is null && ReferenceEquals(captured, latest) ? "NoSearchMatch" : null);
            foreach (var (surface, view) in panes) surface.SetSearch(view.CurrentMatch, searchMatches, scroll: surface == operated);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        finally
        {
            if (searchWork == work) searchWork = null;
        }
    }

    private void SetSearchMatches(TerminalMatch[] matches)
    {
        try { searchMemory.Resize(matches.LongLength * 128); }
        catch (ResourceLimitException) { matches = []; searchMemory.Resize(0); }
        searchMatches = matches;
    }

    private async void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Handled || InputSession is null || e.Text.Length == 0) return;
        ActivatePane((TerminalSurface)sender);
        e.Handled = true;
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
        if (reviewingPaste) return;
        bool singlePreview = false;
        try
        {
            TerminalPaste paste = TerminalPaste.Create(Clipboard.GetText());
            if (paste.CharacterCount == 0) return;
            if (owner is MainWindow host)
            {
                var broadcast = await app.Broadcast.PasteAsync(host, this, paste, (preview, token) =>
                    Dispatcher.InvokeAsync(() => PresentPaste(owner, paste, preview.Pacing, preview.Targets, token),
                        DispatcherPriority.Normal, token).Task);
                if (broadcast != BroadcastInput.PasteOutcome.Inactive)
                {
                    if (broadcast == BroadcastInput.PasteOutcome.Submitted && session == active) operated.RevealCursor();
                    return;
                }
            }
            reviewingPaste = singlePreview = true;
            await active.ValidatePasteAsync(paste, sourceLifetime);
            if (active != session || HasPendingInput || sourceLifetime.IsCancellationRequested) return;
            var (characterDelay, lineDelay, remember) = app.PasteDefaults;
            if (paste.HasNewline)
            {
                var choice = PresentPaste(owner, paste, (characterDelay, lineDelay, remember), null, sourceLifetime);
                if (choice is null || session != active || sourceLifetime.IsCancellationRequested) return;
                (characterDelay, lineDelay, remember) = choice;
                app.PasteDefaults = remember ? (characterDelay, lineDelay, true) : default;
            }

            using var cancellation = new CancellationTokenSource();
            pasteStop = cancellation;
            try
            {
                SetStatus("Connected", Text("Pasting"));
                var progress = new Progress<int>(sent =>
                {
                    if (pasteStop == cancellation && IsRunning && !stop.IsCancellationRequested)
                        SetStatus("Connected", string.Format(CultureInfo.CurrentCulture, Text("PasteProgress"), sent,
                            paste.CharacterCount));
                });
                operated.RevealCursor();
                bool completed = await active.SendPasteAsync(paste, characterDelay, lineDelay,
                    cancellation.Token, stop.Token, progress);
                Interlocked.Exchange(ref receivedRevision, 1);
                ScheduleFrame();
                if (IsRunning && !stop.IsCancellationRequested)
                    SetStatus("Connected", completed ? active.RecordingError : Text("PasteStopped"));
            }
            finally
            {
                pasteStop = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ArgumentException
            or InvalidOperationException or ExternalException)
        {
            if (IsRunning) SetStatus("Connected", exception.Message);
        }
        finally
        {
            if (singlePreview) reviewingPaste = false;
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
        form.AddField("PastePreview", new TextBox
        {
            Text = paste.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            MaxHeight = 260,
            MinHeight = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        form.AddField("PasteCountsLabel", new TextBox { Text = summary, IsReadOnly = true });
        if (targets is not null)
            form.AddField("BroadcastTargets", new TextBox
            {
                Text = string.Join(Environment.NewLine, targets),
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
        pendingSends++;
        try
        {
            TerminalSurface operated = output;
            if (Window.GetWindow(this) is MainWindow host && Broadcast.Send(host, input, stop.Token) is { } send)
            {
                if (await send) operated.RevealCursor();
                return;
            }
            switch (input.Kind)
            {
                case TerminalInputKind.Text: await active.SendTextAsync(input.Text, stop.Token, input.Alt); break;
                case TerminalInputKind.Enter: await active.SendEnterAsync(stop.Token); break;
                case TerminalInputKind.Backspace: await active.SendBackspaceAsync(stop.Token); break;
                case TerminalInputKind.Tab: await active.SendTabAsync(stop.Token); break;
                case TerminalInputKind.Control: await active.SendControlAsync(input.Text[0], stop.Token); break;
                case TerminalInputKind.Key: await active.SendKeyAsync(input.Key, input.Modifiers, stop.Token); break;
            }
            operated.RevealCursor();
            if (!stop.IsCancellationRequested)
            {
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
            if (IsRunning) SetStatus("Connected", exception.Message);
        }
        finally
        {
            pendingSends--;
        }
    }
}
