using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private sealed class VaultFileFaultFixture
    {
        internal readonly TaskCompletionSource<string> Installing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseInstallation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseHandoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Archived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseArchive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly FilePublicationFixture Restore = new();
        internal string? RestoreSource;
        internal bool HoldHandoff;

        internal VaultFileHooks Hooks => new((step, source, target) =>
        {
            if (step == VaultInitializationStep.InstallCandidate && Installing.TrySetResult(source))
                ReleaseInstallation.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            File.Move(source, target, overwrite: false);
        }, () =>
        {
            if (!HoldHandoff) return Task.CompletedTask;
            HoldHandoff = false;
            Handoff.SetResult();
            return ReleaseHandoff.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }, _ => RestoreSource, Restore.Issue, (source, destination) =>
        {
            File.Move(source, destination, overwrite: false);
            Archived.SetResult(destination);
            ReleaseArchive.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new IOException("Controlled error after the archive move.");
        });
    }

    private static async Task CheckVaultFilesAsync(App app, string directory, VaultFileFaultFixture fixture)
    {
        const string master = "fixture password for vault failures";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(38));
        var token = deadline.Token;
        string empty = Path.Combine(directory, "empty.tttykey");
        string first = Path.Combine(directory, "first.tttykey");
        string backup = Path.Combine(directory, "backup.tttykey");
        File.WriteAllBytes(empty, []);
        var originalEmpty = CredentialVault.InspectFile(empty)!;
        CredentialReference reference;
        using (var owned = await CredentialVault.CreateAsync(first, master.AsMemory(), token))
        {
            Guid entry = await owned.AddPasswordAsync("prior metadata", null, "prior fixture secret"u8.ToArray(), token);
            reference = new(owned.Id, entry, "password");
            await owned.BackupAsync(backup, token);
            await owned.UpdateEntryAsync(entry, "later metadata", null, "later fixture secret"u8.ToArray(), token);
        }
        fixture.RestoreSource = backup;
        var host = app.CreateWindow(restorePlacement: false);
        host.Show();
        try
        {
            await SelectAsync(empty);
            await app.Vault.ManageAsync(host);
            await UntilAsync(() => CurrentButton("CreateVault") is not null);
            using (VaultController.ManagementDeliveries.Reserve(VaultController.ManagementDeliveryLimit))
            {
                Start("CreateVault");
                var busy = await FindDialogAsync("Vault", form: false);
                EnsureMessage(busy, App.Text("VaultBusy"));
                Click(busy, "OK");
                await UntilIdleAsync();
                if (fixture.Installing.Task.IsCompleted || CredentialVault.InspectFile(empty) != originalEmpty)
                    throw new InvalidOperationException("Notification pressure admitted vault initialization.");
            }
            Console.WriteLine("PASS VaultManagementDeliveryAdmission");

            Start("CreateVault");
            (await FindDialogAsync("InitializeVault", form: true)).Close();
            await UntilIdleAsync();
            if (fixture.Installing.Task.IsCompleted || CredentialVault.InspectFile(empty) != originalEmpty)
                throw new InvalidOperationException("Dismissed initialization input changed the empty file.");

            Start("CreateVault");
            await AcceptPasswordAsync("InitializeVault", "Save", "InitializeEmptyVaultDescription");
            string stage = await fixture.Installing.Task.WaitAsync(token);
            string parked = Directory.GetFiles(directory, ".tabtty-empty-*.tmp").Single();
            host.Close();
            if (!host.IsVisible || !app.Vault.HasActiveOperations || File.Exists(empty) ||
                CredentialVault.InspectFile(parked) != originalEmpty)
                throw new InvalidOperationException("The visible initialization did not retain its original owner and parked file.");
            try
            {
                await VerifiedFilePublication.Begin(new(empty, "competing writer"u8.ToArray(), null, null, 1024)).ObserveAsync(token);
                throw new InvalidOperationException("Initialization did not retain its shared namespace.");
            }
            catch (IOException) when (!File.Exists(empty)) { }
            byte[] collision = "separately created target"u8.ToArray();
            File.WriteAllBytes(empty, collision);
            fixture.ReleaseInstallation.SetResult();
            var problem = await FindDialogAsync("Vault", form: false);
            EnsureMessage(problem, parked);
            EnsureMessage(problem, stage);
            EnsureMessage(problem, App.Text("PublishedImageOther"));
            Click(problem, "OK");
            await UntilIdleAsync();
            if (!File.ReadAllBytes(empty).AsSpan().SequenceEqual(collision) || !VerifiedFilePublication.IsUnresolved(empty))
                throw new InvalidOperationException("Initialization overwrote the competing file or lost its restriction.");
            File.Move(empty, Path.Combine(directory, "external-file.tttykey"));
            File.Move(stage, empty);
            Start("RecheckFilePublication");
            await AcceptPasswordAsync("RecheckFilePublication", "UnlockVault");
            await UntilIdleAsync();
            if (VerifiedFilePublication.IsUnresolved(empty) || CredentialVault.InspectFile(parked) != originalEmpty)
                throw new InvalidOperationException("Initialization reinspection changed the original or failed to adopt.");
            Console.WriteLine("PASS VaultInitializationCollisionAndReinspection");

            await SelectAsync(first);
            Task manage = app.Vault.ManageAsync(host);
            await AcceptPasswordAsync("UnlockVault", "UnlockVault");
            await manage.WaitAsync(token);
            await UntilAsync(() => CurrentButton("RestoreVault") is not null);
            byte[] prior = File.ReadAllBytes(first);
            Start("RestoreVault");
            Click(await FindDialogAsync("RestoreVault", form: false), "Yes");
            (await FindDialogAsync("RestoreVault", form: true)).Close();
            await UntilIdleAsync();
            if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(prior) ||
                Directory.GetFiles(directory, "*.recovery-*.tttykey").Length != 0)
                throw new InvalidOperationException("Dismissed restore input changed the file or created recovery artifacts.");

            using (var hold = fixture.Restore.PauseNextWrite())
            {
                Start("RestoreVault");
                Click(await FindDialogAsync("RestoreVault", form: false), "Yes");
                await AcceptPasswordAsync("RestoreVault", "UnlockVault");
                await hold.Issued.Task.WaitAsync(token);
                string preservation = hold.Preservation!;
                if (!File.ReadAllBytes(preservation).AsSpan().SequenceEqual(prior) ||
                    !FindControls<ListBox>(host).Any(list => list.Items.OfType<VaultEntryInfo>()
                        .Any(entry => entry.Name == "later metadata")))
                    throw new InvalidOperationException("Restore exposed metadata before verifying preservation.");
                File.Delete(preservation);
                hold.Dispose();
                problem = await FindDialogAsync("Vault", form: false);
                EnsureMessage(problem, preservation);
                EnsureMessage(problem, App.Text("PublishedImageMissing"));
                Click(problem, "OK");
                await UntilIdleAsync();
                Start("RecheckFilePublication");
                problem = await FindDialogAsync("Vault", form: false);
                EnsureMessage(problem, preservation);
                EnsureMessage(problem, App.Text("PublishedImageMissing"));
                Click(problem, "OK");
                await UntilIdleAsync();
                File.WriteAllBytes(preservation, prior);
                Start("RecheckFilePublication");
                await UntilIdleAsync();
                await UntilAsync(() => FindControls<ListBox>(host).Any(list => list.Items.OfType<VaultEntryInfo>()
                    .Any(entry => entry.Name == "prior metadata")));
            }
            Console.WriteLine("PASS VaultRestorePublishesMetadataAfterReinspection");

            fixture.HoldHandoff = true;
            Task<VaultSecret?> old = app.Vault.ReadSavedPasswordAsync(host, reference, token);
            await fixture.Handoff.Task.WaitAsync(token);
            long epoch = app.Vault.SelectionEpoch;
            await SelectAsync(empty);
            await SelectAsync(first);
            if (old.IsCompleted || app.Vault.SelectionEpoch != epoch + 2 || !app.Vault.HasActiveOperations)
                throw new InvalidOperationException("Changing the selected role retired the earlier credential read prematurely.");
            using (var current = await app.Vault.ReadSavedPasswordAsync(host, reference, token))
                if (current is null || !current.Bytes.Span.SequenceEqual("prior fixture secret"u8))
                    throw new InvalidOperationException("The replacement role could not read its current credential.");
            fixture.ReleaseHandoff.SetResult();
            using var discarded = await old.WaitAsync(token);
            if (discarded is not null || app.Vault.HasActiveOperations)
                throw new InvalidOperationException("An old credential survived a selection switch-back.");
            Console.WriteLine("PASS VaultLateCredentialAfterSelectionSwitchBack");

            byte[] beforeArchive = File.ReadAllBytes(first);
            byte[] priorBackup = File.ReadAllBytes(backup);
            if (!app.Activate(InstanceLaunch.Parse(["--edit", first])))
                throw new InvalidOperationException("The archive editor was not admitted.");
            VaultEditorWindow? archiveEditor = null;
            await UntilAsync(() => (archiveEditor = app.Windows.OfType<VaultEditorWindow>()
                .SingleOrDefault(window => window.IsVisible)) is not null);
            await UntilAsync(() => FindControls<Button>(archiveEditor!).Any(button =>
                button.IsEnabled && Equals(button.Content, App.Text("ArchiveVault"))));
            _ = app.Dispatcher.BeginInvoke(() => Click(archiveEditor!, "ArchiveVault"));
            Click(await FindDialogAsync("ArchiveVault", form: false), "Yes");
            string archive = await fixture.Archived.Task.WaitAsync(token);
            archiveEditor!.Close();
            if (!archiveEditor.IsVisible || !app.Vault.HasActiveOperations || File.Exists(first) ||
                !File.ReadAllBytes(archive).AsSpan().SequenceEqual(beforeArchive))
                throw new InvalidOperationException("Archive closure released its owner before the issued move returned.");
            try
            {
                await VerifiedFilePublication.Begin(new(first, "competing initialization"u8.ToArray(), null, null, 1024))
                    .ObserveAsync(token);
                throw new InvalidOperationException("Initialization bypassed the archive namespace.");
            }
            catch (IOException) when (!File.Exists(first)) { }
            fixture.ReleaseArchive.SetResult();
            var acknowledged = await FindDialogAsync("ArchiveVault", form: false);
            EnsureMessage(acknowledged, archive);
            EnsureMessage(acknowledged, App.Text("VaultArchived"));
            if (!archiveEditor.IsVisible) throw new InvalidOperationException("The archive editor closed before acknowledgement.");
            Click(acknowledged, "OK");
            await UntilAsync(() => !archiveEditor.IsVisible && !app.Vault.HasActiveOperations);
            if (File.Exists(first) || app.VaultPath != first || !File.ReadAllBytes(backup).AsSpan().SequenceEqual(priorBackup))
                throw new InvalidOperationException("Archive changed the selected path, created a replacement or moved its backup.");
            Console.WriteLine("PASS VaultArchiveOwnsIssuedMoveUntilTheEditorAcknowledges");
        }
        finally
        {
            fixture.ReleaseInstallation.TrySetResult();
            fixture.ReleaseHandoff.TrySetResult();
            fixture.ReleaseArchive.TrySetResult();
            foreach (Window dialog in app.Windows.Cast<Window>().Where(window =>
                window.Owner == host || window is MainWindow.FormWindow).ToArray())
                if (dialog.IsVisible) dialog.Close();
            if (host.IsVisible) host.Close();
        }

        Task SelectAsync(string path) => app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice,
            app.TerminalFontFamily, app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, path, app.SettingsRevision);

        Button? CurrentButton(string key) => FindControls<Button>(host).SingleOrDefault(button =>
            button.IsVisible && Equals(button.Content, App.Text(key)));

        void Start(string key) => _ = app.Dispatcher.BeginInvoke(() =>
            CurrentButton(key)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        async Task UntilAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(15, token);
        }

        Task UntilIdleAsync() => UntilAsync(() => !app.Vault.HasActiveOperations);

        async Task<Window> FindDialogAsync(string key, bool form)
        {
            Window? match = null;
            await UntilAsync(() => (match = app.Windows.Cast<Window>().SingleOrDefault(window =>
                window != host && window.IsVisible && window.Title == App.Text(key) &&
                (window is MainWindow.FormWindow) == form)) is not null);
            return match!;
        }

        async Task AcceptPasswordAsync(string title, string button, string? message = null)
        {
            Window dialog = await FindDialogAsync(title, form: true);
            if (message is not null) EnsureMessage(dialog, App.Text(message));
            foreach (var box in FindControls<PasswordBox>(dialog)) box.Password = master;
            Click(dialog, button);
        }

        static void EnsureMessage(Window dialog, string expected)
        {
            if (!FindControls<TextBlock>(dialog).Any(text => text.Text.Contains(expected, StringComparison.Ordinal)))
                throw new InvalidOperationException("The vault dialog omitted an expected publication fact.");
        }

        static void Click(Window dialog, string key) => FindControls<Button>(dialog)
            .Single(button => Equals(button.Content, App.Text(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
