using System.Text;
using TabTty.Core;

internal static partial class ArchitectureOwnershipContracts
{
    internal static async Task RunAsync()
    {
        SharedChannelCreditRequiresCommitment();
        await PacketWriterOrdersBothChannelsAndReservesControlAsync();
        await CancelledPacketLeavesTheCipherAndCreditUntouchedAsync();
        PublicationRetainedCapacityCanBeReconciled(count: 64, maxBytes: 1024);
        PublicationRetainedCapacityCanBeReconciled(count: 15, maxBytes: 16 * 1024 * 1024);
        VerifiedPublicationReconcilesTheOriginalOperation();
        ArchiveReinspectionDoesNotMoveTheReplacement();
        await PartialMasterChangeReconcilesWithoutRepublishingAsync();
        await PublicationObservationLeavesAnIssuedMutationOwnedAsync();
        await PublicationCapacityStaysOwnedUntilActualReleaseAsync();
        await PublicationRejectsPhysicalAliasesAndChangedCandidatesAsync();
        PublicationRecoveryRetainsEarlierEvidence();
        PublicationRecoveryHandsOffSurvivingStage();
        await VaultResultAndMetadataAdmissionPrecedeMutationAsync();
        await VaultMasterInputAndRootFailuresPreserveTheGenerationAsync();
        await UnissuedVaultCleanupRetiresWithoutAdoptingAsync();
        await VaultRestoreRetainsIssuedReplacementAsync(unlocked: false);
        await VaultRestoreRetainsIssuedReplacementAsync(unlocked: true);
        await VaultRestoreRefusesChangedSourcesAndRescuesOnlyAuthenticatedBytesAsync();
        await VaultInitializationPreservesObservedFileIdentitiesAsync();
        DisplayRetainsWidthAndAdvancesPastOmittedScalars();
        ExactEmojiWidthsSurviveEveryByteSplit();
        await OmittedGraphemeScalarsStayInEligibleLogsAsync();
        await BroadcastPasteReservesEveryStartBeforeSubmissionAsync();
        await PasteAdmissionRejectsChangedTerminalModeAsync();
        await PreparedPasteRetainsSourceAndCapacityUntilSendReturnsAsync();
        await ReservedPasteEchoSurvivesPressureThenEnforcesLaterAdmissionAsync();
        await FinalUnencodablePageRefusesEveryBroadcastTargetAsync();
        await BoundedPasteAndReceiveHaveIndependentLifetimesAsync();
        await PartialPasteStopsAtACompleteUnitAsync("utf-8", "😀\r\nx", 2, "😀"u8.ToArray(), "😀");
        await PartialPasteStopsAtACompleteUnitAsync("utf-8", "\r\nx", 1, "\r\n"u8.ToArray(), "");
        await PartialPasteStopsAtACompleteUnitAsync("utf-8", "\rx", 1, [13, 0], "");
        await PartialPasteStopsAtACompleteUnitAsync("iso-8859-1", "ÿx", 1, [255, 255], "ÿ");
        await PartialPasteStopsAtACompleteUnitAsync("jis", "日本語", 4, [27, 36, 66, 0x46, 0x7c, 27, 40, 66], "日");
        await PartialPasteStopsAtACompleteUnitAsync("utf-8", "👩‍💻x", 2, "👩"u8.ToArray(), "👩", ai: true);
        await RecordingOwnershipSurvivesObserverAndWriteFailureAsync();
        await RecordingCloseFailureKeepsTheGroupOwnedAsync(timeOut: false);
        await RecordingCloseFailureKeepsTheGroupOwnedAsync(timeOut: true);
        await CancelledRecordingPreparationRetainsItsWholeGroupAsync(failSecondOpen: true);
        await CancelledRecordingPreparationRetainsItsWholeGroupAsync(failSecondOpen: false);
        await ProtectedCloseRetainsPendingRecordingAsync();
        await CompletedRecordingDoesNotWaitForTransportReleaseAsync();
        await RepeatedEofAndPauseShareOneRecordingDrainAsync();
        await RepeatedEofAndPauseShareOneRecordingDrainAsync(failRawFlush: true);
        await TelnetGeometryReportsSupersessionAndFailureAsync();
        await TelnetConsumerFailureSettlesOnlyItsOriginalDeliveryAsync();
        await OrdinarySendsReportTheirDefinitePrefixAsync();
        await OrdinaryTextEchoKeepsOnlyDefiniteCompleteUnitsAsync();
        await PendingTerminalInputLeavesReceiveAvailableAsync(ai: false);
        await PendingTerminalInputLeavesReceiveAvailableAsync(ai: true);
        await CancelledSerialOpenRetainsTheDeviceUntilItClosesAsync();
        await SerialReceiveFailurePreservesAcceptedOutputAsync();
    }

    private static async Task TelnetConsumerFailureSettlesOnlyItsOriginalDeliveryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var observer = new CancellationTokenSource();
        using var wire = new HeldWriteWire([65, 255, 251, 1, 66], holdClose: true);
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnFromConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Controlled Telnet callback failure after applying a payload.");
        var terminal = new TerminalOutput();
        int calls = 0, transitions = 0, eof = 0;
        wire.ReleaseWrite.SetResult();
        var profile = ConnectionProfile.CreateTelnet("Held Telnet delivery", "127.0.0.1");
        var transport = new SessionTransport(wire, profile, (80, 24), async (source, bytes, _) =>
        {
            calls++;
            terminal.Receive(source, bytes);
            consumed.SetResult();
            await returnFromConsumer.Task;
            throw failure;
        }, (_, _) => { transitions++; return Task.CompletedTask; }, (_, _) => { eof++; return Task.CompletedTask; });
        var delivery = transport.ReceiveAsync(deadline.Token);
        try
        {
            await consumed.Task.WaitAsync(deadline.Token);
            var closing = transport.DisconnectAsync();
            await wire.CloseStarted.Task.WaitAsync(deadline.Token);
            var waiting = closing.WaitAsync(observer.Token);
            observer.Cancel();
            try
            {
                await waiting;
                throw new InvalidOperationException("The closed transport observer did not stop.");
            }
            catch (OperationCanceledException) when (observer.IsCancellationRequested) { }
            if (closing.IsCompleted || transport.DeliverySettled.IsCompleted || transport.ActualRelease.IsCompleted)
                throw new InvalidOperationException("Observer cancellation retired held transport work.");
            returnFromConsumer.SetResult();
            try
            {
                await delivery;
                throw new InvalidOperationException("The failed Telnet consumer looked successful.");
            }
            catch (IOException error) when (ReferenceEquals(error, failure)) { }
            await transport.DeliverySettled.WaitAsync(deadline.Token);
            if (transport.ActualRelease.IsCompleted || !ReferenceEquals(transport.Status.DeliveryFailure, failure) ||
                calls != 1 || transitions != 0 || eof != 0 || !terminal.GetRows().Contains("A"))
                throw new InvalidOperationException("The failure replayed payload, delivered later facts or retired the device.");
        }
        finally
        {
            returnFromConsumer.TrySetResult();
            wire.ReleaseClose.TrySetResult();
            await transport.DisconnectAsync().WaitAsync(deadline.Token);
            terminal.ReleaseNativeState();
        }
    }

    private static async Task OrdinarySendsReportTheirDefinitePrefixAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] payload = "draft"u8.ToArray();
        await VerifyAsync(ConnectionProfile.CreateTelnet("TCP short write", "127.0.0.1"), TransportSubmission.Uncertain, 2);
        await VerifyAsync(ConnectionProfile.CreateSerial("COM short write", new("COM1")), TransportSubmission.Partial, 1);

        async Task VerifyAsync(ConnectionProfile profile, TransportSubmission status, int calls)
        {
            var wire = new ShortWriteWire();
            var transport = new SessionTransport(wire, profile, (80, 24), (_, _, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
            try
            {
                var result = await transport.SubmitPreparedAsync(payload, deadline.Token);
                if (result.Status != status || result.DefiniteBytes != 2 || result.Error is not IOException ||
                    wire.Calls != calls || !wire.First.AsSpan().SequenceEqual("dr"u8))
                    throw new InvalidOperationException("An ordinary short write lost its definite prefix or retried a failed suffix.");
            }
            finally { await transport.DisconnectAsync().WaitAsync(deadline.Token); }
        }
    }

    private static async Task OrdinaryTextEchoKeepsOnlyDefiniteCompleteUnitsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await VerifyAsync("utf-8", "A😀Z", [27, 65, 0xf0, 0x9f], "A",
            (session, input) => session.SendTextAsync(input.Text, deadline.Token, altPrefix: true), alt: true);
        await VerifyAsync("iso-8859-1", "AÿZ", [65, 255], "A", async (session, input) =>
        {
            using var prepared = await session.PrepareInputAsync(input, null, deadline.Token);
            await prepared!.SendAsync(deadline.Token);
        });
        await VerifyAsync("jis", "日本語", [27, 36, 66, 0x46, 0x7c, 0x4b], "日",
            (session, input) => session.SendTextAsync(input.Text, deadline.Token));
        await VerifyAsync("utf-8", "A😀Z", [65, 0xf0, 0x9f], "A", async (session, input) =>
        {
            var lease = session.TryGrantAiInput() ?? throw new InvalidOperationException("AI ownership was not admitted.");
            try
            {
                long layout = await session.ObserveAiAsync((_, state) => state.LayoutEpoch, deadline.Token);
                var attempt = new AiInputAttempt(lease, layout, deadline.Token);
                try { await session.SendAiInputAsync(input, null, attempt, deadline.Token); }
                catch (IOException) when (attempt.Admitted && attempt.CompletedWrites == 1) { throw; }
                catch (IOException error) { throw new InvalidOperationException("AI input lost its definite submitted prefix.", error); }
            }
            finally { await session.ReleaseAiInputAsync(lease); }
        });

        async Task VerifyAsync(string encoding, string text, byte[] first, string expectedEcho,
            Func<TerminalSession, TerminalInput, Task> send, bool alt = false)
        {
            var wire = new ShortWriteWire(first.Length);
            var profile = ConnectionProfile.CreateTelnet("Held written prefix", "127.0.0.1", transmitEncoding: encoding);
            await using var session = new TerminalSession(wire, profile, null, 100);
            try
            {
                await send(session, new(TerminalInputKind.Text, text, Alt: alt));
                throw new InvalidOperationException("A failed ordinary send looked successful.");
            }
            catch (IOException error) when (error.Message == "Controlled failure after the first short write.") { }
            string echoed = string.Concat(await session.GetOutputAsync(deadline.Token));
            if (!wire.First.AsSpan().SequenceEqual(first) || wire.Calls != 2 || echoed != expectedEcho)
                throw new InvalidOperationException($"The {encoding} ordinary send echoed a partial unit or retried its suffix.");
        }
    }

    private sealed class ShortWriteWire(int firstWrite = 2) : ISessionWire
    {
        internal int Calls { get; private set; }
        internal byte[] First { get; private set; } = [];
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) =>
            throw new InvalidOperationException("The ordinary output path did not observe short writes.");
        public ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (++Calls != 1) throw new IOException("Controlled failure after the first short write.");
            First = buffer[..firstWrite].ToArray();
            return ValueTask.FromResult(First.Length);
        }
        public void Dispose() { }
    }

    private static async Task PendingTerminalInputLeavesReceiveAvailableAsync(bool ai)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        using var wire = new HeldWriteWire("marker"u8.ToArray());
        var profile = ConnectionProfile.CreateTelnet("Input held during receive", "127.0.0.1");
        await using var session = new TerminalSession(wire, profile, null, 100);
        var input = new TerminalInput(TerminalInputKind.Text, "input");
        if (ai)
        {
            var lease = session.TryGrantAiInput() ?? throw new InvalidOperationException("AI input was not admitted.");
            try
            {
                long layout = await session.ObserveAiAsync((_, observed) => observed.LayoutEpoch, token);
                var attempt = new AiInputAttempt(lease, layout, token);
                await VerifyAsync(session.SendAiInputAsync(input, null, attempt, token));
            }
            finally { await session.ReleaseAiInputAsync(lease); }
        }
        else
        {
            using var prepared = await session.PrepareInputAsync(input, null, token);
            await VerifyAsync(prepared!.SendAsync(token));
        }

        async Task VerifyAsync(Task sending)
        {
            try
            {
                await wire.WriteStarted.Task.WaitAsync(token);
                var output = await session.ReadOutputAsync(token);
                if (output is null || !output.Contains("marker") || sending.IsCompleted)
                    throw new InvalidOperationException("Receive handling waited for pending terminal input.");
            }
            finally
            {
                wire.ReleaseWrite.TrySetResult();
                await sending.WaitAsync(token);
            }
        }
    }

    private static async Task PacketWriterOrdersBothChannelsAndReservesControlAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var leave = new CancellationTokenSource();
        var order = new SshPacketWriterOrder();
        await order.WaitAsync(deadline.Token);
        var queue = new List<Task>();
        Task? abandoned = null;
        for (int i = 0; i < 32; i++)
        {
            if (i == 1) queue.Add(order.WaitAsync(deadline.Token).AsTask());
            Task next = order.WaitAsync(i == 15 ? leave.Token : deadline.Token, payload: true).AsTask();
            if (i == 15) abandoned = next;
            else queue.Add(next);
        }
        try
        {
            await order.WaitAsync(deadline.Token, payload: true);
            throw new InvalidOperationException("Payload took the SSH writer's reserved control admission.");
        }
        catch (ResourceLimitException) { }
        leave.Cancel();
        try { await abandoned!; throw new InvalidOperationException("A stopped packet remained admitted."); }
        catch (OperationCanceledException) { }
        queue.Add(order.WaitAsync(deadline.Token, payload: true).AsTask());
        queue.Add(order.WaitAsync(deadline.Token).AsTask());
        for (int i = 0; i < queue.Count; i++)
        {
            if (queue[i].IsCompleted) throw new InvalidOperationException("An SSH writer bypassed an earlier admission.");
            order.Release();
            await queue[i].WaitAsync(deadline.Token);
        }
        order.Release();
        await order.WaitAsync(deadline.Token);
        order.Release();
    }

    private static async Task FinalUnencodablePageRefusesEveryBroadcastTargetAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var firstWire = new HeldWriteWire("\u001b[?2004h"u8.ToArray());
        using var secondWire = new HeldWriteWire("\u001b[?2004h"u8.ToArray());
        var utf = ConnectionProfile.CreateTelnet("UTF-8", "127.0.0.1");
        var latin = ConnectionProfile.CreateTelnet("Latin", "127.0.0.1", transmitEncoding: "iso-8859-1");
        await using var first = new TerminalSession(firstWire, utf, null, 100);
        await using var second = new TerminalSession(secondWire, latin, null, 100);
        await first.ReadOutputAsync(token);
        await second.ReadOutputAsync(token);
        var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shared = TerminalPaste.Create(new string('x', 64 * 1024) + "日");
        using var prepared = await first.PreparePasteAsync(shared, 0, 0, token, token, null, commit.Task);
        Task<bool> sending = prepared.SendAsync(token);
        try
        {
            if (!await prepared.PasteReady!.WaitAsync(token))
                throw new InvalidOperationException("The UTF-8 target refused valid text.");
            try
            {
                using var invalid = await second.PreparePasteAsync(shared, 0, 0, token, token, null, commit.Task);
                throw new InvalidOperationException("Preflight stopped before the invalid final page.");
            }
            catch (ArgumentException) { }
        }
        finally { commit.TrySetResult(false); }
        if (await sending.WaitAsync(token) || firstWire.WriteStarted.Task.IsCompleted || secondWire.WriteStarted.Task.IsCompleted)
            throw new InvalidOperationException("A target emitted payload or bracket framing before collective preflight.");
    }

    private static async Task OmittedGraphemeScalarsStayInEligibleLogsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string heart = "\u2764\uFE0F" + new string('\u0301', 254);
        string narrowHeart = "\u2764\uFE0F" + new string('\u0301', 253) + "\uFE0E";
        string indic = "\u0915" + new string('\u093C', 255);
        string woman = "\U0001F469" + new string('\u0301', 254) + "\u200D";
        foreach (bool alternate in new[] { false, true })
        foreach (int fragment in new[] { 1, 2, 3, 4, 7, 8192 })
        {
            await VerifyAsync(heart, "\uFE0E", 2, alternate, fragment);
            await VerifyAsync(narrowHeart, "", 1, alternate, fragment);
            // The omitted virama must still link the next consonant into the retained grapheme.
            await VerifyAsync(indic, "\u094D\u0915", 1, alternate, fragment);
            await VerifyAsync(woman, "\U0001F4BB", 2, alternate, fragment, "\U0001F600");
        }

        async Task VerifyAsync(string retained, string omitted, int width, bool alternate, int fragment, string nextEmoji = "")
        {
            string tail = nextEmoji + "X\r\n";
            string original = retained + omitted + tail;
            var files = Enum.GetValues<RecordingFormat>().ToDictionary(format => format, _ => new HeldRecordingFile(holdFlush: true));
            foreach (var file in files.Values) file.CompleteFlush.SetResult();
            var recording = new SessionRecording([.. files.Keys], TimeProvider.System, format => files[format]);
            var profile = ConnectionProfile.CreateSsh("Grapheme projections", "127.0.0.1", "fixture",
                recordRaw: true, recordText: true, recordTerminal: true);
            using var wire = new HeldWriteWire([]);
            await using var session = new TerminalSession(wire, profile, recording, 100);
            byte[] stream = Encoding.UTF8.GetBytes((alternate ? "\u001b[?1049h" : "") + original);
            for (int offset = 0; offset < stream.Length; offset += fragment)
                await session.ProcessPayloadAsync(TerminalReceiveSource.Data,
                    stream.AsSpan(offset, Math.Min(fragment, stream.Length - offset)).ToArray(), deadline.Token);
            var view = (TerminalSnapshot)await session.GetOutputAsync(deadline.Token);
            var cells = view.GetCells(0).Span;
            int expectedCells = nextEmoji.Length == 0 ? 2 : 3;
            if (view.IsAlternateScreen != alternate || cells.Length != expectedCells || cells[0].Width != width ||
                cells[0].Text != retained || cells[1].Column != width || cells[^1].Text != "X" ||
                (nextEmoji.Length != 0 && (cells[1].Text != nextEmoji || cells[1].Width != 2 || cells[2].Column != width + 2)))
                throw new InvalidOperationException($"Stored spans changed with alternate={alternate}, fragment={fragment}.");
            await session.PauseRecordingAsync(deadline.Token);
            byte[] text = Encoding.UTF8.GetBytes(alternate ? original : retained + tail);
            byte[] terminal = Encoding.UTF8.GetBytes(original);
            if (!files[RecordingFormat.Raw].Bytes.ToArray().AsSpan().SequenceEqual(stream) ||
                !files[RecordingFormat.Text].Bytes.ToArray().AsSpan().SequenceEqual(text) ||
                !files[RecordingFormat.Terminal].Bytes.ToArray().AsSpan().EndsWith(terminal))
                throw new InvalidOperationException($"Recording bytes changed with alternate={alternate}, fragment={fragment}.");
        }
    }

    private static async Task PartialMasterChangeReconcilesWithoutRepublishingAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        string path = Path.Combine(directory.Path, "master-change.tttykey");
        const string oldPassword = "original-master-fixture";
        const string newPassword = "replacement-master-fixture";
        using var vault = await CredentialVault.CreateAsync(path, oldPassword.AsMemory(), token);
        string entry = await vault.AddPasswordAsync("fixture", "verified secret payload"u8.ToArray(), token);
        byte[] original = await File.ReadAllBytesAsync(path, token);
        long revision = vault.Revision;
        var before = await vault.ReadMetadataAsync(token);
        int issues = 0;
        Guid operation;
        try
        {
            await vault.ChangeMasterPasswordAsync(newPassword.AsMemory(), (stage, target, preservation) =>
            {
                issues++;
                File.Replace(stage, target, preservation!);
                File.Delete(preservation!);
                throw new IOException("Fixture cannot verify preservation after replacement.");
            }, token);
            throw new InvalidOperationException("The new root was accepted with missing preservation.");
        }
        catch (FilePublicationException error) when (error.Result.Status == FilePublicationStatus.UnverifiedPreservation)
        {
            operation = error.Result.OperationId;
        }
        byte[] candidate = await File.ReadAllBytesAsync(path, token);
        if (!vault.HasUnverifiedPublication || vault.Revision != revision || original.AsSpan().SequenceEqual(candidate) ||
            VerifiedFilePublication.Reconcile(path)?.OperationId != operation)
            throw new InvalidOperationException("The partial operation did not retain its original root and evidence.");
        try
        {
            using VaultSecret secret = await vault.GetPasswordAsync(entry, token);
            throw new InvalidOperationException("A partial publication delivered a credential.");
        }
        catch (IOException) { }
        try
        {
            _ = await vault.ReadMetadataAsync(token);
            throw new InvalidOperationException("A partial publication exposed metadata as current.");
        }
        catch (IOException) { }
        if (await vault.ReconcilePublicationAsync())
            throw new InvalidOperationException("Reconciliation accepted a missing original.");
        await File.WriteAllBytesAsync(path + ".bak", original, token);
        if (!await vault.ReconcilePublicationAsync() || vault.Revision != revision + 1 || issues != 1 ||
            vault.HasUnverifiedPublication || !(await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(candidate))
            throw new InvalidOperationException("Reconciliation rewrote the candidate or failed to adopt its exact generation.");
        using VaultSecret after = await vault.GetPasswordAsync(entry, token);
        var metadata = await vault.ReadMetadataAsync(token);
        if (!after.Bytes.Span.SequenceEqual("verified secret payload"u8))
            throw new InvalidOperationException("The newly adopted root did not retrieve the existing entry.");
        if (metadata.Revision != before.Revision + 1 || !metadata.Entries.SequenceEqual(before.Entries))
            throw new InvalidOperationException("The reconciled root, revision and metadata describe different generations.");
        using var reopened = await CredentialVault.UnlockAsync(path, newPassword.AsMemory(), token);
        using var oldCopy = await CredentialVault.UnlockAsync(path + ".bak", oldPassword.AsMemory(), token);
        if (reopened.Id != vault.Id || oldCopy.Id != vault.Id)
            throw new InvalidOperationException("The master change altered identity or the preserved encrypted generation.");
    }

    private static async Task TelnetGeometryReportsSupersessionAndFailureAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var wire = new HeldWriteWire([255, 253, 31]);
        var transport = new SessionTransport(wire, ConnectionProfile.CreateTelnet("Geometry", "127.0.0.1"), (80, 24),
            (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        try
        {
            wire.ReleaseWrite.SetResult();
            await transport.StartAsync(deadline.Token);
            await transport.ReceiveAsync(deadline.Token);
            var stale = transport.CommitGeometry(100, 25);
            var current = transport.CommitGeometry(255, 36);
            if ((await transport.ApplyGeometryAsync(stale, deadline.Token)).Status != GeometryNotification.Superseded ||
                (await transport.ApplyGeometryAsync(current, deadline.Token)).Status != GeometryNotification.Submitted)
                throw new InvalidOperationException("Telnet did not submit only the newest geometry.");
            byte[] expected = [255, 250, 31, 0, 255, 255, 0, 36, 255, 240];
            if (!wire.Written.ToArray().AsSpan().EndsWith(expected))
                throw new InvalidOperationException("The peer's NAWS data did not represent the committed geometry.");

            wire.FailWrites = true;
            var failed = await transport.ApplyGeometryAsync(transport.CommitGeometry(90, 20), deadline.Token);
            if (failed is not { Status: GeometryNotification.Failed, Error: IOException })
                throw new InvalidOperationException("A failed geometry write was reported as submitted.");
        }
        finally { await transport.DisconnectAsync().WaitAsync(deadline.Token); }
    }

    private static async Task CancelledSerialOpenRetainsTheDeviceUntilItClosesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wire = new HeldWriteWire([], holdClose: true);
        using var replacementDevice = new HeldWriteWire("replacement device"u8.ToArray());
        replacementDevice.ReleaseWrite.SetResult();
        var profile = ConnectionProfile.CreateSerial("Late serial", new("COM254"));
        var transport = new SessionTransport(null, profile, (80, 24), (_, _, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, settings =>
            {
                if (settings != profile.Serial) throw new InvalidOperationException("The serial settings changed during open.");
                opening.SetResult();
                permitOpen.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                return wire;
            });
        var starting = transport.StartAsync(deadline.Token);
        Task? closing = null;
        SessionTransport? replacement = null;
        try
        {
            await opening.Task.WaitAsync(deadline.Token);
            closing = transport.DisconnectAsync();
            if (!ReferenceEquals(closing, transport.DisconnectAsync()))
                throw new InvalidOperationException("Serial disconnect started more than one cleanup.");
            try
            {
                await starting.WaitAsync(deadline.Token);
                throw new InvalidOperationException("The cancelled serial attempt reported readiness.");
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            permitOpen.SetResult();
            await wire.CloseStarted.Task.WaitAsync(deadline.Token);
            await transport.DeliverySettled.WaitAsync(deadline.Token);
            if (transport.Status is not { Ready: false, ReceiveEnded: true, ReleasePending: true } ||
                transport.ActualRelease.IsCompleted || closing.IsCompleted)
                throw new InvalidOperationException("Serial cleanup released a late device before its actual close.");

            byte[]? delivered = null;
            replacement = new SessionTransport(null, profile, (80, 24), (_, payload, _) =>
            {
                delivered = payload;
                return Task.CompletedTask;
            }, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => replacementDevice);
            await replacement.StartAsync(deadline.Token);
            if (await replacement.ReceiveAsync(deadline.Token) != ReceiveOutcome.Delivered ||
                delivered is null || !delivered.AsSpan().SequenceEqual("replacement device"u8) ||
                transport.ActualRelease.IsCompleted)
                throw new InvalidOperationException("A replacement serial adapter did not deliver independently of the old close.");
            wire.ReleaseClose.SetResult();
            await closing.WaitAsync(deadline.Token);
            var output = await replacement.SubmitPreparedAsync("still ready"u8.ToArray(), deadline.Token);
            if (output.Status != TransportSubmission.Submitted || !replacement.Status.Ready ||
                !replacementDevice.Written.ToArray().AsSpan().SequenceEqual("still ready"u8))
                throw new InvalidOperationException("The old serial device's late close affected its replacement.");
        }
        finally
        {
            permitOpen.TrySetResult();
            wire.ReleaseClose.TrySetResult();
            await (closing ?? transport.DisconnectAsync()).WaitAsync(deadline.Token);
            if (replacement is not null) await replacement.DisconnectAsync().WaitAsync(deadline.Token);
        }
        if (!transport.ActualRelease.IsCompletedSuccessfully || wire.CloseCalls != 1)
            throw new InvalidOperationException("The original serial device did not retire once.");
    }

    private static async Task SerialReceiveFailurePreservesAcceptedOutputAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var wire = new HeldWriteWire("ready\r\n"u8.ToArray(), failReadAfterPayload: true);
        var file = new HeldRecordingFile(holdFlush: true);
        file.CompleteFlush.SetResult();
        var part = new SessionRecording([RecordingFormat.Raw], TimeProvider.System, _ => file);
        await using var session = new TerminalSession(wire,
            ConnectionProfile.CreateSerial("Serial recording", new("COM254"), recordRaw: true), part, 100);
        if (!(await session.ReadOutputAsync(deadline.Token))!.Contains("ready"))
            throw new InvalidOperationException("The serial receiver did not publish the accepted data.");
        var resized = await session.ResizeAsync(90, 30, deadline.Token);
        if (!resized.Contains("ready") || wire.Written.Length != 0)
            throw new InvalidOperationException("Serial geometry sent a notification or lost terminal output.");
        try
        {
            await session.ReadOutputAsync(deadline.Token);
            throw new InvalidOperationException("Serial overrun ended as ordinary EOF.");
        }
        catch (IOException error) when (error.Message == "COM receive overrun. Some input was lost.") { }
        await session.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        if (session.TransportStatus.DeliveryFailure is not IOException || !file.Closed ||
            !file.Bytes.ToArray().AsSpan().SequenceEqual("ready\r\n"u8))
            throw new InvalidOperationException("A serial error discarded already accepted recording or hid the error.");
    }

    private static async Task CancelledRecordingPreparationRetainsItsWholeGroupAsync(bool failSecondOpen)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var activation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new PreparationFile(closeStarted, permitClose);
        var second = new PreparationFile(null, null);
        int claimed = 0;
        try
        {
            for (; claimed < 3; claimed++) await ApplicationResources.FilePreparations.WaitAsync(deadline.Token);
            Task<SessionRecording> cancelled = RecordingLifecycle.PreparePartAsync(() => new SessionRecording(
                [RecordingFormat.Raw, RecordingFormat.Text], TimeProvider.System, format =>
                {
                    if (format == RecordingFormat.Raw) return first;
                    secondOpen.SetResult();
                    permitOpen.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                    if (failSecondOpen) throw new IOException("The second format could not open.");
                    return second;
                }), activation.Token);
            await secondOpen.Task.WaitAsync(deadline.Token);
            activation.Cancel();
            try
            {
                await cancelled.WaitAsync(deadline.Token);
                throw new InvalidOperationException("Cancelled preparation activated a recording.");
            }
            catch (OperationCanceledException) when (activation.IsCancellationRequested) { }
            permitOpen.SetResult();
            await closeStarted.Task.WaitAsync(deadline.Token);

            Task<SessionRecording> next = RecordingLifecycle.PreparePartAsync(() => new SessionRecording(
                [RecordingFormat.Raw], TimeProvider.System, _ => new PreparationFile(null, null)), deadline.Token);
            if (next.IsCompleted || first.Closed)
                throw new InvalidOperationException("Preparation released its admission before the old handle closed.");
            permitClose.SetResult();
            await (await next.WaitAsync(deadline.Token)).FinishAsync().WaitAsync(deadline.Token);
            if (!first.Closed || second.Closed == failSecondOpen)
                throw new InvalidOperationException("Preparation did not close exactly the handles it acquired.");
        }
        finally
        {
            permitOpen.TrySetResult();
            permitClose.TrySetResult();
            if (claimed > 0) ApplicationResources.FilePreparations.Release(claimed);
        }
    }

    private sealed class PreparationFile(TaskCompletionSource? closeStarted, TaskCompletionSource? permitClose)
        : IRecordingFile
    {
        public bool Closed { get; private set; }

        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken token) =>
            throw new InvalidOperationException("Unactivated recording reached its output.");

        public ValueTask FlushAsync(CancellationToken token) => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            closeStarted?.SetResult();
            if (permitClose is not null) await permitClose.Task;
            Closed = true;
        }
    }

    private static async Task RepeatedEofAndPauseShareOneRecordingDrainAsync(bool failRawFlush = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var wire = new SplitSourceWire();
        var files = Enum.GetValues<RecordingFormat>().ToDictionary(format => format, _ => new HeldRecordingFile(holdFlush: true));
        var profile = ConnectionProfile.CreateSsh("Split source log", "127.0.0.1", "fixture",
            recordRaw: true, recordText: true, recordTerminal: true);
        var part = new SessionRecording([.. files.Keys], TimeProvider.System, format => files[format]);
        await using var session = new TerminalSession(wire, profile, part, 100);
        await session.ReadOutputAsync(deadline.Token);
        await session.ReadOutputAsync(deadline.Token);
        Task<IReadOnlyList<string>?> eof = session.ReadOutputAsync(deadline.Token);
        try
        {
            await files[RecordingFormat.Raw].FlushStarted.Task.WaitAsync(deadline.Token);
            Task<RecordingOperationResult> pause = session.ChangeRecordingAsync(resume: false, deadline.Token);
            Task close = session.DisposeAsync().AsTask();
            if (await session.ReadOutputAsync(deadline.Token) is not null || pause.IsCompleted || close.IsCompleted ||
                !session.RecordingReleasePending || eof.IsCompleted)
                throw new InvalidOperationException("Repeated EOF or overlapping close changed ownership of pending file work.");
            if (failRawFlush)
            {
                using var replacementWire = new HeldWriteWire("replacement"u8.ToArray());
                var replacementFile = new HeldRecordingFile(holdFlush: true);
                replacementFile.CompleteFlush.SetResult();
                var replacementRecording = new SessionRecording([RecordingFormat.Raw], TimeProvider.System, _ => replacementFile);
                var replacementProfile = ConnectionProfile.CreateSsh("Replacement log", "127.0.0.1", "fixture", recordRaw: true);
                await using var replacement = new TerminalSession(replacementWire, replacementProfile, replacementRecording, 100);
                await replacement.ReadOutputAsync(deadline.Token);
                files[RecordingFormat.Raw].CompleteFlush.SetException(new IOException("Controlled late durable flush failure."));
                foreach (var pair in files.Where(pair => pair.Key != RecordingFormat.Raw))
                    pair.Value.CompleteFlush.TrySetResult();
                await Task.WhenAll(eof, pause, close).WaitAsync(deadline.Token);
                if ((await pause).Status.Disposition != RecordingDisposition.Incomplete ||
                    session.RecordingStatus.Disposition != RecordingDisposition.Incomplete ||
                    session.RecordingReleasePending || replacement.RecordingStatus.Disposition != RecordingDisposition.Active)
                    throw new InvalidOperationException("A late flush did not settle only the original recording generation.");
                replacementWire.ReleaseWrite.SetResult();
                await replacement.SendTextAsync("alive", deadline.Token);
                await replacement.PauseRecordingAsync(deadline.Token);
                if (!replacementWire.Written.ToArray().AsSpan().SequenceEqual("alive"u8) ||
                    !replacementFile.Bytes.ToArray().AsSpan().SequenceEqual("replacement"u8))
                    throw new InvalidOperationException("The replacement's I/O was affected by the old disk failure.");
            }
            else
            {
                foreach (var file in files.Values) file.CompleteFlush.TrySetResult();
                await Task.WhenAll(eof, pause, close).WaitAsync(deadline.Token);
            }
        }
        finally
        {
            foreach (var file in files.Values) file.CompleteFlush.TrySetResult();
        }

        if (!files[RecordingFormat.Raw].Bytes.ToArray().AsSpan().SequenceEqual(new byte[] { 65, 0xe3, 66, 0xe3 }) ||
            !files[RecordingFormat.Text].Bytes.ToArray().AsSpan().SequenceEqual("AB��"u8) ||
            !files[RecordingFormat.Terminal].Bytes.ToArray().AsSpan().SequenceEqual("AB��"u8) ||
            files.Any(pair => !pair.Value.Closed || pair.Value.FlushCalls !=
                (failRawFlush && pair.Key != RecordingFormat.Raw ? 0 : 1)))
            throw new InvalidOperationException("EOF did not finalize each decoder and log exactly once.");
    }

    private sealed class SplitSourceWire : ISessionWire
    {
        private int step;
        public TerminalReceiveSource Source { get; private set; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (step == 2) return ValueTask.FromResult(0);
            Source = step == 0 ? TerminalReceiveSource.Data : TerminalReceiveSource.StandardError;
            buffer.Span[0] = (byte)('A' + step++);
            buffer.Span[1] = 0xe3;
            return ValueTask.FromResult(2);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private static void ArchiveReinspectionDoesNotMoveTheReplacement()
    {
        var directory = Directory.CreateTempSubdirectory("TabTtyArchive-");
        string source = Path.Combine(directory.FullName, "vault.tttykey");
        try
        {
            File.WriteAllBytes(source, "confirmed damaged vault"u8.ToArray());
            var evidence = CredentialVault.InspectFile(source)!;
            try
            {
                CredentialVault.ArchiveFile(source, evidence, (from, to) =>
                {
                    File.Move(from, to);
                    File.WriteAllBytes(from, "external replacement"u8.ToArray());
                    throw new IOException("The issued move lost its original source name.");
                });
                throw new InvalidOperationException("The archive was accepted with a replacement at its source.");
            }
            catch (IOException) { }

            var pending = VerifiedFilePublication.GetUnresolvedArchive(source) ??
                throw new InvalidOperationException("Archive evidence was discarded.");
            try
            {
                CredentialVault.ReconcileArchive(source);
                throw new InvalidOperationException("Reinspection accepted the unresolved source.");
            }
            catch (IOException) { }
            if (!VerifiedFilePublication.IsUnresolved(source) ||
                !File.ReadAllBytes(source).AsSpan().SequenceEqual("external replacement"u8) ||
                CredentialVault.InspectFile(pending.Destination) != evidence)
                throw new InvalidOperationException("Reinspection changed or lost the independently observed file effects.");

            File.Move(source, Path.Combine(directory.FullName, "external-replacement.tttykey"));
            if (CredentialVault.ReconcileArchive(source) != pending.Destination ||
                VerifiedFilePublication.IsUnresolved(source) || File.Exists(source))
                throw new InvalidOperationException("Reinspection did not retire the verified original move.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task PublicationObservationLeavesAnIssuedMutationOwnedAsync()
    {
        var directory = Directory.CreateTempSubdirectory("TabTtyPublication-");
        string target = Path.Combine(directory.FullName, "candidate.ttty");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var mutation = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] candidate = "admitted bytes"u8.ToArray();
        FilePublicationOperation operation = VerifiedFilePublication.Begin(new(target, candidate, null, null, 1024,
            Issue: (stage, installed, _) =>
            {
                File.Move(stage, installed);
                entered.TrySetResult();
                mutation.Wait(deadline.Token);
            }), deadline.Token);
        try
        {
            candidate.AsSpan().Fill(0);
            await entered.Task.WaitAsync(deadline.Token);
            if (operation.Phase != FilePublicationPhase.MutationIssued || operation.ActualRelease.IsCompleted)
                throw new InvalidOperationException("Issued publication ownership was retired early.");
            try
            {
                await operation.ObserveAsync(new CancellationToken(canceled: true));
                throw new InvalidOperationException("Publication observation did not cancel.");
            }
            catch (OperationCanceledException) { }
            try
            {
                VerifiedFilePublication.Begin(new(target, "replacement"u8.ToArray(), null, null, 1024));
                throw new InvalidOperationException("Another operation was admitted during the original mutation.");
            }
            catch (IOException) { }
        }
        finally
        {
            mutation.Set();
            try
            {
                var result = await operation.ObserveAsync(deadline.Token);
                if (result.Status != FilePublicationStatus.Verified || result.OperationId != operation.Id ||
                    operation.Phase != FilePublicationPhase.Retired ||
                    !File.ReadAllBytes(target).AsSpan().SequenceEqual("admitted bytes"u8))
                    throw new InvalidOperationException("The verified image did not match the admitted operation.");
            }
            finally { directory.Delete(recursive: true); }
        }
    }

    private static async Task PublicationCapacityStaysOwnedUntilActualReleaseAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var entered = Enumerable.Range(0, 4).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var operations = new List<FilePublicationOperation>(entered.Length);
        string extra = Path.Combine(directory.Path, "later.ttty");
        try
        {
            for (int index = 0; index < entered.Length; index++)
            {
                var signal = entered[index];
                string target = Path.Combine(directory.Path, $"owned-{index}.ttty");
                operations.Add(VerifiedFilePublication.Begin(new(target, "owned"u8.ToArray(), null, null, 1024,
                    Issue: (staged, path, _) =>
                    {
                        File.Move(staged, path);
                        signal.SetResult();
                        release.Wait(deadline.Token);
                    })));
            }
            await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(deadline.Token);
            try
            {
                await VerifiedFilePublication.Begin(new(extra, "later"u8.ToArray(), null, null, 1024)).ObserveAsync(deadline.Token);
                throw new InvalidOperationException("An additional file mutation bypassed active admission capacity.");
            }
            catch (IOException) when (!File.Exists(extra)) { }
        }
        finally
        {
            release.Set();
            foreach (var operation in operations)
                if ((await operation.ObserveAsync(deadline.Token)).Status != FilePublicationStatus.Verified)
                    throw new InvalidOperationException("An admitted file publication did not settle after release.");
        }
        var result = await VerifiedFilePublication.Begin(new(extra, "later"u8.ToArray(), null, null, 1024))
            .ObserveAsync(deadline.Token);
        if (result.Status != FilePublicationStatus.Verified || !File.ReadAllBytes(extra).AsSpan().SequenceEqual("later"u8))
            throw new InvalidOperationException("Actual publication release did not return admission capacity.");
    }

    private static async Task PartialPasteStopsAtACompleteUnitAsync(string encoding, string source,
        int firstWrite, byte[] expected, string expectedEcho, bool ai = false)
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var wire = new PartialPasteWire(firstWrite, stop);
        var profile = ConnectionProfile.CreateTelnet("Partial paste", "127.0.0.1", transmitEncoding: encoding);
        await using var session = new TerminalSession(wire, profile, null, 100);
        using var paste = TerminalPaste.Create(source);
        bool completed;
        if (ai)
        {
            var lease = session.TryGrantAiInput() ?? throw new InvalidOperationException("AI input was not admitted.");
            try
            {
                long layout = await session.ObserveAiAsync((_, observed) => observed.LayoutEpoch, deadline.Token);
                var attempt = new AiInputAttempt(lease, layout, stop.Token);
                completed = await session.SendAiPasteAsync(paste, 0, 0, framePaste: true, attempt, deadline.Token);
                if (attempt.CompletedWrites != 1)
                    throw new InvalidOperationException("AI paste did not retain the definite submitted operation.");
            }
            finally { await session.ReleaseAiInputAsync(lease); }
        }
        else completed = await session.SendPasteAsync(paste, 0, 0, stop.Token, deadline.Token);
        if (completed ||
            !wire.Bytes.ToArray().AsSpan().SequenceEqual(expected) ||
            string.Concat(await session.GetOutputAsync(deadline.Token)) != expectedEcho)
            throw new InvalidOperationException($"A stopped {encoding} paste split a unit or echoed unsubmitted text.");
    }

    private sealed class PartialPasteWire(int firstWrite, CancellationTokenSource stop) : ISessionWire
    {
        public MemoryStream Bytes { get; } = new();
        private bool first = true;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => ValueTask.FromResult(0);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => Bytes.WriteAsync(buffer, token);

        public async ValueTask<int> WriteSomeAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (!first)
            {
                await WriteAsync(buffer, token);
                return buffer.Length;
            }
            first = false;
            await WriteAsync(buffer[..firstWrite], token);
            stop.Cancel();
            return firstWrite;
        }

        public void Dispose() => Bytes.Dispose();
    }

    internal static async Task SshCreditRecordingAndTransferProgressAsync(int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var profile = ConnectionProfile.CreateSsh("Concurrent ownership", "127.0.0.1", "tabtty-ownership", port,
            recordRaw: true, methods: ["password"]);
        var interactions = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        var wire = await SshSessionWire.OpenAsync(profile, interactions, _ => Task.FromResult((80, 24)),
            (_, _, _) => Task.CompletedTask, deadline.Token, deadline.Token);
        var file = new HeldRecordingFile(holdFlush: true);
        var part = new SessionRecording([RecordingFormat.Raw], TimeProvider.System, _ => file);
        await using var session = new TerminalSession(wire, profile, part, 100);
        Task<IReadOnlyList<string>> sending = session.SendTextAsync("manual", deadline.Token);
        using var transfer = await session.OpenFileTransferAsync(deadline.Token);
        try
        {
            byte[] handle = await transfer.OpenAsync("/home/held-reads"u8.ToArray(), write: false, deadline.Token);
            var reads = Enumerable.Range(0, 8).Select(index => transfer.ReadAsync(handle, index, 1, deadline.Token)).ToArray();
            while (await transfer.StatAsync("/fixture-held-read-count"u8.ToArray(), deadline.Token) is not { Size: 8 })
                await Task.Delay(10, deadline.Token);
            using var excessStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var excess = transfer.ReadAsync(handle, 0, 1, excessStop.Token);
            await transfer.CreateDirectoryAsync("/home/fixture-input"u8.ToArray(), deadline.Token);
            var displayed = await session.ReadOutputAsync(deadline.Token);
            if (displayed is null || !displayed.Any(row => row == "marker") || sending.IsCompleted)
                throw new InvalidOperationException("Input waiting for credit blocked receive or was sent before credit.");

            session.ProtectRecordingClose();
            Task pause = session.PauseRecordingAsync(deadline.Token);
            await file.FlushStarted.Task.WaitAsync(deadline.Token);
            await transfer.CreateDirectoryAsync("/home/fixture-rekey"u8.ToArray(), deadline.Token);
            await sending.WaitAsync(deadline.Token);
            do
            {
                displayed = await session.ReadOutputAsync(deadline.Token);
                if (displayed is null) throw new InvalidOperationException("The peer ended before receiving committed input and reply.");
            } while (!displayed.Any(row => row == "confirmed"));
            if (pause.IsCompleted || !session.RecordingReleasePending || reads.Any(read => read.IsCompleted) ||
                excess.IsCompleted || await transfer.StatAsync("/fixture-held-read-count"u8.ToArray(), deadline.Token) is not { Size: 8 })
                throw new InvalidOperationException("SSH control, SFTP capacity or held recording ownership changed.");
            excessStop.Cancel();
            try
            {
                await excess;
                throw new InvalidOperationException("An extra request entered the eight held file slots.");
            }
            catch (OperationCanceledException) when (excessStop.IsCancellationRequested) { }
            await transfer.CreateDirectoryAsync("/home/fixture-release-reads"u8.ToArray(), deadline.Token);
            byte[] replies = (await Task.WhenAll(reads).WaitAsync(deadline.Token))
                .SelectMany(reply => reply ?? throw new InvalidOperationException("A held read ended early.")).ToArray();
            if (!replies.AsSpan().SequenceEqual("01234567"u8))
                throw new InvalidOperationException("Reverse-order SFTP responses were associated with the wrong requests.");
            await transfer.CloseAsync(handle, deadline.Token);
            file.CompleteFlush.SetResult();
            await pause.WaitAsync(deadline.Token);
            if (!file.Bytes.ToArray().AsSpan().SequenceEqual("marker\r\n\u001b[6n"u8))
                throw new InvalidOperationException("Raw recording included unrelated SFTP or post-pause input.");
        }
        finally { file.CompleteFlush.TrySetResult(); }
    }

    internal static async Task SshEofFlushesWhileReverseInputRemainsAvailableAsync(int port)
    {
        using var folder = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        string profile = Path.Combine(folder.Path, "directional-eof.ttty");
        await ConnectionProfile.CreateSsh("Directional EOF", "127.0.0.1", "tabtty-eof", port,
            recordRaw: true, recordText: true, recordTerminal: true, methods: ["password"]).SaveNewAsync(profile, token);
        var interaction = new SshInteraction((_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<VaultSecret?>(new VaultSecret("local-test-password"u8.ToArray())));
        int startupSizes = 0;
        await using var session = await TerminalSession.ConnectAsync(profile, token, interaction,
            viewport: _ => Task.FromResult(++startupSizes == 1 ? (80, 24) : (132, 50)));
        IReadOnlyList<string>? screen = null;
        while (await session.ReadOutputAsync(token) is { } output) screen = output;
        if (screen?.Contains("tail�") != true || !session.AcceptsInput ||
            session.RecordingStatus is not { Disposition: RecordingDisposition.Active, ReleasePending: true })
            throw new InvalidOperationException("SSH EOF ended the reverse direction or discarded its eligible tail.");

        string logs = Path.Combine(folder.Path, "Logs");
        string raw = Directory.GetFiles(logs, "*.raw.tttylog").Single();
        string text = Directory.GetFiles(logs, "*.log").Single();
        string terminal = Directory.GetFiles(logs, "*.term.tttylog").Single();
        while (new FileInfo(raw).Length < 5 || new FileInfo(text).Length < 7 || new FileInfo(terminal).Length < 7)
            await Task.Delay(10, token);

        await session.SendTextAsync("reverse input", token);
        await session.WaitForTerminalPeerCloseAsync(token);
        await session.PauseRecordingAsync(token);
        byte[] expectedRaw = [0x74, 0x61, 0x69, 0x6c, 0xe3];
        if (session.RecordingReleasePending || !(await File.ReadAllBytesAsync(raw, token)).AsSpan().SequenceEqual(expectedRaw) ||
            !(await File.ReadAllBytesAsync(text, token)).AsSpan().SequenceEqual("tail�"u8) ||
            !(await File.ReadAllBytesAsync(terminal, token)).AsSpan().SequenceEqual("tail�"u8))
            throw new InvalidOperationException("EOF, reverse input and Pause did not preserve the three exact logs.");
    }

    private static void SharedChannelCreditRequiresCommitment()
    {
        var window = new SshChannelWindow(capacity: 16, maximumPacket: 4);
        window.Open(window: 7, packet: 3);
        if (window.ReserveSend(10) != 3 || window.ReserveSend(10) != 3 || window.ReserveSend(10) != 1 ||
            window.ReserveSend(10) != 0)
            throw new InvalidOperationException("Channel packet or send window was exceeded.");
        for (int i = 0; i < 4; i++) window.AcceptReceive(4);
        int grant = window.Consume(4);
        if (grant != 4 || window.Consume(4) != 0)
            throw new InvalidOperationException("Receive grants were not coalesced.");
        try
        {
            window.AcceptReceive(1);
            throw new InvalidOperationException("Queued receive credit was exposed before commitment.");
        }
        catch (InvalidDataException) { }
        window.CommitGrant(grant);
        window.AcceptReceive(4);
        if (window.CompleteGrant() != 4)
            throw new InvalidOperationException("Consumption during an issued grant was lost.");
    }

    private static void VerifiedPublicationReconcilesTheOriginalOperation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TabTtyPublication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "profile.ttty");
            string preserved = target + ".bak";
            byte[] original = "editing basis"u8.ToArray();
            byte[] candidate = "verified candidate"u8.ToArray();
            byte[] digest = System.Security.Cryptography.SHA256.HashData(original);
            File.WriteAllBytes(target, original);
            int issued = 0;
            var result = VerifiedFilePublication.Publish(target, candidate, digest, preserved, 1024, default,
                issue: (source, installed, backup) =>
                {
                    issued++;
                    File.Replace(source, installed, backup);
                    File.Delete(backup!);
                    throw new IOException("Fixture lost access to preservation after replacement.");
                });
            if (result.Status != FilePublicationStatus.UnverifiedPreservation || result.Target != FileImageStatus.Candidate ||
                result.Preservation != FileImageStatus.Missing || result.OperationId == Guid.Empty)
                throw new InvalidOperationException("The publication did not distinguish installed bytes from verified preservation.");

            try
            {
                VerifiedFilePublication.Publish(target, original, System.Security.Cryptography.SHA256.HashData(candidate),
                    preserved, 1024, default);
                throw new InvalidOperationException("Ordinary publication bypassed unresolved preservation.");
            }
            catch (IOException) { }
            var pending = VerifiedFilePublication.Reconcile(target);
            if (pending?.OperationId != result.OperationId || pending.Status == FilePublicationStatus.Verified)
                throw new InvalidOperationException("Reconciliation changed or prematurely accepted the prior operation.");

            File.WriteAllBytes(preserved, original);
            bool adopted = false;
            var verified = VerifiedFilePublication.Reconcile(target, path =>
            {
                adopted = File.ReadAllBytes(path).AsSpan().SequenceEqual(candidate);
            }, result.OperationId);
            if (verified?.Status != FilePublicationStatus.Verified || !adopted || issued != 1 ||
                VerifiedFilePublication.IsUnresolved(target) ||
                !File.ReadAllBytes(preserved).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Reconciliation did not adopt the exact verified image and original once.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void DisplayRetainsWidthAndAdvancesPastOmittedScalars()
    {
        Verify("\u2764\uFE0F" + new string('\u0301', 254), "\uFE0EX", 2, "X");
        Verify("\u2764\uFE0F" + new string('\u0301', 253) + "\uFE0E", "X", 1, "X");
        Verify("\U0001F469" + new string('\u0301', 254) + "\u200D", "\U0001F4BB\U0001F600", 2, "\U0001F600");
        VerifyRetirement();

        static void Verify(string retained, string suffix, int width, string final)
        {
            var terminal = new TerminalOutput(columns: 16, retainedRowLimit: 100);
            try
            {
                byte[] stream = Encoding.UTF8.GetBytes(retained + suffix);
                for (int offset = 0; offset < stream.Length; offset += 7)
                    terminal.Receive(TerminalReceiveSource.Data, stream.AsSpan(offset, Math.Min(7, stream.Length - offset)));
                var cells = terminal.GetRows().GetCells(0).Span;
                if (cells.Length != 2 || cells[0].Text != retained || cells[0].Width != width ||
                    cells[1].Column != width || cells[1].Text != final)
                    throw new InvalidOperationException("An omitted scalar changed width or split the retained grapheme.");
            }
            finally { terminal.ReleaseNativeState(); }
        }

        static void VerifyRetirement()
        {
            string retained = "\u0915" + new string('\u093C', 255);
            var terminal = new TerminalOutput(columns: 8, retainedRowLimit: 100, screenRows: 2);
            try
            {
                terminal.Receive(TerminalReceiveSource.Data, Encoding.UTF8.GetBytes(retained + "\u094D\u0915X"));
                var before = terminal.GetRows();
                terminal.Resize(2, 2);
                var cells = terminal.GetRows().GetCells(0).Span;
                if (cells.Length != 2 || cells[0].Text != retained || cells[1].Text != "X")
                    throw new InvalidOperationException("Reflow split the retained Indic span.");
                terminal.Receive(TerminalReceiveSource.Data, Encoding.ASCII.GetBytes(string.Concat(
                    Enumerable.Repeat("\r\n1", 105))));
                if (terminal.GetRows().Any(row => row.Contains('\u0915')) || before.GetCells(0).Span[0].Text != retained)
                    throw new InvalidOperationException("Retirement retained an evicted span or changed an earlier snapshot.");
            }
            finally { terminal.ReleaseNativeState(); }
        }
    }

    private static void ExactEmojiWidthsSurviveEveryByteSplit()
    {
        (string Text, int Width)[] cases =
        [
            ("\u00A9", 1),
            ("\u00A9\uFE0F\u0301", 2),
            ("\u00A9\uFE0F\uFE0E", 1),
            ("\u00A9\uFE0F\uFE00", 1),
            ("\u2194\uFE0F", 2),
            ("#\u20E3", 2),
            ("#\uFE0F\u20E3", 2),
            ("#\u0301\uFE0F\u20E3", 1),
        ];
        foreach (var (text, width) in cases)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "x");
            for (int split = 0; split <= bytes.Length; split++)
            {
                var terminal = new TerminalOutput(columns: 8, retainedRowLimit: 100, screenRows: 2);
                try
                {
                    terminal.Receive(TerminalReceiveSource.Data, bytes.AsSpan(0, split));
                    terminal.Receive(TerminalReceiveSource.Data, bytes.AsSpan(split));
                    var cells = terminal.GetRows().GetCells(0).Span;
                    if (cells.Length != 2 || cells[0].Text != text || cells[0].Width != width ||
                        cells[1].Column != width || cells[1].Text != "x")
                        throw new InvalidOperationException($"Emoji width or segmentation changed for {text} at byte {split}.");
                }
                finally { terminal.ReleaseNativeState(); }
            }
        }
    }

    private static async Task BroadcastPasteReservesEveryStartBeforeSubmissionAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var firstWire = new HeldWriteWire("\u001b[?2004h"u8.ToArray());
        using var secondWire = new HeldWriteWire("\u001b[?2004h"u8.ToArray());
        var profile = ConnectionProfile.CreateTelnet("Input admission", "127.0.0.1", 23);
        await using var first = new TerminalSession(firstWire, profile, null, 100);
        await using var second = new TerminalSession(secondWire, profile, null, 100);
        await first.ReadOutputAsync(deadline.Token);
        await second.ReadOutputAsync(deadline.Token);
        var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paste = TerminalPaste.Create(new string('x', 1024 * 1024 + 1));
        using var a = await first.PreparePasteAsync(paste, 0, 0, deadline.Token, deadline.Token, null, commit.Task);
        using var b = await second.PreparePasteAsync(paste, 0, 0, deadline.Token, deadline.Token, null, commit.Task);
        Task<bool> sendingFirst = a.SendAsync(deadline.Token);
        try
        {
            if (!await a.PasteReady!.WaitAsync(deadline.Token) || firstWire.WriteStarted.Task.IsCompleted)
                throw new InvalidOperationException("The first target started before the second target armed.");
            Task<bool> sendingSecond = b.SendAsync(deadline.Token);
            if (!await b.PasteReady!.WaitAsync(deadline.Token) || secondWire.WriteStarted.Task.IsCompleted)
                throw new InvalidOperationException("The second target started before collective commitment.");
            commit.SetResult(true);
            firstWire.ReleaseWrite.SetResult();
            secondWire.ReleaseWrite.SetResult();
            if (!await sendingFirst.WaitAsync(deadline.Token) || !await sendingSecond.WaitAsync(deadline.Token))
                throw new InvalidOperationException("The collectively admitted paste was stopped.");
            var expected = "[200~" + paste.Text + "\u001b[201~";
            foreach (var wire in new[] { firstWire, secondWire })
            {
                byte[] received = wire.Written.ToArray();
                if (received.Length != expected.Length + 1 || received[0] != 0x1b ||
                    !received.AsSpan(1).SequenceEqual(Encoding.ASCII.GetBytes(expected)))
                    throw new InvalidOperationException("The target lost bytes or changed bracket framing above 1 MiB.");
            }
        }
        finally
        {
            commit.TrySetResult(false);
            firstWire.ReleaseWrite.TrySetResult();
            secondWire.ReleaseWrite.TrySetResult();
        }
    }

    private static async Task BoundedPasteAndReceiveHaveIndependentLifetimesAsync()
    {
        using (var vectors = TerminalPaste.Create("🇦🇧🇨क्‍क👩‍💻á\r\n"))
        {
            string[] exactUnits = ["🇦🇧", "🇨", "क्‍क", "👩‍💻", "á", "\r\n"];
            if (vectors.CharacterCount != exactUnits.Length || vectors.LineCount != 2 ||
                !vectors.GetChunks(1, 0).Select(chunk => vectors.Text.Substring(chunk.Offset, chunk.Length))
                    .SequenceEqual(exactUnits))
                throw new InvalidOperationException("Paste input changed RI, Indic, ZWJ, or combining boundaries.");
        }
        using (var empty = TerminalPaste.Create(""))
            if (empty.CharacterCount != 0 || empty.GetChunks(0, 0).Any())
                throw new InvalidOperationException("Empty paste reported outbound units.");
        try
        {
            using var malformed = TerminalPaste.Create(new string('X', 64 * 1024) + "\ud800");
            throw new InvalidOperationException("Paste accepted an unpaired surrogate at the final boundary.");
        }
        catch (ArgumentException) { }
        string text = "a" + new string('\u0301', 3000) + "\r\n\U0001F600";
        var paste = TerminalPaste.Create(text);
        if (paste.CharacterCount != 3 || paste.LineCount != 2 || !paste.RequiresConfirmation ||
            TerminalPaste.Create(new string('a', 512)).RequiresConfirmation)
            throw new InvalidOperationException("Native paste analysis did not preserve actual source boundaries.");
        var chunks = paste.GetChunks(characterDelay: 1, lineDelay: 2).ToArray();
        if (chunks[0].CompletedGraphemes != 0 || chunks[0].Delay != 0 ||
            chunks.Sum(chunk => chunk.CompletedGraphemes) != 3 ||
            chunks.Single(chunk => chunk.Delay == 3).Length != 2)
            throw new InvalidOperationException("Pacing advanced inside a grapheme or split CRLF.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stop = new CancellationTokenSource();
        using var wire = new HeldWriteWire("received"u8.ToArray());
        var profile = ConnectionProfile.CreateTelnet("Owned input fixture", "127.0.0.1", 23);
        await using var session = new TerminalSession(wire, profile, null, 100);

        Task<bool> sending = session.SendPasteAsync(paste, 0, 0, stop.Token, timeout.Token);
        try
        {
            await wire.WriteStarted.Task.WaitAsync(timeout.Token);
            var output = await session.ReadOutputAsync(timeout.Token);
            if (output is null || !output.Any(row => row.Contains("received", StringComparison.Ordinal)))
                throw new InvalidOperationException("A blocked write prevented ordered receive processing.");
            stop.Cancel();
        }
        finally { wire.ReleaseWrite.TrySetResult(); }

        if (await sending.WaitAsync(timeout.Token)) throw new InvalidOperationException("Stop did not end the paste.");
        byte[] exactPrefix = Encoding.UTF8.GetBytes(text.AsSpan(0, chunks[0].Length).ToString());
        if (!wire.Written.ToArray().AsSpan().SequenceEqual(exactPrefix))
            throw new InvalidOperationException("Stop repeated or split an already submitted UTF-8 chunk.");
    }

    private static async Task RecordingOwnershipSurvivesObserverAndWriteFailureAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var observation = new CancellationTokenSource();
        using var wire = new HeldWriteWire("raw line\r\n"u8.ToArray());
        var clock = new RecordingClock();
        var file = new HeldRecordingFile();
        var profile = ConnectionProfile.CreateTelnet("Recording failure", "127.0.0.1", 23, recordRaw: true);
        var part = new SessionRecording([RecordingFormat.Raw], clock, _ => file);
        await using var session = new TerminalSession(wire, profile, part, 100);

        await session.ReadOutputAsync(deadline.Token);
        Task pause = session.PauseRecordingAsync(observation.Token);
        try
        {
            await file.WriteStarted.Task.WaitAsync(deadline.Token);
            observation.Cancel();
            try
            {
                await pause.WaitAsync(deadline.Token);
                throw new InvalidOperationException("Cancellation did not stop observation.");
            }
            catch (OperationCanceledException) when (observation.IsCancellationRequested) { }

            clock.ExpireIo();
            await part.Incomplete.WaitAsync(deadline.Token);
            if (session.RecordingStatus is not { Disposition: RecordingDisposition.Incomplete, ReleasePending: true })
                throw new InvalidOperationException("The write timed out before release of its issued buffers.");
            try
            {
                await session.RetryRecordingAsync(deadline.Token);
                throw new InvalidOperationException("Retry did not respect the old part's retained files.");
            }
            catch (IOException) { }
        }
        finally { file.CompleteWrite.TrySetResult(); }

        await session.RecordingActualRelease.WaitAsync(deadline.Token);
        if (session.RecordingStatus is not { Disposition: RecordingDisposition.Incomplete, ReleasePending: false } ||
            file.WriteCalls != 1 || file.FlushCalls != 0 || !file.Closed ||
            !file.Bytes.ToArray().AsSpan().SequenceEqual("raw"u8))
            throw new InvalidOperationException("A late partial write changed the saved outcome or was replayed.");
    }

    private static async Task ProtectedCloseRetainsPendingRecordingAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var wire = new HeldWriteWire("raw line\r\n"u8.ToArray());
        var clock = new RecordingClock();
        var file = new HeldRecordingFile(holdFlush: true);
        var profile = ConnectionProfile.CreateTelnet("Protected recording", "127.0.0.1", 23, recordRaw: true);
        var part = new SessionRecording([RecordingFormat.Raw], clock, _ => file);
        await using var session = new TerminalSession(wire, profile, part, 100);

        await session.ReadOutputAsync(deadline.Token);
        session.ProtectRecordingClose();
        Task close = session.DisposeAsync().AsTask();
        try
        {
            await file.FlushStarted.Task.WaitAsync(deadline.Token);
            clock.ExpireIo();
            if (close.IsCompleted || part.Incomplete.IsCompleted || !session.RecordingStatus.ReleasePending)
                throw new InvalidOperationException("The protected flush was automatically abandoned.");
            if (!session.AbandonCleanup() || close.IsCompleted)
                throw new InvalidOperationException("Explicit loss released ownership before the issued flush returned.");
        }
        finally { file.CompleteFlush.TrySetResult(); }
        await close.WaitAsync(deadline.Token);
        if (session.RecordingStatus is not { Disposition: RecordingDisposition.Incomplete, ReleasePending: false } ||
            !file.Closed || file.FlushCalls != 1 || !file.Bytes.ToArray().AsSpan().SequenceEqual("raw line\r\n"u8))
            throw new InvalidOperationException("A late flush replaced an incomplete result or lost issued data.");
    }

    private static async Task CompletedRecordingDoesNotWaitForTransportReleaseAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var wire = new HeldWriteWire("recorded\r"u8.ToArray(), holdClose: true);
        var file = new HeldRecordingFile(holdFlush: true);
        var profile = ConnectionProfile.CreateTelnet("Transport release", "127.0.0.1", 23, recordRaw: true);
        var part = new SessionRecording([RecordingFormat.Raw], TimeProvider.System, _ => file);
        await using var session = new TerminalSession(wire, profile, part, 100);
        await session.ReadOutputAsync(deadline.Token);
        session.ProtectRecordingClose();
        Task close = session.DisposeAsync().AsTask();
        try
        {
            await file.FlushStarted.Task.WaitAsync(deadline.Token);
            file.CompleteFlush.SetResult();
            await session.RecordingActualRelease.WaitAsync(deadline.Token);
            if (close.IsCompleted || session.TransportActualRelease.IsCompleted ||
                session.TransportStatus is not { ReleasePending: true, ReceiveEnded: true } ||
                session.RecordingReleasePending || session.AbandonCleanup() ||
                session.RecordingStatus.Disposition != RecordingDisposition.Saved ||
                !file.Bytes.ToArray().AsSpan().SequenceEqual("recorded\r"u8))
                throw new InvalidOperationException("Recording and transport release did not settle independently.");
        }
        finally
        {
            file.CompleteFlush.TrySetResult();
            wire.ReleaseClose.TrySetResult();
        }
        await close.WaitAsync(deadline.Token);
    }

    private static async Task PreparedPasteRetainsSourceAndCapacityUntilSendReturnsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var wire = new HeldWriteWire("ready"u8.ToArray());
        var profile = ConnectionProfile.CreateTelnet("Paste capacity", "127.0.0.1");
        await using var session = new TerminalSession(wire, profile, null, 100);
        await session.ReadOutputAsync(deadline.Token);
        using var pressure = await session.PrepareInputAsync(
            new TerminalInput(TerminalInputKind.Text, new string('p', 31 * 16 * 1024)), null, deadline.Token);
        using var paste = TerminalPaste.Create("kept");
        using var another = TerminalPaste.Create("next");
        using var prepared = await session.PreparePasteAsync(paste, 0, 0, deadline.Token, deadline.Token, null,
            Task.FromResult(true));
        paste.Dispose();
        Task<bool> sending = prepared.SendAsync(deadline.Token);
        try
        {
            await wire.WriteStarted.Task.WaitAsync(deadline.Token);
            prepared.Dispose();
            using (paste.HoldSource())
            {
                try
                {
                    await session.ValidatePasteAsync(another, deadline.Token);
                    throw new InvalidOperationException("Disposing an observer returned capacity before its write completed.");
                }
                catch (ResourceLimitException) { }
            }
        }
        finally { wire.ReleaseWrite.TrySetResult(); }
        if (!await sending || !wire.Written.ToArray().AsSpan().SequenceEqual("kept"u8))
            throw new InvalidOperationException("Early observer disposal changed the actual submitted bytes.");
        await session.ValidatePasteAsync(another, deadline.Token);
        try
        {
            using var leaked = paste.HoldSource();
            throw new InvalidOperationException("The finished paste retained its source.");
        }
        catch (ObjectDisposedException) { }
    }

    private static async Task ReservedPasteEchoSurvivesPressureThenEnforcesLaterAdmissionAsync()
    {
        const int capacity = 8 * 1024 * 1024;
        var quota = new ResourceQuota(capacity, collectOnPressure: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var keepReceiving = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wire = new HeldWriteWire("ready"u8.ToArray(), nextInput: keepReceiving.Task);
        using var otherWire = new HeldWriteWire("other"u8.ToArray());
        var profile = ConnectionProfile.CreateSerial("Echo capacity", new("COM254"), localEcho: true);
        await using var session = new TerminalSession(wire, profile, null, 100, terminalState: quota);
        await using var other = new TerminalSession(otherWire,
            ConnectionProfile.CreateSerial("Independent", new("COM253")), null, 100);
        await session.ReadOutputAsync(deadline.Token);
        await other.ReadOutputAsync(deadline.Token);
        wire.ReleaseWrite.SetResult();
        otherWire.ReleaseWrite.SetResult();
        using var pressure = quota.Reserve(0);
        using var paste = TerminalPaste.Create("A😀Z");
        var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<int>(sent =>
        {
            if (sent != 1) throw new InvalidOperationException("A later echo bypassed terminal-state admission.");
            pressure.Add(capacity - quota.Used);
        });
        using var prepared = await session.PreparePasteAsync(paste, 1, 0, deadline.Token, deadline.Token, progress, commit.Task);
        long echoed = Interlocked.Read(ref NativeTerminalCoreDiagnostics.EchoBytes);
        var send = prepared.SendAsync(deadline.Token);
        try
        {
            if (!await prepared.PasteReady!.WaitAsync(deadline.Token))
                throw new InvalidOperationException("The first local echo was not reserved.");
            pressure.Add(capacity - quota.Used);
            commit.SetResult(true);
            try
            {
                await send;
                throw new InvalidOperationException("Later local echo used unavailable terminal-state capacity.");
            }
            catch (ResourceLimitException) { }
            if (!wire.Written.ToArray().AsSpan().SequenceEqual("A😀"u8) ||
                Interlocked.Read(ref NativeTerminalCoreDiagnostics.EchoBytes) - echoed != 1)
                throw new InvalidOperationException("The first echo was double-charged or the unsent suffix was sent.");
            await other.SendTextAsync("alive", deadline.Token);
            if (!otherWire.Written.ToArray().AsSpan().SequenceEqual("alive"u8))
                throw new InvalidOperationException("Echo capacity pressure affected an independent session.");
        }
        finally { commit.TrySetResult(false); }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static async Task PasteAdmissionRejectsChangedTerminalModeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var switched = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wire = new HeldWriteWire("ready"u8.ToArray(), nextInput: switched.Task);
        var profile = ConnectionProfile.CreateSerial("Paste mode", new("COM254"));
        await using var session = new TerminalSession(wire, profile, null, 100);
        await session.ReadOutputAsync(deadline.Token);
        using var paste = TerminalPaste.Create("refused");
        var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var prepared = await session.PreparePasteAsync(paste, 0, 0, deadline.Token, deadline.Token, null, commit.Task);
        var send = prepared.SendAsync(deadline.Token);
        try
        {
            if (!await prepared.PasteReady!.WaitAsync(deadline.Token) || !prepared.CanCommitPaste)
                throw new InvalidOperationException("The unchanged target did not finish paste admission.");
            switched.SetResult("\u001b[?2004h"u8.ToArray());
            await session.ReadOutputAsync(deadline.Token);
            if (prepared.CanCommitPaste)
                throw new InvalidOperationException("A terminal mode change left an old paste admission valid.");
        }
        finally
        {
            commit.TrySetResult(false);
            wire.ReleaseWrite.TrySetResult();
        }
        if (await send || wire.Written.ToArray().Length != 0)
            throw new InvalidOperationException("A stale paste sent its bracket or input before commitment.");
    }

    private static async Task RecordingCloseFailureKeepsTheGroupOwnedAsync(bool timeOut)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var observer = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var nextInput = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wire = new HeldWriteWire("old"u8.ToArray(), nextInput: nextInput.Task);
        var raw = new HeldRecordingFile(holdFlush: true, holdClose: true);
        var text = new HeldRecordingFile(holdFlush: true, holdClose: true);
        var nextRaw = new HeldRecordingFile(holdFlush: true);
        var nextText = new HeldRecordingFile(holdFlush: true);
        foreach (var file in new[] { raw, text, nextRaw, nextText }) file.CompleteFlush.SetResult();
        var clock = new RecordingClock();
        var part = new SessionRecording([RecordingFormat.Raw, RecordingFormat.Text], clock,
            (number, format) => (number, format) switch
            {
                (1, RecordingFormat.Raw) => raw,
                (1, RecordingFormat.Text) => text,
                (2, RecordingFormat.Raw) => nextRaw,
                (2, RecordingFormat.Text) => nextText,
                _ => throw new InvalidOperationException("Unexpected recording part or format."),
            });
        var profile = ConnectionProfile.CreateSsh("Held recording close", "127.0.0.1", "fixture",
            recordRaw: true, recordText: true);
        await using var session = new TerminalSession(wire, profile, part, 100);
        try
        {
            await session.ReadOutputAsync(deadline.Token);
            var abandonedObserver = session.ChangeRecordingAsync(resume: false, observer.Token);
            await raw.CloseStarted.Task.WaitAsync(deadline.Token);
            if (abandonedObserver.IsCompleted || !session.RecordingReleasePending)
                throw new InvalidOperationException("The recording reported a save before file close returned.");
            observer.Cancel();
            try
            {
                await abandonedObserver;
                throw new InvalidOperationException("The recording observer did not stop.");
            }
            catch (OperationCanceledException) when (observer.IsCancellationRequested) { }

            wire.ReleaseWrite.SetResult();
            await session.SendTextAsync("terminal remains active", deadline.Token);
            var pause = session.ChangeRecordingAsync(resume: false, deadline.Token);
            if (timeOut)
            {
                clock.ExpireIo();
                if (raw.Closed || text.CloseStarted.Task.IsCompleted)
                    throw new InvalidOperationException("A close timeout released the issued handle.");
            }
            else
            {
                raw.CompleteClose.SetException(new IOException("Controlled late file close failure."));
                await text.CloseStarted.Task.WaitAsync(deadline.Token);
            }
            if ((await pause).Status is not { Disposition: RecordingDisposition.Incomplete, ReleasePending: true })
                throw new InvalidOperationException("A failed close with another file held did not report incomplete work.");
            try
            {
                await session.RetryRecordingAsync(deadline.Token);
                throw new InvalidOperationException("Retry acquired files before the failed part released its group.");
            }
            catch (IOException error) when (error.Message == "The previous recording part still owns its files.") { }

            raw.CompleteClose.TrySetResult();
            text.CompleteClose.SetResult();
            await part.ActualRelease.WaitAsync(deadline.Token);
            await session.RetryRecordingAsync(deadline.Token);
            if (session.RecordingStatus is not
                { Disposition: RecordingDisposition.Active, Part: 2 })
                throw new InvalidOperationException("Retry did not start a new recording part after actual release.");
            // The first line contains pre-retry text and remains ineligible in Text recording.
            nextInput.SetResult(" mixed\r\nnew"u8.ToArray());
            await session.ReadOutputAsync(deadline.Token);
            await session.PauseRecordingAsync(deadline.Token);
            if (!raw.Bytes.ToArray().AsSpan().SequenceEqual("old"u8) ||
                !text.Bytes.ToArray().AsSpan().SequenceEqual("old"u8) ||
                !nextRaw.Bytes.ToArray().AsSpan().SequenceEqual(" mixed\r\nnew"u8) ||
                !nextText.Bytes.ToArray().AsSpan().SequenceEqual("new"u8) ||
                !wire.Written.ToArray().AsSpan().SequenceEqual("terminal remains active"u8))
                throw new InvalidOperationException($"The recording retry mixed intervals or affected terminal input: " +
                    $"old Raw={Convert.ToHexString(raw.Bytes.ToArray())}, old Text={Convert.ToHexString(text.Bytes.ToArray())}, " +
                    $"new Raw={Convert.ToHexString(nextRaw.Bytes.ToArray())}, new Text={Convert.ToHexString(nextText.Bytes.ToArray())}, " +
                    $"sent={Convert.ToHexString(wire.Written.ToArray())}.");
        }
        finally
        {
            wire.ReleaseWrite.TrySetResult();
            raw.CompleteClose.TrySetResult();
            text.CompleteClose.TrySetResult();
        }
    }

    private sealed class HeldRecordingFile(bool holdFlush = false, bool holdClose = false) : IRecordingFile
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Bytes { get; } = new();
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public bool Closed { get; private set; }

        public async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken token)
        {
            WriteCalls++;
            WriteStarted.TrySetResult();
            if (!holdFlush)
            {
                await CompleteWrite.Task;
                Bytes.Write(buffers[0].Span[..3]);
                throw new IOException("Fixture partial write.");
            }
            foreach (var buffer in buffers) Bytes.Write(buffer.Span);
        }

        public async ValueTask FlushAsync(CancellationToken token)
        {
            FlushCalls++;
            FlushStarted.TrySetResult();
            if (holdFlush) await CompleteFlush.Task;
        }

        public async ValueTask DisposeAsync()
        {
            CloseStarted.TrySetResult();
            try { if (holdClose) await CompleteClose.Task; }
            finally
            {
                Closed = true;
                Bytes.Dispose();
            }
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pending = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.FromSeconds(10))
            {
                var timer = new InactiveTimer();
                pending.Enqueue(() => { if (!timer.Disposed) callback(state); });
                return timer;
            }
            return System.CreateTimer(callback, state, dueTime, period);
        }

        public void ExpireIo()
        {
            while (pending.TryDequeue(out var fire)) fire();
        }

        private sealed class InactiveTimer : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class HeldWriteWire(byte[] payload, bool holdClose = false, bool failReadAfterPayload = false,
        Task<byte[]>? nextInput = null) : ISessionWire
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Written { get; } = new();
        public bool FailWrites { get; set; }
        public int CloseCalls { get; private set; }
        private bool received;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (received)
            {
                if (failReadAfterPayload) throw new IOException("COM receive overrun. Some input was lost.");
                if (nextInput is null) return 0;
                byte[] following = await nextInput.WaitAsync(cancellationToken);
                nextInput = null;
                following.AsSpan().CopyTo(buffer.Span);
                return following.Length;
            }
            received = true;
            payload.AsSpan().CopyTo(buffer.Span);
            return payload.Length;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
            if (FailWrites) throw new IOException("Controlled wire write failure.");
            await Written.WriteAsync(buffer, cancellationToken);
        }

        public async ValueTask CloseAsync()
        {
            CloseCalls++;
            CloseStarted.TrySetResult();
            if (holdClose) await ReleaseClose.Task;
            Dispose();
        }

        public void Dispose()
        {
            ReleaseClose.TrySetResult();
            ReleaseWrite.TrySetResult();
            Written.Dispose();
        }
    }
}
