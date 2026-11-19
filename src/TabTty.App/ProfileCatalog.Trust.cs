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
    private long trustReset;
    private readonly Dictionary<string, long> removals = new(StringComparer.Ordinal);

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

    private static string VersionFor(string location, ConnectionProfile profile, RecordingDestination? destination)
    {
        string version = profile.TrustVersion;
        if (!profile.RecordsOutput) return version;
        destination ??= RecordingDestination.Capture(location, profile);
        return version[..2] + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            version + "\0" + destination.EffectivePath + "\0" + destination.DriveTarget)));
    }

    private void CommitRegistration(string location, string path, ConnectionProfile profile, bool used, string version)
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
                    ["registrationId"] = Guid.NewGuid(), ["path"] = location, ["displayPath"] = path,
                    ["favorite"] = false, ["lastUsed"] = null,
                };
                registrations.Add(registration);
                StoreVersion(state, location, version);
            }
            if (used) registration["lastUsed"] = DateTimeOffset.UtcNow;
        });
        names[location] = profile.DisplayName;
        ProfileSaved?.Invoke();
    }

    private async Task RegisterSavedAsync(string path, ConnectionProfile profile, long reset)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string location = await NormalizeLocationAsync(path, timeout.Token);
        if (reset != trustReset) throw new IOException(Text("ProfileTrustWriteFailed"));
        CommitRegistration(location, path, profile, used: false, VersionFor(location, profile, null));
    }

    private void AdvanceEditorTrust(string location, TrustState starting, string previous, ConnectionProfile saved)
    {
        if (starting.Version != previous || ReadTrust(location) != starting) return;
        string version = VersionFor(location, saved, null);
        LocalState.Change(state => StoreVersion(state, location, version));
    }

    internal void ResetTrust(Window owner)
    {
        if (app.Modal(() => ThemedMessageBox.Show(owner, Text("ProfileTrustResetConfirm"), Text("ProfileTrustReset"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) != MessageBoxResult.Yes) return;
        try
        {
            trustReset++;
            removals.Clear();
            LocalState.Change(state => state.Remove(TrustKey));
        }
        catch (Exception error) when (IsFileProblem(error))
        { MainWindow.ShowError(owner, new IOException(Text("ProfileTrustWriteFailed"), error)); }
    }

    internal async Task<(ConnectionProfile Profile, RecordingDestination? Destination)> AdmitAsync(
        Window? owner, string path, string? macroName, CancellationToken token)
    {
        ConnectionProfile profile = await LoadForUseAsync(owner, path, token);
        string location;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            location = await NormalizeLocationAsync(path, timeout.Token);
        }

        var destination = profile.RecordsOutput ? RecordingDestination.Capture(location, profile) : null;
        string version = VersionFor(location, profile, destination);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var before = ReadTrust(location);
            bool register = macroName is null && before.Registration is null;
            if (register || before.Version != version)
            {
                var choice = await AskAsync(owner, "ProfileTrustReview",
                    Describe(location, profile, destination, macroName, before.Version is not null),
                    "ProfileConnectOnce", register ? "ProfileRegisterAndConnect" : "ProfileTrustAndConnect", token);
                if (choice == ProfileAnswer.Cancel) throw new SshInteractionCancelledException();
                token.ThrowIfCancellationRequested();
                if (ReadTrust(location) != before) continue;
                if (choice == ProfileAnswer.Remember)
                {
                    if (register) CommitRegistration(location, path, profile, used: true, version);
                    else LocalState.Change(state => StoreVersion(state, location, version));
                }
            }
            return (profile, destination);
        }
    }

    private async Task<ConnectionProfile> LoadForUseAsync(Window? owner, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var profile = await ConnectionProfile.TryLoadLocalAsync(path, timeout.Token);
        if (profile is not null) return profile;
        if (await AskAsync(owner, "ProfileReadSource", Text("ProfileReadSourceNotice") + "\n\n" +
            PermissionDisplay.Literal(path), "ProfileReadSource", null, token) == ProfileAnswer.Cancel)
            throw new SshInteractionCancelledException();
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await ConnectionProfile.LoadAsync(path, timeout.Token);
    }

    private static string Describe(string path, ConnectionProfile profile, RecordingDestination? destination,
        string? macro, bool changed)
    {
        var lines = new List<string> { PermissionDisplay.Literal(path) };
        if (changed) lines.Add(Text("ProfileTrustVersionChanged"));
        if (macro is not null) lines.Add(Text("MacroSourceFile") + "  " + PermissionDisplay.Literal(macro));
        string target = profile.Type == "serial" ? profile.Serial.PortName : $"{profile.Host} [{profile.Port}]";
        lines.Add($"{profile.Type.ToUpperInvariant()}  {PermissionDisplay.Literal(target)}");
        if (profile.Type == "ssh")
        {
            lines.Add(PermissionDisplay.Literal(profile.SshUsername));
            var methods = profile.SshMethods;
            var names = methods.Select(method => Text(method == "publicKey" ? "PublicKey" : "VaultPassword"));
            if (profile.SshAllowsKeyboardInteractive) names = names.Append(Text("KeyboardInteractive"));
            lines.Add(string.Join(", ", names));
            foreach (var reference in new[] { methods.Contains("password") ? profile.SshPassword : null,
                methods.Contains("publicKey") ? profile.SshPrivateKey : null })
                if (reference is not null) lines.Add(PermissionDisplay.Literal(reference.ToString()));
        }
        if (destination is not null)
        {
            var formats = new[] { (profile.RecordRaw, "Raw"), (profile.RecordText, "UTF-8"), (profile.RecordTerminal, "JSONL") };
            lines.Add(string.Join(", ", formats.Where(format => format.Item1).Select(format => format.Item2)) +
                "  " + PermissionDisplay.Literal(destination.EffectivePath));
            if (destination.EffectivePath.StartsWith(@"\\", StringComparison.Ordinal))
                lines.Add(Text("ProfileLogNetworkShare"));
        }
        return string.Join("\n", lines);
    }

    private static async Task<ProfileAnswer> AskAsync(Window? owner, string title, string details,
        string once, string? remember, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (owner is { IsLoaded: false }) owner = null;
        var answer = new TaskCompletionSource<ProfileAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow.FormWindow(owner, Text(title))
        {
            Width = 650,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        window.AddField(title, new TextBox
        {
            Text = details, IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
            MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height / 2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        window.AddAction(once, () => Complete(ProfileAnswer.Once));
        if (remember is not null) window.AddAction(remember, () => Complete(ProfileAnswer.Remember));
        var cancel = window.AddAction("Cancel", () => Complete(ProfileAnswer.Cancel));
        cancel.IsDefault = true;
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Complete(ProfileAnswer.Cancel);
        };
        window.Closed += (_, _) => answer.TrySetResult(ProfileAnswer.Cancel);
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
