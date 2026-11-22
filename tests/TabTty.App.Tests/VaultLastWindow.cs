using System.IO;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void CheckLastWindowDuringMasterPublication(App app, string directory, FilePublicationFixture fixture,
        bool standaloneEditor, bool missingBackup)
    {
        const string oldMaster = "old-master-for-close-fixture";
        const string newMaster = "new-master-for-close-fixture";
        byte[] expectedSecret = "fixture credential"u8.ToArray();
        string path = Path.Combine(directory, "last-window.tttykey");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;

        CredentialReference reference = Task.Run(async () =>
        {
            using var created = await CredentialVault.CreateAsync(path, oldMaster.AsMemory(), token);
            return new CredentialReference(await created.AddPasswordAsync("close test", expectedSecret, token),
                "password");
        }, token).GetAwaiter().GetResult();
        byte[] original = File.ReadAllBytes(path);
        byte[]? replacement = null;
        Task<VaultSecret?>? oldRead = null;
        long selectionEpoch = -1;
        bool exitObserved = false;
        bool unverifiedReported = false;
        Exception? failure = null;

        app.Exit += (_, _) =>
        {
            exitObserved = true;
            try
            {
                if (replacement is null || app.Vault.HasActiveOperations || app.Vault.SelectionEpoch != selectionEpoch ||
                    oldRead is not { IsCompleted: true } || missingBackup && !unverifiedReported)
                    throw new InvalidOperationException("Application exit overtook vault publication or the old credential consumer.");
                try
                {
                    using var secret = oldRead.GetAwaiter().GetResult();
                    if (secret is not null)
                        throw new InvalidOperationException("A credential was handed to a closed window after master publication.");
                }
                catch (IOException) when (missingBackup) { }
            }
            catch (Exception error) { failure ??= error; }
        };

        app.Startup += async (_, _) =>
        {
            try
            {
                await app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice, app.TerminalFontFamily,
                    app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, path, app.SettingsRevision);
                selectionEpoch = app.Vault.SelectionEpoch;
                Window host;
                if (standaloneEditor)
                {
                    app.OpenInitialWindows(InstanceLaunch.Parse([path]));
                    await AcceptPasswordAsync("UnlockVault", "UnlockVault", oldMaster);
                    await UntilAsync(() => app.Windows.OfType<VaultEditorWindow>()
                        .SingleOrDefault()?.Content is FormView { IsEnabled: true });
                    host = app.Windows.OfType<VaultEditorWindow>().Single();
                }
                else
                {
                    app.OpenInitialWindows([]);
                    await UntilAsync(() => app.MainWindow is MainWindow { CanAcceptTabs: true });
                    var main = (MainWindow)app.MainWindow;
                    Task manage = app.Vault.ManageAsync(main);
                    await AcceptPasswordAsync("UnlockVault", "UnlockVault", oldMaster);
                    await manage.WaitAsync(token);
                    host = main;
                }

                using var held = fixture.PauseNextWrite();
                var change = FindControls<Button>(host).Single(button => Equals(button.Content, App.Text("ChangeMaster")));
                _ = app.Dispatcher.BeginInvoke(() => change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await AcceptPasswordAsync("ChangeMaster", "Save", newMaster);
                await held.Issued.Task.WaitAsync(token);
                byte[] candidate = File.ReadAllBytes(path);
                if (candidate.AsSpan().SequenceEqual(original) ||
                    !original.AsSpan().SequenceEqual(File.ReadAllBytes(path + ".bak")))
                    throw new InvalidOperationException("Issued replacement did not install the candidate and preserve the original.");
                await UntilAsync(() => FindControls<TextBlock>(host).Any(text =>
                    text.IsVisible && text.Text == App.Text("SavingChanges")));

                oldRead = app.Vault.ReadSavedPasswordAsync(host, reference, token);
                host.Close();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (host.IsVisible || app.Windows.Cast<Window>().Any(window => window.IsVisible) ||
                    exitObserved || !app.Vault.HasActiveOperations || oldRead.IsCompleted || app.Activate([]) ||
                    app.Vault.SelectionEpoch != selectionEpoch)
                    throw new InvalidOperationException("Last-window close did not detach from the issued vault operation.");
                replacement = candidate;
                if (missingBackup)
                {
                    File.Delete(path + ".bak");
                    held.Dispose();
                    Window? report = null;
                    await UntilAsync(() => (report = app.Windows.Cast<Window>()
                        .SingleOrDefault(window => window.IsVisible && window.Title == App.Text("Vault"))) is not null);
                    if (exitObserved || !app.Vault.HasActiveOperations || !FindControls<TextBlock>(report!).Any(text =>
                        text.Text.Contains(App.Text("StoreSaveUnverified"), StringComparison.Ordinal) &&
                        text.Text.Contains(App.Text("PublishedImageMissing"), StringComparison.Ordinal) &&
                        text.Text.Contains(path + ".bak", StringComparison.Ordinal)))
                        throw new InvalidOperationException("The master change lost its unverified preservation status after view closure.");
                    unverifiedReported = true;
                    FindControls<Button>(report!).Single(button => Equals(button.Content, App.Text("OK")))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception error)
            {
                failure = error;
                // The held publication is released by its using scope before shutdown.
                if (!app.Vault.HasActiveOperations) app.Shutdown(1);
            }
        };
        using var timeout = token.Register(() => app.Dispatcher.BeginInvoke(() =>
        {
            failure ??= new TimeoutException("The last-window fixture did not reach quiescent application exit.");
            foreach (MainWindow.FormWindow prompt in app.Windows.OfType<MainWindow.FormWindow>().ToArray())
                prompt.Close();
            app.Shutdown(1);
        }));
        app.Run();
        if (failure is not null) throw failure;
        bool preserved = File.Exists(path + ".bak");
        if (!exitObserved || replacement is null || !replacement.AsSpan().SequenceEqual(File.ReadAllBytes(path)) ||
            preserved == missingBackup || (preserved && !original.AsSpan().SequenceEqual(File.ReadAllBytes(path + ".bak"))))
            throw new InvalidOperationException("Final release did not retain the exact physical candidate and backup.");

        string inspectionPath = path;
        if (missingBackup)
        {
            // The test remains in the same process after WPF exit. Use a new location for independent authentication.
            inspectionPath = Path.Combine(directory, "independent-candidate.tttykey");
            File.Copy(path, inspectionPath);
        }
        Task.Run(async () =>
        {
            using var current = await CredentialVault.UnlockAsync(inspectionPath, newMaster.AsMemory(), token);
            using var currentSecret = await current.GetPasswordAsync(reference.Name, token);
            if (!currentSecret.Bytes.Span.SequenceEqual(expectedSecret))
                throw new InvalidOperationException("The exact installed candidate lost the expected credential.");
            if (!preserved) return;
            using var prior = await CredentialVault.UnlockAsync(path + ".bak", oldMaster.AsMemory(), token);
            using var priorSecret = await prior.GetPasswordAsync(reference.Name, token);
            if (!priorSecret.Bytes.Span.SequenceEqual(expectedSecret))
                throw new InvalidOperationException("The verified candidate or preserved generation lost the expected credential.");
        }, token).GetAwaiter().GetResult();
        Console.WriteLine(missingBackup ? "PASS LastWindowReportsUnverifiedMasterPublication" :
            standaloneEditor ? "PASS LastVaultEditorClosesDuringIssuedMasterPublication" :
            "PASS LastWindowClosesDuringIssuedMasterPublication");

        async Task UntilAsync(Func<bool> ready)
        {
            while (!ready()) await Task.Delay(15, token);
        }

        async Task AcceptPasswordAsync(string title, string action, string value)
        {
            MainWindow.FormWindow? prompt = null;
            await UntilAsync(() => (prompt = app.Windows.OfType<MainWindow.FormWindow>()
                .SingleOrDefault(window => window.IsVisible && window.Title == App.Text(title))) is not null);
            foreach (var box in FindControls<PasswordBox>(prompt!)) box.Password = value;
            FindControls<Button>(prompt!).Single(button => Equals(button.Content, App.Text(action)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
