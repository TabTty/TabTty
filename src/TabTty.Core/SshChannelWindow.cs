namespace TabTty.Core;

// Access is serialized by the channel's gate. A queued grant is not
// peer credit until the writer commits its WINDOW_ADJUST packet.
internal sealed class SshChannelWindow
{
    private uint remainingSend, reservedSend, peerMaximum;
    private readonly int capacity, maximumPacket;
    private int peerReceive;
    private int consumed, pending;

    internal SshChannelWindow(int capacity, int maximumPacket)
    {
        this.capacity = capacity;
        this.maximumPacket = maximumPacket;
        peerReceive = capacity;
    }

    internal void Open(uint window, uint packet)
    {
        if (packet == 0) throw new InvalidDataException("Invalid SSH peer packet limit.");
        remainingSend = window;
        peerMaximum = packet;
    }

    internal int ReserveSend(int requested)
    {
        int count = (int)Math.Min((uint)Math.Min(maximumPacket, requested),
            Math.Min(peerMaximum, remainingSend - reservedSend));
        reservedSend += (uint)count;
        return count;
    }

    internal void CommitSend(int bytes)
    {
        CancelSend(bytes);
        remainingSend -= (uint)bytes;
    }

    internal void CancelSend(int bytes)
    {
        if (bytes < 0 || (uint)bytes > reservedSend)
            throw new InvalidDataException("SSH send reservation underflow.");
        reservedSend -= (uint)bytes;
    }

    internal void AddSend(uint amount)
    {
        if (amount > uint.MaxValue - remainingSend) throw new InvalidDataException("SSH channel window overflow.");
        remainingSend += amount;
    }

    internal void AcceptReceive(int bytes)
    {
        if (bytes > maximumPacket || bytes > peerReceive)
            throw new InvalidDataException("SSH channel receive window or packet limit exceeded.");
        peerReceive -= bytes;
    }

    internal int Consume(int bytes)
    {
        consumed = checked(consumed + bytes);
        return NextGrant();
    }

    internal void CommitGrant(int bytes)
    {
        if (pending != bytes || bytes > capacity - peerReceive)
            throw new InvalidDataException("Invalid SSH receive credit commitment.");
        peerReceive += bytes;
    }

    internal int CompleteGrant()
    {
        pending = 0;
        return NextGrant();
    }

    private int NextGrant()
    {
        if (pending != 0 || consumed == 0 || (consumed < capacity / 2 && peerReceive > maximumPacket)) return 0;
        pending = consumed;
        consumed = 0;
        return pending;
    }
}
