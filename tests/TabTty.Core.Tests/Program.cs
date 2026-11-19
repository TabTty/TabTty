using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TabTty.Core;

try
{
    if (args is ["--native-bundle-probe"])
    {
        var terminal = new TerminalOutput();
        terminal.ReleaseNativeState();
        Console.WriteLine("PASS NativeBundleProbe");
        return 0;
    }
    if (args is ["--single-byte-mappings", var referenceDirectory])
    {
        SingleByteMappings.Run(referenceDirectory);
        Console.WriteLine("PASS SingleByteMappings");
        return 0;
    }
    if (args is ["--independent-openssh", var port, var username, var userKey, var hostFingerprint, var sharedDirectory])
    {
        await IndependentOpenSsh.RunAsync(int.Parse(port), username, userKey, hostFingerprint, sharedDirectory);
        Console.WriteLine("PASS IndependentOpenSshTerminalAndSftp");
        return 0;
    }
    NativeTerminalContracts.Run();
    Console.WriteLine("PASS NativeTerminalContracts");
    await ArchitectureOwnershipContracts.RunAsync();
    Console.WriteLine("PASS ArchitectureOwnershipContracts");
    await MacroContracts.RunAsync();
    if (args.Contains("--macros")) return 0;
    _ = CoreWorkload.Cycle();
    Console.WriteLine("PASS PerformanceCycleMatchesTheIndependentDigest");
    ResourceReservationsRejectOverflowAndReleaseTheirOwnership();
    Console.WriteLine("PASS ResourceReservationsRejectOverflowAndReleaseTheirOwnership");
    TerminalStateBudgetRefusesGrowthWithoutChangingOtherTerminals();
    Console.WriteLine("PASS TerminalStateBudgetRefusesGrowthWithoutChangingOtherTerminals");
    await QueuedInputRejectsExcessWithoutDisconnecting();
    Console.WriteLine("PASS QueuedInputRejectsExcessWithoutDisconnecting");
    await TemporaryLocalEchoDoesNotRewriteTheProfile();
    Console.WriteLine("PASS TemporaryLocalEchoDoesNotRewriteTheProfile");
    BroadcastMouseRespectsGridAndGeneration();
    Console.WriteLine("PASS BroadcastMouseRespectsGridAndGeneration");
    HistoryProofs.Run();
    Console.WriteLine("PASS RetainedHistoryRevisionProofs");
    SplitUtf8AppearsOnlyAfterItsFinalByte();
    Console.WriteLine("PASS SplitUtf8AppearsOnlyAfterItsFinalByte");
    InterleavedReceiveSourcesCompleteTheirOwnCharacters();
    Console.WriteLine("PASS InterleavedReceiveSourcesCompleteTheirOwnCharacters");
    OnlyTheInitialUtf8SignatureOfEachSourceIsConsumed();
    Console.WriteLine("PASS OnlyTheInitialUtf8SignatureOfEachSourceIsConsumed");
    ReceiveEofReplacesEachIncompleteCharacterOnce();
    Console.WriteLine("PASS ReceiveEofReplacesEachIncompleteCharacterOnce");
    CarriageReturnOverwritesWithoutErasingTheSuffix();
    Console.WriteLine("PASS CarriageReturnOverwritesWithoutErasingTheSuffix");
    LineFeedKeepsTheCurrentColumn();
    Console.WriteLine("PASS LineFeedKeepsTheCurrentColumn");
    BackspaceMovesWithoutErasing();
    Console.WriteLine("PASS BackspaceMovesWithoutErasing");
    FullRowWrapsOnTheNextPrintableCharacter();
    Console.WriteLine("PASS FullRowWrapsOnTheNextPrintableCharacter");
    SearchJoinsOnlySoftWrappedRows();
    Console.WriteLine("PASS SearchJoinsOnlySoftWrappedRows");
    SearchRangesSurviveUnrelatedTextAppends();
    Console.WriteLine("PASS SearchRangesSurviveUnrelatedTextAppends");
    SearchRangesSurviveReflowThatRetiresAnEarlierPrefix();
    Console.WriteLine("PASS SearchRangesSurviveReflowThatRetiresAnEarlierPrefix");
    RenditionKeepsTextAndCellsIndependent();
    Console.WriteLine("PASS RenditionKeepsTextAndCellsIndependent");
    ErasedCellsUseTheCurrentBackgroundWithoutInventingText();
    Console.WriteLine("PASS ErasedCellsUseTheCurrentBackgroundWithoutInventingText");
    ReflowPreservesWideCellsAndHardBoundaries();
    Console.WriteLine("PASS ReflowPreservesWideCellsAndHardBoundaries");
    InsertDeleteAndScrollMarginsKeepCellsConsistent();
    Console.WriteLine("PASS InsertDeleteAndScrollMarginsKeepCellsConsistent");
    AlternateModesPreserveAndClearTheirOwnPages();
    Console.WriteLine("PASS AlternateModesPreserveAndClearTheirOwnPages");
    HighlightMatchesOnlyTheConfiguredRange();
    Console.WriteLine("PASS HighlightMatchesOnlyTheConfiguredRange");
    TabMovesToTheNextDefaultStopWithoutErasing();
    Console.WriteLine("PASS TabMovesToTheNextDefaultStopWithoutErasing");
    VerticalTabAndFormFeedUseTheLineFeedMovement();
    Console.WriteLine("PASS VerticalTabAndFormFeedUseTheLineFeedMovement");
    RetentionRetiresTheOldestRowOnlyAfterTheLimit();
    Console.WriteLine("PASS RetentionRetiresTheOldestRowOnlyAfterTheLimit");
    SplitCursorPositionAppliesOnlyAfterCompletion();
    Console.WriteLine("PASS SplitCursorPositionAppliesOnlyAfterCompletion");
    RelativeCursorMovementClipsToTheScreen();
    Console.WriteLine("PASS RelativeCursorMovementClipsToTheScreen");
    EraseInLineClearsTheRequestedCellsAndKeepsTheCursor();
    Console.WriteLine("PASS EraseInLineClearsTheRequestedCellsAndKeepsTheCursor");
    ControlStringPayloadStaysOutOfTheTerminalRows();
    Console.WriteLine("PASS ControlStringPayloadStaysOutOfTheTerminalRows");
    EraseDisplayClearsThePageWithoutMovingItToScrollback();
    Console.WriteLine("PASS EraseDisplayClearsThePageWithoutMovingItToScrollback");
    EraseScrollbackKeepsTheActivePageAndCursor();
    Console.WriteLine("PASS EraseScrollbackKeepsTheActivePageAndCursor");
    await SavedTelnetProfileKeepsItsSettings();
    Console.WriteLine("PASS SavedTelnetProfileKeepsItsSettings");
    await FirstProfileSaveCreatesTheParentFolders();
    Console.WriteLine("PASS FirstProfileSaveCreatesTheParentFolders");
    await ProfileAndRecordingPathsUseOrdinaryFiles();
    Console.WriteLine("PASS ProfileAndRecordingPathsUseOrdinaryFiles");
    await ProfileEditsPreserveIdentityAndRejectExternalChanges();
    Console.WriteLine("PASS ProfileEditsPreserveIdentityAndRejectExternalChanges");
    await SavedSshProfileKeepsTheCredentialReference();
    Console.WriteLine("PASS SavedSshProfileKeepsTheCredentialReference");
    await SavedSerialProfileKeepsItsLineSettings();
    Console.WriteLine("PASS SavedSerialProfileKeepsItsLineSettings");
    LegacyReceiveResynchronizesBeforeControlBytes();
    Console.WriteLine("PASS LegacyReceiveResynchronizesBeforeControlBytes");
    EveryProfileEncodingDecodesAscii();
    Console.WriteLine("PASS EveryProfileEncodingDecodesAscii");
    JisDesignationsRemainIndependentOfVtCommands();
    Console.WriteLine("PASS JisDesignationsRemainIndependentOfVtCommands");
    WideCellsWrapAndEraseAsAWhole();
    Console.WriteLine("PASS WideCellsWrapAndEraseAsAWhole");
    UnicodeClustersKeepTheirTextAcrossReceiveChunks();
    Console.WriteLine("PASS UnicodeClustersKeepTheirTextAcrossReceiveChunks");
    TerminalModesControlWrapAndLineMovement();
    Console.WriteLine("PASS TerminalModesControlWrapAndLineMovement");
    await MissingComPortFailsTheSavedConnectionAttempt();
    Console.WriteLine("PASS MissingComPortFailsTheSavedConnectionAttempt");
    await JisTransmissionUsesIcuDesignations();
    Console.WriteLine("PASS JisTransmissionUsesIcuDesignations");
    await EveryTerminalEncodingUsesTheSavedProfile();
    Console.WriteLine("PASS EveryTerminalEncodingUsesTheSavedProfile");
    FragmentedTelnetCommandsStayOutOfTerminalData();
    Console.WriteLine("PASS FragmentedTelnetCommandsStayOutOfTerminalData");
    await SavedProfileConnectsAndExchangesTerminalData();
    Console.WriteLine("PASS SavedProfileConnectsAndExchangesTerminalData");
    await RawRecordingContainsOnlyReceivedTerminalPayload();
    Console.WriteLine("PASS RawRecordingContainsOnlyReceivedTerminalPayload");
    await RawResumeStartsANewPartWithoutBackfilling();
    Console.WriteLine("PASS RawResumeStartsANewPartWithoutBackfilling");
    await RecordingFormatsProjectTheSameSessionIndependently();
    Console.WriteLine("PASS RecordingFormatsProjectTheSameSessionIndependently");
    await RecordingPreservesUnicodeAcrossLargeLogBuffers();
    Console.WriteLine("PASS RecordingPreservesUnicodeAcrossLargeLogBuffers");
    TextRecordingAllocationContracts.Run();
    Console.WriteLine("PASS TextRecordingAllocationContracts");
    await RecordingResumeDoesNotBackfillSplitUnits();
    Console.WriteLine("PASS RecordingResumeDoesNotBackfillSplitUnits");
    await FailedRecordingCannotStopTheSessionOrItsReplacementPart();
    Console.WriteLine("PASS FailedRecordingCannotStopTheSessionOrItsReplacementPart");
    await ResizeKeepsTextRecordingBoundaries();
    Console.WriteLine("PASS ResizeKeepsTextRecordingBoundaries");
    await TextTabsUseTheSerializedLogicalColumn();
    Console.WriteLine("PASS TextTabsUseTheSerializedLogicalColumn");
    await PasteUsesNegotiatedBracketsWithoutAppendingEnter();
    Console.WriteLine("PASS PasteUsesNegotiatedBracketsWithoutAppendingEnter");
    await FailedUtf8InputDoesNotEchoOrReplay();
    Console.WriteLine("PASS FailedUtf8InputDoesNotEchoOrReplay");
    CredentialInputBoundsPreserveValidEncodings();
    Console.WriteLine("PASS CredentialInputBoundsPreserveValidEncodings");
    if (File.Exists(Path.Combine(AppContext.BaseDirectory, "libcrypto-3-x64.dll")))
    {
        VaultDecryptsAnIndependentFixedContainer();
        Console.WriteLine("PASS VaultDecryptsAnIndependentFixedContainer");
        await SshPacketsAuthenticateBeforeReleasingPayload();
        Console.WriteLine("PASS SshPacketsAuthenticateBeforeReleasingPayload");
        await SshCredentialPacketsUseCoarseLengthBuckets();
        Console.WriteLine("PASS SshCredentialPacketsUseCoarseLengthBuckets");
        OpenSslCiphersMatchIndependentVectors();
        Console.WriteLine("PASS OpenSslCiphersMatchIndependentVectors");
        await VaultCopyUnlocksAndRejectsTampering();
        Console.WriteLine("PASS VaultCopyUnlocksAndRejectsTampering");
        await VaultEditsKeepIdsAndRestoreAnOlderPasswordGeneration();
        Console.WriteLine("PASS VaultEditsKeepIdsAndRestoreAnOlderPasswordGeneration");
        await VaultRecoveryVerifiesBackupBeforeReplacingDamage();
        Console.WriteLine("PASS VaultRecoveryVerifiesBackupBeforeReplacingDamage");
        await VaultInitializesAnEmptyFileAndArchivesItsEncryptedContents();
        Console.WriteLine("PASS VaultInitializesAnEmptyFileAndArchivesItsEncryptedContents");
        await VaultImportsAnIndependentEd25519Key();
        Console.WriteLine("PASS VaultImportsAnIndependentEd25519Key");
        if (Environment.GetEnvironmentVariable("TABTTY_OPENSSH_FIXTURE") is { } openSshFixture)
        {
            await VaultImportsAnEncryptedOpenSshKey(openSshFixture);
            Console.WriteLine("PASS VaultImportsAnEncryptedOpenSshKey");
        }
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("TABTTY_INTEROP_SSH_PORT"), out int sshPort))
    {
        await SshPasswordSessionPreservesChannelBytes(sshPort);
        Console.WriteLine("PASS SshPasswordSessionPreservesChannelBytes");
        if (Environment.GetEnvironmentVariable("TABTTY_INTEROP_EARLY_DATA") is not null)
        {
            await MacroContracts.SshEarlyDataAsync(sshPort);
            Console.WriteLine("PASS MacroReceivesRealSshStartupData");
            await ArchitectureOwnershipContracts.SshStartupFailurePreservesOriginalRecordingAsync(sshPort);
            Console.WriteLine("PASS SshStartupFailurePreservesOriginalRecording");
        }
        await SshKeyboardInteractiveUsesEachChallenge(sshPort);
        Console.WriteLine("PASS SshKeyboardInteractiveUsesEachChallenge");
        await SshPublicKeySignatureIsVerifiedByThePeer(sshPort);
        Console.WriteLine("PASS SshPublicKeySignatureIsVerifiedByThePeer");
        if (Environment.GetEnvironmentVariable("TABTTY_INTEROP_SFTP") is not null)
        {
            await SftpUsesTheSameSshConnectionWithoutRecordingFileData(sshPort);
            Console.WriteLine("PASS SftpUsesTheSameSshConnectionWithoutRecordingFileData");
            if (Environment.GetEnvironmentVariable("TABTTY_INTEROP_OWNERSHIP") is not null)
            {
                await ArchitectureOwnershipContracts.SshCreditRecordingAndTransferProgressAsync(sshPort);
                await ArchitectureOwnershipContracts.SshTerminalAndSftpRetainCommittedPacketsAsync(sshPort);
                Console.WriteLine("PASS SshTerminalAndSftpRetainCommittedPackets");
                await ArchitectureOwnershipContracts.SftpLateOpenAndClosePreserveTheOriginalSlotAsync(sshPort);
                Console.WriteLine("PASS SftpLateOpenAndClosePreserveTheOriginalSlot");
                await ArchitectureOwnershipContracts.SftpLateOpenAndClosePreserveTheOriginalSlotAsync(sshPort, timeOut: true);
                Console.WriteLine("PASS SftpUnansweredOpenAndCloseExpireWaitersWithoutReusingTheSlot");
                Console.WriteLine("PASS SshCreditRecordingAndTransferProgress");
                await ArchitectureOwnershipContracts.SshEofFlushesWhileReverseInputRemainsAvailableAsync(sshPort);
                Console.WriteLine("PASS SshEofFlushesWhileReverseInputRemainsAvailable");
                await ArchitectureOwnershipContracts.SshTinyAlternatingPayloadPreservesOrderAsync(sshPort);
                Console.WriteLine("PASS SshTinyAlternatingPayloadPreservesOrder");
            }
        }
        if (Environment.GetEnvironmentVariable("TABTTY_INTEROP_USER_KEY") is { } keyFixture)
        {
            await VaultImportsAnEncryptedIndependentPrivateKey(keyFixture);
            Console.WriteLine("PASS VaultImportsAnEncryptedIndependentPrivateKey");
        }
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("TABTTY_STRESS_SECONDS"), out int stressSeconds) &&
        stressSeconds is >= 1 and <= 1800)
        await CoreWorkload.RunAsync(stressSeconds, Environment.GetEnvironmentVariable("TABTTY_STRESS_OUTPUT") ??
            Path.Combine(Environment.CurrentDirectory, "artifacts", "core-stress-" + Guid.NewGuid().ToString("N")));
    return 0;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void BroadcastMouseRespectsGridAndGeneration()
{
    var terminal = new TerminalOutput(columns: 60, screenRows: 24);
    terminal.Receive(TerminalReceiveSource.Data, "\u001b[?1002h\u001b[?1006h"u8);
    var press = new TerminalMouseInput(0, 49, 4, false, false, TerminalModifiers.None, terminal.MouseMode.Generation);
    ExpectBytes(terminal.EncodeBroadcastMouse(press), "\u001b[<0;50;5M"u8.ToArray());
    ExpectBytes(terminal.EncodeBroadcastMouse(press with { Column = 69, Motion = true }), []);
    ExpectBytes(terminal.EncodeBroadcastMouse(press with { Column = 58, Motion = true }), "\u001b[<32;59;5M"u8.ToArray());
    ExpectBytes(terminal.EncodeBroadcastMouse(press with { Column = 69, Release = true }), "\u001b[<0;60;5m"u8.ToArray());
    terminal.Receive(TerminalReceiveSource.Data, "\u001b[?1002l\u001b[?9h"u8);
    ExpectBytes(terminal.EncodeBroadcastMouse(press with { Release = true }), []);
    ExpectBytes(terminal.EncodeBroadcastMouse(press with { Release = true, Generation = terminal.MouseMode.Generation }), []);
    terminal.Resize(400, 24);
    terminal.Receive(TerminalReceiveSource.Data, "\u001b[?1006l\u001b[?1000h"u8);
    var classic = press with { Generation = terminal.MouseMode.Generation, Column = 223 };
    ExpectBytes(terminal.EncodeBroadcastMouse(classic), []);
    ExpectBytes(terminal.EncodeBroadcastMouse(classic with { Release = true }), [27, 91, 77, 35, 255, 37]);
}

static void SplitUtf8AppearsOnlyAfterItsFinalByte()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, [0xE3, 0x81]);
    ExpectRows(output, []);

    output.Receive(TerminalReceiveSource.Data, [0x82]);
    ExpectRows(output, ["あ"]);
}

static async Task SftpUsesTheSameSshConnectionWithoutRecordingFileData(int port)
{
    using var folder = new TemporaryDirectory();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    CancellationToken token = deadline.Token;
    string path = Path.Combine(folder.Path, "transfer.ttty");
    await ConnectionProfile.CreateSsh("SSH", "127.0.0.1", "tabtty-sftp", port, recordRaw: true).SaveNewAsync(path);
    int passwords = 0;
    var interaction = new SshInteraction((_, _) => Task.FromResult(true), (_, _) =>
    {
        passwords++;
        return Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray()));
    });
    await using (var session = await TerminalSession.ConnectAsync(path, token, interaction))
    {
        using var transfer = await session.OpenFileTransferAsync(token);
        if (transfer.DisplayName("notes\r01"u8.ToArray()) != "notes<U+000D>01" ||
            transfer.DisplayName("notes<U+000D>01"u8.ToArray()) == "notes<U+000D>01" ||
            transfer.DisplayName("<bytes:FF>"u8.ToArray()) == SftpClient.DisplayBytes([0xff]) ||
            transfer.DisplayPath("/home/notes\r01"u8.ToArray()) != "/home/notes<U+000D>01")
            throw new InvalidOperationException("SFTP file labels must distinguish text, controls and byte markers.");
        byte[] directory = await transfer.ResolveAsync("."u8.ToArray(), token);
        byte[] staged = SftpClient.Child(directory, transfer.EncodeName(".stage"));
        byte[] final = SftpClient.Child(directory, transfer.EncodeName("試験.bin"));
        byte[] contents = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        byte[] output = await transfer.OpenAsync(staged, true, token);
        Task terminal = session.SendTextAsync("shell", token);
        for (int offset = 0; offset < contents.Length; offset += SftpClient.BlockSize)
            await transfer.WriteAsync(output, offset, contents.AsMemory(offset, Math.Min(SftpClient.BlockSize, contents.Length - offset)), token);
        await transfer.CloseAsync(output, token);
        await transfer.RenameAsync(staged, final, false, token);
        SftpEntry[] entries = await transfer.ListAsync(directory, token);
        if (entries.Length != 1 || transfer.DecodeName(entries[0].Name) != "試験.bin")
            throw new InvalidOperationException("SFTP did not list the uploaded file.");
        byte[] input = await transfer.OpenAsync(final, false, token);
        using var received = new MemoryStream();
        while (await transfer.ReadAsync(input, received.Length, SftpClient.BlockSize, token) is { } bytes) received.Write(bytes);
        await transfer.CloseAsync(input, token);
        ExpectBytes(received.ToArray(), contents);
        await transfer.RemoveAsync(final, false, token);
        await terminal;
        IReadOnlyList<string>? screen = await session.ReadOutputAsync(token);
        while (screen is not null && !screen.Contains("shell")) screen = await session.ReadOutputAsync(token);
        if (screen is null || passwords != 1) throw new InvalidOperationException("SFTP affected shell I/O or repeated SSH login.");
        try
        {
            await transfer.ResolveAsync("/fixture-oversize-frame"u8.ToArray(), token);
            throw new InvalidOperationException("An oversized SFTP frame was accepted.");
        }
        catch (SftpException error) when (error.Failure == SftpFailure.InvalidReply) { }
        using var reopened = await session.OpenFileTransferAsync(token);
        ExpectBytes(await reopened.ResolveAsync("."u8.ToArray(), token), "/home"u8.ToArray());
        if (!session.AcceptsInput || passwords != 1)
            throw new InvalidOperationException("An invalid SFTP response closed the shell or reopened the SSH connection.");
        session.Disconnect();
    }
    string log = Directory.GetFiles(Path.Combine(folder.Path, "Logs"), "*.raw.tttylog").Single();
    ExpectBytes(await File.ReadAllBytesAsync(log, token), "shell"u8.ToArray());
    string denied = Path.Combine(folder.Path, "denied.ttty");
    await ConnectionProfile.CreateSsh("SSH", "127.0.0.1", "tabtty-sftp-denied", port).SaveNewAsync(denied);
    await using var deniedSession = await TerminalSession.ConnectAsync(denied, token, interaction);
    try
    {
        using var rejected = await deniedSession.OpenFileTransferAsync(token);
        throw new InvalidOperationException("The fixture was expected to refuse SFTP.");
    }
    catch (SftpException error) when (error.Failure == SftpFailure.SubsystemRefused) { }
    await deniedSession.SendTextAsync("shell", token);
    IReadOnlyList<string>? available = await deniedSession.ReadOutputAsync(token);
    if (available is null || !available.Contains("shell")) throw new InvalidOperationException("SFTP refusal affected the terminal.");
}

static async Task SshPasswordSessionPreservesChannelBytes(int port)
{
    using var folder = new TemporaryDirectory();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    string path = Path.Combine(folder.Path, "interop.ttty");
    await ConnectionProfile.CreateSsh("SSH", "127.0.0.1", "tabtty-interop", port,
        recordRaw: true, receiveEncoding: "cp932").SaveNewAsync(path);
    bool approved = false;
    var interaction = new SshInteraction((key, _) =>
    {
        string format = Environment.GetEnvironmentVariable("TABTTY_INTEROP_HOST_KEY_FORMAT") ?? "ssh-ed25519";
        approved = key.Format == format && key.Destination.Port == port &&
            key.Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal);
        return Task.FromResult(approved);
    }, (request, _) =>
    {
        if (!approved || request.Attempt != 1)
            throw new InvalidOperationException("SSH requested an unexpected password.");
        return Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray()));
    });
    bool expectEarlyData = Environment.GetEnvironmentVariable("TABTTY_INTEROP_EARLY_DATA") is not null;
    Task<TerminalSession> connecting = TerminalSession.ConnectAsync(path, deadline.Token, interaction,
        viewport: _ => Task.FromResult((120, 40)));
    if (expectEarlyData)
    {
        string directory = Path.Combine(folder.Path, "Logs");
        while (!connecting.IsCompleted)
        {
            string? current = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.raw.tttylog").SingleOrDefault() : null;
            if (current is not null && new FileInfo(current).Length >= 6) break;
            await Task.Delay(25, deadline.Token);
        }
    }
    bool startupRecorded = !connecting.IsCompleted;
    await using var session = await connecting;
    if (expectEarlyData && !startupRecorded)
        throw new InvalidOperationException("Early SSH data did not reach the log while terminal startup was pending.");
    await session.SendTextAsync("go", deadline.Token);
    IReadOnlyList<string>? last = null;
    while (await session.ReadOutputAsync(deadline.Token) is { } rows) last = rows;
    if (last is null || !last.SequenceEqual(new[] { expectEarlyData ? "early あ!" : "あ!" }))
        throw new InvalidOperationException("The SSH terminal did not decode DATA and stderr.");
    await session.WaitForTerminalPeerCloseAsync(deadline.Token);
    await session.DisposeAsync();
    string log = Directory.GetFiles(Path.Combine(folder.Path, "Logs"), "*.raw.tttylog").Single();
    byte[] expected = expectEarlyData ? [.. "early "u8, 0x82, 0xA0, 0x21] : [0x82, 0xA0, 0x21];
    ExpectBytes(await File.ReadAllBytesAsync(log), expected);
}

static async Task SshKeyboardInteractiveUsesEachChallenge(int port)
{
    using var folder = new TemporaryDirectory();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    string path = Path.Combine(folder.Path, "interactive.ttty");
    await ConnectionProfile.CreateSsh("SSH", "127.0.0.1", "tabtty-interactive", port,
        methods: ["keyboardInteractive"]).SaveNewAsync(path);
    int rounds = 0;
    var interaction = new SshInteraction((_, _) => Task.FromResult(true),
        (_, _) => throw new InvalidOperationException("Interactive authentication requested a stored password."),
        RequestChallenge: (challenge, _) =>
        {
            rounds++;
            if (rounds == 1 && challenge.Prompts is [{ Echo: true }, { Echo: false }])
                return Task.FromResult<VaultSecret[]?>([new VaultSecret("local-user"u8.ToArray()), new VaultSecret([])]);
            if (rounds == 2 && challenge.Prompts is [{ Echo: false }])
                return Task.FromResult<VaultSecret[]?>([new VaultSecret("local-response"u8.ToArray())]);
            throw new InvalidOperationException("Unexpected SSH challenge shape.");
        });
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token, interaction);
    if (rounds != 2) throw new InvalidOperationException("SSH did not finish both challenge rounds.");
}

static async Task SshPublicKeySignatureIsVerifiedByThePeer(int port)
{
    using var folder = new TemporaryDirectory();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    string path = Path.Combine(folder.Path, "public-key.ttty");
    await ConnectionProfile.CreateSsh("SSH key", "127.0.0.1", "tabtty-key", port,
        methods: ["publicKey"]).SaveNewAsync(path);
    int keys = 0;
    var interaction = new SshInteraction((_, _) => Task.FromResult(true),
        (_, _) => throw new InvalidOperationException("Public-key authentication requested a password."),
        RequestPrivateKey: (_, _) =>
        {
            keys++;
            string? fixture = Environment.GetEnvironmentVariable("TABTTY_INTEROP_USER_KEY");
            byte[] der = fixture is null ? Convert.FromHexString(
                "302E020100300506032B6570042204209D61B19DEFFD5A60BA844AF492EC2CC44449C5697B326919703BAC031CAE7F60") :
                File.ReadAllBytes(fixture + ".der");
            return Task.FromResult<VaultSecret?>(new VaultSecret(der));
        });
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token, interaction);
    if (keys != 1) throw new InvalidOperationException("The SSH client retrieved a key more than once.");
}

static async Task VaultEditsKeepIdsAndRestoreAnOlderPasswordGeneration()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "edit-vault.tttykey");
    string backup = Path.Combine(folder.Path, "backup.tttykey");
    const string oldMaster = "Original vault master passphrase";
    const string newMaster = "Test master 14";
    Guid first;
    Guid third;
    using (var vault = await CredentialVault.CreateAsync(path, oldMaster.AsMemory()))
    {
        first = await vault.AddPasswordAsync("First", null, "one"u8.ToArray());
        Guid second = await vault.AddPasswordAsync("Second", null, "two"u8.ToArray());
        third = await vault.AddPasswordAsync("Third", null, "three"u8.ToArray());
        await vault.DeleteEntryAsync(second);
        await vault.UpdateEntryAsync(first, "Renamed", "operator", "new-one"u8.ToArray());
        var entries = await vault.ListEntriesAsync();
        if (!entries.Select(entry => entry.Id).SequenceEqual(new[] { first, third }) || entries[0].Name != "Renamed")
            throw new InvalidOperationException("Vault edits changed identity or entry order.");
        using (VaultSecret secret = await vault.GetPasswordAsync(vault.Id, first))
            ExpectBytes(secret.Bytes.ToArray(), "new-one"u8.ToArray());
        await vault.BackupAsync(backup);
        await vault.ChangeMasterPasswordAsync(newMaster.AsMemory());
    }
    try
    {
        using var old = await CredentialVault.UnlockAsync(path, oldMaster.AsMemory());
        throw new InvalidOperationException("A changed vault still accepted its old master password.");
    }
    catch (CryptographicException) { }
    using (var vault = await CredentialVault.UnlockAsync(path, newMaster.AsMemory()))
    {
        await vault.DeleteEntryAsync(first);
        await vault.RestoreAsync(backup, oldMaster.AsMemory());
        using VaultSecret secret = await vault.GetPasswordAsync(vault.Id, first);
        ExpectBytes(secret.Bytes.ToArray(), "new-one"u8.ToArray());
    }
    using var restored = await CredentialVault.UnlockAsync(path, oldMaster.AsMemory());
    if (!(await restored.ListEntriesAsync()).Select(entry => entry.Id).SequenceEqual(new[] { first, third }) ||
        Directory.GetFiles(folder.Path, "*.recovery-*.tttykey").Length != 1)
        throw new InvalidOperationException("Restore did not preserve the previous encrypted generation.");
}

static async Task VaultRecoveryVerifiesBackupBeforeReplacingDamage()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "target.tttykey");
    string backup = Path.Combine(folder.Path, "backup.tttykey");
    const string master = "Recovery test master passphrase";
    Guid id;
    Guid entry;
    using (var original = await CredentialVault.CreateAsync(path, master.AsMemory()))
    {
        id = original.Id;
        entry = await original.AddPasswordAsync("Test", null, "value"u8.ToArray());
        await original.BackupAsync(backup);
    }
    byte[] newer = await File.ReadAllBytesAsync(path);
    newer[9] = 2;
    await File.WriteAllBytesAsync(path, newer);
    try
    {
        using var rejected = await CredentialVault.RecoverAsync(path, backup, master.AsMemory());
        throw new InvalidOperationException("Recovery overwrote a newer container.");
    }
    catch (InvalidDataException) { }
    ExpectBytes(await File.ReadAllBytesAsync(path), newer);
    byte[] damaged = "truncated vault"u8.ToArray();
    await File.WriteAllBytesAsync(path, damaged);
    try
    {
        using var rejected = await CredentialVault.RecoverAsync(path, backup, "Wrong recovery passphrase".AsMemory());
        throw new InvalidOperationException("Recovery accepted a wrong backup password.");
    }
    catch (CryptographicException) { }
    ExpectBytes(await File.ReadAllBytesAsync(path), damaged);
    using (var recovered = await CredentialVault.RecoverAsync(path, backup, master.AsMemory()))
    {
        using VaultSecret secret = await recovered.GetPasswordAsync(id, entry);
        ExpectBytes(secret.Bytes.ToArray(), "value"u8.ToArray());
    }
    ExpectBytes(await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(backup));
    string preserved = Directory.GetFiles(folder.Path, "*.recovery-*.tttykey").Single();
    ExpectBytes(await File.ReadAllBytesAsync(preserved), damaged);
}

static async Task VaultInitializesAnEmptyFileAndArchivesItsEncryptedContents()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "credentials.tttykey");
    const string master = "e\u0301abcdefghijklm";
    try
    {
        using var tooShort = await CredentialVault.CreateAsync(path, master.AsMemory(0, master.Length - 1));
        throw new InvalidOperationException("A vault accepted a master password with 13 normalized characters.");
    }
    catch (ArgumentException) { }
    await File.WriteAllBytesAsync(path, []);
    Guid vaultId, entryId;
    VaultFileEvidence originalEmpty = CredentialVault.InspectFile(path)!;
    using (var initialized = await CredentialVault.InitializeEmptyAsync(path, master.AsMemory(), originalEmpty))
    {
        vaultId = initialized.Id;
        entryId = await initialized.AddPasswordAsync("Test", null, "test-secret"u8.ToArray());
    }
    byte[] encrypted = await File.ReadAllBytesAsync(path);
    try
    {
        using var replaced = await CredentialVault.InitializeEmptyAsync(path, master.AsMemory(), originalEmpty);
        throw new InvalidOperationException("Empty-file initialization replaced an existing encrypted vault.");
    }
    catch (IOException) { }
    ExpectBytes(await File.ReadAllBytesAsync(path), encrypted);
    string archived = CredentialVault.ArchiveFile(path) ??
        throw new InvalidOperationException("The existing vault was not archived.");
    if (File.Exists(path) || Path.GetDirectoryName(archived) != folder.Path)
        throw new InvalidOperationException("Archiving did not leave the configured path free in the same folder.");
    ExpectBytes(await File.ReadAllBytesAsync(archived), encrypted);
    using (var preserved = await CredentialVault.UnlockAsync(archived, "\u00e9abcdefghijklm".AsMemory()))
    {
        using VaultSecret secret = await preserved.GetPasswordAsync(vaultId, entryId);
        ExpectBytes(secret.Bytes.ToArray(), "test-secret"u8.ToArray());
    }
    using var replacement = await CredentialVault.CreateAsync(path, master.AsMemory());
    if (replacement.Id == vaultId || (await replacement.ListEntriesAsync()).Count != 0)
        throw new InvalidOperationException("The new vault inherited the archived vault's identity or entries.");
}

static async Task VaultImportsAnEncryptedOpenSshKey(string fixture)
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "openssh-key.tttykey");
    using var vault = await CredentialVault.CreateAsync(path, "OpenSSH key test master password".AsMemory());
    try
    {
        await vault.ImportPrivateKeyAsync("OpenSSH fixture", fixture);
        throw new InvalidOperationException("Encrypted OpenSSH import did not request its passphrase.");
    }
    catch (PrivateKeyPassphraseRequiredException) { }
    Guid id = await vault.ImportPrivateKeyAsync("OpenSSH fixture", fixture, passphrase: "import-test-passphrase"u8.ToArray());
    var entry = (await vault.ListEntriesAsync()).Single();
    string[] publicFile = (await File.ReadAllTextAsync(fixture + ".pub")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    string expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(publicFile[1]))).TrimEnd('=');
    if (entry.Id != id || entry.Fingerprint != expected)
        throw new InvalidOperationException("OpenSSH import changed the independently generated key.");
    using VaultSecret material = await vault.GetPrivateKeyAsync(vault.Id, id);
    if (material.Bytes.IsEmpty) throw new InvalidOperationException("OpenSSH import did not retain the key.");
}

static async Task VaultImportsAnEncryptedIndependentPrivateKey(string fixture)
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "encrypted-key.tttykey");
    string source = Path.Combine(folder.Path, "source.der");
    string suffix = Environment.GetEnvironmentVariable("TABTTY_KEY_SOURCE_SUFFIX") ?? ".encrypted";
    await File.WriteAllBytesAsync(source, await File.ReadAllBytesAsync(fixture + suffix));
    using var vault = await CredentialVault.CreateAsync(path, "Encrypted key test master password".AsMemory());
    try
    {
        await vault.ImportPrivateKeyAsync("Fixture", source);
        throw new InvalidOperationException("Encrypted-key import did not request its passphrase.");
    }
    catch (PrivateKeyPassphraseRequiredException) { }
    try
    {
        await vault.ImportPrivateKeyAsync("Fixture", source, passphrase: "wrong-passphrase"u8.ToArray());
        throw new InvalidOperationException("Encrypted-key import accepted a wrong passphrase.");
    }
    catch (CryptographicException) { }
    if ((await vault.ListEntriesAsync()).Count != 0)
        throw new InvalidOperationException("A failed import changed the vault.");
    Guid entry = await vault.ImportPrivateKeyAsync("Fixture", source, passphrase: "import-test-passphrase"u8.ToArray());
    File.Delete(source);
    using VaultSecret material = await vault.GetPrivateKeyAsync(vault.Id, entry);
    var imported = (await vault.ListEntriesAsync()).Single();
    string expected = "SHA256:" + Convert.ToBase64String(
        SHA256.HashData(await File.ReadAllBytesAsync(fixture + ".pubblob"))).TrimEnd('=');
    if (imported.Fingerprint != expected || material.Bytes.IsEmpty)
        throw new InvalidOperationException("Encrypted-key import lost its independent public identity.");
}

static async Task VaultImportsAnIndependentEd25519Key()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "keys.tttykey");
    string source = Path.Combine(folder.Path, "key.der");
    // RFC 8032 section 7.1 test key, in the RFC 8410 PrivateKeyInfo envelope.
    byte[] der = Convert.FromHexString(
        "302E020100300506032B6570042204209D61B19DEFFD5A60BA844AF492EC2CC44449C5697B326919703BAC031CAE7F60");
    await File.WriteAllBytesAsync(source, der);
    const string master = "Portable key test passphrase";
    Guid entry;
    using (var vault = await CredentialVault.CreateAsync(path, master.AsMemory()))
    {
        using var acquired = CredentialVault.ReadPrivateKeySource(source);
        long reviewedRevision = vault.Revision;
        await File.WriteAllBytesAsync(source, "incompatible external replacement"u8.ToArray());
        Guid other = await vault.AddPasswordAsync("Other entry", null, "test password"u8.ToArray());
        try
        {
            await vault.ImportPrivateKeySourceAsync("Stale key", acquired, expectedRevision: reviewedRevision);
            throw new InvalidOperationException("Import accepted an obsolete vault revision.");
        }
        catch (InvalidOperationException error) when (error.Message == "The reviewed vault revision changed before the operation.") { }
        entry = await vault.ImportPrivateKeySourceAsync("Fixture key", acquired, expectedRevision: vault.Revision);
        if (!(await File.ReadAllBytesAsync(source)).AsSpan().SequenceEqual("incompatible external replacement"u8))
            throw new InvalidOperationException("Import mutated the external source file.");
        await vault.DeleteEntryAsync(other);
        try
        {
            using var deleted = await vault.GetPasswordAsync(vault.Id, other);
            throw new InvalidOperationException("A deleted reference still resolved after import.");
        }
        catch (CredentialLookupException) { }
        File.Delete(source);
    }
    using var reopened = await CredentialVault.UnlockAsync(path, master.AsMemory());
    var saved = (await reopened.ListEntriesAsync()).Single();
    if (saved.Id != entry || saved.Kind != "privateKey" || saved.PublicType != "ssh-ed25519" ||
        saved.Fingerprint != "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromHexString(
            "0000000B7373682D6564323535313900000020D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A"))).TrimEnd('='))
        throw new InvalidOperationException("Imported key metadata does not match the independent public-key vector.");
    using VaultSecret key = await reopened.GetPrivateKeyAsync(reopened.Id, entry);
    ExpectBytes(key.Bytes.ToArray(), der);

    using (var file = new FileStream(source, FileMode.CreateNew)) file.SetLength(1024 * 1024);
    var sources = new List<CredentialVault.PrivateKeySource>();
    try
    {
        for (int index = 0; index < 8; index++) sources.Add(CredentialVault.ReadPrivateKeySource(source));
        try
        {
            using var excess = CredentialVault.ReadPrivateKeySource(source);
            throw new InvalidOperationException("A ninth source bypassed the secret-input budget.");
        }
        catch (ResourceLimitException) { }
    }
    finally
    {
        foreach (var sourceOwner in sources) sourceOwner.Dispose();
    }
    using var admittedAgain = CredentialVault.ReadPrivateKeySource(source);
}

static async Task VaultCopyUnlocksAndRejectsTampering()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "vault.tttykey");
    string copy = Path.Combine(folder.Path, "portable.tttykey");
    const string master = "A portable vault passphrase";
    Guid entry;
    using (var vault = await CredentialVault.CreateAsync(path, master.AsMemory()))
    {
        entry = await vault.AddPasswordAsync("Test credential", "test-user", "sécret"u8.ToArray());
        await vault.BackupAsync(copy);
    }
    File.Delete(path);
    using (var vault = await CredentialVault.UnlockAsync(copy, master.AsMemory()))
    using (var secret = await vault.GetPasswordAsync(vault.Id, entry))
        ExpectBytes(secret.Bytes.ToArray(), "sécret"u8.ToArray());
    byte[] damaged = await File.ReadAllBytesAsync(copy);
    damaged[^1] ^= 1;
    await File.WriteAllBytesAsync(copy, damaged);
    try
    {
        using var vault = await CredentialVault.UnlockAsync(copy, master.AsMemory());
        throw new InvalidOperationException("Vault accepted an altered payload tag.");
    }
    catch (CryptographicException) { }
}

static void ReflowPreservesWideCellsAndHardBoundaries()
{
    var output = new TerminalOutput(columns: 4);
    output.Receive(TerminalReceiveSource.Data, "AあBC\r\nD"u8);
    TerminalSnapshot original = output.GetRows();
    TerminalMatch before = original.Find("あBC", matchCase: true)!.Value;
    output.Resize(3, 24);
    ExpectRows(output, ["Aあ", "BC", "D"]);
    TerminalMatch? after = output.GetRows().MapMatchFrom(original, before);
    if (after != new TerminalMatch(0, 1, 1, 2))
        throw new InvalidOperationException("Reflow discarded a surviving search anchor.");
    output.Resize(8, 24);
    ExpectRows(output, ["AあBC", "D"]);
    if (output.GetRows().GetCells(0).Span[1].Width != 2)
        throw new InvalidOperationException("Reflow split a wide cluster.");
    output.Receive(TerminalReceiveSource.Data, "E"u8);
    ExpectRows(output, ["AあBC", "DE"]);
}

static void InsertDeleteAndScrollMarginsKeepCellsConsistent()
{
    var output = new TerminalOutput(columns: 8, screenRows: 3);
    output.Receive(TerminalReceiveSource.Data, "ABCD\u001b[1;2H\u001b[2@xy\u001b[1P"u8);
    ExpectRows(output, ["AxyCD"]);
    output.Receive(TerminalReceiveSource.Data, "\u001b[2;1Htwo\u001b[3;1Hthree\u001b[2;3r\u001b[3;1H\n"u8);
    ExpectRows(output, ["AxyCD", "three", ""]);
    output.Receive(TerminalReceiveSource.Data, "\u001b[H\u001b[1@Z"u8);
    ExpectRows(output, ["ZAxyCD", "three", ""]);
}

static void AlternateModesPreserveAndClearTheirOwnPages()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "normal\u001b[?47halternate\u001b[?47l"u8);
    ExpectRows(output, ["normal"]);
    output.Receive(TerminalReceiveSource.Data, "\u001b[?47h"u8);
    ExpectRows(output, ["alternate"]);
    output.Receive(TerminalReceiveSource.Data, "\u001b[?1047l\u001b[?47h"u8);
    ExpectRows(output, []);
}

static void HighlightMatchesOnlyTheConfiguredRange()
{
    var style = new TerminalHighlightStyle(0xFF0000, null, null, true);
    var rules = new TerminalHighlightRules([
        new TerminalHighlightRule(Guid.NewGuid(), "Errors", "error\\d+", true, true, style),
        new TerminalHighlightRule(Guid.NewGuid(), "Disabled", "(", false, false, default),
    ]);
    var result = rules.Match("x ERROR42 y", new HashSet<Guid>());
    if (result.Spans is not [var span] || span.Start != 2 || span.End != 9 || span.Style != style)
        throw new InvalidOperationException("Highlighting changed the regex match range.");
}

static void RenditionKeepsTextAndCellsIndependent()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "\u001b[1;31;48:2::4:5:6mあ\u001b[39;49;22mB"u8);
    var snapshot = output.GetRows();
    if (snapshot[0] != "あB") throw new InvalidOperationException("SGR altered terminal text.");
    var cells = snapshot.GetCells(0).Span;
    if (cells.Length != 2 || cells[0].Width != 2 || cells[1].Column != 2 ||
        cells[0].Style.Foreground != 0xCD0000 || cells[0].Style.Background != 0x040506 ||
        !cells[0].Style.Flags.HasFlag(TerminalStyleFlags.Bold) || cells[1].Style != default)
        throw new InvalidOperationException("SGR did not preserve the expected cell attributes.");
}

static void SearchJoinsOnlySoftWrappedRows()
{
    var output = new TerminalOutput(columns: 4);
    output.Receive(TerminalReceiveSource.Data, "AbCdEf\r\nXYZ"u8);
    TerminalSnapshot snapshot = output.GetRows();
    var match = snapshot.Find("cDe", matchCase: false);
    if (match is null || match.Value.StartRow != 0 || match.Value.StartOffset != 2 ||
        match.Value.EndRow != 1 || match.Value.EndOffset != 1)
        throw new InvalidOperationException("A soft-wrapped match did not span the expected cells.");
    if (snapshot.Find("cDe", matchCase: true) is not null || snapshot.Find("fX", matchCase: false) is not null)
        throw new InvalidOperationException("Search ignored a case option or crossed a hard boundary.");
    output.Receive(TerminalReceiveSource.Data, "\u001b[?1049hAbCdEf"u8);
    if (output.GetRows().Find("cDe", matchCase: false) is not null)
        throw new InvalidOperationException("Alternate screen text entered retained-output search.");
}

static void ExpectRows(TerminalOutput output, string[] expected)
{
    var actual = output.GetRows();
    if (!actual.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"Expected rows [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }
}

static void InterleavedReceiveSourcesCompleteTheirOwnCharacters()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, [0xE3, 0x81]);
    output.Receive(TerminalReceiveSource.StandardError, [0x58]);
    output.Receive(TerminalReceiveSource.Data, [0x82]);

    ExpectRows(output, ["Xあ"]);
}

static void OnlyTheInitialUtf8SignatureOfEachSourceIsConsumed()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, [0xEF, 0xBB]);
    output.Receive(TerminalReceiveSource.StandardError, [0xEF, 0xBB, 0xBF, 0x58]);
    output.Receive(TerminalReceiveSource.Data, [0xBF, 0xEF, 0xBB, 0xBF, 0x59]);

    ExpectRows(output, ["X\uFEFFY"]);
}

static void ReceiveEofReplacesEachIncompleteCharacterOnce()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, [0x41, 0xE3, 0x81]);
    output.Receive(TerminalReceiveSource.StandardError, [0xF0, 0x9F]);
    ExpectRows(output, ["A"]);

    output.CompleteReceive();
    output.CompleteReceive();
    ExpectRows(output, ["A\uFFFD\uFFFD"]);
}

static void CarriageReturnOverwritesWithoutErasingTheSuffix()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "ABCDE\rX"u8);

    ExpectRows(output, ["XBCDE"]);
}

static void LineFeedKeepsTheCurrentColumn()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "AB\nX"u8);

    ExpectRows(output, ["AB", "  X"]);
}

static void BackspaceMovesWithoutErasing()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "ABC\b"u8);
    ExpectRows(output, ["ABC"]);

    output.Receive(TerminalReceiveSource.Data, "X"u8);
    ExpectRows(output, ["ABX"]);
}

static void FullRowWrapsOnTheNextPrintableCharacter()
{
    var output = new TerminalOutput(columns: 4);
    output.Receive(TerminalReceiveSource.Data, "ABCD"u8);
    ExpectRows(output, ["ABCD"]);

    output.Receive(TerminalReceiveSource.Data, "E"u8);
    ExpectRows(output, ["ABCD", "E"]);
}

static void TabMovesToTheNextDefaultStopWithoutErasing()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "ABC\r\tX"u8);

    ExpectRows(output, ["ABC     X"]);
}

static void VerticalTabAndFormFeedUseTheLineFeedMovement()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "A\vB\fC"u8);

    ExpectRows(output, ["A", " B", "  C"]);
}

static void RetentionRetiresTheOldestRowOnlyAfterTheLimit()
{
    var output = new TerminalOutput(retainedRowLimit: 100);
    output.Receive(TerminalReceiveSource.Data, "old\r\n"u8);
    for (int index = 0; index < 98; index++)
    {
        output.Receive(TerminalReceiveSource.Data, "keep\r\n"u8);
    }

    output.Receive(TerminalReceiveSource.Data, "before"u8);
    string[] middle = Enumerable.Repeat("keep", 98).ToArray();
    ExpectRows(output, ["old", .. middle, "before"]);

    output.Receive(TerminalReceiveSource.Data, "\r\nnew"u8);
    ExpectRows(output, [.. middle, "before", "new"]);
}

static void SplitCursorPositionAppliesOnlyAfterCompletion()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "ABC\r\nDEF\u001B[1;"u8);
    output.Receive(TerminalReceiveSource.StandardError, "Y"u8);
    ExpectRows(output, ["ABC", "DEFY"]);

    output.Receive(TerminalReceiveSource.Data, "2HX"u8);
    ExpectRows(output, ["AXC", "DEFY"]);
}

static void RelativeCursorMovementClipsToTheScreen()
{
    var output = new TerminalOutput(columns: 4, screenRows: 2);
    output.Receive(TerminalReceiveSource.Data, "ABCD\r\nEFGH\u001B[99A\u001B[99DX\u001B[99CY\u001B[99B\u001B[99DZ"u8);

    ExpectRows(output, ["XBCY", "ZFGH"]);
}

static void EraseInLineClearsTheRequestedCellsAndKeepsTheCursor()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "ABCDE\u001B[1;3H\u001B[KX"u8);
    ExpectRows(output, ["ABX"]);

    output.Receive(TerminalReceiveSource.Data, "\u001B[2KY"u8);
    ExpectRows(output, ["   Y"]);

    output.Receive(TerminalReceiveSource.Data, "\r12345\u001B[1;2H\u001B[1KZ"u8);
    ExpectRows(output, [" Z345"]);
}

static void ControlStringPayloadStaysOutOfTheTerminalRows()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "A\u001B]52;c;example"u8);
    output.Receive(TerminalReceiveSource.StandardError, "X"u8);
    output.Receive(TerminalReceiveSource.Data, "\aB\u001BP1;2|example\u001B"u8);
    ExpectRows(output, ["AXB"]);

    output.Receive(TerminalReceiveSource.Data, "\\C"u8);
    ExpectRows(output, ["AXBC"]);
}

static void EraseDisplayClearsThePageWithoutMovingItToScrollback()
{
    var output = new TerminalOutput(screenRows: 2);
    output.Receive(TerminalReceiveSource.Data, "old\r\nAB\r\nCD\u001B[2JX"u8);

    ExpectRows(output, ["old", "", "  X"]);
}

static void EraseScrollbackKeepsTheActivePageAndCursor()
{
    var output = new TerminalOutput(screenRows: 2);
    output.Receive(TerminalReceiveSource.Data, "old\r\nAB\r\nCD\u001B[3JX"u8);

    ExpectRows(output, ["AB", "CDX"]);
}

static async Task ProfileEditsPreserveIdentityAndRejectExternalChanges()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "edit.ttty");
    ConnectionProfile created = ConnectionProfile.CreateTelnet("Original", "localhost");
    await created.SaveNewAsync(path);
    ConnectionProfile loaded = await ConnectionProfile.LoadAsync(path);
    var draft = JsonNode.Parse(loaded.ToEditorJson())!.AsObject();
    draft["displayName"] = "Edited";
    draft["futureProperty"] = new JsonObject { ["data"] = 17 };
    ConnectionProfile revision = loaded.Edit(draft.ToJsonString());
    await revision.SaveEditAsync();
    ConnectionProfile saved = await ConnectionProfile.LoadAsync(path);
    if (saved.Id != loaded.Id || saved.DisplayName != "Edited" ||
        JsonNode.Parse(saved.ToEditorJson())!["futureProperty"]!["data"]!.GetValue<int>() != 17 ||
        (await ConnectionProfile.LoadAsync(path + ".bak")).DisplayName != "Original")
        throw new InvalidOperationException("Profile editing lost identity, unknown fields, or its previous valid version.");
    if (saved.Duplicate().Id == saved.Id) throw new InvalidOperationException("A duplicate reused the source ID.");
    draft["displayName"] = "External";
    string external = draft.ToJsonString();
    await File.WriteAllTextAsync(path, external);
    try
    {
        await saved.Edit(saved.ToEditorJson()).SaveEditAsync();
        throw new InvalidOperationException("Profile editing overwrote an external change.");
    }
    catch (IOException) { }
    if (await File.ReadAllTextAsync(path) != external)
        throw new InvalidOperationException("A failed profile save modified the external file.");
}

static async Task SavedSshProfileKeepsTheCredentialReference()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "ssh.ttty");
    var reference = new CredentialReference(Guid.NewGuid(), Guid.NewGuid(), "password");
    await ConnectionProfile.CreateSsh("SSH console", "Example.COM", "operator", password: reference,
        receiveEncoding: "cp932").SaveNewAsync(path);
    ConnectionProfile saved = await ConnectionProfile.LoadAsync(path);
    if (saved.Type != "ssh" || saved.Port != 22 || saved.SshUsername != "operator" ||
        saved.SshPassword != reference || saved.ReceiveEncoding != "cp932" || saved.LocalEcho != "off")
        throw new InvalidOperationException("The SSH profile changed its connection snapshot.");
}

static async Task SavedTelnetProfileKeepsItsSettings()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "console.ttty");
    await ConnectionProfile.CreateTelnet("Lab console", "localhost", 2323).SaveNewAsync(path);
    var saved = await ConnectionProfile.LoadAsync(path);
    if (saved.DisplayName != "Lab console" || saved.Host != "localhost" ||
        saved.Port != 2323 || saved.ReceiveEncoding != "utf-8" ||
        saved.Enter != "crlf" || saved.RecordRaw || saved.RecordText || saved.RecordTerminal)
    {
        throw new InvalidOperationException("The saved profile did not preserve the destination and defaults.");
    }
}

static async Task FirstProfileSaveCreatesTheParentFolders()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "TabTty", "Profiles", "first.ttty");
    var profile = ConnectionProfile.CreateTelnet("First profile", "localhost");
    await profile.SaveNewAsync(path);
    ConnectionProfile saved = await ConnectionProfile.LoadAsync(path);
    if (saved.Id != profile.Id || saved.DisplayName != "First profile")
        throw new InvalidOperationException("The first profile was not saved in the requested folder.");
}

static async Task ProfileAndRecordingPathsUseOrdinaryFiles()
{
    using var folder = new TemporaryDirectory();
    var profile = ConnectionProfile.CreateTelnet("NUL.session", "localhost", recordText: true);
    var document = JsonNode.Parse(profile.ToEditorJson())!.AsObject();
    foreach (string rejected in new[] { @"//./directory", @"\/?\UNC\server\share", @"\\server\PIPE\channel",
        @"C:Logs", @"\Logs", "Logs:stream", "local/NUL.txt", @"COM¹.log" })
    {
        document["logging"]!["directory"] = rejected;
        try
        {
            profile.Edit(document.ToJsonString()).ValidateEditorSettings();
            throw new InvalidOperationException("The profile accepted a non-file recording destination.");
        }
        catch (InvalidDataException) { }
    }
    foreach (string accepted in new[] { "//server/share/Logs", @"\\server\\share\a\b", @"C:/Logs",
        @"C:\", "../Logs", @"./ログ/.local", @"./a/../Logs" })
        if (!OrdinaryWindowsPath.IsValid(accepted))
            throw new InvalidOperationException("An ordinary recording path was rejected.");

    string profilePath = Path.Combine(folder.Path, "record.ttty");
    await profile.SaveNewAsync(profilePath);
    SessionRecording recording = SessionRecording.Open(profilePath, profile);
    await recording.FinishAsync();
    if (!Directory.EnumerateFiles(Path.Combine(folder.Path, "Logs"), "_NUL.session-*.log").Any())
        throw new InvalidOperationException("The recording name did not preserve the label as an ordinary file.");
}

static void FragmentedTelnetCommandsStayOutOfTerminalData()
{
    var protocol = new TelnetProtocol("xterm", 80, 24);
    ExpectBytes(protocol.Start(), [255, 251, 24, 255, 251, 31, 255, 253, 1, 255, 251, 3, 255, 253, 3]);
    var first = protocol.Receive([0x41, 13, 255, 251]);
    ExpectBytes(first.Payload, [0x41]);
    ExpectBytes(first.Reply, []);

    var second = protocol.Receive([1, 0, 255, 255, 0x42]);
    ExpectBytes(second.Payload, [13, 255, 0x42]);
    ExpectBytes(second.Reply, []);
    if (!protocol.RemoteEcho) throw new InvalidOperationException("Remote echo was not negotiated.");

    ExpectBytes(protocol.EncodeInput([13, 10, 13, 255]), [13, 10, 13, 0, 255, 255]);
}

static void ExpectBytes(byte[] actual, byte[] expected)
{
    if (!actual.AsSpan().SequenceEqual(expected))
        throw new InvalidOperationException($"Expected bytes {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
}

static async Task SavedProfileConnectsAndExchangesTerminalData()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "loopback.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Loopback", "127.0.0.1", port).SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    using var session = await TerminalSession.ConnectAsync(path, deadline.Token,
        viewport: _ => Task.FromResult((120, 40)));
    using var server = await incoming;
    NetworkStream wire = server.GetStream();
    byte[] negotiation = new byte[15];
    await wire.ReadExactlyAsync(negotiation, deadline.Token);
    await wire.WriteAsync(new byte[] { 255, 253, 31, 255, 251, 1, 62, 32 }, deadline.Token);
    var rows = await session.ReadOutputAsync(deadline.Token);
    if (rows is not null && rows.SequenceEqual([">"]))
        rows = await session.ReadOutputAsync(deadline.Token);
    if (rows is null || !rows.SequenceEqual(["> "]))
        throw new InvalidOperationException("The remote prompt did not reach the terminal.");
    byte[] size = new byte[9];
    await wire.ReadExactlyAsync(size, deadline.Token);
    ExpectBytes(size, [255, 250, 31, 0, 120, 0, 40, 255, 240]);

    await session.SendEnterAsync(deadline.Token);
    byte[] enter = new byte[2];
    await wire.ReadExactlyAsync(enter, deadline.Token);
    ExpectBytes(enter, [13, 10]);
}

static async Task RawRecordingContainsOnlyReceivedTerminalPayload()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "raw.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Raw console", "127.0.0.1", port, recordRaw: true)
        .SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    await server.GetStream().WriteAsync(new byte[] { 255, 251, 1, 239, 187, 191, 13, 0, 65 }, deadline.Token);
    server.Client.Shutdown(SocketShutdown.Send);
    while (await session.ReadOutputAsync(deadline.Token) is not null) { }
    string log = Directory.GetFiles(Path.Combine(folder.Path, "Logs"), "*.raw.tttylog").Single();
    ExpectBytes(await File.ReadAllBytesAsync(log, deadline.Token), [239, 187, 191, 13, 65]);
}

static async Task RawResumeStartsANewPartWithoutBackfilling()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "pause.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Raw pause", "127.0.0.1", port, recordRaw: true)
        .SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    NetworkStream wire = server.GetStream();
    await wire.WriteAsync("A"u8.ToArray(), deadline.Token);
    await session.ReadOutputAsync(deadline.Token);
    await session.PauseRecordingAsync(deadline.Token);
    await wire.WriteAsync("B"u8.ToArray(), deadline.Token);
    await session.ReadOutputAsync(deadline.Token);
    await session.ResumeRecordingAsync(deadline.Token);
    await wire.WriteAsync("C"u8.ToArray(), deadline.Token);
    await session.ReadOutputAsync(deadline.Token);
    await session.PauseRecordingAsync(deadline.Token);

    string directory = Path.Combine(folder.Path, "Logs");
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-p001.raw.tttylog").Single()), [65]);
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-p002.raw.tttylog").Single()), [67]);
}

static async Task RecordingFormatsProjectTheSameSessionIndependently()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "formats.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Formats", "127.0.0.1", port, recordRaw: true,
        recordText: true, recordTerminal: true).SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    await session.SendTextAsync("echo", deadline.Token);
    await server.GetStream().WriteAsync("old\r\0new\a\u001b[6n\r\ntail"u8.ToArray(), deadline.Token);
    server.Client.Shutdown(SocketShutdown.Send);
    while (await session.ReadOutputAsync(deadline.Token) is not null) { }
    string directory = Path.Combine(folder.Path, "Logs");
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*.raw.tttylog").Single()),
        "old\rnew\a\u001b[6n\r\ntail"u8.ToArray());
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*.log").Single()),
        "new\r\ntail"u8.ToArray());
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*.term.tttylog").Single()),
        "echoold\rnew\r\ntail"u8.ToArray());
}

static async Task RecordingResumeDoesNotBackfillSplitUnits()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "cutoff.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Cutoff", "127.0.0.1", port, recordRaw: true,
        recordText: true, recordTerminal: true).SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    var wire = server.GetStream();
    await wire.WriteAsync(new byte[] { 65, 0xE3 }, deadline.Token);
    await session.ReadOutputAsync(deadline.Token);
    await session.PauseRecordingAsync(deadline.Token);
    await wire.WriteAsync(new byte[] { 0x81 }, deadline.Token);
    await session.ReadOutputAsync(deadline.Token);
    await session.ResumeRecordingAsync(deadline.Token);
    await wire.WriteAsync(new byte[] { 0x82, 13, 10, 66 }, deadline.Token);
    server.Client.Shutdown(SocketShutdown.Send);
    while (await session.ReadOutputAsync(deadline.Token) is not null) { }
    string directory = Path.Combine(folder.Path, "Logs");
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-p002.raw.tttylog").Single()),
        [0x82, 13, 10, 66]);
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-p002.log").Single()), [66]);
    ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-p002.term.tttylog").Single()), [13, 10, 66]);
}

static async Task ResizeKeepsTextRecordingBoundaries()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "resize.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Resize", "127.0.0.1", port, recordText: true)
        .SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    await session.ResizeAsync(6, 24, deadline.Token);
    var wire = server.GetStream();
    await wire.WriteAsync("ABCDEFGHIJKL\r\nabc\r\0"u8.ToArray(), deadline.Token);
    await session.ReadOutputAsync(deadline.Token);
    await session.ResizeAsync(3, 24, deadline.Token);
    await session.ResizeAsync(9, 24, deadline.Token);
    await wire.WriteAsync("\ntail"u8.ToArray(), deadline.Token);
    server.Client.Shutdown(SocketShutdown.Send);
    while (await session.ReadOutputAsync(deadline.Token) is not null) { }
    string log = Directory.GetFiles(Path.Combine(folder.Path, "Logs"), "*.log").Single();
    ExpectBytes(await File.ReadAllBytesAsync(log, deadline.Token), "ABCDEFGHIJKL\r\nabc\r\ntail"u8.ToArray());
}

static async Task EveryTerminalEncodingUsesTheSavedProfile()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var token = deadline.Token;
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    foreach (string encoding in TerminalEncodings.Identifiers)
    {
        string path = Path.Combine(folder.Path, encoding + ".ttty");
        var profile = ConnectionProfile.CreateTelnet(encoding, "127.0.0.1", port,
            receiveEncoding: encoding, transmitEncoding: encoding);
        await profile.SaveNewAsync(path, token);
        var accepted = listener.AcceptTcpClientAsync(token);
        await using var session = await TerminalSession.ConnectAsync(path, token, defaultRetainedRows: 100);
        using var peer = await accepted;
        try
        {
            var stream = peer.GetStream();
            await stream.ReadExactlyAsync(new byte[15], token);
            await stream.WriteAsync("ready\u001b[6n\r\nreceived"u8.ToArray(), token);
            while (!(await session.ReadOutputAsync(token))!.Contains("received")) { }
            byte[] reply = new byte[6];
            await stream.ReadExactlyAsync(reply, token);
            if (!reply.AsSpan().SequenceEqual("\u001b[1;6R"u8))
                throw new InvalidOperationException("Wrong cursor reply for " + encoding + ".");
            await session.SendTextAsync("AZ", token);
            byte[] sent = new byte[2];
            await stream.ReadExactlyAsync(sent, token);
            if (!sent.AsSpan().SequenceEqual("AZ"u8))
                throw new InvalidOperationException("Wrong ASCII transmission for " + encoding + ".");
        }
        finally { await session.ReleaseRetainedOutputAsync(); }
    }
}

static async Task FailedRecordingCannotStopTheSessionOrItsReplacementPart()
{
    const string failure = "Controlled recording write failure.";
    using var folder = new TemporaryDirectory();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var profile = ConnectionProfile.CreateTelnet("Failure", "127.0.0.1", 23,
        recordRaw: true, recordText: true, recordTerminal: true);
    string path = Path.Combine(folder.Path, "failure.ttty");
    await profile.SaveNewAsync(path, deadline.Token);
    var first = SessionRecording.Open(path, profile);
    var wire = new RecordingFailureWire();
    await using var session = new TerminalSession(wire, profile, first, 100);
    wire.Next = "before\r\n"u8.ToArray();
    await session.ReadOutputAsync(deadline.Token);
    first.MarkIncomplete(failure);
    wire.Next = "visible only\r\n"u8.ToArray();
    if (!(await session.ReadOutputAsync(deadline.Token))!.Contains("visible only"))
        throw new InvalidOperationException("A failed recording stopped the healthy terminal.");
    try
    {
        await session.PauseRecordingAsync(deadline.Token);
        throw new InvalidOperationException("A failed writer reported a successful pause.");
    }
    catch (IOException error) when (error.Message == failure) { }
    await session.ResumeRecordingAsync(deadline.Token);
    first.MarkIncomplete("Obsolete writer notification.");
    wire.Next = "after\r\n"u8.ToArray();
    await session.ReadOutputAsync(deadline.Token);
    await session.PauseRecordingAsync(deadline.Token);
    string logs = Path.Combine(folder.Path, "Logs");
    foreach (string extension in new[] { "raw.tttylog", "log", "term.tttylog" })
    {
        ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(logs, "*-p001." + extension).Single()),
            "before\r\n"u8.ToArray());
        ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(logs, "*-p002." + extension).Single()),
            "after\r\n"u8.ToArray());
    }
}

static async Task TextTabsUseTheSerializedLogicalColumn()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    listener.Start();
    string path = Path.Combine(folder.Path, "tabs.ttty");
    await ConnectionProfile.CreateTelnet("Tabs", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
        recordText: true).SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    await session.ResizeAsync(10, 4, deadline.Token);
    await server.GetStream().WriteAsync("ABCDEFGHIJA\tB\r\nA\tB\r\n"u8.ToArray(), deadline.Token);
    server.Client.Shutdown(SocketShutdown.Send);
    while (await session.ProcessOutputAsync(deadline.Token)) { }
    byte[] bytes = await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(folder.Path, "Logs"), "*.log").Single());
    ExpectBytes(bytes, "ABCDEFGHIJA       B\r\nA\tB\r\n"u8.ToArray());
}

static async Task PasteUsesNegotiatedBracketsWithoutAppendingEnter()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "paste.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("Paste console", "127.0.0.1", port).SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    NetworkStream wire = server.GetStream();
    await wire.ReadExactlyAsync(new byte[15], deadline.Token);
    await wire.WriteAsync("\u001B[?2004h>"u8.ToArray(), deadline.Token);
    while (await session.ReadOutputAsync(deadline.Token) is { } snapshot && !snapshot.SequenceEqual([">"])) { }
    await session.SendPasteAsync(TerminalPaste.Create("AB\r\nC"), 0, 0, cancellationToken: deadline.Token);
    byte[] bytes = new byte[17];
    await wire.ReadExactlyAsync(bytes, deadline.Token);
    ExpectBytes(bytes, "\u001B[200~AB\r\nC\u001B[201~"u8.ToArray());

    using var stop = new CancellationTokenSource();
    Task<bool> paced = session.SendPasteAsync(TerminalPaste.Create("XY"), 10000, 0,
        stopToken: stop.Token, cancellationToken: deadline.Token);
    byte[] started = new byte[7];
    await wire.ReadExactlyAsync(started, deadline.Token);
    ExpectBytes(started, "\u001B[200~X"u8.ToArray());
    try
    {
        await session.SendTextAsync("blocked", deadline.Token);
        throw new InvalidOperationException("Interactive input entered an active paste.");
    }
    catch (InvalidOperationException exception) when (exception.Message == "A paste is in progress.") { }
    await wire.WriteAsync("\u001B[5n\u001B[?2004l\rREADY"u8.ToArray(), deadline.Token);
    while (await session.ReadOutputAsync(deadline.Token) is { } frame &&
        !frame.Any(row => row.StartsWith("READY", StringComparison.Ordinal))) { }
    stop.Cancel();
    if (await paced) throw new InvalidOperationException("The paced paste was not stopped.");
    byte[] closed = new byte[10];
    await wire.ReadExactlyAsync(closed, deadline.Token);
    ExpectBytes(closed, "\u001B[201~\u001B[0n"u8.ToArray());
    await session.SendPasteAsync(TerminalPaste.Create("界"), 0, 0, cancellationToken: deadline.Token);
    byte[] next = new byte["界"u8.Length];
    await wire.ReadExactlyAsync(next, deadline.Token);
    ExpectBytes(next, "界"u8.ToArray());
}

static async Task FailedUtf8InputDoesNotEchoOrReplay()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "failed-input.ttty");
    var serial = new SerialSettings("COM15", 115200, 8, "none", 1, "none", false, false);
    var profile = ConnectionProfile.CreateSerial("Failed input", serial, localEcho: true, recordTerminal: true);
    var wire = new InterruptedInputWire();
    var recording = SessionRecording.Open(path, profile);
    await using var session = new TerminalSession(wire, profile, recording, 100);
    await session.SendTextAsync("済");
    try
    {
        await session.SendTextAsync("未送信");
        throw new InvalidOperationException("The fixture write was expected to fail.");
    }
    catch (IOException exception) when (exception.Message == InterruptedInputWire.Failure) { }
    var held = await session.GetOutputAsync();
    if (!held.Contains("済") || wire.Submitted.Count != 2)
        throw new InvalidOperationException("A failed write changed local echo or was replayed.");
    ExpectBytes(wire.Submitted[0], "済"u8.ToArray());
    ExpectBytes(wire.Submitted[1], "未送信"u8[..2].ToArray());
    await session.DisposeAsync();
    string file = Directory.EnumerateFiles(folder.Path, "*.term.tttylog", SearchOption.AllDirectories).Single();
    ExpectBytes(await File.ReadAllBytesAsync(file), "済"u8.ToArray());
    await session.ReleaseRetainedOutputAsync();
}

static async Task SavedSerialProfileKeepsItsLineSettings()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "serial.ttty");
    var settings = new SerialSettings("COM15", 115200, 7, "even", 2, "dsrDtr", false, true);
    await ConnectionProfile.CreateSerial("Lab serial", settings, localEcho: true, recordRaw: true).SaveNewAsync(path);
    var saved = await ConnectionProfile.LoadAsync(path);
    if (saved.Type != "serial" || saved.Serial != settings || saved.Enter != "cr" ||
        saved.Backspace != "bs" || saved.LocalEcho != "on" || !saved.RecordRaw)
        throw new InvalidOperationException("The serial profile did not preserve the line and terminal settings.");

    try
    {
        ConnectionProfile.CreateSerial("Invalid", settings with { PortName = @"\\.\COM15" });
        throw new InvalidOperationException("A device namespace was accepted as a COM port name.");
    }
    catch (InvalidDataException) { }
}

static void VaultDecryptsAnIndependentFixedContainer()
{
    using var vector = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vault-v1-vector.json")));
    var fields = vector.RootElement;
    byte[] file = Convert.FromHexString(fields.GetProperty("file").GetString()!);
    byte[] password = VaultContainer.NormalizeMaster(fields.GetProperty("password").GetString()!);
    byte[] root = new byte[32];
    byte[] kek = new byte[32];
    try
    {
        OpenSsl.DeriveVaultKey(password, file.AsSpan(52, 16), kek);
        ExpectBytes(kek, Convert.FromHexString(fields.GetProperty("kek").GetString()!));
        VaultContainer.Validate(file);
        VaultContainer.Unwrap(file, password, root);
        ExpectBytes(root, Convert.FromHexString(fields.GetProperty("root").GetString()!));
        byte[] plaintext = VaultContainer.Decrypt(file, root);
        try { ExpectBytes(plaintext, System.Text.Encoding.UTF8.GetBytes(fields.GetProperty("plaintext").GetString()!)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        file[^1] ^= 1;
        try { VaultContainer.Decrypt(file, root); throw new InvalidOperationException("An altered vault tag was accepted."); }
        catch (CryptographicException) { }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(password);
        CryptographicOperations.ZeroMemory(root);
        CryptographicOperations.ZeroMemory(kek);
    }
}

static void CredentialInputBoundsPreserveValidEncodings()
{
    ExpectBytes(VaultContainer.NormalizeMaster("example phrase e\u0301"), "example phrase é"u8.ToArray());
    string maximum = string.Concat(Enumerable.Repeat("\U0001F600", VaultContainer.MaximumMasterLength));
    ExpectBytes(VaultContainer.NormalizeMaster(maximum), System.Text.Encoding.UTF8.GetBytes(maximum));
    ExpectBytes(OpenSsl.PositiveInteger([0, 128], 1).ToArray(), [128]);
    try
    {
        _ = OpenSsl.PositiveInteger([1, 0], 1);
        throw new InvalidOperationException("An SSH integer exceeded the allowed unsigned width.");
    }
    catch (InvalidDataException) { }
}

static async Task SshPacketsAuthenticateBeforeReleasingPayload()
{
    foreach (string cipher in new[] { "aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "aes256-ctr" })
    {
        bool ctr = cipher.EndsWith("-ctr", StringComparison.Ordinal);
        byte[] key = new byte[cipher.StartsWith("aes128", StringComparison.Ordinal) ? 16 : 32];
        byte[] nonce = new byte[ctr ? 16 : 12];
        byte[] mac = ctr ? new byte[32] : [];
        using var stream = new MemoryStream();
        using (var outgoing = new SshPackets(stream))
        {
            outgoing.SetKeys(true, key.ToArray(), nonce.ToArray(), true, cipher, "hmac-sha2-256-etm@openssh.com", mac.ToArray());
            await outgoing.SendAsync("\x5etest-payload"u8.ToArray(), default);
        }
        byte[] packet = stream.ToArray();
        using (var valid = new SshPackets(new MemoryStream(packet)))
        {
            valid.SetKeys(false, key.ToArray(), nonce.ToArray(), true, cipher, "hmac-sha2-256-etm@openssh.com", mac.ToArray());
            ExpectBytes(await valid.ReadAsync(default), "\x5etest-payload"u8.ToArray());
        }
        packet[^1] ^= 1;
        using var altered = new SshPackets(new MemoryStream(packet));
        altered.SetKeys(false, key, nonce, true, cipher, "hmac-sha2-256-etm@openssh.com", mac);
        try { await altered.ReadAsync(default); throw new InvalidOperationException("An altered SSH packet was accepted."); }
        catch (CryptographicException) { }
    }
}

static async Task SshCredentialPacketsUseCoarseLengthBuckets()
{
    using var stream = new MemoryStream();
    using var writer = new SshPackets(stream);
    using var reader = new SshPackets(stream);
    try { await writer.SendAsync(new byte[5], default, padCredentials: true); }
    catch (InvalidOperationException) { }
    if (stream.Length != 0) throw new InvalidOperationException("A credential packet was sent before encryption.");

    foreach (string cipher in new[] { "aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "aes256-ctr" })
    {
        bool ctr = cipher.EndsWith("-ctr", StringComparison.Ordinal);
        foreach (int tagSize in ctr ? new[] { 32, 64 } : new[] { 16 })
        {
            string? mac = ctr ? $"hmac-sha2-{tagSize * 8}-etm@openssh.com" : null;
            int keySize = cipher.StartsWith("aes128", StringComparison.Ordinal) ? 16 : 32;
            int nonceSize = ctr ? 16 : 12;
            writer.SetKeys(true, new byte[keySize], new byte[nonceSize], true, cipher, mac, ctr ? new byte[tagSize] : []);
            reader.SetKeys(false, new byte[keySize], new byte[nonceSize], true, cipher, mac, ctr ? new byte[tagSize] : []);
            foreach (var (length, expectedBody) in new[] { (5, 128), (85, 128), (123, 128), (124, 256), (65536, 65664) })
            {
                byte[] payload = new byte[length];
                long start = stream.Position;
                await writer.SendAsync(payload, default, padCredentials: true);
                if (stream.Position - start != 4 + expectedBody + tagSize)
                    throw new InvalidOperationException("Incorrect size of a padded SSH credential packet.");
                stream.Position = start;
                ExpectBytes(await reader.ReadAsync(default), payload);
            }
        }
    }
}

static void OpenSslCiphersMatchIndependentVectors()
{
    // AES-128-CBC fixture with the traditional PEM derivation and an empty passphrase.
    ExpectBytes(OpenSsl.DecryptLegacyPem("AES-128-CBC", Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
        [], Convert.FromHexString("cd356969e1b016b933899e2cfc983681")), "PEM fixture"u8.ToArray());
    byte[] result = new byte[16];
    using (var counter = new OpenSsl.Ctr(Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c"),
        Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"), encrypt: true))
    {
        byte[] plaintext = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a");
        counter.Transform(plaintext.AsSpan(0, 7), result.AsSpan(0, 7));
        counter.Transform(plaintext.AsSpan(7), result.AsSpan(7));
    }
    ExpectBytes(result, Convert.FromHexString("874d6191b620e3261bef6864990db6ce"));

    byte[] tag = new byte[16];
    OpenSsl.Gcm(true, new byte[32], new byte[12], [], new byte[16], result, tag);
    ExpectBytes(result, Convert.FromHexString("cea7403d4d606b6e074ec5d3baf39d18"));
    ExpectBytes(tag, Convert.FromHexString("d0d1c8a799996bf0265b98b5d48ab919"));
    ExpectBytes(OpenSsl.Hmac("SHA256", Enumerable.Repeat((byte)0x0b, 20).ToArray(), 0x48692054, "here"u8, []),
        Convert.FromHexString("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"));
}

static async Task TemporaryLocalEchoDoesNotRewriteTheProfile()
{
    var transport = new PausedSendWire();
    transport.Resume();
    var profile = ConnectionProfile.CreateSerial("Echo", new SerialSettings("COM1"));
    await using var session = new TerminalSession(transport, profile, null, 5000);
    await session.SendTextAsync("hidden");
    await session.SetLocalEchoAsync("on");
    await session.SendTextAsync("shown");
    if (!session.LocalEchoEnabled || profile.LocalEcho != "off")
        throw new InvalidOperationException("A temporary echo change altered the saved policy.");
    if (!(await session.GetOutputAsync()).SequenceEqual(["shown"]))
        throw new InvalidOperationException("The changed local echo policy did not affect submitted input.");
    await session.SetLocalEchoAsync("off");
    await session.SendTextAsync("hidden again");
    if (!(await session.GetOutputAsync()).SequenceEqual(["shown"]))
        throw new InvalidOperationException("Disabling local echo changed retained output.");
}

static async Task QueuedInputRejectsExcessWithoutDisconnecting()
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var transport = new PausedSendWire();
    await using var session = new TerminalSession(transport,
        ConnectionProfile.CreateSerial("Input", new SerialSettings("COM1")), null, 5000);
    var sends = new List<Task<IReadOnlyList<string>>>();
    for (int index = 0; index < 256; index++) sends.Add(session.SendKeyAsync(TerminalKey.Up, cancellationToken: deadline.Token));
    try
    {
        await session.SendKeyAsync(TerminalKey.Up, cancellationToken: deadline.Token);
        throw new InvalidOperationException("The input item limit was exceeded.");
    }
    catch (ResourceLimitException) { }
    transport.Resume();
    await Task.WhenAll(sends);
    await session.SendTextAsync("still connected", deadline.Token);
    if (transport.Writes != 257) throw new InvalidOperationException("Input refusal lost or duplicated an accepted send.");
}

static void SearchRangesSurviveReflowThatRetiresAnEarlierPrefix()
{
    var output = new TerminalOutput(6, 100, 4);
    output.Receive(TerminalReceiveSource.Data, System.Text.Encoding.UTF8.GetBytes(new string('x', 650) + "MARKER"));
    var original = output.GetRows();
    var match = original.Find("MARKER", true)!.Value;
    output.Resize(4, 4);
    var updated = output.GetRows();
    TerminalMatch? mapped = updated.MapMatchFrom(original, match);
    if (mapped != updated.Find("MARKER", true))
        throw new InvalidOperationException("A surviving range lost its anchor when reflow retired the earlier prefix.");
}

static void SearchRangesSurviveUnrelatedTextAppends()
{
    var output = new TerminalOutput();
    output.Receive(TerminalReceiveSource.Data, "prefix match"u8);
    var original = output.GetRows();
    var found = original.Find("match", true)!.Value;
    output.Receive(TerminalReceiveSource.Data, " and more"u8);
    var updated = output.GetRows();
    if (updated.MapMatchFrom(original, found) != found)
        throw new InvalidOperationException("An unchanged search range was lost when its row grew.");
}

static void TerminalStateBudgetRefusesGrowthWithoutChangingOtherTerminals()
{
    var quota = new ResourceQuota(350000);
    var output = new TerminalOutput(20, 100, 4, "utf-8", false, 1, quota);
    bool refused = false;
    for (int index = 0; index < 1000 && !refused; index++)
    {
        try { output.Receive(TerminalReceiveSource.Data, "abcdefghijklmnopqrst\r\n"u8); }
        catch (ResourceLimitException) { refused = true; }
    }
    if (!refused || quota.Used > 350000 || quota.Used < 0)
        throw new InvalidOperationException("Terminal state admission exceeded its quota.");
    var unaffected = new TerminalOutput();
    unaffected.Receive(TerminalReceiveSource.Data, "still connected"u8);
    ExpectRows(unaffected, ["still connected"]);
}

static void ResourceReservationsRejectOverflowAndReleaseTheirOwnership()
{
    var quota = new ResourceQuota(100);
    using var first = quota.Reserve(60);
    using (var second = quota.Reserve(40))
    {
        try { using var extra = quota.Reserve(1); throw new InvalidOperationException("Quota overflow was admitted."); }
        catch (ResourceLimitException) { }
        if (quota.Used != 100) throw new InvalidOperationException("Failed admission altered resource ownership.");
    }
    if (quota.Used != 60) throw new InvalidOperationException("Released reservation remained charged.");
    first.Resize(100);
    first.Resize(20);
    if (quota.Used != 20) throw new InvalidOperationException("Reservation resize did not preserve accounting.");
    var child = new ResourceQuota(100, quota);
    using var nested = child.Reserve(80);
    try { using var extra = child.Reserve(1); throw new InvalidOperationException("Parent quota overflow was admitted."); }
    catch (ResourceLimitException) { }
    if (child.Used != 80 || quota.Used != 100)
        throw new InvalidOperationException("Parent quota failure changed resource ownership.");
}

static void ErasedCellsUseTheCurrentBackgroundWithoutInventingText()
{
    var output = new TerminalOutput(columns: 6, screenRows: 2);
    output.Receive(TerminalReceiveSource.Data, "ABCDE\r\u001b[44m\u001b[2C\u001b[K"u8);
    ExpectRows(output, ["AB"]);
    var snapshot = output.GetRows();
    var cells = snapshot.GetCells(0).Span;
    if (cells.Length != 6 || cells[2].Style.Background != 0x0000EE || cells[5].Style.Background != 0x0000EE)
        throw new InvalidOperationException("Erase did not paint blank cells with the current background.");
    output.Receive(TerminalReceiveSource.Data, "\u001b[0mX"u8);
    ExpectRows(output, ["ABX"]);
    output.Resize(8, 2);
    ExpectRows(output, ["ABX"]);
}

static void LegacyReceiveResynchronizesBeforeControlBytes()
{
    var output = new TerminalOutput(receiveEncoding: "cp932");
    output.Receive(TerminalReceiveSource.Data, [0x9B]);
    output.Receive(TerminalReceiveSource.StandardError, "X"u8);
    output.Receive(TerminalReceiveSource.Data, [0x5B, 0x81, 0x60, 0x82, 0x0D, 0x0A, 0x41]);
    ExpectRows(output, ["X\u5ABC\uFF5E\uFFFD", "A"]);

    var euc = new TerminalOutput(receiveEncoding: "euc-jp");
    euc.Receive(TerminalReceiveSource.Data, [0xA1, 0xC1, 0xF9, 0xA1, 0x8E, 0xB1, 0x8F, 0xA1, 0xA1]);
    ExpectRows(euc, ["\uFF5E\u7E8A\uFF71\uFFFD"]);

    var strict932 = new TerminalOutput(receiveEncoding: "cp932");
    strict932.Receive(TerminalReceiveSource.Data, [0xA0, 0xFD, 0xFE, 0xFF]);
    ExpectRows(strict932, ["\uFFFD\uFFFD\uFFFD\uFFFD"]);
    var strict857 = new TerminalOutput(receiveEncoding: "cp857");
    strict857.Receive(TerminalReceiveSource.Data, [0xD5]);
    ExpectRows(strict857, ["\uFFFD"]);
    var strict950 = new TerminalOutput(receiveEncoding: "cp950");
    strict950.Receive(TerminalReceiveSource.Data, [0xA2, 0xA4]);
    ExpectRows(strict950, ["\u2550"]);
}

static void EveryProfileEncodingDecodesAscii()
{
    if (TerminalEncodings.Identifiers.Distinct().Count() != 47)
        throw new InvalidOperationException("The profile encoding inventory is incomplete.");
    foreach (string encoding in TerminalEncodings.Identifiers)
    {
        var output = new TerminalOutput(receiveEncoding: encoding);
        output.Receive(TerminalReceiveSource.Data, "A"u8);
        ExpectRows(output, ["A"]);
        output.ReleaseNativeState();
    }
}

static void JisDesignationsRemainIndependentOfVtCommands()
{
    var output = new TerminalOutput(receiveEncoding: "jis", receiveSevenBitKana: true);
    output.Receive(TerminalReceiveSource.Data, "\u001B("u8);
    output.Receive(TerminalReceiveSource.StandardError, "X"u8);
    output.Receive(TerminalReceiveSource.Data, "J\\~\u001B$B!A\u001B$(D!!\u001B(B\u000E1\u000F"u8);
    ExpectRows(output, ["X\u00A5\u203E\uFF5E\uFFFD\uFF71"]);
    output.Receive(TerminalReceiveSource.Data, "\u001B[1;1HA"u8);
    ExpectRows(output, ["A\u00A5\u203E\uFF5E\uFFFD\uFF71"]);
}

static async Task MissingComPortFailsTheSavedConnectionAttempt()
{
    if (!OperatingSystem.IsWindows()) return;
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "missing-com.ttty");
    await ConnectionProfile.CreateSerial("Missing port", new SerialSettings("COM4294967295")).SaveNewAsync(path);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
        throw new InvalidOperationException("An unavailable COM port reported a successful connection.");
    }
    catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception) { }
}

static async Task JisTransmissionUsesIcuDesignations()
{
    using var folder = new TemporaryDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    string path = Path.Combine(folder.Path, "jis.ttty");
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await ConnectionProfile.CreateTelnet("JIS console", "127.0.0.1", port, receiveEncoding: "cp932",
        transmitEncoding: "jis", japanese: new JapaneseSettings("@", "J", TransmitKana: "soSi"))
        .SaveNewAsync(path, deadline.Token);
    var incoming = listener.AcceptTcpClientAsync(deadline.Token);
    await using var session = await TerminalSession.ConnectAsync(path, deadline.Token);
    using var server = await incoming;
    NetworkStream wire = server.GetStream();
    await wire.ReadExactlyAsync(new byte[15], deadline.Token);
    try
    {
        await session.SendPasteAsync(TerminalPaste.Create("A\u301C"), 0, 0, cancellationToken: deadline.Token);
        throw new InvalidOperationException("A best-fit substitution was accepted.");
    }
    catch (System.Text.EncoderFallbackException) { }

    await session.SendPasteAsync(TerminalPaste.Create("\uFF5E\\\uFF71"), 0, 0, cancellationToken: deadline.Token);
    byte[] expected = "\u001B$B!A\u001B(B\\\u000E1\u000F"u8.ToArray();
    byte[] actual = new byte[expected.Length];
    await wire.ReadExactlyAsync(actual, deadline.Token);
    ExpectBytes(actual, expected);
    await wire.WriteAsync("\u001B[?1h>"u8.ToArray(), deadline.Token);
    while (await session.ReadOutputAsync(deadline.Token) is { } rows && !rows.Any(row => row.EndsWith('>'))) { }
    await session.SendKeyAsync(TerminalKey.Up, cancellationToken: deadline.Token);
    await session.SendKeyAsync(TerminalKey.Left, TerminalModifiers.Control, deadline.Token);
    byte[] keys = new byte[9];
    await wire.ReadExactlyAsync(keys, deadline.Token);
    ExpectBytes(keys, "\u001BOA\u001B[1;5D"u8.ToArray());
}

static void WideCellsWrapAndEraseAsAWhole()
{
    var output = new TerminalOutput(columns: 4);
    output.Receive(TerminalReceiveSource.Data, "ABあ"u8);
    ExpectRows(output, ["ABあ"]);
    output.Receive(TerminalReceiveSource.Data, "X"u8);
    ExpectRows(output, ["ABあ", "X"]);
    output.Receive(TerminalReceiveSource.Data, "\u001B[1;4HZ"u8);
    ExpectRows(output, ["AB Z", "X"]);
}

static void UnicodeClustersKeepTheirTextAcrossReceiveChunks()
{
    var combined = new TerminalOutput(columns: 3);
    combined.Receive(TerminalReceiveSource.Data, "ABe"u8);
    combined.Receive(TerminalReceiveSource.Data, "\u0301"u8);
    ExpectRows(combined, ["ABe\u0301"]);
    combined.Receive(TerminalReceiveSource.Data, "Z"u8);
    ExpectRows(combined, ["ABe\u0301", "Z"]);

    var emoji = new TerminalOutput(columns: 4);
    emoji.Receive(TerminalReceiveSource.Data, "ABC\u2764"u8);
    emoji.Receive(TerminalReceiveSource.Data, "\uFE0F"u8);
    ExpectRows(emoji, ["ABC", "\u2764\uFE0F"]);

    var zeroWidth = new TerminalOutput(columns: 2);
    zeroWidth.Receive(TerminalReceiveSource.Data, "A\uFEFFB"u8);
    ExpectRows(zeroWidth, ["A\uFEFFB"]);
}

static void TerminalModesControlWrapAndLineMovement()
{
    var output = new TerminalOutput(columns: 4);
    output.Receive(TerminalReceiveSource.Data, "\u001B[?7lABCあ"u8);
    ExpectRows(output, ["ABあ"]);
    output.Receive(TerminalReceiveSource.Data, "\u001B[20h\nX"u8);
    ExpectRows(output, ["ABあ", "X"]);
    output.Receive(TerminalReceiveSource.Data, "\u001BDY\u001BEZ"u8);
    ExpectRows(output, ["ABあ", "X", " Y", "Z"]);
}

// Add the invocation beside RecordingFormatsProjectTheSameSessionIndependently:
// await RecordingPreservesUnicodeAcrossLargeLogBuffers();
// Console.WriteLine("PASS RecordingPreservesUnicodeAcrossLargeLogBuffers");

static async Task RecordingPreservesUnicodeAcrossLargeLogBuffers()
{
    using var folder = new TemporaryDirectory();
    string path = Path.Combine(folder.Path, "unicode-log-buffers.ttty");
    var profile = ConnectionProfile.CreateSerial("Unicode log buffers", new SerialSettings("COM1"),
        recordRaw: true, recordText: true, recordTerminal: true);
    await profile.SaveNewAsync(path);
    var recording = SessionRecording.Open(path, profile);
    var terminal = new TerminalOutput(1024, 5000, 4);
    var projections = new TerminalRecordings(terminal, profile, recording, 5000, (1024, 4));
    // The first surrogate pair straddles the builder's original 16-character chunk.
    // The combined line is wider than the stack scratch buffer in UTF-16, but fits the grid.
    string cluster = "\U0001F642" + new string('\u0301', 10);
    string text = new string('x', 15) + string.Concat(Enumerable.Repeat(cluster, 400)) + "\r\n";
    byte[] expected = System.Text.Encoding.UTF8.GetBytes(text);
    try
    {
        projections.Receive(TerminalReceiveSource.Data, expected);
        projections.CompleteReceive();
    }
    finally { await recording.FinishAsync(); }
    if (recording.Failure is { } failure) throw new InvalidOperationException(failure);
    string directory = Path.Combine(folder.Path, "Logs");
    foreach (string pattern in new[] { "*.raw.tttylog", "*.log", "*.term.tttylog" })
        ExpectBytes(await File.ReadAllBytesAsync(Directory.GetFiles(directory, pattern).Single()), expected);
}

sealed class TemporaryDirectory : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateDirectory(
        System.IO.Path.Combine(AppContext.BaseDirectory, $"tabtty-tests-{Guid.NewGuid():N}"));
    public string Path => directory.FullName;
    public void Dispose() => directory.Delete(recursive: true);
}

sealed class PausedSendWire : ISessionWire
{
    private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Writes { get; private set; }
    public void Resume() => resume.TrySetResult();
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => ValueTask.FromResult(0);
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await resume.Task.WaitAsync(cancellationToken);
        Writes++;
    }
    public void Dispose() => Resume();
}

sealed class InterruptedInputWire : ISessionWire
{
    internal const string Failure = "Fixture lost the connection within a UTF-8 scalar.";
    internal List<byte[]> Submitted { get; } = [];
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => ValueTask.FromResult(0);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (Submitted.Count == 0)
        {
            Submitted.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        Submitted.Add(buffer[..2].ToArray());
        return ValueTask.FromException(new IOException(Failure));
    }
    public void Dispose() { }
}

sealed class RecordingFailureWire : ISessionWire
{
    internal byte[] Next { get; set; } = [];

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Next.AsSpan().CopyTo(buffer.Span);
        int length = Next.Length;
        Next = [];
        return ValueTask.FromResult(length);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public void Dispose() { }
}
