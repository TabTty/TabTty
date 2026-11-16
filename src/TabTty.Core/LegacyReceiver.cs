using System.Text;

namespace TabTty.Core;

internal sealed class LegacyReceiver(TerminalEncoding encoding, bool sevenBitKana) : IDisposable
{
    private NativeTerminalCore.LegacyDecoder? decoder;
    internal bool HasIncompleteUnit { get; private set; }

    public void Decode(ReadOnlySpan<byte> bytes, bool flush, int interval,
        Action<Rune, int> emit, Func<bool> inVtSequence)
    {
        if (bytes.IsEmpty && (!flush || decoder is null)) return;
        decoder ??= new NativeTerminalCore.LegacyDecoder(encoding.NativePage, sevenBitKana);
        foreach (byte value in bytes)
            HasIncompleteUnit = decoder.Step(value, inVtSequence(), interval, emit);
        if (flush) HasIncompleteUnit = decoder.Step(-1, false, interval, emit);
    }

    public void Dispose() => decoder?.Dispose();
}
