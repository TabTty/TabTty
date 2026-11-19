namespace TabTty.Core;

// One encrypted packet writer, with FIFO admission across both channel
// writers and transport control. Payload cannot consume the control slots.
internal sealed class SshPacketWriterOrder
{
    private const int MaximumWaiters = 64;
    private const int MaximumPayloadWaiters = 32;
    private readonly object gate = new();
    private readonly LinkedList<Waiter> queue = new();
    private bool held;
    private int payloadWaiters;

    private sealed class Waiter(bool payload)
    {
        internal bool Payload { get; } = payload;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal ValueTask WaitAsync(CancellationToken token = default, bool payload = false)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (!held)
            {
                held = true;
                return ValueTask.CompletedTask;
            }
            if (queue.Count == MaximumWaiters || payload && payloadWaiters == MaximumPayloadWaiters)
                throw new ResourceLimitException("The SSH packet writer queue is full.");
            var node = queue.AddLast(new Waiter(payload));
            if (payload) payloadWaiters++;
            return WaitForTurnAsync(node, token);
        }
    }

    private async ValueTask WaitForTurnAsync(LinkedListNode<Waiter> node, CancellationToken token)
    {
        using var registration = token.Register(() =>
        {
            lock (gate)
            {
                if (node.List is null) return;
                Remove(node);
                node.Value.Ready.TrySetCanceled(token);
            }
        });
        await node.Value.Ready.Task.ConfigureAwait(false);
    }

    internal void Release()
    {
        lock (gate)
        {
            if (queue.First is { } next)
            {
                Remove(next);
                next.Value.Ready.SetResult();
            }
            else held = false;
        }
    }

    private void Remove(LinkedListNode<Waiter> node)
    {
        if (node.Value.Payload) payloadWaiters--;
        queue.Remove(node);
    }
}
