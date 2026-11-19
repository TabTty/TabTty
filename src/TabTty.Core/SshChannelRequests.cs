namespace TabTty.Core;

internal readonly record struct SshChannelReply(bool Success, long ReceiveFence);

// A committed want-reply request retains its correlation slot even when the
// initiating caller leaves. Notifications do not enter this ledger.
internal sealed class SshChannelRequests
{
    private readonly object gate = new();
    private TaskCompletionSource<SshChannelReply>? pending;
    private Exception? closed;

    internal async Task<SshChannelReply> RequestAsync(Func<Action, Task> submit, CancellationToken observation)
    {
        var reply = new TaskCompletionSource<SshChannelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        await submit(() =>
        {
            lock (gate)
            {
                if (closed is { } failure) throw failure;
                if (pending is not null) throw new InvalidDataException("An SSH channel request is still awaiting a reply.");
                pending = reply;
            }
        }).ConfigureAwait(false);
        return await reply.Task.WaitAsync(observation).ConfigureAwait(false);
    }

    internal void Complete(bool success, long priorPayload = 0)
    {
        lock (gate)
        {
            if (pending is not { } request) throw new InvalidDataException("Unexpected SSH channel request reply.");
            pending = null;
            request.TrySetResult(new(success, priorPayload));
        }
    }

    internal void End(Exception failure)
    {
        lock (gate)
        {
            closed ??= failure;
            if (pending is not { } request) return;
            pending = null;
            request.TrySetException(failure);
            _ = request.Task.Exception;
        }
    }
}
