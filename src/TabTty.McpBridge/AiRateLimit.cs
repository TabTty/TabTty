namespace TabTty.AiTransport;

internal sealed class AiRateLimit(int rate, int burst, long now)
{
    // One call costs 1000 units. Each elapsed millisecond replenishes rate units.
    private const int CallCost = 1000;
    private long available = burst * CallCost;
    private long updated = now;

    internal static AiRateLimit OrdinaryCalls(long now) => new(20, 40, now);
    internal static AiRateLimit ControlReturns(long now) => new(4, 8, now);

    internal bool Take(long time, out int retry)
    {
        available = Math.Min(burst * CallCost, available + Math.Max(0, time - updated) * rate);
        updated = Math.Max(updated, time);
        if (available < CallCost)
        {
            retry = (int)((CallCost - available + rate - 1) / rate);
            return false;
        }
        available -= CallCost;
        retry = 0;
        return true;
    }
}
