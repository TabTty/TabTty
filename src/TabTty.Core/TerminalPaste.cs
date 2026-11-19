using System.Text;

namespace TabTty.Core;

public sealed class TerminalPaste : IDisposable
{
    internal static UTF8Encoding Encoding { get; } = new(false, true);
    internal const long DefaultMaxSourceBytes = 512L * 1024 * 1024;
    private readonly ResourceReservation sourceMemory;
    private int owners = 1;
    private int disposed;
    public string Text { get; }
    public int CharacterCount { get; }
    public int LineCount { get; }
    public bool HasNewline => LineCount > 1;
    public bool EndsWithNewline => Text.EndsWith('\r') || Text.EndsWith('\n');
    public long SourceBytes => (long)Text.Length * sizeof(char);
    internal bool RequiresConfirmation => HasNewline || SourceBytes > 1024;

    private TerminalPaste(string text, ResourceReservation sourceMemory, CancellationToken token)
    {
        Text = text;
        this.sourceMemory = sourceMemory;
        (CharacterCount, LineCount) = token.CanBeCanceled ? AnalyzeCancellable(text, token) :
            NativeTerminalCore.AnalyzePaste(text);
    }

    public static TerminalPaste Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ownership = ReserveSource(text.Length);
        return CreateOwned(text, ownership);
    }

    internal static ResourceReservation ReserveSource(int units) =>
        ApplicationResources.TerminalState.Reserve(checked((long)units * sizeof(char) + 128));

    internal static TerminalPaste CreateOwned(string text, ResourceReservation ownership, CancellationToken token = default)
    {
        try { return new TerminalPaste(text, ownership, token); }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    internal IDisposable HoldSource()
    {
        lock (sourceMemory)
        {
            ObjectDisposedException.ThrowIf(owners == 0, this);
            owners++;
            return new SourceInterest(this);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) ReleaseSource();
    }

    private void ReleaseSource()
    {
        lock (sourceMemory)
            if (--owners == 0) sourceMemory.Dispose();
    }

    private sealed class SourceInterest(TerminalPaste paste) : IDisposable
    {
        private TerminalPaste? source = paste;
        public void Dispose() => Interlocked.Exchange(ref source, null)?.ReleaseSource();
    }

    private static (int Characters, int Lines) AnalyzeCancellable(string text, CancellationToken token)
    {
        int offset = 0, characters = 0, lines = 1;
        uint context = 0;
        while (offset < text.Length)
        {
            token.ThrowIfCancellationRequested();
            var remainder = text.AsSpan(offset);
            var (length, completed, _) = NativeTerminalCore.NextPasteChunk(remainder, 0, 0, ref context);
            var (_, pageLines) = NativeTerminalCore.AnalyzePaste(remainder[..length]);
            lines += pageLines - 1;
            characters += completed;
            offset += length;
        }
        token.ThrowIfCancellationRequested();
        return (characters, lines);
    }

    internal readonly record struct Chunk(int Offset, int Length, int CompletedGraphemes, int Delay);

    internal IEnumerable<Chunk> GetChunks(int characterDelay, int lineDelay)
    {
        uint context = 0;
        int position = 0;
        while (position < Text.Length)
        {
            var (length, completed, delay) = NativeTerminalCore.NextPasteChunk(Text.AsSpan(position),
                characterDelay, lineDelay, ref context);
            yield return new Chunk(position, length, completed, delay);
            position += length;
        }
        GC.KeepAlive(sourceMemory);
    }
}
