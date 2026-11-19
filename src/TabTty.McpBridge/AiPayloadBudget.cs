namespace TabTty.AiTransport;

internal sealed class AiPayloadBudget
{
    private const int Total = 8 * 1024 * 1024;
    private const int ControlReserve = 512 * 1024;
    private int used;

    internal bool TryReserve(int bytes, bool priority)
    {
        int limit = priority ? Total : Total - ControlReserve;
        while (true)
        {
            int current = Volatile.Read(ref used);
            if (bytes > limit - current) return false;
            if (Interlocked.CompareExchange(ref used, current + bytes, current) == current) return true;
        }
    }

    internal void Release(int bytes) => Interlocked.Add(ref used, -bytes);
}
