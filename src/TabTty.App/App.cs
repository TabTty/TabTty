using System.Globalization;
using System.IO;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TabTty.Core;

namespace TabTty.App;

public sealed class App : Application
{
    internal const double DefaultTerminalFontSize = 11;
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
    internal MainWindow? LastTabWindow => lastActive is { CanAcceptTabs: true } selected
        ? selected : windowOrder.LastOrDefault(candidate => candidate.CanAcceptTabs);
    private int modalDepth;
    private bool exitCommitted;
    private bool started;
    private SettingsPublication? settingsPublication;
    private readonly Action<string, string, string?>? issueSettings;
    internal Action<string, string, string?>? IssueMasterPassword { get; }
    internal VaultFileHooks? VaultFiles { get; }
    private readonly HashSet<Task> cleanupOwners = [];

    internal string LanguageChoice => settings["language"]!.GetValue<string>();
    internal string ThemeChoice => settings["theme"]!.GetValue<string>();
    internal string BellChoice => settings["bell"]!.GetValue<string>();
    internal string TerminalFontFamily => settings["defaultTerminalFont"]!["family"]!.GetValue<string>();
    internal double TerminalFontSize => settings["defaultTerminalFont"]!["sizePoints"]!.GetValue<double>();
    internal int RetainedRows => settings["defaultRetainedRows"]!.GetValue<int>();
    internal bool AutoCloseDisconnectedTabs => settings["autoCloseDisconnectedTabs"]?.GetValue<bool>() ?? DefaultAutoCloseDisconnectedTabs;
    internal long MaxPasteTextBytes => settings["maxPasteTextBytes"]?.GetValue<long>() ??
        TabTty.Core.TerminalPaste.DefaultMaxSourceBytes;
    internal bool BroadcastAcrossWindows => settings["broadcastAcrossWindows"]?.GetValue<bool>() ?? false;
    internal KeyboardShortcuts Shortcuts { get; private set; } = KeyboardShortcuts.Defaults;
    internal RecordingDefaults LogDefaults => ReadLogDefaults(settings);

    private static RecordingDefaults ReadLogDefaults(JsonObject state)
    {
        bool date = state["logNameIncludeDate"]?.GetValue<bool>() ?? true;
        bool time = date && state["logNameIncludeTime"]?.GetValue<bool>() == true;
        bool microseconds = time && state["logNameIncludeMicroseconds"]?.GetValue<bool>() == true;
        string dateFormat = date ? "_<datetime:yyyy-MM-dd" +
            (time ? "_HH-mm-ss" + (microseconds ? "-ffffff" : "") : "") + ">" : "";
        string suffix = dateFormat + (state["logNameIncludeSequence"]?.GetValue<bool>() != false ? "_<serial:0,4>" : "");
        return new(state["defaultLogDirectory"]?.GetValue<string>() ?? "",
            state["defaultLogFilenameTemplate"]?.GetValue<string>() ?? RecordingDefaults.ProfileName + suffix,
            state["defaultMacroLogFilenameTemplate"]?.GetValue<string>() ?? RecordingDefaults.MacroAndProfileName + suffix);
    }
    internal BroadcastInput Broadcast { get; }
    internal WindowStatusReports StatusReports { get; } = new();
    internal AiCollaboration Ai { get; }
    internal int SettingsRevision { get; private set; }
    internal static string DefaultVaultPath => Path.Combine(LocalState.DirectoryPath, "credentials.tttykey");
    internal string VaultPath => VaultPathFor(settings);
    internal string ProfileSaveDirectory => settings["profileSaveDirectory"]?.GetValue<string>() ?? DefaultProfileSaveDirectory;

    private static string DefaultProfileSaveDirectory
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolderOption.DoNotVerify);
            return documents.Length == 0 ? "" : Path.Combine(documents, "TabTty", "Profiles");
        }
    }
    internal (int Character, int Line, bool Remember) PasteDefaults { get; set; }
    internal VaultController Vault { get; }
    internal SshHostTrust HostTrust { get; }
    internal HighlightCatalog Highlights { get; }
    internal ProfileCatalog Profiles { get; }
    internal SemaphoreSlim SshPrompts { get; } = new(1);

    public App() : this(DefaultSettingsPath) { }

    private static void ApplyContextSubmenuTemplates(ItemsControl menu, ControlTemplate template)
    {
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            if (!item.HasItems) continue;
            item.Template = template;
            ApplyContextSubmenuTemplates(item, template);
        }
    }

    internal App(string settingsPath, Action<string, string, string?>? issueSettings = null,
        Action<string, string, string?>? issueMasterPassword = null, Action<string, string, string?>? issueProfiles = null,
        VaultFileHooks? vaultFiles = null, Action<string, string, string?>? issueOtherStores = null,
        Action<string>? validateProfileStage = null)
    {
        WindowFrameAccent.Register();
        Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
        var menuResources = new ResourceDictionary
        {
            Source = new Uri("/TabTty;component/MenuStyles.xaml", UriKind.Relative),
        };
        var contextMenuStyle = (Style)menuResources[typeof(ContextMenu)];
        var submenuTemplate = (ControlTemplate)menuResources["ContextSubmenuTemplate"];
        // Style handlers run after the menu's own Opened handlers have populated its commands.
        contextMenuStyle.Setters.Add(new EventSetter(ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => ApplyContextSubmenuTemplates((ContextMenu)sender, submenuTemplate))));
        Resources[typeof(ContextMenu)] = contextMenuStyle;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TabTty;component/DetailsTabs.xaml", UriKind.Relative),
        });
        Broadcast = new BroadcastInput(this);
        Ai = new AiCollaboration(this);
        this.settingsPath = settingsPath;
        this.issueSettings = issueSettings;
        IssueMasterPassword = issueMasterPassword;
        VaultFiles = vaultFiles;
        Vault = new VaultController(this);
        string storeDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        HostTrust = new SshHostTrust(this, Path.Combine(storeDirectory, "host-keys.json"), issueOtherStores);
        Highlights = new HighlightCatalog(this, Path.Combine(storeDirectory, "highlighting.json"), issueOtherStores);
        Profiles = new ProfileCatalog(this, issueProfiles, validateProfileStage);
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
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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
    internal static CultureInfo UiLanguage => language;

    internal static string Text(string key) => LocalizedText(key)
        ?? throw new InvalidOperationException($"Missing localized string: {key}.");

    internal void ReadSavedSettings()
    {
        if (File.Exists(settingsPath)) settings = ReadSettings(settingsPath, out settingsOriginal);
    }

    private void Start(InstanceLaunch[] paths)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            ReadSavedSettings();
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
        string? previousVault = LocalState.Read()["selectedVaultPath"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(previousVault) && Path.IsPathFullyQualified(previousVault))
            legacyVaultPath = previousVault;
        OpenInitialWindows(paths);
    }

    internal void OpenInitialWindows(InstanceLaunch[] paths)
    {
        started = true;
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

                MainWindow? window = LastTabWindow;
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
                if (launch is { IsMacro: true }) await window.OpenExternalMacroAsync(launch);
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
            drainingActivations || pendingActivations.Count != 0 || windowOrder.Count != 0 || HasEditor(closedEditor) ||
            settingsPublication is not null || Vault.HasActiveOperations) return;
        CommitExit();
        Shutdown();
    }

    internal void VaultWorkSettled() => ShutdownIfFinished();

    private SettingsPublication ReserveSettingsPublication(string priorPath, string nextPath)
    {
        if (settingsPublication is not null) throw new InvalidOperationException(Text("VaultBusy"));
        return settingsPublication = new(this, Vault.ReservePathChange(priorPath, nextPath));
    }

    private sealed class SettingsPublication(App app, IDisposable? selection) : IDisposable
    {
        public void Dispose()
        {
            if (!ReferenceEquals(app.settingsPublication, this)) return;
            selection?.Dispose();
            app.settingsPublication = null;
            app.ShutdownIfFinished();
        }
    }

    private string VaultPathFor(JsonObject state) => state.ContainsKey("credentialVaultPath")
        ? state["credentialVaultPath"]?.GetValue<string>() ?? DefaultVaultPath
        : legacyVaultPath ?? DefaultVaultPath;

    internal sealed record SettingsChanges(string? Language = null, string? Theme = null, string? Bell = null,
        string? FontFamily = null, double? FontSize = null, int? RetainedRows = null, bool? AutoClose = null,
        string? VaultPath = null, bool? Broadcast = null, long? PasteMaximum = null, string? ProfileDirectory = null,
        string? LogDirectory = null, string? LogTemplate = null, string? MacroLogTemplate = null,
        IReadOnlyDictionary<ShortcutAction, string?>? Shortcuts = null);

    internal Dictionary<ShortcutAction, string?> ChangedShortcuts(KeyboardShortcuts edited) =>
        KeyboardShortcuts.Commands.Where(command => Shortcuts[command.Action] != edited[command.Action])
            .ToDictionary(command => command.Action, command => edited[command.Action]);

    internal async Task SaveSettingsAsync(int revision, SettingsChanges edits)
    {
        if (revision != SettingsRevision)
            throw new InvalidOperationException(Text("ManagementTabOutdated"));
        if (settingsReadOnly)
            throw new InvalidOperationException(Text("RestoreSettings"));
        if (!ValidTerminalDefaults(edits.FontSize ?? TerminalFontSize, edits.RetainedRows ?? RetainedRows))
            throw new InvalidDataException(Text("InvalidTerminalDefaults"));
        string priorVaultPath = VaultPath;
        string vaultPath = edits.VaultPath is { } selectedVault ? NormalizeVaultPath(selectedVault) : priorVaultPath;
        using var publication = ReserveSettingsPublication(priorVaultPath, vaultPath);
        var candidate = (JsonObject)settings.DeepClone();
        if (edits.Broadcast is { } savedScope) candidate["broadcastAcrossWindows"] = savedScope;
        if (edits.Shortcuts is { Count: > 0 } shortcuts)
        {
            if (candidate[KeyboardShortcuts.SettingsKey] is not JsonObject stored)
                candidate[KeyboardShortcuts.SettingsKey] = stored = new JsonObject();
            foreach (var (action, choice) in shortcuts) stored[action.ToString()] = choice;
            candidate.Remove("tabFocusKey");
        }
        if (edits.PasteMaximum is { } maximum)
        {
            if (maximum <= 0) throw new ArgumentException(Text("InvalidPasteSourceMaximum"));
            candidate["maxPasteTextBytes"] = maximum;
        }
        if (edits.Language is { } language) candidate["language"] = language;
        if (edits.Theme is { } theme) candidate["theme"] = theme;
        if (edits.Bell is { } bell) candidate["bell"] = bell;
        if (edits.FontFamily is { } family)
        {
            FontCatalog.Validate(family, TerminalFontFamily);
            candidate["defaultTerminalFont"]!["family"] = family;
        }
        if (edits.FontSize is { } size) candidate["defaultTerminalFont"]!["sizePoints"] = size;
        if (edits.RetainedRows is { } rows) candidate["defaultRetainedRows"] = rows;
        if (edits.AutoClose is { } close) candidate["autoCloseDisconnectedTabs"] = close;
        if (edits.VaultPath is not null)
            candidate["credentialVaultPath"] = vaultPath;
        if (edits.ProfileDirectory is { } profileSaveDirectory)
            candidate["profileSaveDirectory"] = NormalizeProfileSaveDirectory(profileSaveDirectory);
        string[] legacyFlags = ["logNameIncludeDate", "logNameIncludeTime", "logNameIncludeMicroseconds", "logNameIncludeSequence"];
        if (legacyFlags.Any(candidate.ContainsKey))
        {
            var legacy = ReadLogDefaults(candidate);
            candidate["defaultLogFilenameTemplate"] ??= legacy.ProfileTemplate;
            candidate["defaultMacroLogFilenameTemplate"] ??= legacy.MacroTemplate;
            foreach (string flag in legacyFlags) candidate.Remove(flag);
        }
        if (edits.LogDirectory is { } logDirectory) candidate["defaultLogDirectory"] = logDirectory;
        if (edits.LogTemplate is { } logTemplate) candidate["defaultLogFilenameTemplate"] = logTemplate;
        if (edits.MacroLogTemplate is { } macroLogTemplate) candidate["defaultMacroLogFilenameTemplate"] = macroLogTemplate;
        if (edits.LogDirectory is not null || edits.LogTemplate is not null || edits.MacroLogTemplate is not null)
            ReadLogDefaults(candidate).Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 65536) throw new InvalidDataException("Settings exceed 64 KiB.");
        ValidateSettings(JsonStoreRecovery.ParseObject(bytes));
        var operation = JsonStoreRecovery.BeginSave(settingsPath, bytes, settingsOriginal,
            path => _ = ReadSettings(path, out _), issueSettings);
        JsonStoreRecovery.RequireVerified(await operation.ObserveAsync());
        AdoptSettings(candidate, bytes);
    }

    internal void ShowStoreRecovery(MainWindow owner) => JsonStoreRecovery.Show(this, owner,
    [
        new JsonStoreRecovery.Store(Text("Settings"), settingsPath, path => _ = ReadSettings(path, out _),
            () => { }, () => JsonSerializer.SerializeToUtf8Bytes(Defaults()), candidate =>
        {
            string priorVaultPath = VaultPath;
            JsonObject prepared = ReadSettings(candidate, out byte[] preparedBytes);
            var reservation = ReserveSettingsPublication(priorVaultPath, VaultPathFor(prepared));
            return new JsonStoreRecovery.Adoption(reservation, () => AdoptSettings(prepared, preparedBytes));
        }),
        Highlights.RecoveryStore(), LocalState.RecoveryStore(), HostTrust.RecoveryStore(),
    ]);

    private void AdoptSettings(JsonObject verified, byte[] bytes)
    {
        string priorVaultPath = VaultPath;
        string priorFolder = Profiles.ActiveFolder;
        bool priorScope = BroadcastAcrossWindows;
        settings = verified;
        settingsOriginal = bytes;
        settingsReadOnly = false;
        SettingsRevision++;
        if (!string.Equals(priorVaultPath, VaultPath, StringComparison.Ordinal)) Vault.SelectionChanged();
        if (priorScope != BroadcastAcrossWindows) Broadcast.ScopeChanged();
        ApplySettings();
        if (priorFolder != Profiles.ActiveFolder) Profiles.ProfileFolderChanged();
        UpdateWindowLabels();
    }

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
        Shortcuts = KeyboardShortcuts.Read(settings);
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
        ["defaultTerminalFont"] = new JsonObject { ["family"] = "Consolas", ["sizePoints"] = DefaultTerminalFontSize },
        ["defaultRetainedRows"] = 5000,
        ["autoCloseDisconnectedTabs"] = DefaultAutoCloseDisconnectedTabs,
        ["defaultLogDirectory"] = RecordingDefaults.BuiltIn.Directory,
        ["defaultLogFilenameTemplate"] = RecordingDefaults.BuiltIn.ProfileTemplate,
        ["defaultMacroLogFilenameTemplate"] = RecordingDefaults.BuiltIn.MacroTemplate,
    };

    internal static string NormalizeVaultPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DefaultVaultPath;
        if (!OrdinaryWindowsPath.IsValid(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".tttykey", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Text("InvalidVaultPath"));
        string full = Path.GetFullPath(path);
        if (!OrdinaryWindowsPath.IsValid(full) || !string.Equals(Path.GetExtension(full), ".tttykey", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Text("InvalidVaultPath"));
        return full;
    }

    private static string? NormalizeProfileSaveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!OrdinaryWindowsPath.IsValid(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException(Text("InvalidProfileSaveDirectory"));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OrdinaryWindowsPath.IsValid(full))
            throw new ArgumentException(Text("InvalidProfileSaveDirectory"));
        return full;
    }

    private static JsonObject ReadSettings(string path, out byte[] bytes)
    {
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[65537];
        int length = stream.ReadAtLeast(buffer, buffer.Length, false);
        if (length == buffer.Length) throw new InvalidDataException("Settings exceed 64 KiB.");
        bytes = buffer.AsSpan(0, length).ToArray();
        return ValidateSettings(JsonStoreRecovery.ParseObject(bytes));
    }

    private static JsonObject ValidateSettings(JsonObject parsed)
    {
        if (parsed["schemaVersion"]?.GetValue<int>() != 1 ||
            parsed["requiredFeatures"] is not JsonArray { Count: 0 } ||
            parsed["language"]?.GetValue<string>() is not ("en" or "ja" or "system") ||
            parsed["theme"]?.GetValue<string>() is not ("light" or "dark" or "system") ||
            parsed["bell"]?.GetValue<string>() is not ("visual" or "off") ||
            parsed["defaultTerminalFont"] is not JsonObject font ||
            string.IsNullOrWhiteSpace(font["family"]?.GetValue<string>()) ||
            font["family"]!.GetValue<string>().EnumerateRunes().Count() > 256 ||
            font["family"]!.GetValue<string>().Any(char.IsControl) ||
            !ValidTerminalDefaults(font["sizePoints"]?.GetValue<double>(), parsed["defaultRetainedRows"]?.GetValue<int>()) ||
            (parsed.ContainsKey("maxPasteTextBytes") &&
                (parsed["maxPasteTextBytes"] is not JsonValue maxPaste || !maxPaste.TryGetValue<long>(out long maximum) ||
                 maximum <= 0)) ||
            (parsed.ContainsKey("broadcastAcrossWindows") &&
                (parsed["broadcastAcrossWindows"] is not JsonValue broadcast || !broadcast.TryGetValue<bool>(out _))) ||
            !IsOptionalBoolean("logNameIncludeDate") || !IsOptionalBoolean("logNameIncludeTime") ||
            !IsOptionalBoolean("logNameIncludeMicroseconds") || !IsOptionalBoolean("logNameIncludeSequence") ||
            !IsOptionalLogText("defaultLogDirectory", 32760) ||
            !IsOptionalLogText("defaultLogFilenameTemplate", RecordingNameTemplate.MaximumLength) ||
            !IsOptionalLogText("defaultMacroLogFilenameTemplate", RecordingNameTemplate.MaximumLength) ||
            (parsed.ContainsKey("autoCloseDisconnectedTabs") &&
                (parsed["autoCloseDisconnectedTabs"] is not JsonValue autoClose || !autoClose.TryGetValue<bool>(out _))))
        {
            throw new InvalidDataException("Invalid application settings.");
        }
        _ = KeyboardShortcuts.Read(parsed);

        if (parsed["credentialVaultPath"] is { } vaultPath)
        {
            try { parsed["credentialVaultPath"] = NormalizeVaultPath(vaultPath.GetValue<string>()); }
            catch (ArgumentException exception) { throw new InvalidDataException(Text("InvalidVaultPath"), exception); }
        }
        if (parsed["profileSaveDirectory"] is { } profileDirectory)
        {
            try { parsed["profileSaveDirectory"] = NormalizeProfileSaveDirectory(profileDirectory.GetValue<string>()); }
            catch (ArgumentException exception) { throw new InvalidDataException(Text("InvalidProfileSaveDirectory"), exception); }
        }
        return parsed;

        bool IsOptionalBoolean(string key) => !parsed.ContainsKey(key) ||
            parsed[key] is JsonValue value && value.TryGetValue<bool>(out _);
        bool IsOptionalLogText(string key, int maximum) => !parsed.ContainsKey(key) ||
            parsed[key] is JsonValue value && value.TryGetValue<string>(out string? text) &&
            text is not null && text.Length <= maximum && !text.Contains('\0');
    }

    private static bool ValidTerminalDefaults(double? size, int? rows) =>
        size is >= 6 and <= 72 && rows is >= 100 and <= 100_000;
}
