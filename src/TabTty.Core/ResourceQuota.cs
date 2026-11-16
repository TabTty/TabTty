namespace TabTty.Core;

internal sealed class ResourceLimitException(string message = "The session reached its resource limit. Capture may be incomplete.")
    : IOException(message);

internal sealed class ResourceQuota(long limit, ResourceQuota? parent = null,
    string message = "The session reached its resource limit. Capture may be incomplete.", bool collectOnPressure = true)
{
    private long used;
    public long Used => Interlocked.Read(ref used);

    public ResourceReservation Reserve(long size)
    {
        var reservation = new ResourceReservation(this);
        reservation.Resize(size);
        return reservation;
    }

    internal void Change(long delta)
    {
        if (delta == 0) return;
        if (delta <= 0) { Interlocked.Add(ref used, delta); parent?.Change(delta); return; }
        if (TryAcquire(delta)) return;
        if (collectOnPressure && limit >= 1024 * 1024)
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true);
            GC.WaitForPendingFinalizers();
            if (TryAcquire(delta)) return;
        }
        throw new ResourceLimitException(message);
    }

    private bool TryAcquire(long delta)
    {
        while (true)
        {
            long current = Used;
            if (delta > limit - current) return false;
            if (Interlocked.CompareExchange(ref used, current + delta, current) != current) continue;
            if (parent is null || parent.TryAcquire(delta)) return true;
            Interlocked.Add(ref used, -delta);
            return false;
        }
    }
}

internal sealed class ResourceReservation(ResourceQuota quota) : IDisposable
{
    private long size;
    private bool disposed;
    public long Size => size;

    public void Resize(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        lock (this)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            quota.Change(value - size);
            size = value;
        }
    }

    public void Add(long delta)
    {
        if (delta != 0) Resize(checked(size + delta));
    }

    internal ResourceReservation Separate(long chargedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chargedBytes);
        lock (this)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (chargedBytes > size) throw new InvalidDataException("The operation exceeded its reserved output bound.");
            var ownership = new ResourceReservation(quota) { size = chargedBytes };
            size -= chargedBytes;
            return ownership;
        }
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        lock (this)
        {
            if (disposed) return;
            quota.Change(-size);
            size = 0;
            disposed = true;
        }
    }

    ~ResourceReservation() => Release();
}

internal static class ApplicationResources
{
    public static ResourceQuota TerminalState { get; } = new(4L * 1024 * 1024 * 1024);
    public static ResourceQuota Receive { get; } = new(128L * 1024 * 1024);
    public static ResourceQuota LogPayload { get; } = new(256L * 1024 * 1024);
    public static SemaphoreSlim LogIo { get; } = new(128);
    public static ResourceQuota SearchCache { get; } = new(64L * 1024 * 1024);
    public static ResourceQuota LayoutCache { get; } = new(128L * 1024 * 1024);
    public static SemaphoreSlim FilePreparations { get; } = new(4);
}
