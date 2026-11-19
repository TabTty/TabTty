namespace TabTty.Core;

// Channel closure is independent of release of payloads, parsers and packet keys.
internal sealed class SshChannelClosure
{
    private readonly TaskCompletionSource protocolEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int receiveEof;
    private int closeRequested;
    private int closeScheduled;
    private int releaseStarted;

    internal bool ReceiveEnded => Volatile.Read(ref receiveEof) != 0;
    internal bool CloseRequested => Volatile.Read(ref closeRequested) != 0;
    internal Task ProtocolEnded => protocolEnded.Task;
    internal Task ActualRelease => released.Task;

    internal void ReceiveEof() => Interlocked.Exchange(ref receiveEof, 1);
    internal void EndProtocol() => protocolEnded.TrySetResult();
    internal bool RequestClose() => Interlocked.Exchange(ref closeRequested, 1) == 0;
    internal bool ScheduleClosePacket() => Interlocked.Exchange(ref closeScheduled, 1) == 0;
    internal bool BeginRelease() => Interlocked.Exchange(ref releaseStarted, 1) == 0;
    internal void EndRelease() => released.TrySetResult();
}
