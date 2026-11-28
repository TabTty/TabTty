namespace TabTty.Core;

internal sealed record TransferSnapshot(TransferAttributes Attributes, byte[]? Fingerprint,
    string[]? Members = null, IReadOnlyDictionary<string, TransferSnapshot>? Children = null)
{
    public bool HasUnsupportedEntries => Attributes.Kind is not (TransferKind.File or TransferKind.Directory) ||
        Children is not null && Children.Values.Any(child => child.HasUnsupportedEntries);

    public static string NameOf(TransferPath path) => path.Local is { } local
        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(local))
        : Convert.ToHexString(path.Remote.AsSpan(Array.LastIndexOf(path.Remote!, (byte)'/') + 1));

    public static async Task<TransferSnapshot> CaptureAsync(IFileOperationIo files, TransferPath path,
        TransferAttributes attributes, CancellationToken token)
    {
        if (attributes.Kind is not (TransferKind.File or TransferKind.Directory) ||
            path.Local is not null && attributes.LocalIdentity is null)
            throw new SftpException(SftpFailure.Unsupported);
        byte[]? fingerprint = attributes.Kind == TransferKind.File ? await files.FingerprintAsync(path, attributes, token) : null;
        return new(attributes, fingerprint);
    }

    public static async Task<TransferSnapshot> CaptureTreeAsync(IFileOperationIo files, TransferPath path,
        CancellationToken token, TransferSnapshotBudget budget, int depth = 0, bool allowUnsupportedChildren = false)
    {
        budget.Add(path, depth);
        TransferAttributes attributes = await files.StatAsync(path, token) ?? throw new SftpException(SftpFailure.Missing);
        if (allowUnsupportedChildren && depth != 0 && attributes.Kind is not (TransferKind.File or TransferKind.Directory))
            return new(attributes, null);
        TransferSnapshot snapshot = await CaptureAsync(files, path, attributes, token);
        if (attributes.Kind != TransferKind.Directory) return snapshot;
        var children = new Dictionary<string, TransferSnapshot>(StringComparer.Ordinal);
        foreach (TransferItem child in await files.ListAsync(path, token))
            children.Add(NameOf(child.Path), await CaptureTreeAsync(files, child.Path, token, budget, depth + 1, allowUnsupportedChildren));
        await files.RecheckAsync(path, attributes, null, token);
        return snapshot with { Members = children.Keys.ToArray(), Children = children };
    }

    public async Task VerifyAsync(IFileOperationIo files, TransferPath path, CancellationToken token)
    {
        TransferAttributes? current = await files.StatAsync(path, token);
        if (!TransferAttributes.SameOverwriteTarget(Attributes, current)) throw new SftpException(SftpFailure.SourceChanged);
        if (Fingerprint is not null)
        {
            byte[] actualFingerprint = await files.FingerprintAsync(path, current!, token);
            if (!Fingerprint.AsSpan().SequenceEqual(actualFingerprint)) throw new SftpException(SftpFailure.SourceChanged);
        }
        if (Members is null && Children is null) return;
        var actual = (await files.ListAsync(path, token)).ToDictionary(item => NameOf(item.Path), StringComparer.Ordinal);
        if (Members is not null && (Members.Length != actual.Count || Members.Any(name => !actual.ContainsKey(name))))
            throw new SftpException(SftpFailure.SourceChanged);
        if (Children is null) return;
        foreach (var (name, child) in Children)
        {
            if (!actual.TryGetValue(name, out TransferItem? entry)) throw new SftpException(SftpFailure.SourceChanged);
            await child.VerifyAsync(files, entry.Path, token);
        }
    }
}

internal sealed class TransferSnapshotBudget
{
    private long bytes;
    private int entries;

    public void Add(TransferPath path, int depth = 0)
    {
        bytes += (path.Local?.Length * 2L ?? path.Remote!.Length * 2L) + 256;
        if (++entries > SftpClient.MaxEntries || depth > 128 || bytes > 8 * 1024 * 1024)
            throw new SftpException(SftpFailure.Capacity);
    }
}

internal sealed record TransferChange(TransferPath Source, TransferPath Target, TransferSnapshot Before,
    TransferSnapshot After, TransferChange[] Children, bool Renamed = false)
{
    public TransferChange Reverse() => new(Target, Source, After, Before, Children.Select(child => child.Reverse()).ToArray(), Renamed);
}

internal sealed class TransferRun(bool describesChanges)
{
    public List<TransferChange> Changes { get; } = [];
    public bool Modified { get; set; }
    public bool Failed { get; set; }
    public bool Uncertain { get; set; }
    public bool DescribesChanges { get; set; } = describesChanges;
    public bool HistoryUnavailable { get; set; }
    public bool AllowReplacement { get; init; } = true;
    public bool StopOnFailure { get; init; }
    public bool Move { get; set; }
    public HashSet<TransferPath> UnknownSources { get; } = [];
    public TransferSnapshotBudget Budget { get; } = new();
}

internal sealed record ClipboardEntry(TransferItem Item, TransferSnapshot? Snapshot, string? Error = null);
