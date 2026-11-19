using System.Collections;

namespace TabTty.Core;

internal static class VaultPublicationCapacity
{
    internal const int ResultLimit = 64;
    internal const long MetadataLimit = 64L * 1024 * 1024;
    internal static readonly ResourceQuota Results = new(ResultLimit,
        message: "Too many vault publication results are owned.", collectOnPressure: false);
    internal static readonly ResourceQuota Metadata = new(MetadataLimit,
        message: "The vault metadata budget was exhausted.");
}

// A management view can retain an older projection after the vault has adopted
// another generation. The charge follows the shared projection, not the root.
internal sealed class VaultMetadata(IReadOnlyList<VaultEntryInfo> entries, ResourceReservation charge)
    : IReadOnlyList<VaultEntryInfo>
{
    public int Count => entries.Count;
    public VaultEntryInfo this[int index] => entries[index];

    public IEnumerator<VaultEntryInfo> GetEnumerator()
    {
        foreach (VaultEntryInfo entry in entries) yield return entry;
        GC.KeepAlive(charge);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
