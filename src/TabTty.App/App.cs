using System.Globalization;
using System.IO;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace TabTty.App;

public sealed class App : Application
{
    private const bool DefaultAutoCloseDisconnectedTabs = true;
    private static readonly ResourceManager Strings = new("TabTty.App.Strings", typeof(App).Assembly);
    private static CultureInfo language = CultureInfo.GetCultureInfo("en");
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabTty", "settings.json");
    private readonly string settingsPath;
    private JsonObject settings = Defaults();
    private bool settingsReadOnly;
    private byte[]? settingsOriginal;
    private string? legacyVaultPath;
    private readonly Queue<InstanceLaunch?> pendingActivations = new();
    private bool drainingActivations;
    private readonly List<MainWindow> windowOrder = [];
    private MainWindow? lastActive;
    private int modalDepth;
    private bool exitCommitted;
    private bool started;
    private readonly HashSet<Task> cleanupOwners = [];

    internal string LanguageChoice => settings["language"]!.GetValue<string>();
    internal string ThemeChoice => settings["theme"]!.GetValue<string>();
    internal string BellChoice => settings["bell"]!.GetValue<string>();
    internal string TerminalFontFamily => settings["defaultTerminalFont"]!["family"]!.GetValue<string>();
    internal double TerminalFontSize => settings["defaultTerminalFont"]!["sizePoints"]!.GetValue<double>();
    internal int RetainedRows => settings["defaultRetainedRows"]!.GetValue<int>();
    internal bool AutoCloseDisconnectedTabs => settings["autoCloseDisconnectedTabs"]?.GetValue<bool>() ?? DefaultAutoCloseDisconnectedTabs;
    internal bool BroadcastAcrossWindows => settings["broadcastAcrossWindows"]?.GetValue<bool>() ?? false;
    internal BroadcastInput Broadcast { get; }
    internal AiCollaboration Ai { get; }
    internal int SettingsRevision { get; private set; }
    internal static string DefaultVaultPath => Path.Combine(LocalState.DirectoryPath, "credentials.tttykey");
    internal string VaultPath => VaultPathFor(settings);
    internal (int Character, int Line, bool Remember) PasteDefaults { get; set; }
    internal VaultController Vault { get; }
    internal SshHostTrust HostTrust { get; }
    internal HighlightCatalog Highlights { get; }
    internal ProfileCatalog Profiles { get; }
    internal SemaphoreSlim SshPrompts { get; } = new(1);

    public App() : this(DefaultSettingsPath) { }

    internal App(string settingsPath)
    {
        Broadcast = new BroadcastInput(this);
        Ai = new AiCollaboration(this);
        this.settingsPath = settingsPath;
        Vault = new VaultController(this);
        HostTrust = new SshHostTrust(this);
        Highlights = new HighlightCatalog(this);
        Profiles = new ProfileCatalog(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Ai.Dispose();
        Vault.Dispose();
        base.OnExit(e);
    }

    [STAThread]
    public static void Main(string[] args)
    {
        using var activation = new InstanceActivation();
        InstanceLaunch[] paths;
        try
        {
            paths = InstanceLaunch.Parse(args);
            if (!activation.IsPrimary)
            {
                if (!activation.ForwardAsync(paths).GetAwaiter().GetResult())
                    ShowActivationError();
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OperationCanceledException
            or UnauthorizedAccessException)
        {
            ShowActivationError(exception.Message);
            return;
        }

        var app = new App();
        app.Startup += (_, _) =>
        {
            app.Start(paths);
            app.Ai.Listen();
            activation.Listen(pathsToOpen => app.Dispatcher.InvokeAsync(() => app.Activate(pathsToOpen)).Task);
        };
        app.Run();
    }

    private static void ShowActivationError(string? details = null)
    {
        var app = new App();
        try
        {
            if (File.Exists(app.settingsPath)) app.settings = ReadSettings(app.settingsPath, out _);
        }
        catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception)) { }
        app.ApplySettings();
        string message = Text("ActivationFailed");
        ThemedMessageBox.Show(details is null ? message : $"{message}\n{details}", "TabTty",
            icon: MessageBoxImage.Error);
    }

    internal static string? LocalizedText(string key) => Strings.GetString(key, language);

    internal static string Text(string key) => LocalizedText(key)
        ?? throw new InvalidOperationException($"Missing localized string: {key}.");

    private void Start(InstanceLaunch[] paths)
    {
        started = true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (File.Exists(settingsPath))
                settings = ReadSettings(settingsPath, out settingsOriginal);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or FormatException)
        {
            ApplySettings();
            if (ThemedMessageBox.Show(Text("RestoreSettings"), "TabTty", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }

            settingsReadOnly = true;
        }

        ApplySettings();
        if (!LocalState.Initialize() || !Highlights.Initialize()) { Shutdown(); return; }
        ProfileFileAssociation.RegisterOnStartup();
        string? previousVault = LocalState.Read()["selectedVaultPath"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(previousVault) && Path.IsPathFullyQualified(previousVault))
            legacyVaultPath = previousVault;
        Activate(paths);
    }

    internal MainWindow CreateWindow(bool restorePlacement = true)
    {
        var window = new MainWindow(this);
        if (restorePlacement) WindowPlacement.Restore(window);
        windowOrder.Add(window);
        window.Loaded += (_, _) =>
        {
            ScheduleActivations();
        };
        window.Activated += (_, _) =>
        {
            windowOrder.Remove(window);
            windowOrder.Add(window);
            lastActive = window;
        };
        window.Closed += (_, _) =>
        {
            windowOrder.Remove(window);
            if (lastActive == window) lastActive = null;
            if (exitCommitted && windowOrder.Count == 0)
                foreach (FileTransferWindow transfer in Windows.OfType<FileTransferWindow>().ToArray())
                    _ = transfer.CloseWithTerminalAsync();
            ScheduleActivations();
            ShutdownIfFinished();
        };
        return window;
    }

    internal T Modal<T>(Func<T> action)
    {
        modalDepth++;
        try
        {
            return action();
        }
        finally
        {
            modalDepth--;
            ScheduleActivations();
        }
    }

    internal bool CanOwnCleanup => cleanupOwners.Count < 32;

    internal bool OwnCleanup(Task cleanup)
    {
        if (!CanOwnCleanup) return false;
        cleanupOwners.Add(cleanup);
        _ = cleanup.ContinueWith(task =>
        {
            _ = task.Exception;
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => cleanupOwners.Remove(task));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    internal bool Activate(InstanceLaunch[] paths)
    {
        if (!InstanceLaunch.Valid(paths) || Math.Max(1, paths.Length) > InstanceLaunch.MaximumPending - pendingActivations.Count ||
            Dispatcher.HasShutdownStarted || exitCommitted)
            return false;
        if (paths.Length == 0) pendingActivations.Enqueue(null);
        foreach (var path in paths) pendingActivations.Enqueue(path);
        ScheduleActivations();
        return true;
    }

    private void ScheduleActivations()
    {
        if (!drainingActivations && modalDepth == 0 && pendingActivations.Count != 0 && !exitCommitted)
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)DrainActivations);
    }

    private async void DrainActivations()
    {
        if (drainingActivations || modalDepth != 0 || Dispatcher.HasShutdownStarted || exitCommitted) return;
        drainingActivations = true;
        try
        {
            while (modalDepth == 0 && !exitCommitted && pendingActivations.TryPeek(out var launch))
            {
                if (launch is { IsVault: true } or { Edit: true })
                {
                    pendingActivations.Dequeue();
                    if (launch.IsVault) await Vault.OpenEditorAsync(launch.Path);
                    else await Profiles.EditFromExplorerAsync(launch.Path);
                    continue;
                }

                MainWindow? window = lastActive is { CanAcceptTabs: true } selected
                    ? selected
                    : windowOrder.LastOrDefault(candidate => candidate.CanAcceptTabs);
                if (window is null)
                {
                    // Loaded schedules the next drain for a window that is still initializing.
                    if (windowOrder.Any(candidate => !candidate.IsClosing)) return;
                    window = CreateWindow();
                    MainWindow = window;
                    window.Show();
                    if (!window.CanAcceptTabs) return;
                }
                pendingActivations.Dequeue();
                window.RestoreMacroHost();
                window.Activate();
                if (launch is { IsMacro: true }) await window.OpenMacroAsync(launch.Path, launch.Arguments);
                else if (launch is not null) window.OpenProfile(launch.Path);
            }
        }
        finally
        {
            drainingActivations = false;
            ScheduleActivations();
            ShutdownIfFinished();
        }
    }

    internal void CommitWindowClose(MainWindow window)
    {
        if (!started || drainingActivations || windowOrder.Any(other => other != window) || HasEditor()) return;
        CommitExit();
    }

    internal void EditorClosed(Window editor)
    {
        if (started && !drainingActivations && windowOrder.Count == 0 && !HasEditor(editor))
            CommitExit();
        ShutdownIfFinished(editor);
    }

    private bool HasEditor(Window? except = null) =>
        Windows.Cast<Window>().Any(window => window != except && window is ProfileEditorWindow or VaultEditorWindow);

    private void CommitExit()
    {
        exitCommitted = true;
        pendingActivations.Clear();
    }

    private void ShutdownIfFinished(Window? closedEditor = null)
    {
        if (!started || Dispatcher.HasShutdownStarted ||
            drainingActivations || pendingActivations.Count != 0 || windowOrder.Count != 0 || HasEditor(closedEditor)) return;
        CommitExit();
        Shutdown();
    }

    private string VaultPathFor(JsonObject state) => state.ContainsKey("credentialVaultPath")
        ? state["credentialVaultPath"]?.GetValue<string>() ?? DefaultVaultPath
        : legacyVaultPath ?? DefaultVaultPath;

    internal void SaveSettings(string languageChoice, string themeChoice, string bellChoice,
        string terminalFontFamily, double terminalFontSize, int retainedRows, bool autoCloseDisconnectedTabs,
        string vaultPath, int revision, bool? broadcastAcrossWindows = null)
    {
        if (revision != SettingsRevision)
            throw new InvalidOperationException(Text("ManagementTabOutdated"));
        if (settingsReadOnly)
            throw new InvalidOperationException(Text("RestoreSettings"));
        string priorVaultPath = VaultPath;
        vaultPath = vaultPath == priorVaultPath ? priorVaultPath : NormalizeVaultPath(vaultPath);
        bool vaultChanged = !string.Equals(priorVaultPath, vaultPath, StringComparison.Ordinal);
        if (vaultChanged) Vault.CheckPathChange(priorVaultPath, vaultPath);
        FontCatalog.Validate(terminalFontFamily, TerminalFontFamily);
        var candidate = (JsonObject)settings.DeepClone();
        bool scopeChanged = broadcastAcrossWindows is { } scope && scope != BroadcastAcrossWindows;
        if (broadcastAcrossWindows is { } savedScope) candidate["broadcastAcrossWindows"] = savedScope;
        candidate["language"] = languageChoice;
        candidate["theme"] = themeChoice;
        candidate["bell"] = bellChoice;
        candidate["defaultTerminalFont"]!["family"] = terminalFontFamily;
        candidate["defaultTerminalFont"]!["sizePoints"] = terminalFontSize;
        candidate["defaultRetainedRows"] = retainedRows;
        candidate["autoCloseDisconnectedTabs"] = autoCloseDisconnectedTabs;
        if (vaultChanged || settings["credentialVaultPath"] is not null)
            candidate["credentialVaultPath"] = vaultPath;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 65536) throw new InvalidDataException("Settings exceed 64 KiB.");
        JsonStoreRecovery.Save(settingsPath, bytes, settingsOriginal);
        settingsOriginal = bytes;
        settings = candidate;
        if (scopeChanged) Broadcast.ScopeChanged();
        if (vaultChanged) Vault.SelectionChanged(priorVaultPath);
        SettingsRevision++;
        ApplySettings();
        UpdateWindowLabels();
    }

    internal void ShowStoreRecovery(MainWindow owner) => JsonStoreRecovery.Show(this, owner,
    [
        new JsonStoreRecovery.Store(Text("Settings"), settingsPath, path => _ = ReadSettings(path, out _), () =>
        {
            string priorVaultPath = VaultPath;
            bool priorScope = BroadcastAcrossWindows;
            settings = ReadSettings(settingsPath, out settingsOriginal);
            if (priorScope != BroadcastAcrossWindows) Broadcast.ScopeChanged();
            if (!string.Equals(priorVaultPath, VaultPath, StringComparison.Ordinal)) Vault.SelectionChanged(priorVaultPath);
            settingsReadOnly = false;
            SettingsRevision++;
            ApplySettings();
            UpdateWindowLabels();
        }, () => JsonSerializer.SerializeToUtf8Bytes(Defaults())),
        Highlights.RecoveryStore(), LocalState.RecoveryStore(), HostTrust.RecoveryStore(),
    ]);

    private void UpdateWindowLabels()
    {
        foreach (Window window in Windows)
        {
            if (window is MainWindow main) main.UpdateLabels();
            else if (window is ProfileEditorWindow editor) editor.UpdateLabels();
            else if (window is VaultEditorWindow vaultEditor) vaultEditor.UpdateLabels();
            else if (window is FileTransferWindow transfer) transfer.UpdateLabels();
        }
    }

    private void ApplySettings()
    {
        string selected = LanguageChoice == "system" ? SystemLanguage.Resolve() : LanguageChoice;
        language = CultureInfo.GetCultureInfo(selected == "ja" ? "ja" : "en");
#pragma warning disable WPF0001 // .NET 10 Fluent theme integration.
        ThemeMode = ThemeChoice switch
        {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }

    private static JsonObject Defaults() => new()
    {
        ["schemaVersion"] = 1,
        ["requiredFeatures"] = new JsonArray(),
        ["language"] = "system",
        ["theme"] = "system",
        ["bell"] = "visual",
        ["defaultTerminalFont"] = new JsonObject { ["family"] = "Consolas", ["sizePoints"] = 12d },
        ["defaultRetainedRows"] = 5000,
        ["autoCloseDisconnectedTabs"] = DefaultAutoCloseDisconnectedTabs,
    };

    internal static string NormalizeVaultPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DefaultVaultPath;
        if (path.Length > 32760 || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.AsSpan().ContainsAny('\0', '*', '?') ||
            path.AsSpan(path.Length > 1 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? 2 : 0).Contains(':') ||
            !string.Equals(Path.GetExtension(path), ".tttykey", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Text("InvalidVaultPath"));
        string full = Path.GetFullPath(path);
        if (full.Length > 32760 || !string.Equals(Path.GetExtension(full), ".tttykey", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Text("InvalidVaultPath"));
        return full;
    }

    private static JsonObject ReadSettings(string path, out byte[] bytes)
    {
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[65537];
        int length = stream.ReadAtLeast(buffer, buffer.Length, false);
        if (length == buffer.Length) throw new InvalidDataException("Settings exceed 64 KiB.");
        bytes = buffer.AsSpan(0, length).ToArray();
        JsonObject parsed = JsonStoreRecovery.ParseObject(bytes);
        if (parsed["schemaVersion"]?.GetValue<int>() != 1 ||
            parsed["requiredFeatures"] is not JsonArray { Count: 0 } ||
            parsed["language"]?.GetValue<string>() is not ("en" or "ja" or "system") ||
            parsed["theme"]?.GetValue<string>() is not ("light" or "dark" or "system") ||
            parsed["bell"]?.GetValue<string>() is not ("visual" or "off") ||
            parsed["defaultTerminalFont"] is not JsonObject font ||
            string.IsNullOrWhiteSpace(font["family"]?.GetValue<string>()) ||
            font["family"]!.GetValue<string>().EnumerateRunes().Count() > 256 ||
            font["family"]!.GetValue<string>().Any(char.IsControl) ||
            font["sizePoints"]?.GetValue<double>() is not (>= 6 and <= 72) ||
            parsed["defaultRetainedRows"]?.GetValue<int>() is not (>= 100 and <= 100000) ||
            (parsed.ContainsKey("broadcastAcrossWindows") &&
                (parsed["broadcastAcrossWindows"] is not JsonValue broadcast || !broadcast.TryGetValue<bool>(out _))) ||
            (parsed.ContainsKey("autoCloseDisconnectedTabs") &&
                (parsed["autoCloseDisconnectedTabs"] is not JsonValue autoClose || !autoClose.TryGetValue<bool>(out _))))
        {
            throw new InvalidDataException("Invalid application settings.");
        }

        if (parsed["credentialVaultPath"] is { } vaultPath)
        {
            try { parsed["credentialVaultPath"] = NormalizeVaultPath(vaultPath.GetValue<string>()); }
            catch (ArgumentException exception) { throw new InvalidDataException(Text("InvalidVaultPath"), exception); }
        }
        return parsed;
    }
}
