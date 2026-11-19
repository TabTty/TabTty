namespace TabTty.Core;

internal readonly record struct SshSendRange(int Offset, int Length);

internal readonly record struct SshSendState(int TotalBytes, int DefiniteBytes, SshSendRange? Pending,
    SshSendRange? Uncertain, bool Settled)
{
    internal SshSendRange Uncommitted
    {
        get
        {
            int offset = DefiniteBytes + ((Pending ?? Uncertain)?.Length ?? 0);
            return new(offset, TotalBytes - offset);
        }
    }
}

// One contiguous prefix and at most one committed packet belong to this send.
// Observers can stop waiting independently of the actual packet writer.
internal sealed class SshSendProgress(int length)
{
    private readonly object gate = new();
    private SshSendState state = new(length, 0, null, null, false);
    private TaskCompletionSource? changed;

    internal SshSendState State { get { lock (gate) return state; } }

    internal Task WaitForChangeAsync(SshSendState observed, CancellationToken token)
    {
        Task signal;
        lock (gate)
        {
            if (state != observed || state.Settled) return Task.CompletedTask;
            signal = (changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        return signal.WaitAsync(token);
    }

    internal void Commit(int count)
    {
        lock (gate)
            Publish(state with { Pending = new(state.DefiniteBytes, count) });
    }

    internal void Submitted()
    {
        lock (gate)
            Publish(state with { DefiniteBytes = state.DefiniteBytes + state.Pending!.Value.Length, Pending = null });
    }

    internal void Failed()
    {
        lock (gate)
            if (state.Pending is { } range) Publish(state with { Pending = null, Uncertain = range });
    }

    internal void Settle()
    {
        lock (gate)
            if (!state.Settled) Publish(state with { Settled = true });
    }

    private void Publish(SshSendState next)
    {
        state = next;
        changed?.TrySetResult();
        changed = null;
    }
}
