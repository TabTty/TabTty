using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed partial class ProfileCatalog
{
    private const string TrustKey = "profileTrust";
    private const string RecordingMappingsKey = "recordingMappings";
    private long trustReset;
    private readonly Dictionary<string, long> removals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> temporaryRecordingMappings = new(StringComparer.Ordinal);

    private enum ProfileAnswer { Cancel, Once, Remember }
    private readonly record struct TrustState(Guid? Registration, string? Version, long Reset, long Removal);

    private TrustState ReadTrust(string location)
    {
        var state = LocalState.ReadForProfileTrust();
        return new(FindRegistration(state, location)?["registrationId"]?.GetValue<Guid>(),
            state[TrustKey]?[location]?.GetValue<string>(), trustReset, removals.GetValueOrDefault(location));
    }

    private static JsonObject? FindRegistration(JsonObject state, string location) =>
        state["registrations"]!.AsArray().OfType<JsonObject>()
            .FirstOrDefault(item => item["path"]?.GetValue<string>() == location);

    private static void StoreVersion(JsonObject state, string location, string version)
    {
        if (state[TrustKey] is not JsonObject trust)
            state[TrustKey] = trust = new JsonObject();
        trust[location] = version;
    }

    private void CommitRegistration(string location, ConnectionProfile profile, bool used, string version)
    {
        LocalState.Change(state =>
        {
            var registration = FindRegistration(state, location);
            if (registration is null)
            {
                var registrations = state["registrations"]!.AsArray();
                if (registrations.Count >= 10_000) throw new IOException(Text("LocalStateInvalid"));
                registration = new JsonObject
                {
                    ["registrationId"] = Guid.NewGuid(), ["path"] = location,
                    ["favorite"] = false, ["lastUsed"] = null,
                };
                registrations.Add(registration);
                StoreVersion(state, location, version);
            }
            StoreRegistrationGroup(registration, profile.GroupName);
            if (used) registration["lastUsed"] = DateTimeOffset.UtcNow;
        });
        RememberMetadata(location, profile);
    }

    private async Task<string> RegisterSavedAsync(string path, ConnectionProfile profile, long reset, bool selectNewProfile = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string location = await NormalizeLocationAsync(path, timeout.Token);
        configuredFolder = null;
        if (reset != trustReset || Path.GetDirectoryName(location) != ActiveFolder) throw new IOException(Text("ProfileTrustWriteFailed"));
        CommitRegistration(location, profile, used: false, profile.TrustVersion);
        if (selectNewProfile) lastCreatedProfile = (lastCreatedProfile.Sequence + 1, location);
        ProfileSaved?.Invoke(null);
        return location;
    }

    internal void ResetTrust(Window owner, Action<Exception> reportFailure)
    {
        if (app.Modal(() => ThemedMessageBox.Show(owner, Text("ProfileTrustResetConfirm"), Text("ProfileTrustReset"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No,
            acceptKey: "Reset")) != MessageBoxResult.Yes) return;
        try
        {
            trustReset++;
            removals.Clear();
            LocalState.Change(state => state.Remove(TrustKey));
        }
        catch (Exception error) when (IsFileProblem(error))
        {
            reportFailure(new IOException(Text("ProfileTrustWriteFailed"), error));
        }
    }

    internal sealed record OnceApproval(string Location, string Version);
    internal sealed class Admission(ConnectionProfile profile, OnceApproval? once, MacroProgram? program) : IDisposable
    {
        internal ConnectionProfile Profile { get; } = profile;
        internal OnceApproval? Once { get; } = once;
        internal MacroProgram? Program { get; private set; } = program;
        internal MacroProgram? TakeProgram()
        {
            var value = Program;
            Program = null;
            return value;
        }
        public void Dispose() => TakeProgram()?.Dispose();
    }

    internal async Task<ConnectionProfile> AdmitAsync(Window? owner, string path, string? macroName, CancellationToken token)
    {
        using var admission = await AdmitAsync(owner, path, macroName, token, reconnect: false, previousOnce: null);
        return admission.Profile;
    }

    internal async Task<Admission> AdmitAsync(Window? owner, string path, string? macroName, CancellationToken token,
        bool reconnect, OnceApproval? previousOnce, MacroRun? caller = null, TerminalTab? tab = null)
    {
        ConnectionProfile profile = await LoadForUseAsync(owner, path, token, macroName, tab);
        string location = await ProfileLocationAsync(profile, token);
        string version = profile.TrustVersion;
        var once = new OnceApproval(location, version);
        MacroProgram? program = CompilePostConnectionSource(profile, caller);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string folder = app.ProfileSaveDirectory;
                var before = ReadTrust(location);
                bool unregistered = macroName is null && (before.Registration is null || Path.GetDirectoryName(location) != ActiveFolder);
                if (unregistered && previousOnce == once)
                    return Complete(profile, once);
                if (!unregistered && before.Version == version)
                    return Complete(profile, null);

                var choice = await AskAsync(owner, "ProfileTrustReview", window =>
                {
                    AddProfileDetails(window, location, profile, macroName, before.Version is not null || previousOnce is not null);
                    if (unregistered && Path.GetDirectoryName(location) != ActiveFolder)
                    {
                        window.AddNotice("ProfileImportLogDirectory");
                        window.AddField("ProfileImportLogBase", new DialogText(PermissionDisplay.Literal(folder)));
                    }
                }, "ProfileConnectOnce", unregistered ? "ProfileImportAndConnect" : "ProfileTrustAndConnect", token, tab);
                if (choice == ProfileAnswer.Cancel) throw new SshInteractionCancelledException();
                token.ThrowIfCancellationRequested();
                if (ReadTrust(location) != before || folder != app.ProfileSaveDirectory) continue;
                if (choice == ProfileAnswer.Once)
                    return Complete(profile, unregistered && (!reconnect || previousOnce is not null) ? once : null);
                if (!unregistered)
                {
                    LocalState.Change(state => StoreVersion(state, location, version));
                    return Complete(profile, null);
                }

                await registrationChanges.WaitAsync(token);
                try
                {
                    if (ReadTrust(location) != before || folder != app.ProfileSaveDirectory) continue;
                    var imported = await ImportRevisionAsync(profile, location, folder, before, used: true, token);
                    if (imported is not null) return Complete(imported, null);
                }
                finally { registrationChanges.Release(); }
            }
        }
        finally { program?.Dispose(); }

        Admission Complete(ConnectionProfile admitted, OnceApproval? approval)
        {
            var result = new Admission(admitted, approval, program);
            program = null;
            return result;
        }
    }

    private static MacroProgram? CompilePostConnectionSource(ConnectionProfile profile, MacroRun? caller = null)
    {
        try { return caller is null ? profile.CompilePostConnectionMacro() : caller.CompilePostConnection(profile); }
        catch (MacroSourceException error) { throw new InvalidDataException(TerminalTab.PostConnectionSourceError(error), error); }
    }

    private static async Task<bool> ReviewProfileRegistrationAsync(Window owner, string path,
        ConnectionProfile profile, CancellationToken token) =>
        !profile.HasPostConnectionMacro || await AskAsync(owner, "PostConnectionMacroRegistration",
            window => AddProfileDetails(window, path, profile, null, false),
            "PostConnectionMacroTrustProfile", null, token) == ProfileAnswer.Once;

    internal async Task ApproveRecordingDestinationAsync(Window? owner, RecordingDestination destination, CancellationToken token,
        TerminalTab? tab = null)
    {
        if (destination.DriveTarget is null) return;
        string key = Digest(Path.TrimEndingDirectorySeparator(destination.Path).ToUpperInvariant());
        string version = Digest(destination.EffectivePath.ToUpperInvariant() + "\0" + destination.DriveTarget.ToUpperInvariant());
        while (true)
        {
            token.ThrowIfCancellationRequested();
            destination.Verify();
            string? previous = RememberedMapping();
            if (previous == version) return;
            if (previous is not null && await AskAsync(owner, "LogDestinationReview", window =>
                {
                    window.AddNotice("LogDestinationReviewNotice");
                    window.AddField("LogRequestedDestination", new DialogText(PermissionDisplay.Literal(destination.Path)));
                    window.AddField("LogEffectiveDestination", new DialogText(PermissionDisplay.Literal(destination.EffectivePath)));
                }, "ApproveLogDestination", null, token, tab) == ProfileAnswer.Cancel)
                throw new RecordingStartException(Text("LogDestinationRejected"));

            token.ThrowIfCancellationRequested();
            destination.Verify();
            if (RememberedMapping() != previous) continue;
            if (LocalState.IsTemporary) temporaryRecordingMappings[key] = version;
            else LocalState.Change(state =>
            {
                if (state[RecordingMappingsKey] is not JsonObject mappings)
                    state[RecordingMappingsKey] = mappings = new JsonObject();
                mappings[key] = version;
            });
            return;
        }

        string? RememberedMapping() => LocalState.IsTemporary ? temporaryRecordingMappings.GetValueOrDefault(key) :
            LocalState.ReadForProfileTrust()[RecordingMappingsKey]?[key]?.GetValue<string>();
        static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private async Task<ConnectionProfile> LoadForUseAsync(Window? owner, string path, CancellationToken token,
        string? macroName = null, TerminalTab? tab = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var profile = await ConnectionProfile.TryLoadLocalAsync(path, timeout.Token);
        if (profile is not null) return profile;
        if (await AskAsync(owner, "ProfileReadSource", window =>
            {
                window.AddNotice("ProfileReadSourceNotice");
                window.AddField("ProfileListFilePath", new DialogText(PermissionDisplay.Literal(path)));
                if (macroName is not null) window.AddField("MacroSourceFile", new DialogText(PermissionDisplay.Literal(macroName)));
            }, "ProfileReadSource", null, token, tab) == ProfileAnswer.Cancel)
            throw new SshInteractionCancelledException();
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await ConnectionProfile.LoadAsync(path, timeout.Token);
    }

    private static void AddProfileDetails(MainWindow.FormWindow window, string path, ConnectionProfile profile,
        string? macro, bool changed)
    {
        string target = profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host} [{profile.Port}]";
        if (changed) window.AddNotice("ProfileTrustVersionChanged");
        window.AddField("ProfileListFilePath", new DialogText(PermissionDisplay.Literal(path)));
        window.AddField("ProfileListProtocol", new DialogText(profile.Type.ToUpperInvariant()), inline: true);
        window.AddField("ProfileListDestination", new DialogText(PermissionDisplay.Literal(target)));
        if (macro is not null) window.AddField("MacroSourceFile", new DialogText(PermissionDisplay.Literal(macro)));
        if (profile.Type == "ssh")
        {
            window.AddField("SshUsername", new DialogText(PermissionDisplay.Literal(profile.SshUsername)));
            var methods = profile.SshMethods;
            var names = methods.Select(method => Text(method == "publicKey" ? "PublicKey" : "VaultPassword"));
            if (profile.SshAllowsKeyboardInteractive) names = names.Append(Text("KeyboardInteractive"));
            window.AddField("ProfileAuthenticationGroup", new DialogText(string.Join(", ", names)), inline: true);
            if (profile.SshPassword is { } password)
                window.AddField("ProfilePasswordEntry", new DialogText(PermissionDisplay.Literal(password.Name)));
            if (profile.SshPrivateKey is { } key)
                window.AddField("ProfilePrivateKeyEntry", new DialogText(PermissionDisplay.Literal(key.Name)));
        }
        if (profile.RecordsOutput)
        {
            var formats = new[] { (profile.RecordRaw, "RecordRaw"), (profile.RecordText, "RecordText"), (profile.RecordTerminal, "RecordTerminal") };
            var selected = formats.Where(format => format.Item1).Select(format => Text(format.Item2));
            window.AddField("RecordedLogs", new DialogText(string.Join(", ", selected)), inline: true);
        }
        if (profile.HasPostConnectionMacro)
        {
            window.AddNotice("PostConnectionMacroDescription");
            window.AddField("PostConnectionMacro", new DialogText(PermissionDisplay.PastePage(profile.PostConnectionSource.AsSpan()), 220));
        }
    }

    private static async Task<ProfileAnswer> AskAsync(Window? owner, string title, Action<MainWindow.FormWindow> addDetails,
        string once, string? remember, CancellationToken token, TerminalTab? tab = null)
    {
        token.ThrowIfCancellationRequested();
        owner = tab?.DecisionHost ?? owner;
        if (owner is { IsLoaded: false }) owner = null;
        var answer = new TaskCompletionSource<ProfileAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow.FormWindow(owner, Text(title))
        {
            Width = 650,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        addDetails(window);
        window.AddDialogAction(once, () => Complete(ProfileAnswer.Once));
        if (remember is not null) window.AddDialogAction(remember, () => Complete(ProfileAnswer.Remember));
        var cancel = window.AddDialogAction("Cancel", () => Complete(ProfileAnswer.Cancel), isDefault: true);
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Complete(ProfileAnswer.Cancel);
        };
        window.Closed += (_, _) => answer.TrySetResult(ProfileAnswer.Cancel);
        if (tab is not null)
        {
            await tab.PresentDecisionAsync(window, token);
            return await answer.Task;
        }
        window.Show();
        cancel.Focus();
        using var cancellation = token.Register(() => window.Dispatcher.BeginInvoke(() =>
        {
            if (answer.TrySetCanceled(token)) window.Close();
        }));
        return await answer.Task;

        void Complete(ProfileAnswer choice)
        {
            if (answer.TrySetResult(choice)) window.Close();
        }
    }
}
