using System.Diagnostics;

namespace TabTty.Core;

internal sealed class SshRemoteDeadline : IDisposable
{
    private readonly CancellationTokenSource cancellation;
    private TimeSpan remaining;
    private long started;
    public CancellationToken Parent { get; }
    public CancellationToken Token => cancellation.Token;

    public SshRemoteDeadline(CancellationToken parent, TimeSpan duration)
    {
        Parent = parent;
        remaining = duration;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        Resume();
    }

    public void Pause()
    {
        Token.ThrowIfCancellationRequested();
        cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
        remaining -= Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) { cancellation.Cancel(); Token.ThrowIfCancellationRequested(); }
    }

    public void Resume()
    {
        Token.ThrowIfCancellationRequested();
        started = Stopwatch.GetTimestamp();
        cancellation.CancelAfter(remaining);
    }

    public void Dispose() => cancellation.Dispose();
}
