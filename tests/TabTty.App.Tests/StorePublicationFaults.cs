using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task CheckHighlightAndHostTrustPublicationAsync(App app, MainWindow owner,
        FilePublicationFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var closeDialogsOnTimeout = deadline.Token.Register(() => app.Dispatcher.BeginInvoke(() =>
        {
            foreach (var dialog in app.Windows.Cast<Window>().Where(window => window.Owner == owner).ToArray())
            {
                Console.Error.WriteLine($"Unexpected pending dialog: {dialog.Title}");
                foreach (var text in FindControls<TextBlock>(dialog)) Console.Error.WriteLine(text.Text);
                dialog.Close();
            }
        }));
        var highlights = app.Highlights.RecoveryStore();
        var hosts = app.HostTrust.RecoveryStore();
        Guid highlightId = Guid.NewGuid();
        byte[] priorHighlights = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, requiredFeatures = Array.Empty<string>(), keep = "preserved",
            sets = new[] { new { setId = highlightId, name = "Previous", enabled = true, rules = Array.Empty<object>() } },
        });
        File.WriteAllBytes(highlights.Path, priorHighlights);
        app.Highlights.Show(owner);
        Click(await VisibleButtonAsync(owner, "RemoveHighlightSet"));
        byte[] changedHighlights = [.. priorHighlights, (byte)'\n'];
        File.WriteAllBytes(highlights.Path, changedHighlights);
        Click(await VisibleButtonAsync(owner, "Save"));
        await DismissAsync("Error", "OK");
        if (!File.ReadAllBytes(highlights.Path).AsSpan().SequenceEqual(changedHighlights) ||
            app.Highlights.Find(highlightId)?.Name != "Previous" || File.Exists(highlights.Path + ".bak"))
            throw new InvalidOperationException("A stale highlight save changed the external file or the effective catalog.");
        File.WriteAllBytes(highlights.Path, priorHighlights);
        using (HoldArtifactCapacity())
        {
            var dismissal = DismissAsync("Error", "OK");
            Click(await VisibleButtonAsync(owner, "Save"));
            await dismissal;
            if (!File.ReadAllBytes(highlights.Path).AsSpan().SequenceEqual(priorHighlights) ||
                app.Highlights.Find(highlightId)?.Name != "Previous" || File.Exists(highlights.Path + ".bak"))
                throw new InvalidOperationException("A highlight save under capacity pressure created a backup or changed state.");
        }
        using (var held = fixture.PauseNextWrite())
        {
            Click(await VisibleButtonAsync(owner, "Save"));
            await held.Issued.Task.WaitAsync(deadline.Token);
            if (app.Highlights.Find(highlightId)?.Name != "Previous" ||
                !File.ReadAllBytes(held.Preservation!).AsSpan().SequenceEqual(priorHighlights))
                throw new InvalidOperationException("Pending highlight publication changed the effective selection or original.");
            byte[] candidate = File.ReadAllBytes(highlights.Path);
            File.Delete(held.Preservation!);
            held.Dispose();
            await DismissAsync("Error", "OK");
            if (app.Highlights.Find(highlightId)?.Name != "Previous" || !VerifiedFilePublication.IsUnresolved(highlights.Path))
                throw new InvalidOperationException("Unverified highlight publication replaced the effective catalog.");
            File.WriteAllBytes(held.Preservation!, priorHighlights);
            await RecheckAsync(highlights);
            using var json = JsonDocument.Parse(File.ReadAllBytes(highlights.Path));
            if (app.Highlights.Find(highlightId) is not null ||
                !File.ReadAllBytes(highlights.Path).AsSpan().SequenceEqual(candidate) ||
                json.RootElement.GetProperty("keep").GetString() != "preserved")
                throw new InvalidOperationException("Highlight reinspection did not adopt the validated stored revision.");
        }
        Console.WriteLine("PASS HighlightPublicationRetainsEffectiveCatalogUntilReinspection");

        byte[] priorHosts = hosts.Defaults();
        File.WriteAllBytes(hosts.Path, priorHosts);
        const string algorithm = "ssh-ed25519";
        using var keyWriter = new SshWriter();
        keyWriter.Text(algorithm);
        keyWriter.String(Convert.FromHexString("D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A"));
        byte[] blob = keyWriter.Span.ToArray();
        var host = new SshHostKey(SshDestination.Parse("127.0.0.1", 2222), algorithm, blob,
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='));
        byte[] changedHosts = [.. priorHosts, (byte)'\n'];
        Task<bool> staleApproval = StartApproval(host, deadline.Token);
        await DismissAsync("NewHostKey", "TrustHostKey", () => File.WriteAllBytes(hosts.Path, changedHosts));
        try
        {
            await staleApproval;
            throw new InvalidOperationException("A host-key approval overwrote an external change.");
        }
        catch (FilePublicationException error) when (error.Result.Status == FilePublicationStatus.Conflict) { }
        if (!File.ReadAllBytes(hosts.Path).AsSpan().SequenceEqual(changedHosts) || File.Exists(hosts.Path + ".bak"))
            throw new InvalidOperationException("Stale host-key approval changed the external file.");
        File.WriteAllBytes(hosts.Path, priorHosts);
        using (HoldArtifactCapacity())
        {
            Task<bool> refused = StartApproval(host, deadline.Token);
            await DismissAsync("NewHostKey", "TrustHostKey");
            try
            {
                await refused;
                throw new InvalidOperationException("Host approval bypassed publication capacity.");
            }
            catch (ResourceLimitException) { }
            if (!File.ReadAllBytes(hosts.Path).AsSpan().SequenceEqual(priorHosts) || File.Exists(hosts.Path + ".bak"))
                throw new InvalidOperationException("A capacity refusal changed the host keys.");
        }
        using (var held = fixture.PauseNextWrite())
        {
            Task<bool> approving = StartApproval(host, deadline.Token);
            await DismissAsync("NewHostKey", "TrustHostKey");
            await held.Issued.Task.WaitAsync(deadline.Token);
            if (approving.IsCompleted || !VerifiedFilePublication.IsPublicationActive(hosts.Path) ||
                !File.ReadAllBytes(held.Preservation!).AsSpan().SequenceEqual(priorHosts))
                throw new InvalidOperationException("Host authentication passed before preservation was verified.");
            byte[] candidate = File.ReadAllBytes(hosts.Path);
            File.Delete(held.Preservation!);
            held.Dispose();
            try
            {
                await approving;
                throw new InvalidOperationException("Host approval was accepted without original preservation.");
            }
            catch (FilePublicationException error) when (error.Result.Status == FilePublicationStatus.UnverifiedPreservation) { }
            try
            {
                app.HostTrust.ApprovedFormats(host.Destination);
                throw new InvalidOperationException("The incomplete trust record was offered to a new login.");
            }
            catch (IOException error) when (error.Message == App.Text("StoreSaveUnverified")) { }

            File.WriteAllBytes(held.Preservation!, priorHosts);
            await RecheckAsync(hosts);
            if (!app.HostTrust.ApprovedFormats(host.Destination).SequenceEqual([algorithm]) ||
                !await app.HostTrust.ApproveAsync(owner, host, deadline.Token) ||
                !File.ReadAllBytes(hosts.Path).AsSpan().SequenceEqual(candidate))
                throw new InvalidOperationException("Host reinspection changed the file or rejected the exact approved key.");
        }
        Console.WriteLine("PASS HostTrustPublicationRejectsUnverifiedApproval");

        using (var held = fixture.PauseNextWrite())
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            var second = host with { Destination = SshDestination.Parse("127.0.0.1", 2223) };
            byte[] before = File.ReadAllBytes(hosts.Path);
            Task<bool> approving = StartApproval(second, stop.Token);
            await DismissAsync("NewHostKey", "TrustHostKey");
            await held.Issued.Task.WaitAsync(deadline.Token);
            stop.Cancel();
            if (approving.IsCompleted || !VerifiedFilePublication.IsPublicationActive(hosts.Path))
                throw new InvalidOperationException("Cancelling login released issued host-key publication.");
            held.Dispose();
            try
            {
                await approving;
                throw new InvalidOperationException("Cancelled login accepted its host-key approval result.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            if (!app.HostTrust.ApprovedFormats(second.Destination).SequenceEqual([algorithm]) ||
                !File.ReadAllBytes(held.Preservation!).AsSpan().SequenceEqual(before))
                throw new InvalidOperationException("Cancellation discarded a verified host-key commit.");
        }
        Console.WriteLine("PASS StorePublicationExternalChangesCapacityAndCancellation");

        const string additionalFormat = "ecdsa-sha2-nistp256";
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = ecdsa.ExportParameters(false).Q;
        using var additionalWriter = new SshWriter();
        additionalWriter.Text(additionalFormat);
        additionalWriter.Text("nistp256");
        additionalWriter.String([4, .. point.X!, .. point.Y!]);
        byte[] additionalBlob = additionalWriter.Span.ToArray();
        var additional = new SshHostKey(host.Destination, additionalFormat, additionalBlob,
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(additionalBlob)).TrimEnd('='));
        Task<bool> declined = StartApproval(additional, deadline.Token);
        Window? keyDialog = null;
        await WaitForAsync(() => (keyDialog = app.Windows.Cast<Window>().SingleOrDefault(window =>
            window.IsVisible && window.Owner == owner && window.Title == App.Text("AdditionalHostKey"))) is not null);
        var shown = FindControls<TextBox>(keyDialog!).Select(control => control.Text).ToArray();
        if (!shown.Any(text => text.Contains(algorithm) && text.Contains(host.Fingerprint)) ||
            !shown.Contains(additional.Fingerprint) || !(await VisibleButtonAsync(keyDialog!, "Cancel")).IsDefault)
            throw new InvalidOperationException("Additional host-key approval omitted trust history or default cancellation.");
        keyDialog!.Close();
        if (await declined || !app.HostTrust.ApprovedFormats(host.Destination).SequenceEqual([algorithm]))
            throw new InvalidOperationException("Declining an additional host key changed trust.");
        Task<bool> added = StartApproval(additional, deadline.Token);
        await DismissAsync("AdditionalHostKey", "AddHostKey");
        if (!await added || !app.HostTrust.ApprovedFormats(host.Destination).SequenceEqual([algorithm, additionalFormat]))
            throw new InvalidOperationException("Additional host-key approval failed to preserve both keys.");
        Console.WriteLine("PASS AdditionalSshKeyRequiresInformedApproval");

        Task<bool> StartApproval(SshHostKey presented, CancellationToken token) => app.Dispatcher
            .InvokeAsync(() => app.HostTrust.ApproveAsync(owner, presented, token)).Task.Unwrap();

        IDisposable HoldArtifactCapacity() => VerifiedFilePublication.ReserveNamespace(
            [Path.Combine(Path.GetDirectoryName(highlights.Path)!, "capacity-reservation")], 256L * 1024 * 1024);

        async Task RecheckAsync(JsonStoreRecovery.Store store)
        {
            app.ShowStoreRecovery(owner);
            var recheck = await VisibleButtonAsync(owner, "RecheckFilePublication");
            var selection = FindControls<ComboBox>(owner).Single(combo => combo.IsVisible &&
                combo.Items.OfType<JsonStoreRecovery.Store>().Any());
            selection.SelectedItem = selection.Items.OfType<JsonStoreRecovery.Store>().Single(item => item.Path == store.Path);
            Click(recheck);
            await WaitForAsync(() => !VerifiedFilePublication.IsUnresolved(store.Path));
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
        }

        async Task DismissAsync(string title, string button, Action? beforeClick = null)
        {
            Window? dialog = null;
            await WaitForAsync(() => (dialog = app.Windows.Cast<Window>().SingleOrDefault(window =>
                window.IsVisible && window.Owner == owner && window.Title == App.Text(title))) is not null);
            beforeClick?.Invoke();
            Click(await VisibleButtonAsync(dialog!, button));
            await WaitForAsync(() => !dialog!.IsVisible);
        }

        async Task<Button> VisibleButtonAsync(DependencyObject parent, string key)
        {
            Button? button = null;
            await WaitForAsync(() => (button = FindControls<Button>(parent).SingleOrDefault(item =>
                item.IsVisible && Equals(item.Content, App.Text(key)))) is not null);
            return button!;
        }

        async Task WaitForAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(15, deadline.Token);
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
