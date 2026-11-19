namespace TabTty.Core;

internal sealed partial class SshSessionWire
{
    private static readonly ResourceQuota TransferBuffers = new(32 * 1024 * 1024);
    private readonly object transferGate = new();
    private TransferChannel? transfer;
    private uint nextTransferId;
    private bool transferReopenWaiting;

    internal static ResourceReservation ReserveTransferStorage(int bytes)
    {
        try { return TransferBuffers.Reserve(bytes); }
        catch (ResourceLimitException error) { throw new SftpException(SftpFailure.Capacity, error); }
    }

    public async Task<SftpClient> OpenTransferAsync(string encoding, CancellationToken token)
    {
        TransferChannel channel;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        deadline.CancelAfter(wait);
        bool ownsReopen = false;
        try
        {
            while (true)
            {
                Task previous;
                lock (transferGate)
                {
                    if (stop.IsCancellationRequested || peerClosed) throw new SftpException(SftpFailure.Disconnected);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (transferReopenWaiting && !ownsReopen)
                        throw new SftpException(SftpFailure.Capacity);
                    if (transfer is null)
                    {
                        channel = new TransferChannel(this, checked(++nextTransferId));
                        transfer = channel;
                        break;
                    }
                    if (!transfer.Closing) throw new SftpException(SftpFailure.Capacity);
                    transferReopenWaiting = ownsReopen = true;
                    previous = transfer.ActualRelease;
                }
                await previous.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            throw new SftpException(SftpFailure.Timeout);
        }
        finally
        {
            if (ownsReopen) lock (transferGate) transferReopenWaiting = false;
        }
        bool sent = false;
        try
        {
            var sftp = new SftpClient(channel, encoding, wait);
            channel.BindParser(sftp);
            using var request = new SshWriter();
            request.Byte(90);
            request.Text("session");
            request.Number(channel.Id);
            request.Number(ReceiveWindow);
            request.Number(MaxChunk);
            await SendTransferPacketAsync(request.Memory, deadline.Token, committed: () => sent = true).ConfigureAwait(false);
            await channel.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var subsystem = channel.Message(98);
            subsystem.Text("subsystem");
            subsystem.Byte(1);
            subsystem.Text("sftp");
            var requested = await channel.Requests.RequestAsync(commit => SendTransferPacketAsync(subsystem.Memory, deadline.Token,
                () => channel.IsUsable, commit), deadline.Token).ConfigureAwait(false);
            if (!requested.Success)
                throw new SftpException(SftpFailure.SubsystemRefused);
            await sftp.InitializeAsync(deadline.Token).ConfigureAwait(false);
            return sftp;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            if (sent) channel.Close(); else ReleaseTransferChannel(channel);
            throw new TimeoutException("SFTP startup timed out.");
        }
        catch { if (sent) channel.Close(); else ReleaseTransferChannel(channel); throw; }
    }

    private async Task SendTransferPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken token,
        Func<bool>? canSend = null, Action? committed = null, bool payload = false,
        SshSendProgress? progress = null, int count = 0)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        await send.WaitAsync(linked.Token, payload).ConfigureAwait(false);
        try
        {
            if (canSend is not null && !canSend()) throw new SftpException(SftpFailure.Disconnected);
            bool issued = false;
            try
            {
                if (progress is null)
                    await packets.SendAsync(packet, stop.Token, linked.Token, Commit).ConfigureAwait(false);
                else
                    await packets.SendChannelAsync(packet, stop.Token, linked.Token, Commit, progress, count).ConfigureAwait(false);

                void Commit()
                {
                    committed?.Invoke();
                    issued = true;
                }
            }
            catch (OperationCanceledException) when (!issued && !stop.IsCancellationRequested) { throw; }
            catch { Dispose(); throw; }
        }
        finally { send.Release(); }
    }

    private async Task ProcessTransferAsync(uint id, byte type, ReadOnlyMemory<byte> body, CancellationToken token, bool defer)
    {
        TransferChannel? channel;
        lock (transferGate) channel = transfer;
        if (channel?.Id != id) throw new InvalidDataException("Invalid SSH channel identifier.");
        byte[]? response = channel.Process(type, body.Span);
        if (response is not null) await RespondAsync(response, token, defer).ConfigureAwait(false);
        if (type == 95) channel.ScheduleDiscardedCredit();
        if (channel.Finished) ReleaseTransferChannel(channel);
    }

    private void ReleaseTransferChannel(TransferChannel channel)
    {
        if (channel.BeginRelease()) _ = ReleaseTransferChannelAsync(channel);
    }

    private async Task ReleaseTransferChannelAsync(TransferChannel channel)
    {
        await channel.ReleaseAsync().ConfigureAwait(false);
        lock (transferGate)
        {
            if (transfer == channel) transfer = null;
        }
        channel.EndRelease();
    }

    private void EndTransferChannel()
    {
        TransferChannel? channel;
        lock (transferGate) channel = transfer;
        if (channel is not null) ReleaseTransferChannel(channel);
    }

    internal sealed class TransferChannel(SshSessionWire owner, uint id)
    {
        private readonly ResourceReservation memory = ReserveTransferStorage(2 * 1024 * 1024);
        private byte[] input = new byte[ReceiveWindow];
        private int inputStart, inputCount;
        private TaskCompletionSource? inputChanged;
        private Task? parserDelivery;
        private readonly object gate = new();
        private readonly SemaphoreSlim write = new(1);
        private TaskCompletionSource windowChanged = NewSignal();
        private uint peerId;
        private readonly SshChannelWindow window = new(ReceiveWindow, MaxChunk);
        private int discardedGrant;
        private bool confirmed;
        private readonly SshChannelClosure closure = new();
        public uint Id { get; } = id;
        public bool Finished => closure.ProtocolEnded.IsCompleted;
        public bool Closing => closure.CloseRequested;
        public bool IsUsable { get { lock (gate) return !Closing && !closure.ReceiveEnded && !Finished; } }
        public Task ActualRelease => closure.ActualRelease;
        public TaskCompletionSource Opened { get; } = NewSignal();
        public SshChannelRequests Requests { get; } = new();


        public SshWriter Message(byte type)
        {
            var packet = new SshWriter();
            packet.Byte(type);
            packet.Number(peerId);
            return packet;
        }

        public byte[]? Process(byte type, ReadOnlySpan<byte> body)
        {
            var reader = new SshReader(body);
            lock (gate)
            {
                switch (type)
                {
                    case 91 when !confirmed && !Opened.Task.IsCompleted:
                        peerId = reader.Number();
                        window.Open(reader.Number(), reader.Number());
                        reader.End();
                        confirmed = true;
                        Opened.TrySetResult();
                        return Closing ? CloseMessage() : null;
                    case 92 when !confirmed && !Opened.Task.IsCompleted:
                        reader.Number();
                        reader.String();
                        reader.String();
                        reader.End();
                        Opened.TrySetException(new SftpException(SftpFailure.ChannelRefused));
                        closure.EndProtocol();
                        return null;
                    case 93 when confirmed:
                        uint increase = reader.Number();
                        reader.End();
                        window.AddSend(increase);
                        Signal();
                        return null;
                    case 94 or 95 when confirmed:
                        if (type == 95) reader.Number();
                        ReadOnlySpan<byte> bytes = reader.String();
                        reader.End();
                        if (closure.ReceiveEnded) throw new InvalidDataException("Invalid SFTP channel data after EOF.");
                        window.AcceptReceive(bytes.Length);
                        if (Closing || bytes.IsEmpty) return null;
                        if (type == 95)
                        {
                            int grant = window.Consume(bytes.Length);
                            if (grant != 0) discardedGrant = grant;
                            return null;
                        }
                        if (bytes.Length > input.Length - inputCount)
                            throw new InvalidDataException("SFTP channel receive capacity exceeded.");
                        int offset = (inputStart + inputCount) & (ReceiveWindow - 1);
                        int first = Math.Min(bytes.Length, input.Length - offset);
                        bytes[..first].CopyTo(input.AsSpan(offset));
                        bytes[first..].CopyTo(input);
                        inputCount += bytes.Length;
                        SignalInput();
                        return null;
                    case 96 when confirmed:
                        reader.End();
                        closure.ReceiveEof();
                        SignalInput();
                        return null;
                    case 97 when confirmed:
                        reader.End();
                        closure.EndProtocol();
                        return CloseMessage();
                    case 98 when confirmed:
                        reader.String();
                        if (!reader.Boolean() || Closing) return null;
                        using (SshWriter failure = Message(100)) return failure.Memory.ToArray();
                    case 99 or 100 when confirmed:
                        reader.End();
                        Requests.Complete(type == 99);
                        return null;
                    default:
                        throw new InvalidDataException("Unexpected SFTP SSH channel message.");
                }
            }
        }

        public void BindParser(SftpClient parser)
        {
            lock (gate)
            {
                if (Closing || Finished || parserDelivery is not null)
                    throw new SftpException(SftpFailure.Disconnected);
                parserDelivery = Task.Run(() => DeliverAsync(parser));
            }
        }

        private async Task DeliverAsync(SftpClient parser)
        {
            SftpException failure;
            try
            {
                byte[] chunk = new byte[MaxChunk];
                while (true)
                {
                    int count = await ReadAsync(chunk).ConfigureAwait(false);
                    await parser.ConsumeAsync(chunk.AsMemory(0, count)).ConfigureAwait(false);
                    ReturnCredit(count);
                }
            }
            catch (SftpException error) { failure = error; }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
            {
                failure = new SftpException(SftpFailure.InvalidReply, error);
            }
            parser.CompleteInput(failure);
        }

        private async ValueTask<int> ReadAsync(Memory<byte> output)
        {
            while (true)
            {
                Task changed;
                lock (gate)
                {
                    if (Closing) throw new SftpException(SftpFailure.Disconnected);
                    if (inputCount != 0)
                    {
                        int count = Math.Min(output.Length, inputCount);
                        int first = Math.Min(count, input.Length - inputStart);
                        input.AsSpan(inputStart, first).CopyTo(output.Span);
                        input.AsSpan(0, count - first).CopyTo(output.Span[first..]);
                        inputStart = (inputStart + count) & (ReceiveWindow - 1);
                        inputCount -= count;
                        return count;
                    }
                    if (closure.ReceiveEnded || Finished) throw new SftpException(SftpFailure.Disconnected);
                    changed = (inputChanged ??= NewSignal()).Task;
                }
                await changed.ConfigureAwait(false);
            }
        }

        private void SignalInput()
        {
            inputChanged?.TrySetResult();
            inputChanged = null;
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token, SshSendProgress? progress = null)
        {
            progress ??= new(data.Length);
            bool admitted = false;
            bool partial = false;
            try
            {
                await write.WaitAsync(token).ConfigureAwait(false);
                admitted = true;
                while (!data.IsEmpty)
                {
                    token.ThrowIfCancellationRequested();
                    Task signal;
                    int count;
                    lock (gate)
                    {
                        if (Closing || Finished) throw new SftpException(SftpFailure.Disconnected);
                        signal = windowChanged.Task;
                        count = window.ReserveSend(data.Length);
                    }
                    if (count == 0) { await signal.WaitAsync(token).ConfigureAwait(false); continue; }
                    using var packet = Message(94);
                    packet.String(data.Span[..count]);
                    try
                    {
                        await owner.SendTransferPacketAsync(packet.Memory, token, () => IsUsable,
                            () => { lock (gate) window.CommitSend(count); },
                            payload: true, progress: progress, count: count).ConfigureAwait(false);
                    }
                    catch (Exception error) when (progress.State.Uncertain is not null && count == data.Length)
                    {
                        throw new SshFrameSubmissionUncertainException(error);
                    }
                    catch (Exception error) when (progress.State.Uncertain is null && !owner.stop.IsCancellationRequested &&
                        error is OperationCanceledException or ResourceLimitException or SftpException)
                    {
                        lock (gate) window.CancelSend(count);
                        throw;
                    }
                    data = data[count..];
                    partial = !data.IsEmpty;
                    if (partial) await Task.Yield();
                }
            }
            finally
            {
                if (partial) Close();
                if (admitted) write.Release();
                progress.Settle();
            }
        }

        private void ReturnCredit(int count)
        {
            int grant;
            lock (gate) grant = Closing ? 0 : window.Consume(count);
            if (grant != 0) _ = ReturnDiscardedAsync(grant);
        }

        public void ScheduleDiscardedCredit()
        {
            int grant;
            lock (gate)
            {
                grant = discardedGrant;
                discardedGrant = 0;
            }
            if (grant != 0) _ = ReturnDiscardedAsync(grant);
        }

        private async Task ReturnDiscardedAsync(int grant)
        {
            try { await ReturnAsync(grant).ConfigureAwait(false); }
            catch (Exception) when (owner.stop.IsCancellationRequested) { }
        }

        private async Task ReturnAsync(int count)
        {
            while (count != 0)
            {
                using var packet = Message(93);
                packet.Number((uint)count);
                int grant = count;
                try
                {
                    await owner.SendTransferPacketAsync(packet.Memory, owner.stop.Token, () => !Closing && !Finished, () =>
                    {
                        lock (gate) window.CommitGrant(grant);
                    }).ConfigureAwait(false);
                }
                catch (SftpException error) when (error.Failure == SftpFailure.Disconnected) { return; }
                lock (gate) count = Closing ? 0 : window.CompleteGrant();
            }
        }

        private byte[]? CloseMessage()
        {
            closure.RequestClose();
            Signal();
            SignalInput();
            if (!confirmed || !closure.ScheduleClosePacket()) return null;
            using var packet = Message(97);
            return packet.Memory.ToArray();
        }

        private void Signal()
        {
            windowChanged.TrySetResult();
            windowChanged = NewSignal();
        }

        public void Close()
        {
            lock (gate)
            {
                if (!closure.RequestClose()) return;
                Signal();
                SignalInput();
            }
            _ = CloseAsync();
        }

        private async Task CloseAsync()
        {
            await write.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[]? packet;
                lock (gate) packet = CloseMessage();
                if (packet is not null) await owner.SendTransferPacketAsync(packet, owner.stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (owner.stop.IsCancellationRequested) { }
            finally { write.Release(); }
        }

        public bool BeginRelease() => closure.BeginRelease();
        public void EndRelease() => closure.EndRelease();

        public async Task ReleaseAsync()
        {
            Task parser;
            lock (gate)
            {
                closure.RequestClose();
                Signal();
                var failure = new SftpException(SftpFailure.Disconnected);
                Opened.TrySetException(failure);
                Requests.End(failure);
                SignalInput();
                parser = parserDelivery ?? Task.CompletedTask;
            }
            await parser.ConfigureAwait(false);
            lock (gate)
            {
                inputCount = 0;
                input = [];
            }
            await write.WaitAsync().ConfigureAwait(false);
            try
            {
                await owner.send.WaitAsync().ConfigureAwait(false);
                try { memory.Dispose(); }
                finally { owner.send.Release(); }
            }
            finally { write.Release(); }
        }
    }
}

internal sealed class SshFrameSubmissionUncertainException(Exception cause)
    : IOException("The last SSH packet of an SFTP request may have been submitted.", cause);
