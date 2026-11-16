using TabTty.Core;

namespace TabTty.App;

internal sealed class VaultUnlockInteraction
{
    private readonly object gate = new();
    private Interaction? pending;

    private sealed class Interaction
    {
        public readonly TaskCompletionSource<CredentialVault?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Lifetime = new();
        public int Waiters;
    }

    public bool IsPending
    {
        get { lock (gate) return pending is not null; }
    }

    public Task<CredentialVault?> WaitAsync(Func<Task<CredentialVault?>> start, CancellationToken token = default) =>
        WaitAsync(_ => start(), token);

    public Task<CredentialVault?> WaitAsync(Func<CancellationToken, Task<CredentialVault?>> start, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Interaction interaction;
        bool initiate;
        lock (gate)
        {
            initiate = pending is null;
            interaction = pending ??= new Interaction();
            interaction.Waiters++;
        }
        if (initiate) _ = CompleteAsync(interaction, start);
        return AwaitAsync(interaction, token);
    }

    private async Task<CredentialVault?> AwaitAsync(Interaction interaction, CancellationToken token)
    {
        try { return await interaction.Completion.Task.WaitAsync(token); }
        finally
        {
            lock (gate)
            {
                if (--interaction.Waiters == 0 && !interaction.Completion.Task.IsCompleted)
                    interaction.Lifetime.Cancel();
            }
        }
    }

    private async Task CompleteAsync(Interaction interaction, Func<CancellationToken, Task<CredentialVault?>> start)
    {
        CredentialVault? result = null;
        Exception? failure = null;
        try { result = await start(interaction.Lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { failure = exception; }
        lock (gate)
        {
            if (failure is null) interaction.Completion.SetResult(result);
            else interaction.Completion.SetException(failure);
            pending = null;
            interaction.Lifetime.Dispose();
        }
    }
}
