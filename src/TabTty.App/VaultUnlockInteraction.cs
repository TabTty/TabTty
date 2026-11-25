using System.Windows;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class VaultUnlockInteraction(Action? onIdle = null, Action<CredentialVault>? adopt = null)
{
    private const int MaximumConsumers = 256;
    private readonly object gate = new();
    private Interaction? pending;
    private int admittedConsumers;

    internal readonly record struct UnlockStatus(Guid? OperationId, int Consumers, bool Retiring);
    internal UnlockStatus Status
    {
        get { lock (gate) return new(pending?.Id, admittedConsumers, pending?.Retiring == true); }
    }

    private sealed class Interaction
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly TaskCompletionSource<CredentialVault?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Lifetime = new();
        public readonly List<Consumer> Consumers = [];
        public UnlockHosts? Hosts;
        public bool Retiring;
        public bool AcceptingResult;
    }

    internal sealed class UnlockHosts(Func<bool, Window?> select, Func<TerminalTab[]> tabs)
    {
        internal Window? Current => select(false);
        internal Window? InitializationHost => select(true);
        internal TerminalTab[] Tabs => tabs();
        internal event Action? Changed;
        internal void NotifyChanged() => Changed?.Invoke();
    }

    private sealed class Consumer(Window? host, CancellationToken token, bool allowInitialization, TerminalTab? tab)
    {
        internal Window? Host => tab?.DecisionHost ?? host;
        internal TerminalTab? Tab => tab;
        internal CancellationToken Token { get; } = token;
        internal bool AllowInitialization { get; } = allowInitialization;
    }

    internal sealed class Interest(Task<CredentialVault?> result, CancellationTokenSource lifetime) : IDisposable
    {
        private int disposed;

        internal Task<CredentialVault?> ObserveAsync(CancellationToken observation = default) =>
            result.WaitAsync(observation);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            _ = result.ContinueWith(task =>
            {
                _ = task.Exception;
                lifetime.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public bool IsPending
    {
        get { lock (gate) return pending is not null || admittedConsumers != 0; }
    }

    public Task<CredentialVault?> WaitAsync(Func<Task<CredentialVault?>> start, CancellationToken token = default) =>
        WaitAsync(null, (_, _) => start(), token);

    public async Task<CredentialVault?> WaitAsync(Window? host,
        Func<UnlockHosts, CancellationToken, Task<CredentialVault?>> start, CancellationToken token = default,
        bool allowInitialization = false, TerminalTab? tab = null)
    {
        using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (host is not null && tab is null) host.Closed += StopInterest;
        try
        {
            using var interest = Admit(host, start, hostLifetime.Token, allowInitialization, tab);
            return await interest.ObserveAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && hostLifetime.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (host is not null && tab is null) host.Closed -= StopInterest;
        }

        void StopInterest(object? sender, EventArgs args) => hostLifetime.Cancel();
    }

    internal Interest Admit(Window? host, Func<UnlockHosts, CancellationToken, Task<CredentialVault?>> start,
        CancellationToken stop = default, bool allowInitialization = false, TerminalTab? tab = null)
    {
        stop.ThrowIfCancellationRequested();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (gate)
        {
            if (admittedConsumers >= MaximumConsumers)
            {
                lifetime.Dispose();
                throw new InvalidOperationException(Text("VaultBusy"));
            }
            admittedConsumers++;
        }
        return new(WaitOwnedAsync(new Consumer(host, lifetime.Token, allowInitialization, tab), start, lifetime.Token), lifetime);
    }

    private async Task<CredentialVault?> WaitOwnedAsync(Consumer consumer,
        Func<UnlockHosts, CancellationToken, Task<CredentialVault?>> start, CancellationToken token)
    {
        Interaction? joined = null;
        try
        {
            while (joined is null)
            {
                bool initiate = false;
                Task? retirement = null;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (pending is { Retiring: true } previous) retirement = previous.Completion.Task;
                    else
                    {
                        initiate = pending is null;
                        joined = pending ??= new Interaction();
                        joined.Consumers.Add(consumer);
                    }
                }

                if (retirement is not null)
                {
                    try { await retirement.WaitAsync(token); }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                }
                else if (initiate) _ = CompleteAsync(joined!, start);
                else joined!.Hosts?.NotifyChanged();
            }
            return await joined.Completion.Task.WaitAsync(token);
        }
        finally
        {
            lock (gate)
            {
                admittedConsumers--;
                if (joined is not null)
                {
                    joined.Consumers.Remove(consumer);
                    if (joined.Consumers.Count == 0 && !joined.AcceptingResult && !joined.Completion.Task.IsCompleted)
                    {
                        joined.Retiring = true;
                        _ = joined.Lifetime.CancelAsync();
                    }
                }
            }
            joined?.Hosts?.NotifyChanged();
            if (!IsPending) onIdle?.Invoke();
        }
    }

    private Window? SelectHost(Interaction interaction, bool forInitialization)
    {
        Window?[] hosts;
        lock (gate) hosts = interaction.Consumers.Where(consumer => !consumer.Token.IsCancellationRequested &&
                (!forInitialization || consumer.AllowInitialization))
            .Select(consumer => consumer.Host).ToArray();
        return hosts.FirstOrDefault(host => host?.IsVisible == true);
    }

    private async Task CompleteAsync(Interaction interaction,
        Func<UnlockHosts, CancellationToken, Task<CredentialVault?>> start)
    {
        CredentialVault? result = null;
        Exception? failure = null;
        interaction.Hosts = new(eligible => SelectHost(interaction, eligible), () =>
        {
            lock (gate) return interaction.Consumers.Where(consumer => !consumer.Token.IsCancellationRequested)
                .Select(consumer => consumer.Tab).OfType<TerminalTab>().Distinct().ToArray();
        });
        try { result = await start(interaction.Hosts, interaction.Lifetime.Token); }
        catch (OperationCanceledException) when (interaction.Lifetime.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        if (result is not null)
        {
            lock (gate)
                interaction.AcceptingResult = !interaction.Retiring &&
                    interaction.Consumers.Any(consumer => !consumer.Token.IsCancellationRequested);
            if (interaction.AcceptingResult)
            {
                try { adopt?.Invoke(result); }
                catch (Exception exception) { failure = exception; }
            }
            if (!interaction.AcceptingResult || failure is not null)
            {
                result.Dispose();
                result = null;
            }
        }
        lock (gate)
        {
            if (failure is null) interaction.Completion.SetResult(result);
            else
            {
                interaction.Completion.SetException(failure);
                _ = interaction.Completion.Task.Exception;
            }
            pending = null;
            interaction.Lifetime.Dispose();
        }
        if (!IsPending) onIdle?.Invoke();
    }
}
