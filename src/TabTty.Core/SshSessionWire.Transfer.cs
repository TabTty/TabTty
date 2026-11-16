using System.Threading.Channels;

namespace TabTty.Core;

internal sealed partial class SshSessionWire
{
    private static readonly ResourceQuota TransferBuffers = new(32 * 1024 * 1024);
    private readonly object transferGate = new();
    private TransferChannel? transfer;
    private uint nextTransferId;

    public async Task<SftpClient> OpenTransferAsync(string encoding, CancellationToken token)
    {
        TransferChannel channel;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        deadline.CancelAfter(wait);
        while (true)
        {
            Task previous;
            lock (transferGate)
            {
                if (stop.IsCancellationRequested || peerClosed) throw new SftpException(SftpFailure.Disconnected);
                if (transfer is null)
                {
                    channel = new TransferChannel(this, checked(++nextTransferId));
                    transfer = channel;
                    break;
                }
                if (!transfer.Closing) throw new SftpException(SftpFailure.Capacity);
                previous = transfer.Released.Task;
            }
            try { await previous.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !stop.IsCancellationRequested)
            { throw new SftpException(SftpFailure.Timeout); }
        }
        bool sent = false;
        try
        {
            using var request = new SshWriter();
            request.Byte(90);
            request.Text("session");
            request.Number(channel.Id);
            request.Number(ReceiveWindow);
            request.Number(MaxChunk);
            await SendTransferPacketAsync(request.Memory, deadline.Token).ConfigureAwait(false);
            sent = true;
            await channel.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var subsystem = channel.Message(98);
            subsystem.Text("subsystem");
            subsystem.Byte(1);
            subsystem.Text("sftp");
            await SendTransferPacketAsync(subsystem.Memory, deadline.Token, () => channel.IsUsable).ConfigureAwait(false);
            await channel.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var sftp = new SftpClient(channel, encoding, wait);
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

    private async Task SendTransferPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken token, Func<bool>? canSend = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        await send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (canSend is not null && !canSend()) throw new SftpException(SftpFailure.Disconnected);
            try { await packets.SendAsync(packet, stop.Token).ConfigureAwait(false); }
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
        if (channel.Finished) ReleaseTransferChannel(channel);
    }

    private void ReleaseTransferChannel(TransferChannel channel)
    {
        lock (transferGate)
        {
            if (transfer == channel) transfer = null;
        }
        channel.Release();
    }

    private void EndTransferChannel()
    {
        TransferChannel? channel;
        lock (transferGate) channel = transfer;
        if (channel is not null) ReleaseTransferChannel(channel);
    }

    internal sealed class TransferChannel(SshSessionWire owner, uint id)
    {
        private readonly ResourceReservation memory = TransferBuffers.Reserve(2 * 1024 * 1024);
        private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(1024);
        private readonly object gate = new();
        private readonly SemaphoreSlim write = new(1);
        private TaskCompletionSource windowChanged = NewSignal();
        private uint peerId, sendWindow, maxPacket;
        private int credit = ReceiveWindow, returned;
        private bool confirmed, closing, sentClose, eof;
        private int finished;
        public uint Id { get; } = id;
        public bool Finished => Volatile.Read(ref finished) != 0;
        public bool Closing { get { lock (gate) return closing; } }
        public bool IsUsable { get { lock (gate) return !closing && !eof && !Finished; } }
        public TaskCompletionSource Released { get; } = NewSignal();
        public TaskCompletionSource Opened { get; } = NewSignal();
        public TaskCompletionSource Started { get; } = NewSignal();
        public ChannelReader<byte[]> Input => input.Reader;

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
                        sendWindow = reader.Number();
                        maxPacket = reader.Number();
                        reader.End();
                        if (maxPacket == 0) throw new InvalidDataException("Invalid SFTP channel packet size.");
                        confirmed = true;
                        Opened.TrySetResult();
                        return closing ? CloseMessage() : null;
                    case 92 when !confirmed && !Opened.Task.IsCompleted:
                        reader.Number();
                        reader.String();
                        reader.String();
                        reader.End();
                        Opened.TrySetException(new SftpException(SftpFailure.ChannelRefused));
                        Volatile.Write(ref finished, 1);
                        return null;
                    case 93 when confirmed:
                        uint increase = reader.Number();
                        reader.End();
                        if (increase > uint.MaxValue - sendWindow) throw new InvalidDataException("SFTP channel window overflow.");
                        sendWindow += increase;
                        Signal();
                        return null;
                    case 94 or 95 when confirmed:
                        if (type == 95) reader.Number();
                        ReadOnlySpan<byte> bytes = reader.String();
                        reader.End();
                        if (bytes.Length > MaxChunk || bytes.Length > credit || eof)
                            throw new InvalidDataException("Invalid SFTP channel data.");
                        credit -= bytes.Length;
                        if (closing || bytes.IsEmpty) return null;
                        if (type == 95) return CreditMessage(bytes.Length);
                        if (!input.Writer.TryWrite(bytes.ToArray()))
                        {
                            input.Writer.TryComplete(new SftpException(SftpFailure.Capacity));
                            return CloseMessage();
                        }
                        return null;
                    case 96 when confirmed:
                        reader.End();
                        eof = true;
                        input.Writer.TryComplete();
                        return null;
                    case 97 when confirmed:
                        reader.End();
                        Volatile.Write(ref finished, 1);
                        return CloseMessage();
                    case 98 when confirmed:
                        reader.String();
                        if (!reader.Boolean() || closing) return null;
                        using (SshWriter failure = Message(100)) return failure.Memory.ToArray();
                    case 99 when confirmed:
                        reader.End();
                        Started.TrySetResult();
                        return null;
                    case 100 when confirmed:
                        reader.End();
                        Started.TrySetException(new SftpException(SftpFailure.SubsystemRefused));
                        return null;
                    default:
                        throw new InvalidDataException("Unexpected SFTP SSH channel message.");
                }
            }
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            await write.WaitAsync(token).ConfigureAwait(false);
            bool partial = false;
            try
            {
                while (!data.IsEmpty)
                {
                    token.ThrowIfCancellationRequested();
                    Task signal;
                    int count;
                    lock (gate)
                    {
                        if (closing || Finished) throw new SftpException(SftpFailure.Disconnected);
                        signal = windowChanged.Task;
                        count = (int)Math.Min((uint)Math.Min(MaxChunk, data.Length), Math.Min(maxPacket, sendWindow));
                        sendWindow -= (uint)count;
                    }
                    if (count == 0) { await signal.WaitAsync(token).ConfigureAwait(false); continue; }
                    using var packet = Message(94);
                    packet.String(data.Span[..count]);
                    try { await owner.SendTransferPacketAsync(packet.Memory, token, () => IsUsable).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!owner.stop.IsCancellationRequested)
                    {
                        lock (gate) sendWindow += (uint)count;
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
                write.Release();
            }
        }

        public Task ReturnCreditAsync(int count)
        {
            byte[]? packet;
            lock (gate) packet = closing ? null : CreditMessage(count);
            return packet is null ? Task.CompletedTask : ReturnAsync(packet);
        }

        private async Task ReturnAsync(byte[] packet)
        {
            try { await owner.SendTransferPacketAsync(packet, owner.stop.Token, () => !Closing && !Finished).ConfigureAwait(false); }
            catch (SftpException error) when (error.Failure == SftpFailure.Disconnected) { }
        }

        private byte[]? CreditMessage(int count)
        {
            returned += count;
            if (returned < ReceiveWindow / 2 && credit > MaxChunk) return null;
            using var packet = Message(93);
            packet.Number((uint)returned);
            credit += returned;
            returned = 0;
            return packet.Memory.ToArray();
        }

        private byte[]? CloseMessage()
        {
            closing = true;
            Signal();
            input.Writer.TryComplete();
            if (!confirmed || sentClose) return null;
            sentClose = true;
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
                if (closing) return;
                closing = true;
                Signal();
                input.Writer.TryComplete();
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

        public void Release()
        {
            lock (gate)
            {
                closing = true;
                Signal();
                var failure = new SftpException(SftpFailure.Disconnected);
                Opened.TrySetException(failure);
                Started.TrySetException(failure);
                input.Writer.TryComplete();
                while (input.Reader.TryRead(out _)) { }
            }
            memory.Dispose();
            Released.TrySetResult();
        }
    }
}
