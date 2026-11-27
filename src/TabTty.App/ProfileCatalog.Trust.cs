using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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

                string details = Describe(location, profile, macroName,
                    before.Version is not null || previousOnce is not null);
                if (unregistered && Path.GetDirectoryName(location) != ActiveFolder)
                    details += "\n\n" + Text("ProfileImportLogDirectory") + "\n" + PermissionDisplay.Literal(folder);
                var choice = await AskAsync(owner, "ProfileTrustReview", details,
                    "ProfileConnectOnce", unregistered ? "ProfileImportAndConnect" : "ProfileTrustAndConnect", token, tab);
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
            Describe(path, profile, null, false), "PostConnectionMacroTrustProfile", null, token) == ProfileAnswer.Once;

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
            if (previous is not null && await AskAsync(owner, "LogDestinationReview",
                PermissionDisplay.Literal(destination.Path) + "\n" + PermissionDisplay.Literal(destination.EffectivePath) +
                "\n\n" + Text("LogDestinationReviewNotice"), "ApproveLogDestination", null, token, tab) == ProfileAnswer.Cancel)
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
        var details = PermissionDisplay.Literal(path) + "\n\n" + Text("ProfileReadSourceNotice");
        if (macroName is not null) details += "\n" + Text("MacroSourceFile") + "  " + PermissionDisplay.Literal(macroName);
        if (await AskAsync(owner, "ProfileReadSource", details, "ProfileReadSource", null, token, tab) == ProfileAnswer.Cancel)
            throw new SshInteractionCancelledException();
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await ConnectionProfile.LoadAsync(path, timeout.Token);
    }

    private static string Describe(string path, ConnectionProfile profile, string? macro, bool changed)
    {
        var lines = new List<string> { PermissionDisplay.Literal(path) };
        string target = profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host} [{profile.Port}]";
        lines.Add($"{profile.Type.ToUpperInvariant()}  {PermissionDisplay.Literal(target)}");
        if (changed) lines.Add(Text("ProfileTrustVersionChanged"));
        if (macro is not null) lines.Add(Text("MacroSourceFile") + "  " + PermissionDisplay.Literal(macro));
        if (profile.Type == "ssh")
        {
            lines.Add(PermissionDisplay.Literal(profile.SshUsername));
            var methods = profile.SshMethods;
            var names = methods.Select(method => Text(method == "publicKey" ? "PublicKey" : "VaultPassword"));
            if (profile.SshAllowsKeyboardInteractive) names = names.Append(Text("KeyboardInteractive"));
            lines.Add(string.Join(", ", names));
            foreach (var reference in new[] { profile.SshPassword, profile.SshPrivateKey })
                if (reference is not null) lines.Add(PermissionDisplay.Literal(reference.ToString()));
        }
        if (profile.RecordsOutput)
        {
            var formats = new[] { (profile.RecordRaw, "Raw"), (profile.RecordText, "Text"), (profile.RecordTerminal, "Terminal") };
            lines.Add(string.Join(", ", formats.Where(format => format.Item1).Select(format => format.Item2)));
        }
        if (profile.HasPostConnectionMacro)
        {
            lines.Add(Text("PostConnectionMacroDescription"));
            lines.Add(PermissionDisplay.PastePage(profile.PostConnectionSource.AsSpan()));
        }
        return string.Join("\n", lines);
    }

    private static async Task<ProfileAnswer> AskAsync(Window? owner, string title, string details,
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
        window.AddDisplay(title, new DialogText(details,
            title == "ProfileReadSource" ? null : Math.Max(120, SystemParameters.WorkArea.Height / 2)));
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
