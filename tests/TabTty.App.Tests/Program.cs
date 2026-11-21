using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.IO;
using System.Security.AccessControl;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool nativeClipboard = args is ["--isolated-clipboard", _];
        bool clipboardFixture = nativeClipboard || args is ["--clipboard-block", _];
        if (nativeClipboard)
        {
            try { PrepareClipboardFixtureStation(); }
            catch (System.ComponentModel.Win32Exception error)
            {
                Console.Error.WriteLine($"NOT RUN native clipboard fixture: {error.Message} ({error.NativeErrorCode}).");
                return 2;
            }
        }
        string settingsDirectory = Directory.CreateTempSubdirectory("TabTty-settings-test-").FullName;
        LocalState.UseTestDirectory(settingsDirectory);
        MacroTrust.UseTestStore();
        if (args is not ["--profile-trust"])
            AcceptFixtureProfileReviews();
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        var settingsFixture = args is ["--file-editors"] ? new FilePublicationFixture() : null;
        var profileFixture = args is ["--file-editors"] ? new FilePublicationFixture() : null;
        var otherStoreFixture = args is ["--store-publication-faults"] ? new FilePublicationFixture() : null;
        var vaultFaults = args is ["--vault-file-faults"] ? new VaultFileFaultFixture() : null;
        bool standaloneVault = args is ["--vault-last-editor"];
        bool missingMasterBackup = args is ["--vault-last-window-unverified"];
        var masterFixture = standaloneVault || missingMasterBackup || args is ["--vault-last-window"]
            ? new FilePublicationFixture() : null;
#pragma warning disable WPF0001 // Match the application's startup theme before creating test windows.
        var application = new App(settingsPath, settingsFixture is null ? null : settingsFixture.Issue,
            masterFixture is null ? null : masterFixture.Issue, profileFixture is null ? null : profileFixture.Issue,
            vaultFaults?.Hooks, otherStoreFixture is null ? null : otherStoreFixture.Issue,
            profileFixture is null ? null : profileFixture.Validate)
            { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
        try
        {
            if (otherStoreFixture is not null)
            {
                var storeFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(async () =>
                {
                    var owner = new MainWindow(application);
                    owner.Show();
                    await CheckHighlightAndHostTrustPublicationAsync(application, owner, otherStoreFixture);
                }).Task.Unwrap();
                _ = run.ContinueWith(_ => storeFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(storeFrame);
                run.GetAwaiter().GetResult();
                return 0;
            }
            if (vaultFaults is not null)
            {
                var vaultFileFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(() =>
                    CheckVaultFilesAsync(application, settingsDirectory, vaultFaults)).Task.Unwrap();
                _ = run.ContinueWith(_ => vaultFileFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(vaultFileFrame);
                run.GetAwaiter().GetResult();
                return 0;
            }
            if (masterFixture is not null)
            {
                CheckLastWindowDuringMasterPublication(application, settingsDirectory, masterFixture,
                    standaloneVault, missingMasterBackup);
                return 0;
            }
            if (clipboardFixture)
            {
                var clipboardFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(() =>
                    CheckClipboardAndSftpAsync(application, settingsDirectory, int.Parse(args[1]), nativeClipboard)).Task.Unwrap();
                _ = run.ContinueWith(_ => clipboardFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(clipboardFrame);
                run.GetAwaiter().GetResult();
                Console.WriteLine(nativeClipboard ? "PASS IsolatedNativeClipboardAndSftp" : "PASS ClipboardBlockAndSftp");
                return 0;
            }
            if (args is ["--visual-check", var check])
            {
                switch (check)
                {
                    case "terminal": TerminalSurfaceRendersItsCellSnapshot(); break;
                    case "menu": MainMenuOpensAndReusesManagementTabs(application); break;
                    case "authentication": AuthenticationDialogsIdentifyTheRecipient(application); break;
                    case "keyboard": MainMenuAcceptsKeyboardCommands(); break;
                    case "menu-pointer": MainMenuOpensNestedSubmenusWithMouse(); break;
                    case "desktop": RequireDesktopInput(); break;
                    default: throw new ArgumentException("Unknown visual check.", nameof(args));
                }
                Console.WriteLine($"PASS {check}");
                return 0;
            }
            if (args is ["--background-memory", var count, var memoryReport] &&
                int.TryParse(count, out int receivers) && receivers is >= 1 and <= 6)
            {
                var memoryFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(() =>
                    BackgroundReceiveMemory(settingsDirectory, receivers, memoryReport)).Task.Unwrap();
                _ = run.ContinueWith(_ => memoryFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(memoryFrame);
                run.GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--architecture-workload", var port, var seconds, var report] &&
                int.TryParse(port, out int fixturePort) && int.TryParse(seconds, out int duration) &&
                duration is >= 3 and <= 1800)
            {
                var workloadFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(() =>
                    ArchitectureWorkload(application, settingsDirectory, fixturePort, duration, report)).Task.Unwrap();
                _ = run.ContinueWith(_ => workloadFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(workloadFrame);
                run.GetAwaiter().GetResult();
                return 0;
            }
            if (args.SequenceEqual(["--file-operations"]))
            {
                CheckFileOperations(settingsDirectory).GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--native-utf8-baseline" or "--disconnected-tabs" or "--terminal-tab-close" or "--broadcast-splits" or
                "--profile-editor-heading" or
                "--macro-launch" or "--macro-network" or "--profile-trust" or "--vault-first-use" or "--vault-rename"] or
                ["--transfer-window-close" or "--fullscreen-vim" or "--overload-controls" or "--sftp-batch", _])
            {
                var integrationFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(() => args[0] switch
                {
                    "--native-utf8-baseline" => TerminalReceivesWhilePresentationIsPaused(verifyNativeResidency: true),
                    "--disconnected-tabs" => DisconnectedTabsRespectSettingsAndBulkClose(application, settingsPath),
                    "--terminal-tab-close" => CheckTerminalClosure(application, settingsDirectory),
                    "--profile-editor-heading" => CheckProfileEditorHeadingAsync(application, settingsDirectory),
                    "--broadcast-splits" => BroadcastAndSplitViews(application, settingsDirectory),
                    "--macro-launch" => MacroLaunchAndDialog(application, settingsDirectory),
                    "--macro-network" => SharedConstantsNetworkAsync(application, settingsDirectory),
                    "--profile-trust" => CheckProfileTrustAsync(application, settingsDirectory),
                    "--vault-first-use" => CheckVaultFirstUseAsync(application, settingsDirectory),
                    "--vault-rename" => CheckVaultRenameAsync(application, settingsDirectory),
                    "--fullscreen-vim" => CheckFullScreenApplicationAsync(application, settingsDirectory, args[1]),
                    "--overload-controls" => CheckOverloadControlsAsync(application, settingsDirectory, args[1]),
                    "--sftp-batch" => SftpBatchCopiesFolderTrees(int.Parse(args[1]), settingsDirectory),
                    _ => CheckTransferWindowClose(settingsDirectory, int.Parse(args[1])),
                }).Task.Unwrap();
                _ = run.ContinueWith(_ => integrationFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(integrationFrame);
                run.GetAwaiter().GetResult();
                Console.WriteLine($"PASS {args[0]}");
                return 0;
            }
            if (args.SequenceEqual(["--ai-collaboration"]))
            {
                RunAiCollaboration();
                return 0;
            }
            if (args.SequenceEqual(["--file-editors"]))
            {
                var editorActivationFrame = new DispatcherFrame();
                Task run = application.Dispatcher.InvokeAsync(async () =>
                {
                    await CheckProfileEditorHeadingAsync(application, settingsDirectory);
                    Console.WriteLine("PASS CheckProfileEditorHeading");
                    await EditorKeepsSavedRevisionAndRestoresDraft(application);
                    Console.WriteLine("PASS EditorKeepsSavedRevisionAndRestoresDraft");
                    await CheckFileEditors(application, settingsDirectory, settingsFixture!, profileFixture!);
                }).Task.Unwrap();
                _ = run.ContinueWith(_ => editorActivationFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(editorActivationFrame);
                run.GetAwaiter().GetResult();
                return 0;
            }
            CheckFileOperations(settingsDirectory).GetAwaiter().GetResult();
            TextProviderKeepsLogicalTextAndSurvivingRanges();
            Console.WriteLine("PASS TextProviderKeepsLogicalTextAndSurvivingRanges");
            TextProviderMovesByPinnedUnicodeCharacters();
            Console.WriteLine("PASS TextProviderMovesByPinnedUnicodeCharacters");
            bool visualChecksPassed = true;
            RunVisualCheck(nameof(TerminalSurfaceRendersItsCellSnapshot), TerminalSurfaceRendersItsCellSnapshot);
            var highlightFrame = new DispatcherFrame();
            Task highlightTest = application.Dispatcher.InvokeAsync(HighlightWaitsForIdleOrViewportChange).Task.Unwrap();
            _ = highlightTest.ContinueWith(_ => highlightFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(highlightFrame);
            highlightTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS HighlightWaitsForIdleOrViewportChange");
            ProfileEditorKeepsUnchangedOptionsAndEditsTypedFields(application);
            Console.WriteLine("PASS ProfileEditorKeepsUnchangedOptionsAndEditsTypedFields");
            AuthenticationDialogsIdentifyTheRecipient(application);
            Console.WriteLine("PASS AuthenticationDialogsIdentifyTheRecipient");
            WindowPlacementKeepsNormalBoundsInsideChangedWorkAreas();
            Console.WriteLine("PASS WindowPlacementKeepsNormalBoundsInsideChangedWorkAreas");
            ProfileLocationUsesTheStoredSpelling();
            Console.WriteLine("PASS ProfileLocationUsesTheStoredSpelling");
            StoreRecoveryPreservesTheOriginalAndValidatesTheBackup();
            Console.WriteLine("PASS StoreRecoveryPreservesTheOriginalAndValidatesTheBackup");
            MissingTerminalFontUsesAnInstalledFixedPitchFallback();
            Console.WriteLine("PASS MissingTerminalFontUsesAnInstalledFixedPitchFallback");
            Task.Run(() => VaultUnlockWaitersShareOneInteraction(settingsDirectory)).GetAwaiter().GetResult();
            Console.WriteLine("PASS VaultUnlockWaitersShareOneInteraction");
            var macroFrame = new DispatcherFrame();
            Task macroTest = application.Dispatcher.InvokeAsync(() => MacroLaunchAndDialog(application, settingsDirectory)).Task.Unwrap();
            _ = macroTest.ContinueWith(_ => macroFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(macroFrame);
            macroTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS MacroLaunchAndDialog");
            var frame = new DispatcherFrame();
            Task inputTest = application.Dispatcher.InvokeAsync(TerminalSendsSpacesAndSwitchesTabsAfterDisconnect).Task.Unwrap();
            _ = inputTest.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            inputTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS TerminalSendsSpacesAndSwitchesTabsAfterDisconnect");
            var presentationFrame = new DispatcherFrame();
            Task presentationTest = application.Dispatcher.InvokeAsync(() => TerminalReceivesWhilePresentationIsPaused(verifyNativeResidency: false)).Task.Unwrap();
            _ = presentationTest.ContinueWith(_ => presentationFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(presentationFrame);
            presentationTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS TerminalReceivesWhilePresentationIsPaused");

            {
                var nativePresentationFrame = new DispatcherFrame();
                Task nativePresentationTest = application.Dispatcher.InvokeAsync(() => TerminalReceivesWhilePresentationIsPaused(verifyNativeResidency: true)).Task.Unwrap();
                _ = nativePresentationTest.ContinueWith(_ => nativePresentationFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(nativePresentationFrame);
                nativePresentationTest.GetAwaiter().GetResult();
                Console.WriteLine("PASS NativeTerminalReceivesWhilePresentationIsPaused");
            }
            RunVisualCheck(nameof(MainMenuOpensAndReusesManagementTabs), () => MainMenuOpensAndReusesManagementTabs(application));
            RunVisualCheck(nameof(MainMenuAcceptsKeyboardCommands), MainMenuAcceptsKeyboardCommands);
            ManagementPagesShareResponsiveLayout(application);
            Console.WriteLine("PASS ManagementPagesShareResponsiveLayout");
            var closeFrame = new DispatcherFrame();
            Task closeTest = application.Dispatcher.InvokeAsync(() => DisconnectedTabsRespectSettingsAndBulkClose(application, settingsPath)).Task.Unwrap();
            _ = closeTest.ContinueWith(_ => closeFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(closeFrame);
            closeTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS DisconnectedTabsRespectSettingsAndBulkClose");
            var tabCloseFrame = new DispatcherFrame();
            Task tabCloseTest = application.Dispatcher.InvokeAsync(() => CheckTerminalClosure(application, settingsDirectory)).Task.Unwrap();
            _ = tabCloseTest.ContinueWith(_ => tabCloseFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(tabCloseFrame);
            tabCloseTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS CheckTerminalTabClose");
            var broadcastFrame = new DispatcherFrame();
            Task broadcastTest = application.Dispatcher.InvokeAsync(() => BroadcastAndSplitViews(application, settingsDirectory)).Task.Unwrap();
            _ = broadcastTest.ContinueWith(_ => broadcastFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(broadcastFrame);
            broadcastTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS BroadcastAndSplitViews");
            RunAiCollaboration();
            var settingsFrame = new DispatcherFrame();
            Task settingsTest = application.Dispatcher.InvokeAsync(() => SettingsSwitchLanguageAndThemeInOpenWindows(application)).Task.Unwrap();
            _ = settingsTest.ContinueWith(_ => settingsFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(settingsFrame);
            settingsTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS SettingsSwitchLanguageAndThemeInOpenWindows");
            var editorFrame = new DispatcherFrame();
            Task editorTest = application.Dispatcher.InvokeAsync(() => EditorKeepsSavedRevisionAndRestoresDraft(application)).Task.Unwrap();
            _ = editorTest.ContinueWith(_ => editorFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(editorFrame);
            editorTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS EditorKeepsSavedRevisionAndRestoresDraft");
            MessageBoxesUseTheApplicationThemeAndSafeDefault(application);
            Console.WriteLine("PASS MessageBoxesUseTheApplicationThemeAndSafeDefault");
            if (Environment.GetEnvironmentVariable("TABTTY_INTEROP_SFTP") is not null &&
                int.TryParse(Environment.GetEnvironmentVariable("TABTTY_INTEROP_SSH_PORT"), out int sshPort))
            {
                var transferFrame = new DispatcherFrame();
                Task transferTest = application.Dispatcher.InvokeAsync(async () =>
                {
                    await SftpBatchCopiesFolderTrees(sshPort, settingsDirectory);
                    Console.WriteLine("PASS SftpBatchCopiesFolderTrees");
                    await CheckTransferWindowClose(settingsDirectory, sshPort);
                }).Task.Unwrap();
                _ = transferTest.ContinueWith(_ => transferFrame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(transferFrame);
                transferTest.GetAwaiter().GetResult();
                Console.WriteLine("PASS CheckTransferWindowClose");
            }
            return visualChecksPassed ? 0 : 1;

            void RunVisualCheck(string name, Action check)
            {
                try
                {
                    check();
                    Console.WriteLine($"PASS {name}");
                }
                catch (DesktopInputUnavailableException exception)
                {
                    Console.WriteLine($"NOT RUN {name}: {exception.Message}");
                }
                catch (InvalidOperationException exception)
                {
                    visualChecksPassed = false;
                    Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                }
            }
        }
        catch (DesktopInputUnavailableException exception)
        {
            Console.WriteLine($"NOT RUN: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            application.Shutdown();
            Directory.Delete(settingsDirectory, recursive: true);
        }

        void RunAiCollaboration()
        {
            var aiFrame = new DispatcherFrame();
            Task aiTest = application.Dispatcher.InvokeAsync(() => AiSharesAnExistingTerminal(application, settingsDirectory)).Task.Unwrap();
            _ = aiTest.ContinueWith(_ => aiFrame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(aiFrame);
            aiTest.GetAwaiter().GetResult();
            Console.WriteLine("PASS AiSharesAnExistingTerminal");
        }
    }

    private static async Task SftpBatchCopiesFolderTrees(int port, string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        string profile = Path.Combine(root, "transfer.ttty");
        await ConnectionProfile.CreateSsh("SFTP", "127.0.0.1", "tabtty-sftp", port).SaveNewAsync(profile);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        await using var session = await TerminalSession.ConnectAsync(profile, token, interaction);
        using var client = await session.OpenFileTransferAsync(token);
        var files = new TransferFiles(client, session.FileTransferWait);
        string source = Path.Combine(root, "upload");
        string target = Path.Combine(root, "download");
        Directory.CreateDirectory(Path.Combine(source, "tree", "empty"));
        var mergeDirectory = Directory.CreateDirectory(Path.Combine(target, "tree"));
        string originalAcl = mergeDirectory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        byte[] expected = Enumerable.Range(0, 70_001).Select(value => (byte)value).ToArray();
        string sourceFile = Path.Combine(source, "tree", "試験.bin");
        await File.WriteAllBytesAsync(sourceFile, expected, token);
        DateTimeOffset sourceModified = DateTimeOffset.FromUnixTimeSeconds(1_650_000_000);
        File.SetLastWriteTimeUtc(sourceFile, sourceModified.UtcDateTime);
        File.SetLastAccessTimeUtc(sourceFile, sourceModified.AddDays(7).UtcDateTime);
        await File.WriteAllTextAsync(Path.Combine(target, "tree", "unrelated.txt"), "keep", token);
        await File.WriteAllTextAsync(Path.Combine(target, "tree", "試験.bin"), "replace", token);
        TransferPath upload = await files.ResolveAsync(false, source, null, token);
        TransferPath download = await files.ResolveAsync(false, target, null, token);
        TransferPath remote = await files.ResolveAsync(true, ".", null, token);
        var owner = new FileOperationOwnership();
        var operations = new FileOperations(files, owner, token);
        var results = new List<string>();
        operations.Changed += update => { if (update is FileOperationUpdate.Entry entry) results.Add(entry.Reason); };
        int approvals = 0;
        ValueTask<TransferChoice> Approve(TransferItem incoming, TransferPath _, TransferAttributes existing)
        {
            if (incoming.Attributes.Size != expected.Length || existing.Size != 7)
                throw new InvalidOperationException("The overwrite prompt did not receive both file sizes.");
            approvals++;
            return ValueTask.FromResult(TransferChoice.Overwrite);
        }
        ValueTask<TransferChoice> Confirm(TransferQuestion question) => Approve(question.Source!, question.Path, question.Existing!);
        string uploaded = await operations.Copy(await files.ListAsync(upload, token), remote, Confirm)!;
        var remoteTimes = await client.StatAsync(client.EncodePath("/home/tree/試験.bin"), token);
        if (remoteTimes?.Modified != sourceModified || remoteTimes?.Accessed != sourceModified)
            throw new InvalidOperationException("An upload must publish only its source modification time.");
        string downloaded = await operations.Copy(await files.ListAsync(remote, token), download, Confirm)!;
        if (uploaded != "TransferFinished" || downloaded != "TransferFinished")
            throw new InvalidOperationException($"{uploaded}, {downloaded}");
        byte[] actual = await File.ReadAllBytesAsync(Path.Combine(target, "tree", "試験.bin"), token);
        if (!actual.SequenceEqual(expected) || approvals != 1 ||
            File.ReadAllText(Path.Combine(target, "tree", "試験.bin") + ":Zone.Identifier") != "[ZoneTransfer]\r\nZoneId=3\r\n" ||
            !Directory.Exists(Path.Combine(target, "tree", "empty")) ||
            !PrivateWindowsFile.HasPrivateAccess(new DirectoryInfo(Path.Combine(target, "tree", "empty"))) ||
            mergeDirectory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) != originalAcl ||
            !File.Exists(Path.Combine(target, "tree", "unrelated.txt")) || results.Any(key => key.StartsWith("Sftp", StringComparison.Ordinal)))
            throw new InvalidOperationException("The SFTP batch did not preserve file bytes, the empty directory, and unrelated content. " + string.Join(", ", results));

        string recycleFixture = Path.Combine(source, "recycle-fixture.txt");
        await File.WriteAllTextAsync(recycleFixture, "synthetic fixture", token);
        TransferItem localFile = (await files.ListAsync(upload, token)).Single(item => item.Label == "recycle-fixture.txt");
        CheckStatus(await operations.Delete(upload, [localFile], question =>
            ValueTask.FromResult(question.Kind == TransferQuestionKind.Delete ? TransferChoice.Overwrite : TransferChoice.Skip))!);
        if (File.Exists(recycleFixture) || operations.Results is not [{ Reason: "TransferDeleted" }])
            throw new InvalidOperationException("Windows recycling did not remove the synthetic file.");

        CheckStatus(await operations.Create(upload, "local-folder")!);
        var folder = (await files.ListAsync(upload, token)).Single(item => item.Label == "local-folder");
        CheckStatus(await operations.Rename(upload, folder, "renamed-folder")!);
        if (Directory.Exists(Path.Combine(source, "local-folder")) || !Directory.Exists(Path.Combine(source, "renamed-folder")))
            throw new InvalidOperationException("The local folder rename was not applied.");
        CheckStatus(await operations.Create(remote, "remote-folder")!);
        var remoteEntries = await files.ListAsync(remote, token);
        var remoteFolder = remoteEntries.Single(item => item.Label == "remote-folder");
        var remoteTree = remoteEntries.Single(item => item.Label == "tree");
        var remoteFile = (await files.ListAsync(remoteTree.Path, token)).Single(item => item.Label == "試験.bin");
        CheckStatus(await operations.Rename(remoteTree.Path, remoteFile, "renamed.bin")!);
        if (!(await files.ListAsync(remoteTree.Path, token)).Any(item => item.Label == "renamed.bin"))
            throw new InvalidOperationException("The remote rename was not applied.");
        CheckStatus(await operations.Delete(remote, [remoteFolder, remoteTree], question =>
            ValueTask.FromResult(question.Kind == TransferQuestionKind.Delete ? TransferChoice.Overwrite : TransferChoice.Skip))!);
        if ((await files.ListAsync(remote, token)).Length != 0)
            throw new InvalidOperationException("The remote recursive delete left fixture entries.");

        byte[] providerBytes = "bounded local provider"u8.ToArray();
        TransferPath providerSource = files.Child(remote, "provider.bin");
        var remoteHandle = await client.OpenAsync(providerSource.Remote!, write: true, token);
        await client.WriteAsync(remoteHandle, 0, providerBytes, token);
        await client.CloseAsync(remoteHandle, token);
        var providerEntry = (await files.ListAsync(remote, token)).Single(item => item.Label == "provider.bin");
        var entered = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedFiles = new TransferFiles(client, session.FileTransferWait, owner, beforeLocalWrite: async data =>
        {
            entered.TrySetResult(data);
            await provider.Task.WaitAsync(token);
        });
        var staging = new FileOperations(blockedFiles, owner, token);
        Task<string> pendingProvider = staging.Copy([providerEntry], download,
            _ => ValueTask.FromResult(TransferChoice.Overwrite))!;
        try
        {
            var heldBytes = await entered.Task.WaitAsync(token);
            client.Dispose();
            using var reopenedClient = await session.OpenFileTransferAsync(token);
            var reopenedFiles = new TransferFiles(reopenedClient, session.FileTransferWait, owner);
            var reopened = new FileOperations(reopenedFiles, owner, token);
            var current = await reopenedFiles.ResolveAsync(true, ".", null, token);
            _ = await reopenedFiles.ListAsync(current, token);
            if (reopened.Create(current, "pending") is not null || reopened.Results.Count != 0)
                throw new InvalidOperationException("Reopening restored results or released a pending mutation.");
            if (!heldBytes.Span.SequenceEqual(providerBytes) || !owner.IsBusy || pendingProvider.IsCompleted)
                throw new InvalidOperationException("Channel release changed the local provider's admitted block.");
            try
            {
                using var unexpected = SftpClient.ReserveProviderStorage(30 * 1024 * 1024);
                throw new InvalidOperationException("The channel returned a block still owned by the local provider.");
            }
            catch (SftpException error) when (error.Failure == SftpFailure.Capacity) { }
            {
                await using var newSession = await TerminalSession.ConnectAsync(profile, token, interaction);
                using var newClient = await newSession.OpenFileTransferAsync(token);
                var newOwner = new FileOperationOwnership();
                var newFiles = new TransferFiles(newClient, newSession.FileTransferWait, newOwner);
                var newOperations = new FileOperations(newFiles, newOwner, token);
                var newDirectory = await newFiles.ResolveAsync(true, ".", null, token);
                CheckStatus(await newOperations.Create(newDirectory, "new-generation")!);
                if (!owner.IsBusy) throw new InvalidOperationException("Another SSH connection released the original provider.");
                newClient.Dispose();
            }
            provider.SetResult();
            string providerResult = await pendingProvider.WaitAsync(token);
            if (providerResult == "TransferFinished" || File.Exists(Path.Combine(target, "provider.bin")))
                throw new InvalidOperationException("Closing SFTP published a partial download.");
            while (owner.IsBusy) await Task.Delay(10, token);
            using (SftpClient.ReserveProviderStorage(30 * 1024 * 1024)) { }
            CheckStatus(await reopened.Create(current, "after-completion")!);
        }
        finally { provider.TrySetResult(); }

        static void CheckStatus(string status)
        {
            if (status != "TransferFinished") throw new InvalidOperationException(status);
        }
    }

    private static void AuthenticationDialogsIdentifyTheRecipient(App app)
    {
        app.Profiles.CreateProfile();
        var editor = app.Windows.OfType<ProfileEditorWindow>().Single();
        try
        {
            var editorView = (FormView)editor.Content;
            AssertSaveRequiresExplicitActivation(editorView, "Create");
            if (editor.Title != App.Text("NewProfileWindowTitle"))
                throw new InvalidOperationException("A new profile did not use the creation window title.");
            if (ProfileEditorHeading(editorView).Text != App.Text("NewProfile"))
                throw new InvalidOperationException("A new profile did not keep its creation heading.");
            if (((RadioButton)editorView.FindName("ProtocolSSH")).IsChecked != true)
                throw new InvalidOperationException("A new profile did not start with SSH selected.");
            var savedPassword = new CredentialReference("Shared password", "password");
            var savedKey = new CredentialReference("Shared key", "privateKey");
            var profile = ConnectionProfile.CreateSsh("Shared", "fixture.example", "original", password: savedPassword,
                methods: ["password", "publicKey"], privateKey: savedKey);
            var profileForm = new FormView();
            var profileEditor = new ProfileEditor(profileForm, profile, app);
            var telnetOption = (RadioButton)profileForm.FindName("ProtocolTelnet");
            var protocolChoices = (Panel)LogicalTreeHelper.GetParent(telnetOption);
            var telnetDescription = AssertDescriptionBefore(protocolChoices, "TelnetPrivacyNotice");
            if (telnetDescription.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The clear-text notice appeared for SSH.");
            telnetOption.IsChecked = true;
            if (telnetDescription.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Selecting Telnet did not show the clear-text notice.");
            ((RadioButton)profileForm.FindName("ProtocolSSH")).IsChecked = true;

            var sshFields = (StackPanel)profileForm.FindName("sshFields");
            var authentication = (StackPanel)profileForm.FindName("sshAuthenticationFields");
            var username = (TextBox)authentication.FindName("SshUsername");
            if (sshFields.FindName("SshUsername") is not null ||
                authentication.FindName("PrimarySshMethod") is not ComboBox ||
                authentication.FindName("SshPasswordSource") is not TextBox passwordSource ||
                authentication.FindName("PublicKey") is not TextBox ||
                authentication.FindName("AllowKeyboardInteractive") is not CheckBox keyboardInteractive)
                throw new InvalidOperationException("The SSH authentication fields were not grouped on their own page.");
            AssertStandaloneCheckBox(keyboardInteractive, "AllowKeyboardInteractive");
            AssertStandaloneCheckBox((CheckBox)sshFields.FindName("SshLiveness"), "SshLiveness");
            AssertDescriptionBefore(passwordSource, "SshSavedPasswordDestinationNotice");
            AssertDescriptionBefore((TextBox)profileForm.FindName("LogDirectory"), "NetworkLogDestinationNotice");
            string[] logKeys = ["RecordRaw", "RecordText", "RecordTerminal"];
            CheckBox[] logs = logKeys.Select(key => (CheckBox)profileForm.FindName(key)).ToArray();
            var choices = (WrapPanel)LogicalTreeHelper.GetParent(logs[0]);
            var loggingPage = (Panel)LogicalTreeHelper.GetParent(choices);
            int choicesIndex = loggingPage.Children.IndexOf(choices);
            if (choicesIndex < 1 || loggingPage.Children[choicesIndex - 1] is not TextBlock title ||
                title.Text != App.Text("RecordedLogs") || title.FontWeight != FontWeights.Bold ||
                !choices.Children.OfType<CheckBox>().SequenceEqual(logs) ||
                !logs.Select(check => ((TextBlock)check.Content).Text).SequenceEqual(logKeys.Select(App.Text)))
                throw new InvalidOperationException("The three recording choices did not share a single field title.");
            if (username.IsReadOnly || username.Text != "original")
                throw new InvalidOperationException("Saved credentials overrode the editable SSH username.");
            username.Text = "Changed";
            logs[0].IsChecked = logs[2].IsChecked = true;
            var changed = profileEditor.GetDraft();
            if (changed.SshUsername != "Changed" || changed.SshPassword != savedPassword || changed.SshPrivateKey != savedKey)
                throw new InvalidOperationException("Editing the SSH username changed the selected credentials.");
            if (!changed.RecordRaw || changed.RecordText || !changed.RecordTerminal)
                throw new InvalidOperationException("Recording choices were not independently editable.");

            var destination = SshDestination.Parse("fixture.example", 2222);
            var challenge = new SshChallenge(destination, "Example", "Sign in", [new("User", true), new("Code", false)]);
            var (dialog, inputs) = TerminalTab.CreateSshChallengeForm(editor, challenge);
            try
            {
                var contents = (FormView)dialog.Content;
                string expected = string.Format(App.Text("SshServerQuestions"), destination);
                if (contents.Children[0] is not TextBlock notice || notice.Text != expected ||
                    DockPanel.GetDock(notice) != Dock.Top || inputs is not [TextBox { IsUndoEnabled: false }, PasswordBox] ||
                    !FindControls<Button>(contents).Any(button => Equals(button.Content, App.Text("SendSshAnswers"))))
                    throw new InvalidOperationException("The SSH form did not keep the recipient above the scrolling server questions.");
            }
            finally { dialog.Close(); }

            var key = new VaultEntryInfo("Team key", "privateKey", "ssh-ed25519", "SHA256:fixture");
            var password = new VaultEntryInfo("Team\u2028password");
            VaultEntryInfo[] entries = [key, password, password with { Name = "Another password" },
                key with { Name = "Another key" }];
            var request = new SshPrivateKeyRequest(destination, "operator\u200B", null);
            var (keyPicker, keys) = VaultFileController.CreateCredentialPicker(editor, entries, "privateKey", request);
            try
            {
                string[] fields = FindControls<TextBox>(keyPicker).Where(box => box.IsReadOnly).Select(box => box.Text).ToArray();
                if (!fields.Contains(destination.ToString()) || !fields.Contains("operator<U+200B>") ||
                    keys.SelectedIndex != -1 || keys.Items.Count != 2 ||
                    !keys.Items[0].ToString()!.Split('\n').Contains(key.Fingerprint) ||
                    !keys.Items[1].ToString()!.Split('\n').Contains("Another key") ||
                    !FindControls<Button>(keyPicker).Any(button => button.IsDefault && button.IsCancel &&
                        Equals(button.Content, App.Text("CancelSshConnection"))))
                    throw new InvalidOperationException("Private-key selection must identify the SSH recipient and key before authentication.");
            }
            finally { keyPicker.Close(); }

            var (passwordPicker, passwords) = VaultFileController.CreateCredentialPicker(editor, entries, "password", null);
            try
            {
                string[][] rows = passwords.Items.Cast<object>().Select(item => item.ToString()!.Split('\n')).ToArray();
                if (rows.Length != 2 || rows[0] is not ["Team<U+2028>password"] || rows[1] is not ["Another password"])
                    throw new InvalidOperationException("The credential picker must identify passwords by their distinct names.");
            }
            finally { passwordPicker.Close(); }
        }
        finally { editor.Close(); }

        static TextBlock AssertDescriptionBefore(FrameworkElement control, string key)
        {
            var parent = (Panel)LogicalTreeHelper.GetParent(control);
            int index = parent.Children.IndexOf(control);
            if (index < 2 || parent.Children[index - 1] is not TextBlock description ||
                description.Text != App.Text(key) || description.Opacity >= 1 ||
                (parent.Children[index - 2] is Label label ? label.Content : parent.Children[index - 2])
                    is not TextBlock title || title.FontWeight != FontWeights.Bold)
                throw new InvalidOperationException($"The field did not put its description after its bold title: {key}.");
            return description;
        }
    }

    private static void AssertStandaloneCheckBox(CheckBox check, string key)
    {
        if (AutomationProperties.GetLabeledBy(check) is not Label { Content: TextBlock title } label ||
            title.Text != App.Text(key) || title.FontWeight != FontWeights.Bold ||
            check.Content is not TextBlock content || content.Text != App.Text("Enabled") ||
            LogicalTreeHelper.GetParent(check) is not Panel parent ||
            parent.Children.IndexOf(label) + 1 != parent.Children.IndexOf(check))
            throw new InvalidOperationException($"The enable check box did not have a separate title: {key}.");
    }

    private static Button AssertSaveRequiresExplicitActivation(FormView form, string key = "Save")
    {
        var save = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text(key)));
        if (save.IsDefault || KeyboardNavigation.GetAcceptsReturn(save))
            throw new InvalidOperationException("The Save button could be activated by Enter.");
        return save;
    }

    private static void MainMenuOpensAndReusesManagementTabs(App app)
    {
#pragma warning disable WPF0001 // WPF Fluent theme integration.
        ThemeMode previous = app.ThemeMode;
        app.ThemeMode = ThemeMode.Dark;
        var window = new MainWindow(app);
        try
        {
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            if (window.WindowStyle != WindowStyle.None || chrome.CaptionHeight <= 0 || chrome.ResizeBorderThickness.Top <= 0)
                throw new InvalidOperationException("The tab strip did not replace the native caption.");
            var menuButton = (Button)window.FindName("menuButton");
            var tabs = (TabControl)window.FindName("tabs");
            Click("ToolsMenu", "Settings");
            var settings = (TabItem)tabs.SelectedItem;
            Click("FileMenu", "New");
            var editor = app.Windows.OfType<ProfileEditorWindow>().Single();
            try
            {
                Click("FileMenu", "New");
                if (tabs.Items.Count != 1 || editor.Owner is not null || !editor.IsVisible ||
                    !ReferenceEquals(editor, app.Windows.OfType<ProfileEditorWindow>().Single()))
                    throw new InvalidOperationException("New profile did not open or reuse an independent editor window.");
            }
            finally { editor.Close(); }
            Click("ToolsMenu", "HighlightSets");
            var highlights = (TabItem)tabs.SelectedItem;
            Click("ToolsMenu", "ProfileCatalog");
            if (tabs.Items.Count != 3 || tabs.SelectedContent is not FormView || settings == highlights)
                throw new InvalidOperationException("Management commands did not open independent tabs.");
            Click("ToolsMenu", "Settings");
            if (tabs.SelectedItem != settings || tabs.Items.Count != 3)
                throw new InvalidOperationException("Opening settings again discarded or duplicated its tab.");
            CheckTabStripLayout(window, menuButton, settings);
            CheckFixedWidthTabHeaders(window, settings, highlights);
            CheckTabStripNavigation(window, tabs);
            var add = (Button)tabs.Template.FindName("newTabButton", tabs);
            var root = (FrameworkElement)window.Content;
            double lastTabEnd = tabs.Items.OfType<TabItem>()
                .Max(tab => tab.TranslatePoint(new Point(tab.ActualWidth, 0), root).X);
            if (add.TranslatePoint(new Point(), root).X < lastTabEnd - 1)
                throw new InvalidOperationException("The new-tab button is not beside the rightmost tab.");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, add));
            try
            {
                MenuItem[] actions = add.ContextMenu.Items.OfType<MenuItem>().ToArray();
                if (!add.ContextMenu.IsOpen || !Equals(actions.LastOrDefault()?.Header, App.Text("ProfileCatalog")) ||
                    actions.SkipLast(1).Any(item => item.HasItems || item.ToolTip is not string))
                    throw new InvalidOperationException("The new-tab menu did not open with direct profile actions.");
            }
            finally { add.ContextMenu.IsOpen = false; }
            string? openedProfile = null;
            var registered = MainWindow.CreateProfileMenuItem("My_Server", "fixture.ttty", path => openedProfile = path);
            registered.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, registered));
            if (System.Windows.Automation.AutomationProperties.GetName(registered) != "My_Server" ||
                !Equals(registered.Header, "My__Server") || openedProfile != "fixture.ttty")
                throw new InvalidOperationException("The direct profile action did not preserve the display name or open its file.");
            var tools = ((Menu)window.FindName("mainMenu")).Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, App.Text("ToolsMenu")));
            var vault = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("Vault")));
            if (!vault.Items.OfType<MenuItem>().Select(item => item.Header)
                .SequenceEqual(new[] { App.Text("UnlockVault"), App.Text("ManageVault"), App.Text("RestoreVault"), App.Text("ArchiveVault") }))
                throw new InvalidOperationException("The vault commands were not grouped under Credential vault.");

            DesktopInputUnavailableException? visualUnavailable = null;
            try { OpenMenuUsesWindowTextFont(window, menuButton, settings); }
            catch (DesktopInputUnavailableException exception) { visualUnavailable = exception; }
            bool? completed = null;
            var settingsForm = (FormView)settings.Content;
            AssertSaveRequiresExplicitActivation(settingsForm);
            var modal = new FormView(modal: true);
            bool saved = false;
            modal.AddButtons(() => saved = true);
            var modalSave = AssertSaveRequiresExplicitActivation(modal);
            modalSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, modalSave));
            if (!saved) throw new InvalidOperationException("An explicit click did not save the dialog.");
            var settingChecks = FindControls<CheckBox>(settingsForm).ToArray();
            AssertStandaloneCheckBox(settingChecks[0], "AutoCloseDisconnectedTabs");
            AssertStandaloneCheckBox(settingChecks[1], "BroadcastAcrossWindows");
            settingsForm.Completed += result => completed = result;
            int revision = app.SettingsRevision;
            var dismiss = ((DockPanel)settings.Header).Children.OfType<Button>().Single();
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dismiss));
            if (completed != false || tabs.Items.Count != 2 || !tabs.Items.Contains(highlights) || app.SettingsRevision != revision)
                throw new InvalidOperationException("The settings tab close button did not cancel its draft.");
            app.ThemeMode = ThemeMode.Light;
            if (menuButton.Foreground is not SolidColorBrush lightIcon || window.Background is not SolidColorBrush lightStrip ||
                lightIcon.Color.R >= lightStrip.Color.R)
                throw new InvalidOperationException("The tab strip did not update after changing to the light theme.");
            if (visualUnavailable is not null) throw visualUnavailable;

            void Click(string section, string action)
            {
                var group = ((Menu)window.FindName("mainMenu")).Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, App.Text(section)));
                var command = group.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text(action)));
                command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, command));
            }
        }
        finally
        {
            window.Close();
            app.ThemeMode = previous;
        }
#pragma warning restore WPF0001
    }

    private static async Task EditorKeepsSavedRevisionAndRestoresDraft(App app)
    {
        string directory = Directory.CreateTempSubdirectory("TabTty-editor-test-").FullName;
        try
        {
            string path = Path.Combine(directory, "fixture.ttty");
            var profile = ConnectionProfile.CreateTelnet("Initial", "localhost");
            await profile.SaveNewAsync(path);
            var form = new FormView();
            app.Profiles.AddEditor(form, profile, path);
            var name = (TextBox)form.FindName("Name");
            var timeout = (TextBox)form.FindName("WaitSeconds");
            var buttons = FormFields(form).Children.OfType<StackPanel>().Last();
            var save = buttons.Children.OfType<Button>().Single(button => Equals(button.Content, App.Text("Save")));
            var discard = buttons.Children.OfType<Button>().Single(button => Equals(button.Content, App.Text("DiscardChanges")));
            var close = buttons.Children.OfType<Button>().Single(button => Equals(button.Content, App.Text("CloseDialog")));
            if (!close.IsEnabled || buttons.Children.IndexOf(close) != buttons.Children.IndexOf(discard) + 1)
                throw new InvalidOperationException("The Close button was not available beside Discard changes.");
            bool? completed = null;
            form.Completed += result => completed = result;
            if (form.HasUnsavedChanges) throw new InvalidOperationException("A pristine editor has unsaved changes.");
            CheckActions(changed: false);
            name.Text = "Modified";
            CheckActions(changed: true);
            var navigation = FindControls<ListBox>(form).Single();
            navigation.SelectedIndex = 2;
            navigation.SelectedIndex = 0;
            if (name.Text != "Modified" || !form.HasUnsavedChanges)
                throw new InvalidOperationException("Switching profile categories discarded the draft.");
            int confirmations = 0;
            form.RequestClose(() => { confirmations++; return false; });
            if (confirmations != 1 || completed is not null || name.Text != "Modified")
                throw new InvalidOperationException("Rejecting the close confirmation did not retain the draft.");
            name.Text = "Initial";
            if (form.HasUnsavedChanges) throw new InvalidOperationException("Reverting a field left the editor dirty.");
            CheckActions(changed: false);

            timeout.Text = "invalid";
            await ClickSave();
            if (!form.HasUnsavedChanges || completed is not null)
                throw new InvalidOperationException("A failed save cleared the draft or closed the editor.");
            CheckActions(changed: true);
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, discard));
            if (form.HasUnsavedChanges || completed is not null || timeout.Text == "invalid")
                throw new InvalidOperationException("Discard did not restore the initial values and keep the tab open.");
            CheckActions(changed: false);

            foreach (string revision in new[] { "First save", "Second save" })
            {
                name.Text = revision;
                await ClickSave();
                if (form.HasUnsavedChanges || completed is not null ||
                    (await ConnectionProfile.LoadAsync(path)).DisplayName != revision)
                    throw new InvalidOperationException("Saving did not advance the saved revision while keeping the tab open.");
                CheckActions(changed: false);
            }
            name.Text = "Unsaved";
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, discard));
            if (name.Text != "Second save" || form.HasUnsavedChanges || completed is not null)
                throw new InvalidOperationException("Discard did not restore the most recent saved revision.");
            name.Text = "Discard on close";
            form.RequestClose(() => { confirmations++; return true; });
            if (confirmations != 2 || completed != false || (await ConnectionProfile.LoadAsync(path)).DisplayName != "Second save")
                throw new InvalidOperationException("Accepting the close confirmation did not discard the unsaved draft.");

            void CheckActions(bool changed)
            {
                if (save.IsEnabled != changed || discard.IsEnabled != changed || !close.IsEnabled)
                    throw new InvalidOperationException("The editor buttons did not reflect its unsaved changes.");
            }

            async Task ClickSave()
            {
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, save));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (form.IsBusy) await Task.Delay(10, deadline.Token);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void OpenMenuUsesWindowTextFont(Window window, Button button, TabItem tab)
    {
        RequireDesktopInput();
        var menu = (Menu)window.FindName("mainMenu");
        var root = (FrameworkElement)window.Content;
        window.Width = 960;
        window.Height = 620;
        window.Show();
        window.Activate();
        var source = PresentationSource.FromVisual(window);
        menu.Opacity = 0;
        try
        {
            root.Measure(new Size(960, 620));
            root.Arrange(new Rect(0, 0, 960, 620));
            root.UpdateLayout();
            var dismiss = ((DockPanel)tab.Header).Children.OfType<Button>().Single();
            Point closePosition = dismiss.TranslatePoint(new Point(dismiss.ActualWidth / 2, dismiss.ActualHeight / 2), root);
            var inputHit = root.InputHitTest(closePosition);
            DependencyObject? hit = inputHit as DependencyObject;
            while (hit is Visual && hit != dismiss) hit = VisualTreeHelper.GetParent(hit);
            if (hit != dismiss || !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(dismiss))
                throw new InvalidOperationException($"The tab close button was not hit. Target: {inputHit?.GetType().Name}.");

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (!menu.IsVisible || !ReferenceEquals(PresentationSource.FromVisual(menu), source))
                throw new InvalidOperationException("The menu did not open inside the window.");
            var group = (MenuItem)menu.Items[0];
            var nextGroup = (MenuItem)menu.Items[1];
            var panel = (Border)menu.Template.FindName("mainMenuPanel", menu);
            double panelRight = panel.TranslatePoint(new Point(panel.ActualWidth, 0), root).X;
            if (Math.Abs(panelRight - root.ActualWidth) > 1)
                throw new InvalidOperationException("The root menu did not align with the window's right edge.");
            var arrow = (TextBlock)group.Template.FindName("submenuArrow", group);
            var label = (ContentPresenter)group.Template.FindName("label", group);
            var nextLabel = (ContentPresenter)nextGroup.Template.FindName("label", nextGroup);
            double labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), root).X;
            double nextLabelRight = nextLabel.TranslatePoint(new Point(nextLabel.ActualWidth, 0), root).X;
            if (!arrow.IsVisible || arrow.Text != "\uE76B" || arrow.TranslatePoint(new Point(arrow.ActualWidth, 0), root).X >
                label.TranslatePoint(new Point(), root).X || Math.Abs(labelRight - nextLabelRight) > 1)
                throw new InvalidOperationException("The menu did not align labels to the right with a leftward arrow.");
            Point nextPosition = nextGroup.TranslatePoint(new Point(), root);
            var command = (MenuItem)group.Items[0];
            group.IsSubmenuOpen = true;
            root.UpdateLayout();
            if (!command.IsVisible || !ReferenceEquals(PresentationSource.FromVisual(command), source))
                throw new InvalidOperationException($"The submenu did not open inside the window. " +
                    $"Open={group.IsSubmenuOpen}, loaded={group.IsLoaded}, visible={command.IsVisible}, " +
                    $"inline={group.Template.FindName("submenu", group) is not null}.");
            var category = new Rect(group.TranslatePoint(new Point(), root), group.RenderSize);
            var row = new Rect(command.TranslatePoint(new Point(), root), command.RenderSize);
            if (row.Left < 0 || row.Right > category.Left ||
                nextPosition != nextGroup.TranslatePoint(new Point(), root))
                throw new InvalidOperationException("The submenu did not open beside its category within the window.");
            var rowCenter = command.TranslatePoint(new Point(command.ActualWidth / 2, command.ActualHeight / 2), window);
            if (MainWindow.Ancestor<MenuItem>(window.InputHitTest(rowCenter) as DependencyObject) != command)
                throw new InvalidOperationException("The displayed submenu command did not receive mouse hit testing.");
            if (group.FontFamily.Source != window.FontFamily.Source ||
                command.FontFamily.Source != window.FontFamily.Source)
                throw new InvalidOperationException(
                    $"The open menu uses {group.FontFamily.Source} and {command.FontFamily.Source} for its text.");
        }
        finally
        {
            if (menu.IsVisible) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            menu.Opacity = 1;
        }
    }

    private static void MainMenuAcceptsKeyboardCommands()
    {
        RequireDesktopInput();
        var window = new MainWindow((App)Application.Current);
        try
        {
            window.Show();
            window.Activate();
            var button = (Button)window.FindName("menuButton");
            var menu = (Menu)window.FindName("mainMenu");
            var file = (MenuItem)menu.Items[0];
            var edit = (MenuItem)menu.Items[1];
            button.Focus();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitForInput();
            if (!file.IsKeyboardFocused)
                throw new InvalidOperationException($"The application menu did not receive keyboard focus. " +
                    $"Active={window.IsActive}, menu={menu.IsVisible}, windowHandle={new WindowInteropHelper(window).Handle}, " +
                    $"foreground={VisualCheckForegroundWindow()}, focused={Keyboard.FocusedElement?.GetType().Name}.");
            Press(Key.Down);
            if (!edit.IsKeyboardFocused) throw new InvalidOperationException("Down did not select the next category.");
            Press(Key.Up);
            Press(Key.Left);
            var first = (MenuItem)file.Items[0];
            if (!file.IsSubmenuOpen || !first.IsKeyboardFocused)
                throw new InvalidOperationException("Left did not open the category and focus its first command.");
            Press(Key.Right);
            if (file.IsSubmenuOpen || !file.IsKeyboardFocused)
                throw new InvalidOperationException("Right did not return focus to the category.");
            Press(Key.Up);
            var tools = (MenuItem)menu.Items[^1];
            if (!tools.IsKeyboardFocused) throw new InvalidOperationException("Up did not wrap to the last category.");
            Press(Key.Enter);
            var vault = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("Vault")));
            if (!vault.IsKeyboardFocused) Press(Key.Down);
            if (!vault.IsKeyboardFocused) throw new InvalidOperationException("Down did not focus the vault submenu.");
            Press(Key.Left);
            var unlock = (MenuItem)vault.Items[0];
            if (!unlock.IsKeyboardFocused)
                throw new InvalidOperationException("The nested submenu did not receive keyboard focus.");
            if (unlock.TranslatePoint(new Point(unlock.ActualWidth, 0), window).X > vault.TranslatePoint(new Point(), window).X)
                throw new InvalidOperationException("The nested submenu did not continue the leftward cascade.");
            Press(Key.Left);
            if (!unlock.IsKeyboardFocused)
                throw new InvalidOperationException("Left moved away from a command without a submenu.");
            Press(Key.Escape);
            if (!vault.IsKeyboardFocused || vault.IsSubmenuOpen || !tools.IsSubmenuOpen)
                throw new InvalidOperationException("Escape did not close only the nested submenu.");
            Press(Key.Escape);
            if (!tools.IsKeyboardFocused || tools.IsSubmenuOpen || !menu.IsVisible)
                throw new InvalidOperationException("Escape did not return from the submenu to the main menu.");
            Press(Key.Escape);
            if (menu.IsVisible || !button.IsKeyboardFocused)
                throw new InvalidOperationException("Escape did not close the application menu and restore focus.");

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitForInput();
            var windowMenu = (MenuItem)menu.Items[3];
            windowMenu.Focus();
            Press(Key.Left);
            var tabGroups = windowMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("TabGroups")));
            tabGroups.Focus();
            Press(Key.Left);
            var split = (MenuItem)tabGroups.Items[0];
            if (!split.IsKeyboardFocused || !ReferenceEquals(split.Style, menu.Resources["ApplicationMenuItem"]) ||
                !ReferenceEquals(PresentationSource.FromVisual(split), PresentationSource.FromVisual(window)))
                throw new InvalidOperationException("A dynamically populated submenu did not open inside the window.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            for (int index = 0; index < 12; index++)
                file.Items.Add(new MenuItem { Header = $"Fixture command {index}" });
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            WaitForInput();
            Press(Key.Left);
            var flyout = (ApplicationSubmenu)file.Template.FindName("submenu", file);
            var scroll = FindControls<ScrollViewer>(flyout.Child!).Single();
            var last = (MenuItem)file.Items[^1];
            Press(Key.Up);
            if (scroll.ScrollableHeight <= 0 || scroll.VerticalOffset <= 0 || !last.IsKeyboardFocused)
                throw new InvalidOperationException("Keyboard navigation did not scroll the submenu in a small window.");
            if (!System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(last))
                throw new InvalidOperationException("Submenu rows could not receive mouse input over the window caption.");
            Press(Key.Escape);
            Press(Key.Up);
            Press(Key.Left);
            var settings = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("Settings")));
            settings.Focus();
            Press(Key.Enter);
            if (menu.IsVisible || ((TabControl)window.FindName("tabs")).SelectedContent is not FormView)
                throw new InvalidOperationException("Invoking a submenu command did not open its tab and close the menu.");
        }
        finally { window.Close(); }

        void Press(Key key)
        {
            var source = PresentationSource.FromVisual(window);
            var focused = (UIElement)Keyboard.FocusedElement;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            focused.RaiseEvent(args);
            if (!args.Handled)
            {
                args.RoutedEvent = Keyboard.KeyDownEvent;
                focused.RaiseEvent(args);
            }
            WaitForInput();
        }

        static void WaitForInput() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", ExactSpelling = true)]
    private static extern nint VisualCheckForegroundWindow();

    private static void MainMenuOpensNestedSubmenusWithMouse()
    {
        RequireDesktopInput();
        var window = new MainWindow((App)Application.Current)
        {
            Width = 880, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        if (!GetCursorPos(out var original)) throw new InvalidOperationException("The test could not read the mouse position.");
        try
        {
            window.Show();
            window.Activate();
            var button = (Button)window.FindName("menuButton");
            var menu = (Menu)window.FindName("mainMenu");
            var tools = (MenuItem)menu.Items[^1];
            var vault = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("Vault")));
            var unlock = (MenuItem)vault.Items[0];
            CheckCascade();
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            CheckCascade();

            void CheckCascade()
            {
                MoveMouseTo(button);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                WaitFor(() => IsDisplayed(tools), "the first level to open");
                MoveMouseTo(tools);
                WaitFor(() => IsDisplayed(vault), "the second level to open");
                ClickMouse();
                if (!menu.IsVisible || !tools.IsSubmenuOpen || !IsDisplayed(vault))
                    throw new InvalidOperationException("Clicking an expanded category closed the application menu or its submenu.");
                MoveMouseTo(vault);
                WaitFor(() => IsDisplayed(unlock), "the third level to open");
                ClickMouse();
                if (!menu.IsVisible || !tools.IsSubmenuOpen || !vault.IsSubmenuOpen || !IsDisplayed(unlock))
                    throw new InvalidOperationException("Clicking an expanded nested category closed a menu.");
                MoveMouseTo(unlock);
                if (!tools.IsSubmenuOpen || !vault.IsSubmenuOpen)
                    throw new InvalidOperationException("Moving the mouse onto the third level closed its parent menu.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                WaitFor(() => !menu.IsVisible && Mouse.Captured != menu, "the menu to close and release the mouse");
            }
        }
        finally
        {
            window.Close();
            SetCursorPos(original.X, original.Y);
        }

        bool IsDisplayed(MenuItem item) => item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0 &&
            ReferenceEquals(PresentationSource.FromVisual(item), PresentationSource.FromVisual(window));

        void MoveMouseTo(FrameworkElement item)
        {
            window.UpdateLayout();
            var center = new Point(item.ActualWidth / 2, item.ActualHeight / 2);
            var hit = window.InputHitTest(item.TranslatePoint(center, window)) as Visual;
            if (hit is null || (hit != item && !item.IsAncestorOf(hit)))
                throw new InvalidOperationException($"The center of {item} was covered by {hit}.");
            var point = item.PointToScreen(center);
            if (!SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                throw new InvalidOperationException("The test could not move the mouse.");
            WaitFor(() => Mouse.DirectlyOver is Visual over && (over == item || item.IsAncestorOf(over)), $"the mouse to reach {item}");
        }

        static void ClickMouse()
        {
            const uint leftDown = 0x0002;
            const uint leftUp = 0x0004;
            MouseEvent(leftDown, 0, 0, 0, 0);
            MouseEvent(leftUp, 0, 0, 0, 0);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        static void WaitFor(Func<bool> ready, string expected)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }
            if (!ready()) throw new InvalidOperationException(
                $"Timed out waiting for {expected} (capture: {Mouse.Captured?.GetType().Name}, hit: {Mouse.DirectlyOver?.GetType().Name}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition { internal int X, Y; }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPosition point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "mouse_event", ExactSpelling = true)]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);

    private sealed class DesktopInputUnavailableException(string message) : Exception(message);

    private static void RequireDesktopInput()
    {
        var control = new TextBox();
        var probe = new Window { Content = control, Width = 160, Height = 100, ShowInTaskbar = false };
        try
        {
            probe.Show();
            probe.Activate();
            control.Focus();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            bool captured = Mouse.Capture(control, CaptureMode.SubTree);
            nint foreground = VisualCheckForegroundWindow();
            if (captured) Mouse.Capture(null);
            if (foreground == 0 && !captured)
                throw new DesktopInputUnavailableException(
                    $"An independent WPF window could not acquire foreground input or mouse capture (focused={control.IsKeyboardFocused}).");
        }
        finally { probe.Close(); }
    }

    private static void CheckTabStripLayout(Window window, Button menu, TabItem item)
    {
        const int width = 1120, height = 620;
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        Point menuPosition = menu.TranslatePoint(new Point(), root);
        Point tabPosition = item.TranslatePoint(new Point(), root);
        if (menuPosition.X <= tabPosition.X + item.ActualWidth || menuPosition.Y > 12 ||
            menu.ActualWidth <= 0 || item.ActualHeight <= 0)
            throw new InvalidOperationException("The menu and window buttons did not fit next to the tabs.");
        var tabs = (TabControl)window.FindName("tabs");
        var strip = (Border)tabs.Template.FindName("tabStripRow", tabs);
        if (strip.BorderThickness.Bottom != 0)
            throw new InvalidOperationException("The tab strip drew a bottom border.");
        if (menu.Foreground is not SolidColorBrush foreground || window.Background is not SolidColorBrush background ||
            foreground.Color.R <= background.Color.R)
            throw new InvalidOperationException("The menu icon did not contrast with the dark tab strip.");
        var applicationMenu = (Menu)window.FindName("mainMenu");
        applicationMenu.ApplyTemplate();
        var menuPanel = (Border)applicationMenu.Template.FindName("mainMenuPanel", applicationMenu);
        if (menuPanel.Background is not SolidColorBrush menuBackground ||
            applicationMenu.Foreground is not SolidColorBrush menuForeground ||
            menuBackground.Color.R >= menuForeground.Color.R)
            throw new InvalidOperationException("The application menu did not use the dark theme.");

        if (Environment.GetEnvironmentVariable("TABTTY_CHROME_RENDER_ARTIFACT") is { } artifact)
            RenderLayoutPreview(root, window.Background, artifact);
    }

    private static void ManagementPagesShareResponsiveLayout(App app)
    {
#pragma warning disable WPF0001 // WPF Fluent theme integration.
        ThemeMode previous = app.ThemeMode;
        app.ThemeMode = ThemeMode.Dark;
        var window = new MainWindow(app);
        try
        {
            app.Profiles.Show(window);
            window.OpenForm("ManageVault", app.Vault.BuildManagement, pageTitleKey: "VaultEntries");
            app.Highlights.Show(window);
            OpenSettings(window);
            var tabs = (TabControl)window.FindName("tabs");
            var root = (FrameworkElement)window.Content;
            string[] titleKeys = ["ProfileCatalog", "VaultEntries", "HighlightSets", "Settings"];
            if (tabs.Items.Count != titleKeys.Length) throw new InvalidOperationException("A management page failed to open.");
            for (int index = 0; index < titleKeys.Length; index++)
            {
                tabs.SelectedIndex = index;
                var form = (FormView)tabs.SelectedContent;
                bool detailsPage = titleKeys[index] is "ProfileCatalog" or "VaultEntries";
                var fields = detailsPage
                    ? form.Children.OfType<StackPanel>().Single(panel => DockPanel.GetDock(panel) == Dock.Top)
                    : FormFields(form);
                var heading = FindControls<TextBlock>(form).SingleOrDefault(text =>
                    AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level1);
                if (heading?.Text != App.Text(titleKeys[index]))
                    throw new InvalidOperationException("The management page did not begin with its localized heading.");
                if (detailsPage)
                {
                    Layout(new Size(1000, 660));
                    Border? frame;
                    if (titleKeys[index] == "ProfileCatalog")
                    {
                        var groups = form.Children.OfType<TabControl>().Single();
                        var presenter = groups.Template.FindName("PART_SelectedContentHost", groups) as ContentPresenter;
                        frame = presenter is null ? null : VisualTreeHelper.GetParent(presenter) as Border;
                    }
                    else frame = form.Children.OfType<Border>().Single();
                    if (frame is null || frame.BorderThickness != new Thickness(1))
                        throw new InvalidOperationException("The details page does not have a shared rectangular content frame.");
                    double tallHeight = frame.ActualHeight;
                    Layout(new Size(520, 460));
                    if (frame.ActualHeight <= 0 || tallHeight - frame.ActualHeight < 150)
                        throw new InvalidOperationException("The details frame did not follow the available height.");
                }
                else if (titleKeys[index] != "Settings")
                {
                    var columns = fields.Children.OfType<Grid>().Single(grid => grid.ColumnDefinitions.Count == 2);
                    var details = (StackPanel)columns.Children[1];
                    Layout(new Size(1000, 660));
                    if (Grid.GetColumn(details) != 1 || Grid.GetRow(details) != 0)
                        throw new InvalidOperationException("The wide management layout did not show two columns.");
                    Layout(new Size(520, 660));
                    if (Grid.GetColumn(details) != 0 || Grid.GetRow(details) != 1)
                        throw new InvalidOperationException("The narrow management layout did not stack its columns.");
                }

                if (titleKeys[index] is "HighlightSets" or "Settings" &&
                    !form.Children.OfType<Grid>().Any(footer => DockPanel.GetDock(footer) == Dock.Bottom &&
                        FindControls<Button>(footer).Any(button => Equals(button.Content, App.Text("Save")))))
                    throw new InvalidOperationException("The editor save button was not fixed at the bottom of the page.");

                if (titleKeys[index] == "VaultEntries")
                {
                    var entries = FindControls<ListView>(form).Single();
                    string path = ProfileLocation.Normalize(app.VaultPath);
                    string expectedPath = string.Format(App.Text("VaultPathDisplay"), path);
                    if (FindControls<TabControl>(form).Any() ||
                        !FindControls<TextBlock>(form).Any(label => label.Text == expectedPath) ||
                        FindControls<TextBox>(form).Any(field => field.Text == path))
                        throw new InvalidOperationException("Vault management did not show the path as plain text under a single list.");
                    var actions = FindControls<Button>(form).Select(button => button.Content).OfType<string>();
                    var rowActions = entries.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header);
                    string[] expected = ["AddVaultPassword", "ImportPrivateKey", "BackupVault", "RestoreVault",
                        "ChangeMaster", "RecheckFilePublication", "ArchiveVault"];
                    if (System.Windows.Automation.AutomationProperties.GetName(entries) != App.Text("VaultEntries") ||
                        !expected.Select(App.Text).All(actions.Contains) ||
                        !new[] { "VaultEditEntry", "VaultDeleteEntry", "CopyMacroReference" }.Select(App.Text).All(rowActions.Contains))
                        throw new InvalidOperationException("Vault entries and their operations were not on the landing page.");
                }
                if (titleKeys[index] == "Settings")
                {
                    AssertNavigation(form, "SettingsLanguageAndTheme", "SettingsTerminalDefaults", "SettingsTabBehavior",
                        "SettingsLogNaming", "SettingsVault", "SettingsMaintenance");
                }
                if (Environment.GetEnvironmentVariable("TABTTY_MANAGEMENT_RENDER_DIR") is { } folder &&
                    titleKeys[index] is "VaultEntries" or "Settings")
                {
                    Layout(new Size(1000, 660));
                    RenderLayoutPreview(root, window.Background, Path.Combine(folder, titleKeys[index] + ".png"));
                }
            }

            var editor = new ProfileEditorWindow(app, "NewProfile", null, ProfileEditor.NewProfile("ssh"), creating: true);
            try
            {
                var form = (FormView)editor.Content;
                AssertNavigation(form, "ProfileConnectionGroup", "ProfileAuthenticationGroup",
                    "ProfileTerminalGroup", "ProfileLoggingGroup");
                if (Environment.GetEnvironmentVariable("TABTTY_MANAGEMENT_RENDER_DIR") is { } preview)
                {
                    form.Measure(new Size(940, 660));
                    form.Arrange(new Rect(0, 0, 940, 660));
                    form.UpdateLayout();
                    RenderLayoutPreview(form, editor.Background, Path.Combine(preview, "NewProfile.png"));
                }
            }
            finally { editor.Close(); }

            void Layout(Size size)
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
            }
        }
        finally
        {
            window.Close();
            app.ThemeMode = previous;
        }
#pragma warning restore WPF0001
    }

    private static void AssertNavigation(FormView form, params string[] titles)
    {
        var navigation = FindControls<ListBox>(form).Single();
        var layout = (Grid)LogicalTreeHelper.GetParent(navigation);
        var heading = layout.Children.OfType<TextBlock>().Single();
        var scroller = layout.Children.OfType<ScrollViewer>().Single();
        if (Grid.GetRow(heading) != 0 || Grid.GetColumnSpan(heading) != 2 ||
            Grid.GetRow(navigation) != 1 || Grid.GetColumn(navigation) != 0 ||
            Grid.GetRow(scroller) != 1 || Grid.GetColumn(scroller) != 1)
            throw new InvalidOperationException("The page title did not span the navigation and settings columns.");
        var pages = FormFields(form).Children.OfType<StackPanel>().ToArray();
        if (!navigation.Items.OfType<ListBoxItem>().Select(item => ((TextBlock)item.Content).Text)
            .SequenceEqual(titles.Select(App.Text)) || pages.Length != titles.Length)
            throw new InvalidOperationException("The settings categories did not match the available pages.");
        for (int index = 0; index < pages.Length; index++)
        {
            navigation.SelectedIndex = index;
            if (pages[index].Visibility != Visibility.Visible ||
                pages.Where((_, other) => other != index).Any(page => page.Visibility != Visibility.Collapsed))
                throw new InvalidOperationException("Selecting a settings category did not show its page.");
        }
        navigation.SelectedIndex = 0;
    }

    private static IEnumerable<T> FindControls<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (T descendant in FindControls<T>(child)) yield return descendant;
        }
    }

    private static StackPanel FormFields(FormView form) =>
        (StackPanel)FindControls<ScrollViewer>(form).Single(view => view.Content is StackPanel).Content;

    private static void OpenSettings(MainWindow window)
    {
        var tools = ((Menu)window.FindName("mainMenu")).Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, App.Text("ToolsMenu")));
        var settings = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("Settings")));
        settings.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, settings));
    }

    private static async Task SettingsSwitchLanguageAndThemeInOpenWindows(App app)
    {
        var window = new MainWindow(app);
        string originalLanguage = app.LanguageChoice;
        string originalTheme = app.ThemeChoice;
        try
        {
            OpenSettings(window);
            var tabs = (TabControl)window.FindName("tabs");
            var form = (FormView)tabs.SelectedContent;
            var language = FindControls<ComboBox>(form).Single(choice => choice.Items.Contains(App.Text("English")));
            var theme = FindControls<ComboBox>(form).Single(choice => choice.Items.Contains(App.Text("Dark")));
            var save = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("Save")));

            await Apply("Japanese", "Dark", "ja", "dark", "設定", "認証情報 vault", "vault の管理");
            await Apply("English", "Light", "en", "light", "Settings", "Credential vault", "Manage vault");
            bool systemJapanese = SystemLanguage.Resolve() == "ja";
            await Apply("System", "System", "system", "system", systemJapanese ? "設定" : "Settings",
                systemJapanese ? "認証情報 vault" : "Credential vault", systemJapanese ? "vault の管理" : "Manage vault");

            async Task Apply(string languageKey, string themeKey, string languageCode, string themeMode, string settingsLabel,
                string vaultLabel, string manageLabel)
            {
                language.SelectedItem = App.Text(languageKey);
                theme.SelectedItem = App.Text(themeKey);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, save));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (form.IsBusy) await Task.Delay(15, deadline.Token);
                if (app.LanguageChoice != languageCode || app.ThemeChoice != themeMode || tabs.SelectedContent != form)
                    throw new InvalidOperationException("Saving did not apply language and theme in the open settings tab.");

                var menu = (Menu)window.FindName("mainMenu");
                var tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, App.Text("ToolsMenu")));
                var vault = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, vaultLabel));
                if (!tools.Items.OfType<MenuItem>().Any(item => Equals(item.Header, settingsLabel)) ||
                    !vault.Items.OfType<MenuItem>().Any(item => Equals(item.Header, manageLabel)))
                    throw new InvalidOperationException("The open window did not switch its settings and vault menu labels.");
#pragma warning disable WPF0001 // .NET 10 Fluent theme integration.
                var expectedTheme = themeMode == "dark" ? ThemeMode.Dark : themeMode == "light" ? ThemeMode.Light : ThemeMode.System;
                if (app.ThemeMode != expectedTheme)
                    throw new InvalidOperationException("Saving did not update the application theme.");
#pragma warning restore WPF0001
            }
        }
        finally
        {
            await app.SaveSettingsAsync(originalLanguage, originalTheme, app.BellChoice, app.TerminalFontFamily, app.TerminalFontSize,
                app.RetainedRows, app.AutoCloseDisconnectedTabs, app.VaultPath, app.SettingsRevision);
            window.Close();
        }
    }

    private static void RenderLayoutPreview(FrameworkElement root, Brush background, string path)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var bounds = new Rect(root.RenderSize);
            context.DrawRectangle(background, null, bounds);
            context.DrawRectangle(new VisualBrush(root), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void CheckFixedWidthTabHeaders(Window window, TabItem shortTab, TabItem longTab)
    {
        var longHeader = (DockPanel)longTab.Header;
        var label = longHeader.Children.OfType<TextBlock>().Single();
        string original = label.Text;
        label.Text = "Production server — 東京リージョンの長い接続プロファイル名";
        try
        {
            var root = (FrameworkElement)window.Content;
            root.UpdateLayout();
            var untrimmed = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize };
            untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var close = longHeader.Children.OfType<Button>().Single();
            var shortClose = ((DockPanel)shortTab.Header).Children.OfType<Button>().Single();
            double closeStart = close.TranslatePoint(new Point(), longTab).X;
            if (Math.Abs(shortTab.ActualWidth - 240) > 0.5 || Math.Abs(longTab.ActualWidth - 240) > 0.5 ||
                label.TextTrimming != TextTrimming.CharacterEllipsis || label.ActualWidth <= 0 ||
                label.ActualWidth >= untrimmed.DesiredSize.Width ||
                label.TranslatePoint(new Point(label.ActualWidth, 0), longTab).X > closeStart ||
                Math.Abs(closeStart - shortClose.TranslatePoint(new Point(), shortTab).X) > 0.5 ||
                closeStart + close.ActualWidth > longTab.ActualWidth)
                throw new InvalidOperationException("The fixed-width headers did not constrain long text and align their close buttons.");

        }
        finally { label.Text = original; }
    }

    private static void CheckTabStripNavigation(Window window, TabControl tabs)
    {
        var root = (FrameworkElement)window.Content;
        Size wide = root.RenderSize;
        var strip = (ScrollViewer)tabs.Template.FindName("tabStripScroller", tabs);
        var listButton = (Button)tabs.Template.FindName("tabListButton", tabs);
        var firstTab = (TabItem)tabs.Items[0];
        if (listButton.TranslatePoint(new Point(listButton.ActualWidth, 0), root).X >
            firstTab.TranslatePoint(new Point(), root).X || !listButton.IsEnabled)
            throw new InvalidOperationException("The tab list button was not available at the start of the strip.");
        ExpectFades(left: false, right: false);
        try
        {
            Layout(new Size(window.MinWidth + 60, wide.Height));
            if (strip.ViewportWidth < firstTab.ActualWidth)
                throw new InvalidOperationException("The split controls and window buttons left no room for a full tab.");
            Layout(new Size(window.MinWidth, wide.Height));
            if (strip.ViewportWidth < firstTab.ActualWidth || strip.ScrollableWidth <= 0 ||
                strip.HorizontalScrollBarVisibility != ScrollBarVisibility.Hidden ||
                strip.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
                throw new InvalidOperationException("The narrow strip did not provide a complete tab and hidden scrolling.");
            ExpectFades(left: false, right: true);

            listButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, listButton));
            ContextMenu menu = listButton.ContextMenu;
            try
            {
                MenuItem[] entries = menu.Items.OfType<MenuItem>().ToArray();
                if (!menu.IsOpen || !entries.Select(item => item.Header).SequenceEqual(
                    new[] { App.Text("Settings"), App.Text("HighlightSets"), App.Text("ProfileCatalog") }) ||
                    !entries[0].IsChecked || entries.Skip(1).Any(item => item.IsChecked))
                    throw new InvalidOperationException("The tab list did not include every tab in order and mark the active tab.");
                entries[^1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, entries[^1]));
            }
            finally { menu.IsOpen = false; }
            if (tabs.SelectedIndex != 2) throw new InvalidOperationException("The tab list did not select the hidden tab.");
            ExpectFades(left: true, right: false);

            tabs.SelectedIndex = 0;
            ExpectFades(left: false, right: true);
            tabs.SelectedIndex = 1;
            ExpectFades(left: true, right: true);
        }
        finally
        {
            tabs.SelectedIndex = 0;
            Layout(wide);
        }
        ExpectFades(left: false, right: false);

        void Layout(Size size)
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
        }

        void ExpectFades(bool left, bool right)
        {
            root.UpdateLayout();
            var stops = (strip.OpacityMask as LinearGradientBrush)?.GradientStops;
            if ((stops?[0].Color.A == 0) != left || (stops?[^1].Color.A == 0) != right)
                throw new InvalidOperationException($"Unexpected tab strip fade at {strip.HorizontalOffset} of {strip.ScrollableWidth}.");
        }

    }

    private static void MessageBoxesUseTheApplicationThemeAndSafeDefault(App app)
    {
#pragma warning disable WPF0001 // WPF Fluent theme integration.
        ThemeMode previous = app.ThemeMode;
        try
        {
            (Color darkBackground, Color darkForeground) = Measure(ThemeMode.Dark);
            (Color lightBackground, Color lightForeground) = Measure(ThemeMode.Light);
            if (darkBackground.R >= darkForeground.R || lightBackground.R <= lightForeground.R ||
                darkBackground.R >= lightBackground.R)
                throw new InvalidOperationException("The confirmation window did not use the selected theme.");
        }
        finally { app.ThemeMode = previous; }

        (Color Background, Color Foreground) Measure(ThemeMode mode)
        {
            app.ThemeMode = mode;
            var dialog = ThemedMessageBox.Create(null, "Confirmation", "TabTty", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No, _ => { });
            try
            {
                var layout = (DockPanel)dialog.Content;
                var actions = (StackPanel)layout.Children[0];
                Button no = actions.Children.OfType<Button>().Single(button => Equals(button.Content, App.Text("No")));
                if (!no.IsDefault || !no.IsCancel || dialog.ThemeMode != mode)
                    throw new InvalidOperationException("The confirmation did not preserve its negative default.");
                return (((SolidColorBrush)dialog.Background).Color, ((SolidColorBrush)dialog.Foreground).Color);
            }
            finally { dialog.Close(); }
        }
#pragma warning restore WPF0001
    }

    private static async Task DisconnectedTabsRespectSettingsAndBulkClose(App app, string settingsPath)
    {
        RecordingDefaults originalLogDefaults = app.LogDefaults;
        var host = new MainWindow(app);
        var finalHost = new MainWindow(app) { ShowActivated = false, ShowInTaskbar = false };
        var terminals = new List<TerminalTab>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string profile = Path.Combine(Path.GetDirectoryName(settingsPath)!, "auto-close.ttty");
        try
        {
            await ConnectionProfile.CreateTelnet("Close test", "127.0.0.1", port).SaveNewAsync(profile, deadline.Token);
            var tabs = (TabControl)host.FindName("tabs");
            OpenSettings(host);
            var form = (FormView)tabs.SelectedContent;
            var autoClose = FindControls<CheckBox>(form).First();
            var languageChoice = FindControls<ComboBox>(form).Single(choice => choice.Items.Contains(App.Text("English")));
            if (autoClose.IsChecked != true || app.LanguageChoice != "system" || !Equals(languageChoice.SelectedItem, App.Text("System")))
                throw new InvalidOperationException("Settings did not default to system language and automatic closure.");

            using (TcpClient firstPeer = await Connect(host))
            {
                firstPeer.Client.Shutdown(SocketShutdown.Send);
                await terminals[^1].Completion.WaitAsync(deadline.Token);
                while (tabs.Items.Count != 1) await Task.Delay(15, deadline.Token);
                if (tabs.Items.Count != 1 || !terminals[^1].IsDisconnected)
                    throw new InvalidOperationException("Default settings did not close a disconnected tab.");
            }

            var navigation = FindControls<ListBox>(form).Single();
            var save = FindControls<Button>(form).Single(button => Equals(button.Content, App.Text("Save")));
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "SettingsLogNaming"));
            TextBox LogField(string key) => FindControls<TextBox>(form).Single(field =>
                AutomationProperties.GetLabeledBy(field) is Label { Content: TextBlock label } && label.Text == App.Text(key));
            var logDirectory = LogField("DefaultLogDirectory");
            var logTemplate = LogField("DefaultLogFilenameTemplate");
            var macroTemplate = LogField("DefaultMacroLogFilenameTemplate");
            if (new RecordingDefaults(logDirectory.Text, logTemplate.Text, macroTemplate.Text) != RecordingDefaults.BuiltIn)
                throw new InvalidOperationException("The separate log filename defaults were missing.");
            logDirectory.Text = "Archive";
            logTemplate.Text = "<cpname>_<uuid>";
            macroTemplate.Text = "<macroname>-<cpname>_<serial:1,3>";
            navigation.SelectedIndex = 2;
            autoClose.IsChecked = false;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, save));
            while (form.IsBusy) await Task.Delay(15, deadline.Token);
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(settingsPath)))
                if (app.AutoCloseDisconnectedTabs || saved.RootElement.GetProperty("autoCloseDisconnectedTabs").GetBoolean() ||
                    app.LogDefaults != new RecordingDefaults(logDirectory.Text, logTemplate.Text, macroTemplate.Text) ||
                    saved.RootElement.GetProperty("defaultLogDirectory").GetString() != "Archive" ||
                    saved.RootElement.GetProperty("defaultMacroLogFilenameTemplate").GetString() != macroTemplate.Text ||
                    saved.RootElement.GetProperty("language").GetString() != "system")
                    throw new InvalidOperationException("Saving settings did not preserve the disabled override and system language choice.");

            for (int index = 0; index < 2; index++)
            {
                using TcpClient peer = await Connect(host);
                CheckTabHeader((TabItem)tabs.SelectedItem, disconnected: false);
                peer.Client.Shutdown(SocketShutdown.Send);
                await terminals[^1].Completion.WaitAsync(deadline.Token);
                CheckTabHeader((TabItem)tabs.SelectedItem, disconnected: true);
            }
            TerminalTab[] disconnected = terminals.Skip(1).ToArray();
            if (tabs.Items.Count != 3 || disconnected.Any(terminal => !terminal.IsDisconnected))
                throw new InvalidOperationException("Disabling auto-close did not retain disconnected tabs.");

            using TcpClient activePeer = await Connect(host);
            TerminalTab active = terminals[^1];
            navigation.SelectedIndex = 2;
            autoClose.IsChecked = true;
            logTemplate.Text = "<cpname>_<datetime:yyyy-MM-dd_HH-mm-ss-ffffff>";
            navigation.SelectedIndex = 0;
            var themeChoice = FindControls<ComboBox>(form).Single(choice => choice.Items.Contains(App.Text("Dark")));
            themeChoice.SelectedItem = App.Text("Dark");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, save));
            while (form.IsBusy) await Task.Delay(15, deadline.Token);
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(settingsPath)))
                if (!app.AutoCloseDisconnectedTabs || !saved.RootElement.GetProperty("autoCloseDisconnectedTabs").GetBoolean() ||
                    app.LogDefaults.ProfileTemplate != logTemplate.Text ||
                    saved.RootElement.GetProperty("defaultLogFilenameTemplate").GetString() != logTemplate.Text ||
                    app.ThemeChoice != "dark" ||
                    tabs.Items.Count != 4 || form.HasUnsavedChanges)
                    throw new InvalidOperationException("Saving auto-close did not persist the preference or removed existing tabs.");

            var failed = new TerminalTab(Path.Combine(Path.GetDirectoryName(profile)!, "missing.ttty"));
            terminals.Add(failed);
            host.AddTerminalTab(failed);
            failed.Start();
            await failed.Completion.WaitAsync(deadline.Token);
            CheckTabHeader((TabItem)tabs.SelectedItem, disconnected: false, caption: "missing");

            using var source = new HwndSource(new HwndSourceParameters("TabTty context menu test")
            {
                WindowStyle = unchecked((int)0x80000000), Width = 1650, Height = 420,
            });
            var root = (FrameworkElement)host.Content;
            host.Content = null;
            source.RootVisual = root;
            source.AddHook(host.HandleWindowMessage);
            root.UpdateLayout();
            var strip = (Border)tabs.Template.FindName("tabStripRow", tabs);
            ContextMenu menu = strip.ContextMenu;
            menu.Opacity = 0;
            var command = (MenuItem)menu.Items[0];
            Point rightClick = strip.PointToScreen(new Point(strip.ActualWidth - 20, 20));
            int position = ((int)rightClick.X & 0xffff) | ((int)rightClick.Y << 16);
            if (!PostMessage(source.Handle, 0x00a5, 2, position)) // WM_NCRBUTTONUP, HTCAPTION
                throw new InvalidOperationException("The tab bar right-click could not be posted.");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            if (!menu.IsOpen || !command.IsEnabled || !Equals(command.Header, App.Text("CloseDisconnectedTabs")))
                throw new InvalidOperationException("The tab bar right-click did not offer to close disconnected tabs.");
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, command));
            menu.IsOpen = false;
            while (tabs.Items.Count > 3) await Task.Delay(15, deadline.Token);
            if (!tabs.Items.OfType<TabItem>().Select(item => item.Content).SequenceEqual(new object[] { form, active, failed }) ||
                !active.IsRunning || failed.IsDisconnected)
                throw new InvalidOperationException("Closing disconnected tabs did not preserve the editor, active session, and failed attempt.");

            root.UpdateLayout();
            var header = (DockPanel)((TabItem)tabs.SelectedItem).Header;
            var headerHit = root.InputHitTest(header.TranslatePoint(new Point(30, 10), root));
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Apps)
            {
                RoutedEvent = Keyboard.KeyUpEvent,
                Source = headerHit,
            });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
            if (header.ContextMenu is not { IsOpen: true } terminalMenu ||
                !terminalMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, App.Text("BroadcastInput"))))
                throw new InvalidOperationException("The terminal header did not open its broadcast context menu.");
            terminalMenu.IsOpen = false;

            activePeer.Client.Shutdown(SocketShutdown.Send);
            await active.Completion.WaitAsync(deadline.Token);
            while (tabs.Items.Count != 2) await Task.Delay(15, deadline.Token);
            if (tabs.Items.Count != 2 || !active.IsDisconnected)
                throw new InvalidOperationException("Auto-close did not remove the tab after disconnect.");

            finalHost.Show();
            using TcpClient lastPeer = await Connect(finalHost);
            lastPeer.Client.Shutdown(SocketShutdown.Send);
            await terminals[^1].Completion.WaitAsync(deadline.Token);
            var finalTabs = (TabControl)finalHost.FindName("tabs");
            var welcome = (FrameworkElement)finalHost.FindName("welcomePanel");
            while (finalTabs.HasItems) await Task.Delay(15, deadline.Token);
            if (!finalHost.IsVisible || finalHost.IsClosing || !welcome.IsVisible)
                throw new InvalidOperationException("Auto-close did not return the window to its welcome screen.");

            OpenSettings(finalHost);
            if (finalTabs.SelectedItem is not TabItem settingsTab || welcome.IsVisible)
                throw new InvalidOperationException("The empty window could not open a new tab.");
            var closeSettings = ((DockPanel)settingsTab.Header).Children.OfType<Button>().Single();
            closeSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (finalTabs.HasItems || !finalHost.IsVisible || finalHost.IsClosing || !welcome.IsVisible)
                throw new InvalidOperationException("Closing the final settings tab did not keep the window open.");

            finalHost.Close();
            if (finalHost.IsVisible)
                throw new InvalidOperationException("An explicit window close did not close the empty window.");
        }
        finally
        {
            foreach (TerminalTab terminal in terminals) terminal.Disconnect();
            await Task.WhenAll(terminals.Select(terminal => terminal.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
            host.Close();
            if (!finalHost.IsClosing) finalHost.Close();
            if (app.LogDefaults != originalLogDefaults)
                await app.SaveSettingsAsync(app.LanguageChoice, app.ThemeChoice, app.BellChoice, app.TerminalFontFamily,
                    app.TerminalFontSize, app.RetainedRows, app.AutoCloseDisconnectedTabs, app.VaultPath, app.SettingsRevision,
                    logDefaults: originalLogDefaults);
        }

        static void CheckTabHeader(TabItem tab, bool disconnected, string caption = "Close test")
        {
            var header = (DockPanel)tab.Header;
            var icon = header.Children.OfType<System.Windows.Shapes.Path>().Single(path => path.Name == "DisconnectedIndicator");
            var label = header.Children.OfType<TextBlock>().Single();
            string description = disconnected ? $"({App.Text("Disconnected")}) {caption}" : caption;
            if (icon.Visibility != (disconnected ? Visibility.Visible : Visibility.Collapsed) ||
                label.Text != caption || !Equals(tab.ToolTip, description) ||
                AutomationProperties.GetName(tab) != description)
                throw new InvalidOperationException("The tab header did not match the connection state.");
        }

        async Task<TcpClient> Connect(MainWindow window)
        {
            var terminal = new TerminalTab(profile);
            terminals.Add(terminal);
            window.AddTerminalTab(terminal);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.Connected += _ => connected.TrySetResult();
            terminal.Start();
            TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            try
            {
                await connected.Task.WaitAsync(deadline.Token);
                return peer;
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }
    }

    private static async Task TerminalReceivesWhilePresentationIsPaused(bool verifyNativeResidency)
    {
        long initialModels = Interlocked.Read(ref NativeTerminalCoreDiagnostics.CreatedModels);
        long initialFedBytes = Interlocked.Read(ref NativeTerminalCoreDiagnostics.FedBytes);
        string directory = Directory.CreateTempSubdirectory("TabTty-presentation-test-").FullName;
        string path = Path.Combine(directory, "recording.ttty");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var terminal = new TerminalTab(path);
        var sessionTab = new TabItem { Content = terminal };
        var otherTab = new TabItem { Content = new TextBlock() };
        var tabs = new TabControl();
        tabs.Items.Add(sessionTab);
        tabs.Items.Add(otherTab);
        var host = new Window { Content = tabs, Width = 640, Height = 360, ShowActivated = false, ShowInTaskbar = false };
        var surface = (TerminalSurface)((ScrollViewer)((DockPanel)terminal.Content).Children[^1]).Content;
        using var received = new MemoryStream();

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await ConnectionProfile.CreateTelnet("Presentation test", "127.0.0.1", port, recordRaw: true,
                    recordText: true, recordTerminal: verifyNativeResidency)
                .SaveNewAsync(path, deadline.Token);
            host.Show();
            terminal.Start();
            using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            NetworkStream wire = peer.GetStream();
            await wire.ReadExactlyAsync(new byte[15], deadline.Token);
            string textLog = Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories).Single();
            string rawLog = Directory.EnumerateFiles(directory, "*.raw.tttylog", SearchOption.AllDirectories).Single();

            if (verifyNativeResidency)
            {
                await WaitUntilAsync(() => terminal.InputSession is not null);
                var connected = terminal.InputSession ?? throw new InvalidOperationException("The tab has no connected input session.");
                await connected.SetLocalEchoAsync("off", deadline.Token);
                try
                {
                    await connected.SendTextAsync("unsent\ud800", deadline.Token);
                    throw new InvalidOperationException("Malformed Unicode reached the terminal transport.");
                }
                catch (EncoderFallbackException) { }
                await connected.SendTextAsync("入力", deadline.Token);
                byte[] sent = new byte["入力"u8.Length];
                await wire.ReadExactlyAsync(sent, deadline.Token);
                if (!sent.AsSpan().SequenceEqual("入力"u8))
                    throw new InvalidOperationException("The peer did not receive the expected UTF-8 input bytes.");
                await connected.SetLocalEchoAsync("followRemote", deadline.Token);
                var composition = new TextComposition(InputManager.Current, surface, "変換", TextCompositionAutoComplete.Off);
                surface.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                surface.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                byte[] composed = new byte["変換"u8.Length];
                await wire.ReadExactlyAsync(composed, deadline.Token);
                if (!composed.AsSpan().SequenceEqual("変換"u8))
                    throw new InvalidOperationException("The WPF composition did not send the committed UTF-8 bytes.");
                await WaitForLineAsync("変換");
            }

            string visibleLine = verifyNativeResidency ? "visible 表示" : "visible";
            string editedLinePrefix = "";
            if (verifyNativeResidency)
            {
                await SendOutputAsync("\rABCD\r\u001b[2G\u001b[2P\u001b[2@Z\r\n");
                await WaitForLineAsync("AZ D");
                editedLinePrefix = "AZ D\r\n";
            }
            await SendOutputAsync((verifyNativeResidency ? "\u001b]0;Presentation test\u0007\u001b[?2004h" : "") + visibleLine + "\r\n");
            await WaitForLineAsync(visibleLine);

            SystemCommands.MinimizeWindow(host);
            await WaitUntilAsync(() => host.WindowState == WindowState.Minimized);
            await CheckPausedAsync("minimizex\bd\r\n", editedLinePrefix + visibleLine + "\r\nminimized\r\n");
            SystemCommands.RestoreWindow(host);
            await WaitForLineAsync("minimized");

            tabs.SelectedItem = otherTab;
            host.UpdateLayout();
            string committedText = editedLinePrefix + visibleLine + "\r\nminimized\r\nbackground\r\n";
            await CheckPausedAsync("\u001b[32mbackground\u001b[0m\r\n", committedText);
            tabs.SelectedItem = sessionTab;
            await WaitForLineAsync("background");

            SystemCommands.MinimizeWindow(host);
            await WaitUntilAsync(() => host.WindowState == WindowState.Minimized);
            await CheckPausedAsync("final line", committedText);
            string finalText = committedText + "final line";
            if (verifyNativeResidency)
            {
                finalText += "ALT\r\n";
                await CheckPausedAsync("\u001b[?1049hALT\r\n\u001b[?1049l", finalText);
            }
            peer.Client.Shutdown(SocketShutdown.Send);
            await terminal.Completion.WaitAsync(deadline.Token);
            Equal(await File.ReadAllTextAsync(textLog, deadline.Token), finalText);
            string log = Directory.EnumerateFiles(directory, "*.raw.tttylog", SearchOption.AllDirectories).Single();
            byte[] recorded = await File.ReadAllBytesAsync(log, deadline.Token);
            if (!recorded.AsSpan().SequenceEqual(received.ToArray()))
                throw new InvalidOperationException("The raw log did not retain all received bytes.");

            if (verifyNativeResidency)
            {
                string terminalLog = Directory.EnumerateFiles(directory, "*.term.tttylog", SearchOption.AllDirectories).Single();
                string operations = await File.ReadAllTextAsync(terminalLog, deadline.Token);
                if (!operations.StartsWith("変換\rABCD", StringComparison.Ordinal) ||
                    operations.Contains("入力", StringComparison.Ordinal) ||
                    operations.Contains("\u001b[5n", StringComparison.Ordinal))
                    throw new InvalidOperationException("The Terminal log did not preserve only committed local echo and display operations.");
                if (Interlocked.Read(ref NativeTerminalCoreDiagnostics.CreatedModels) - initialModels < 2 ||
                    Interlocked.Read(ref NativeTerminalCoreDiagnostics.FedBytes) <= initialFedBytes)
                    throw new InvalidOperationException("The naturally sized terminal did not stay native through startup, minimize, restore, and tab switching.");
            }

            tabs.Items.Remove(sessionTab);
            sessionTab.Content = null;
            var destination = new Window { Content = terminal, Width = 640, Height = 360, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                destination.Show();
                await WaitForLineAsync("final line");
                foreach (string line in new[] { visibleLine, "minimized", "background", "final line" })
                    if (surface.Snapshot?.Contains(line) != true)
                        throw new InvalidOperationException("The restored terminal lost retained output: " + line);
            }
            finally { destination.Close(); }

            async Task SendOutputAsync(string output)
            {
                byte[] payload = Encoding.UTF8.GetBytes(output + "\u001b[5n");
                received.Write(payload);
                await wire.WriteAsync(payload, deadline.Token);
                var reply = new byte[4];
                await wire.ReadExactlyAsync(reply, deadline.Token);
                Equal(Encoding.ASCII.GetString(reply), "\u001b[0n");
                if (verifyNativeResidency)
                {
                    await WaitUntilAsync(() =>
                    {
                        using var stream = new FileStream(rawLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        return stream.Length == received.Length;
                    });
                }
            }

            async Task CheckPausedAsync(string output, string expectedText)
            {
                TerminalSnapshot? before = surface.Snapshot;
                await SendOutputAsync(output);
                await Task.Delay(250, deadline.Token);
                while (true)
                {
                    using var reader = new StreamReader(new FileStream(textLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    string actual = await reader.ReadToEndAsync(deadline.Token);
                    if (actual == expectedText) break;
                    if (!expectedText.StartsWith(actual, StringComparison.Ordinal)) Equal(actual, expectedText);
                    await Task.Delay(50, deadline.Token);
                }
                if (!ReferenceEquals(before, surface.Snapshot))
                    throw new InvalidOperationException("The hidden terminal display was updated while receiving output.");
            }

            async Task WaitForLineAsync(string line)
            {
                try { await WaitUntilAsync(() => surface.Snapshot?.Contains(line) == true); }
                catch (OperationCanceledException exception)
                {
                    string rows = string.Join(" | ", surface.Snapshot?.Take(6) ?? []);
                    throw new InvalidOperationException($"Expected terminal row '{line}'. Visible={surface.IsPresentationActive}, status={terminal.ConnectionStatus}, rows={rows}", exception);
                }
            }

            async Task WaitUntilAsync(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(20, deadline.Token);
            }
        }
        finally
        {
            terminal.Disconnect();
            await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            host.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task TerminalSendsSpacesAndSwitchesTabsAfterDisconnect()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "input-test-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        listener.Start();
        string path = Path.Combine(directory, "input.ttty");
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await ConnectionProfile.CreateTelnet("Input test", "127.0.0.1", port).SaveNewAsync(path, deadline.Token);
            var tab = new TerminalTab(path);
            Equal(tab.TabCaption, "input");
            string caption = tab.TabCaption;
            tab.CaptionChanged += () => caption = tab.TabCaption;
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.Connected += _ => connected.TrySetResult();
            tab.Start();
            try
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
                NetworkStream wire = peer.GetStream();
                await wire.ReadExactlyAsync(new byte[15], deadline.Token);
                await connected.Task.WaitAsync(deadline.Token);
                Equal(caption, "Input test");

                var panel = (System.Windows.Controls.DockPanel)tab.Content;
                var scroller = (System.Windows.Controls.ScrollViewer)panel.Children[^1];
                var surface = (TerminalSurface)scroller.Content;
                using var window = new HwndSource(new HwndSourceParameters("TabTty keyboard test")
                {
                    WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
                    ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW
                    PositionX = -32000, PositionY = -32000, Width = 640, Height = 360,
                });
                window.RootVisual = tab;
                tab.UpdateLayout();
                var input = (TextBox)VisualTreeHelper.GetChild(surface, 0);
                InputMethod.SetIsInputMethodEnabled(input, false);
                if (!surface.Focus() || !input.IsKeyboardFocused)
                    throw new InvalidOperationException("The terminal input control did not receive keyboard focus.");

                foreach (char letter in "sudo") await PostInput(0x0102, letter, 1); // WM_CHAR
                await PostKey(0x20, 0x39); // VK_SPACE
                foreach (char letter in "-i") await PostInput(0x0102, letter, 1);
                await PostKey(0x0D, 0x1C); // VK_RETURN

                var command = new List<byte>();
                var next = new byte[1];
                while (command.Count < 20)
                {
                    await wire.ReadExactlyAsync(next, deadline.Token);
                    if (next[0] == 13) break;
                    command.Add(next[0]);
                }
                Equal(Encoding.ASCII.GetString(command.ToArray()), "sudo -i");
                peer.Client.Shutdown(SocketShutdown.Send);
                await tab.Completion.WaitAsync(deadline.Token);
                Equal(caption, "Input test");
                await CheckTabSwitchingAfterDisconnect();

                async Task CheckTabSwitchingAfterDisconnect()
                {
                    var host = new MainWindow((App)Application.Current);
                    try
                    {
                        host.OpenForm("Settings", _ => { });
                        host.OpenForm("ProfileCatalog", _ => { });
                        var tabs = (TabControl)host.FindName("tabs");
                        var disconnected = (TabItem)tabs.SelectedItem;
                        window.RootVisual = null;
                        disconnected.Content = tab;
                        var root = (FrameworkElement)host.Content;
                        host.Content = null;
                        window.SizeToContent = SizeToContent.WidthAndHeight;
                        root.Width = 1000;
                        root.Height = 360;
                        window.RootVisual = root;
                        window.AddHook(host.HandleWindowMessage);
                        root.UpdateLayout();
                        var other = (TabItem)tabs.Items[0];
                        var headerPoint = ((DockPanel)other.Header).TranslatePoint(new Point(36, 12), root);
                        if (root.InputHitTest(headerPoint) is not UIElement hit ||
                            !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(hit))
                            throw new InvalidOperationException("Windows treats the tab header as a draggable caption after disconnect.");
                        if (System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome((FormView)other.Content))
                            throw new InvalidOperationException("Tab content inherited the header's chrome hit testing.");
                        if (!surface.Focus() || !input.IsKeyboardFocused)
                            throw new InvalidOperationException("The disconnected terminal did not receive focus before the click.");
                        hit.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseDownEvent,
                        });
                        if (tabs.SelectedItem != other)
                            throw new InvalidOperationException("Clicking another tab after disconnect did not select it.");
                        tabs.SelectedItem = disconnected;
                        root.UpdateLayout();
                        var add = (Button)tabs.Template.FindName("newTabButton", tabs);
                        var menu = (Button)host.FindName("menuButton");
                        double afterAdd = add.TranslatePoint(new Point(add.ActualWidth, 0), root).X;
                        double beforeMenu = menu.TranslatePoint(new Point(), root).X;
                        await WheelAt(disconnected.TranslatePoint(new Point(40, 16), root), -120, expected: 0);
                        var gap = new Point((afterAdd + beforeMenu) / 2, 21);
                        await WheelAt(gap, 120, expected: 1);
                        await WheelAt(new Point(80, 200), -120, expected: 1);
                        await WheelAt(gap, -60, expected: 1);
                        await WheelAt(gap, -60, expected: 0);

                        async Task WheelAt(Point position, int delta, int expected)
                        {
                            Point screen = root.PointToScreen(position);
                            await PostInput(0x020A, delta << 16, PackPoint(screen)); // WM_MOUSEWHEEL
                            if (tabs.SelectedIndex != expected)
                                throw new InvalidOperationException($"Native wheel at {position} after disconnect selected tab {tabs.SelectedIndex}, expected {expected}.");
                        }
                    }
                    finally
                    {
                        window.RemoveHook(host.HandleWindowMessage);
                        window.RootVisual = null;
                        host.Close();
                    }

                    static int PackPoint(Point point) => ((int)point.X & 0xFFFF) | ((int)point.Y << 16);
                }

                async Task PostKey(int key, int scanCode)
                {
                    int flags = scanCode << 16 | 1;
                    await PostInput(0x0100, key, flags); // WM_KEYDOWN
                    await PostInput(0x0101, key, flags | unchecked((int)0xC0000000)); // WM_KEYUP
                }

                async Task PostInput(uint message, int key, int flags)
                {
                    if (!PostMessage(window.Handle, message, key, flags))
                        throw new InvalidOperationException("Could not post keyboard input to the test window.");
                    await tab.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
                }
            }
            finally
            {
                tab.Disconnect();
                await tab.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private static async Task VaultUnlockWaitersShareOneInteraction(string directory)
    {
        int adoptions = 0;
        var interaction = new VaultUnlockInteraction(adopt: _ => adoptions++);
        var pending = new TaskCompletionSource<CredentialVault?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var detached = new CancellationTokenSource();
        Task<CredentialVault?> first = interaction.WaitAsync(() => pending.Task);
        Task<CredentialVault?> second = interaction.WaitAsync(UnexpectedPrompt);
        Task<CredentialVault?> third = interaction.WaitAsync(UnexpectedPrompt, detached.Token);
        detached.Cancel();
        try { await third; throw new InvalidOperationException("The detached waiter did not cancel."); }
        catch (OperationCanceledException) { }
        pending.SetException(new IOException("unlock failed"));
        foreach (Task<CredentialVault?> waiter in new[] { first, second })
        {
            try { await waiter; throw new InvalidOperationException("The shared unlock failure was lost."); }
            catch (IOException exception) when (exception.Message == "unlock failed") { }
        }
        int prompts = 0;
        if (await interaction.WaitAsync(() =>
            {
                prompts++;
                return Task.FromResult<CredentialVault?>(null);
            }) is not null || prompts != 1)
            throw new InvalidOperationException("A subsequent attempt did not start a new interaction.");

        var verifying = new TaskCompletionSource<CredentialVault?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var original = interaction.Admit(null, (_, _) => verifying.Task);
        var admitted = interaction.Status;
        try
        {
            await original.ObserveAsync(new CancellationToken(canceled: true));
            throw new InvalidOperationException("The detached observation did not cancel.");
        }
        catch (OperationCanceledException) { }
        if (interaction.Status != admitted)
            throw new InvalidOperationException("Cancelling observation changed the admitted unlock operation.");
        using var joinedObserver = interaction.Admit(null, (_, _) => UnexpectedPrompt());
        verifying.SetResult(null);
        await Task.WhenAll(original.ObserveAsync(), joinedObserver.ObserveAsync());
        if (interaction.IsPending) throw new InvalidOperationException("Completed consumers retained their admission.");

        using var last = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var enteredCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<CredentialVault?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CredentialVault?> retiring = interaction.WaitAsync(null, async (_, workerLifetime) =>
        {
            using var registration = workerLifetime.Register(() => enteredCancellation.TrySetResult());
            return await late.Task;
        }, last.Token);
        Guid retiredId = interaction.Status.OperationId!.Value;
        last.Cancel();
        try
        {
            await retiring.WaitAsync(deadline.Token);
            throw new InvalidOperationException("The last consumer retained its observation after Stop.");
        }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
        await enteredCancellation.Task.WaitAsync(deadline.Token);
        var replacement = new TaskCompletionSource<CredentialVault?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CredentialVault?> next = interaction.WaitAsync(() =>
        {
            replacementStarted.SetResult();
            return replacement.Task;
        }, deadline.Token);
        if (replacementStarted.Task.IsCompleted || interaction.Status is not { Retiring: true } ||
            interaction.Status.OperationId != retiredId)
            throw new InvalidOperationException("An abandoned native result released its worker before actual completion.");
        string retiredPath = Path.Combine(directory, "retired-unlock.tttykey");
        const string retiredMaster = "fixture master for retired generation";
        using var discarded = await CredentialVault.CreateAsync(retiredPath, retiredMaster.AsMemory(), deadline.Token);
        late.SetResult(discarded);
        await replacementStarted.Task.WaitAsync(deadline.Token);
        if (interaction.Status.OperationId == retiredId || next.IsCompleted || adoptions != 0)
            throw new InvalidOperationException("A late unlock result was delivered to a replacement attempt.");
        try
        {
            await discarded.ListEntriesAsync(deadline.Token);
            throw new InvalidOperationException("A retired native result kept its unlocked vault.");
        }
        catch (ObjectDisposedException) { }
        using var fresh = await CredentialVault.UnlockAsync(retiredPath, retiredMaster.AsMemory(), deadline.Token);
        replacement.SetResult(fresh);
        if (!ReferenceEquals(await next.WaitAsync(deadline.Token), fresh) || adoptions != 1)
            throw new InvalidOperationException("The replacement unlock was not adopted exactly once.");

        static Task<CredentialVault?> UnexpectedPrompt() =>
            throw new InvalidOperationException("Concurrent unlock opened another prompt.");
    }

    private static void MissingTerminalFontUsesAnInstalledFixedPitchFallback()
    {
        var surface = new TerminalSurface();
        surface.ConfigureFont("__TabTty_test_missing_font__", 12);
        if (surface.AutomationFontFamily == "__TabTty_test_missing_font__" || !surface.UsesFontFallback)
            throw new InvalidOperationException("The terminal did not resolve and report its missing primary font.");
    }

    private static void ProfileLocationUsesTheStoredSpelling()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MixedCase-" + Guid.NewGuid().ToString("N") + ".ttty");
        try
        {
            File.WriteAllText(path, "{}");
            string alternate = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path).ToLowerInvariant());
            Equal(ProfileLocation.Normalize(alternate), ProfileLocation.Normalize(path));
            Equal(Path.GetFileName(ProfileLocation.Normalize(path)), Path.GetFileName(path));
        }
        finally { File.Delete(path); }
    }

    private static void StoreRecoveryPreservesTheOriginalAndValidatesTheBackup()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "store-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "state.json");
            string backup = target + ".bak";
            File.WriteAllText(target, "broken original");
            File.WriteAllText(backup, "{\"schemaVersion\":1}");
            JsonStoreRecovery.Restore(target, backup, path =>
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                if (parsed.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                    throw new InvalidDataException();
            });
            Equal(File.ReadAllText(target), "{\"schemaVersion\":1}");
            Equal(File.ReadAllText(Directory.GetFiles(directory, "*.recovery-*").Single()), "broken original");
            File.WriteAllText(backup, "invalid backup");
            try
            {
                JsonStoreRecovery.Restore(target, backup, path =>
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                });
                throw new InvalidOperationException("An invalid recovery backup was accepted.");
            }
            catch (System.Text.Json.JsonException) { }
            Equal(File.ReadAllText(target), "{\"schemaVersion\":1}");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WindowPlacementKeepsNormalBoundsInsideChangedWorkAreas()
    {
        Rect small = WindowPlacement.Constrain(new Rect(-1500, -900, 1200, 900), new Rect(100, 20, 400, 260));
        if (small != new Rect(100, 20, 400, 260))
            throw new InvalidOperationException("Window placement did not fit the smaller monitor.");
        Rect normal = WindowPlacement.Constrain(new Rect(5000, 6000, 1000, 700), new Rect(-1920, 0, 1920, 1040));
        if (normal != new Rect(-1000, 340, 1000, 700))
            throw new InvalidOperationException("Window placement did not clamp bounds to the target monitor.");
    }

    private static void ProfileEditorKeepsUnchangedOptionsAndEditsTypedFields(App app)
    {
        var original = ConnectionProfile.CreateTelnet("Test", "localhost", receiveEncoding: "cp932",
            transmitEncoding: "euc-jp", recordText: true);
        using var parsed = System.Text.Json.JsonDocument.Parse(original.ToEditorJson());
        var form = new FormView();
        var editor = new ProfileEditor(form, original, app);
        var navigation = FindControls<ListBox>(form).Single();
        var authenticationMenu = navigation.Items.OfType<ListBoxItem>()
            .Single(item => Equals(item.Tag, "ProfileAuthenticationGroup"));
        if (authenticationMenu.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Telnet showed SSH authentication in its navigation.");
        ConnectionProfile unchanged = editor.GetDraft();
        Equal(unchanged.ToEditorJson(), original.ToEditorJson());
        ((System.Windows.Controls.TextBox)form.FindName("WaitSeconds")).Text = "45";
        ((System.Windows.Controls.TextBox)form.FindName("LogDirectory")).Text = "SessionLogs";
        var telnet = (StackPanel)form.FindName("telnetFields");
        ((ComboBox)telnet.FindName("EnterSequence")).SelectedValue = "lf";
        ConnectionProfile changed = editor.GetDraft();
        if (changed.Id != original.Id || changed.WaitSeconds != 45 || changed.LogDirectory != "SessionLogs" ||
            changed.Enter != "lf" || changed.TransmitEncoding != "euc-jp")
            throw new InvalidOperationException("The native editor did not preserve and update typed profile fields.");

        ((RadioButton)form.FindName("ProtocolSSH")).IsChecked = true;
        var ssh = (StackPanel)form.FindName("sshFields");
        ((TextBox)ssh.FindName("Host")).Text = "example.test";
        if (authenticationMenu.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Selecting SSH did not make its authentication page available.");
        navigation.SelectedItem = authenticationMenu;
        var authentication = (StackPanel)form.FindName("sshAuthenticationFields");
        ((TextBox)authentication.FindName("SshUsername")).Text = "operator";
        ConnectionProfile sshDraft = editor.GetDraft();
        if (ssh.Visibility != Visibility.Visible || telnet.Visibility != Visibility.Collapsed ||
            sshDraft.Type != "ssh" || sshDraft.SshUsername != "operator" || sshDraft.Port != 22 || sshDraft.Id != original.Id ||
            sshDraft.WaitSeconds != 45 || sshDraft.LogDirectory != "SessionLogs")
            throw new InvalidOperationException("Switching to SSH did not preserve common settings or use SSH defaults.");
        ((RadioButton)form.FindName("ProtocolCOM")).IsChecked = true;
        var serial = (StackPanel)form.FindName("serialFields");
        ((ComboBox)serial.FindName("ComPort")).Text = "COM7";
        ConnectionProfile serialDraft = editor.GetDraft();
        if (serialDraft.Type != "serial" || serialDraft.Serial.PortName != "COM7" || serialDraft.Serial.BaudRate != 9600 ||
            authenticationMenu.Visibility != Visibility.Collapsed || navigation.SelectedIndex != 0)
            throw new InvalidOperationException("Switching to COM did not use the serial fields.");
        ((RadioButton)form.FindName("ProtocolTelnet")).IsChecked = true;
        if (editor.GetDraft().Enter != "lf")
            throw new InvalidOperationException("Switching back to Telnet lost the unsaved Telnet fields.");
    }

    private static void TextProviderKeepsLogicalTextAndSurvivingRanges()
    {
        var terminal = new TerminalOutput(columns: 6, retainedRowLimit: 100, screenRows: 3);
        var surface = new TerminalSurface();
        terminal.Receive(TerminalReceiveSource.Data, "ABCDEFGHIJKL\r\nZ"u8);
        surface.SetSnapshot(terminal.GetRows());
        var peer = (TerminalAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(surface);
        ITextProvider provider = peer;
        ITextRangeProvider all = provider.DocumentRange;
        Equal(all.GetText(-1), "ABCDEFGHIJKL\nZ");
        Equal(all.GetText(5), "ABCDE");
        ITextRangeProvider match = all.FindText("GHI", backward: false, ignoreCase: false)!;
        Equal(match.GetText(-1), "GHI");
        terminal.Resize(3, 3);
        var resized = terminal.GetRows();
        surface.SetSnapshot(resized);
        Equal(match.GetText(-1), "GHI");
        terminal.Receive(TerminalReceiveSource.Data, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\r\nnext", 150))));
        var retired = terminal.GetRows();
        surface.SetSnapshot(retired);
        Equal(match.GetText(-1), "");
        if (!provider.DocumentRange.GetText(-1).EndsWith("next", StringComparison.Ordinal))
            throw new InvalidOperationException("The UIA provider did not expose the complete retained range.");
    }

    private static void TextProviderMovesByPinnedUnicodeCharacters()
    {
        var terminal = new TerminalOutput();
        var surface = new TerminalSurface();
        terminal.Receive(TerminalReceiveSource.Data, "A👩‍💻B"u8);
        surface.SetSnapshot(terminal.GetRows());
        ITextProvider provider = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(surface);
        ITextRangeProvider range = provider.DocumentRange;
        range.ExpandToEnclosingUnit(TextUnit.Character);
        Equal(range.GetText(-1), "A");
        if (range.Move(TextUnit.Character, 1) != 1)
            throw new InvalidOperationException("The UIA range did not advance a character.");
        Equal(range.GetText(-1), "👩‍💻");
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
        Equal(range.GetText(-1), "");

        var saturated = new TerminalOutput(columns: 6, retainedRowLimit: 100, screenRows: 3);
        try
        {
            string retained = "👩" + new string('\u0301', 254) + "\u200D";
            byte[] bytes = Encoding.UTF8.GetBytes(retained + "💻😀");
            for (int index = 0; index < bytes.Length; index += 7)
                saturated.Receive(TerminalReceiveSource.Data, bytes.AsSpan(index, Math.Min(7, bytes.Length - index)));
            surface.SetSnapshot(saturated.GetRows());
            range = provider.DocumentRange;
            range.ExpandToEnclosingUnit(TextUnit.Character);
            Equal(range.GetText(-1), retained);
            var anchor = range.Clone();
            if (range.Move(TextUnit.Character, 1) != 1)
                throw new InvalidOperationException("The UIA range split or joined the bounded stored span.");
            Equal(range.GetText(-1), "😀");
            saturated.Resize(4, 3);
            surface.SetSnapshot(saturated.GetRows());
            Equal(anchor.GetText(-1), retained);
            saturated.Receive(TerminalReceiveSource.Data, "\u001b[?1049halternate"u8);
            surface.SetSnapshot(saturated.GetRows());
            Equal(provider.DocumentRange.GetText(-1), "alternate");
            saturated.Receive(TerminalReceiveSource.Data, "\u001b[?1049l"u8);
            surface.SetSnapshot(saturated.GetRows());
            Equal(provider.DocumentRange.GetText(-1), retained + "😀");
            byte[] tail = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\r\ntail", 120)));
            saturated.Receive(TerminalReceiveSource.Data, tail);
            surface.SetSnapshot(saturated.GetRows());
            Equal(anchor.GetText(-1), "");
        }
        finally
        {
            terminal.ReleaseNativeState();
            saturated.ReleaseNativeState();
        }
    }

    private static void TerminalSurfaceRendersItsCellSnapshot()
    {
        var output = new TerminalOutput(columns: 80);
        output.Receive(TerminalReceiveSource.Data,
            "TabTty terminal\r\n\u001b[31mRed\u001b[0m White\r\n日本語 あいうえお 👩‍💻\r\n"u8);
        var surface = new TerminalSurface();
        surface.SetSnapshot(output.GetRows());
        surface.SetSearch(new TerminalMatch(1, 4, 1, 9), [], scroll: false);
        var frame = surface.Scroller;
        frame.Measure(new Size(900, 230));
        frame.Arrange(new Rect(0, 0, 900, 230));
        frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 230, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        byte[] pixels = new byte[900 * 230 * 4];
        bitmap.CopyPixels(pixels, 900 * 4, 0);
        if (!Enumerable.Range(0, pixels.Length / 4).Any(index =>
                pixels[index * 4 + 2] > 100 && pixels[index * 4] < 80))
            throw new InvalidOperationException($"The terminal renderer did not draw its red rendition. " +
                $"Presentation={surface.IsCurrentSnapshotRendered}, viewport={surface.ViewportHeight}, " +
                $"pixels={pixels.Count(value => value != 0)}.");
        string? artifact = Environment.GetEnvironmentVariable("TABTTY_RENDER_ARTIFACT");
        if (artifact is not null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = new FileStream(artifact, FileMode.Create, FileAccess.Write);
            encoder.Save(file);
        }
    }

    private static async Task HighlightWaitsForIdleOrViewportChange()
    {
        var surface = new TerminalSurface();
        var highlight = new TerminalHighlightView(surface);
        var tab = new TabControl();
        var terminalTab = new TabItem { Content = surface.Scroller };
        var otherTab = new TabItem { Content = new TextBlock { Text = "Other" } };
        surface.Scroller.Loaded += (_, _) => surface.SetPresentationEnabled(true);
        surface.Scroller.Unloaded += (_, _) => surface.SetPresentationEnabled(false);
        tab.Items.Add(terminalTab);
        tab.Items.Add(otherTab);
        var window = new Window { Content = tab, Width = 500, Height = 330, ShowActivated = false };
        var terminal = new TerminalOutput(columns: 40, retainedRowLimit: 100, screenRows: 8);
        try
        {
            window.Show();
            terminal.Receive(TerminalReceiveSource.Data, "ERROR\r\n"u8);
            SetSnapshot();
            highlight.SetRules(new HighlightSet(Guid.NewGuid(), "Test", true,
                [new TerminalHighlightRule(Guid.NewGuid(), "Error", "ERROR", true, false, new(0xFF0000, null, null, null))]));
            await WaitForHighlight();
            for (int i = 0; i < 27; i++)
            {
                terminal.Receive(TerminalReceiveSource.Data, "next ERROR\r\n"u8);
                SetSnapshot();
                await Task.Delay(90);
                if (CurrentErrorHasHighlight())
                    throw new InvalidOperationException($"Continuous output triggered a rule evaluation at update {i}.");
            }
            await WaitForHighlight();

            terminal.Receive(TerminalReceiveSource.Data, "scroll ERROR\r\n"u8);
            SetSnapshot();
            surface.Scroller.ScrollToTop();
            window.UpdateLayout();
            await Task.Delay(170);
            surface.ScrollToEnd();
            window.UpdateLayout();
            await WaitForHighlight(timeoutMilliseconds: 300);

            tab.SelectedItem = otherTab;
            window.UpdateLayout();
            await surface.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            terminal.Receive(TerminalReceiveSource.Data, "switch ERROR\r\n"u8);
            SetSnapshot();
            tab.SelectedItem = terminalTab;
            window.UpdateLayout();
            await WaitForHighlight(timeoutMilliseconds: 350);
        }
        finally { window.Close(); }

        void SetSnapshot()
        {
            var snapshot = terminal.GetRows();
            surface.UpdateSnapshotPosition(snapshot);
            highlight.SetSnapshot(snapshot);
            window.UpdateLayout();
        }

        bool CurrentErrorHasHighlight()
        {
            var snapshot = surface.Snapshot!;
            int lastError = snapshot.CursorRow - 1;
            return highlight.CaptureAiRow(lastError - snapshot.ScreenStartRow + 1) is { } overlay &&
                overlay.Spans.Any(span => span.Style.Foreground == 0xFF0000);
        }

        async Task WaitForHighlight(int timeoutMilliseconds = 2000)
        {
            long deadline = Environment.TickCount64 + timeoutMilliseconds;
            do
            {
                if (CurrentErrorHasHighlight()) return;
                await Task.Delay(15);
            }
            while (Environment.TickCount64 < deadline);
            throw new InvalidOperationException($"Highlight timed out (visible: {surface.IsPresentationActive}, " +
                $"rows: {surface.VisibleRows()}, cursor: {surface.Snapshot?.CursorRow}, " +
                $"offset: {surface.VerticalOffset}, selected: {tab.SelectedItem == terminalTab}).");
        }
    }

    private static void Equal(string actual, string expected)
    {
        if (actual != expected) throw new InvalidOperationException($"Expected [{expected}], got [{actual}].");
    }
}
