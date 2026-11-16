using System.Text;

namespace TabTty.Core;

public static class TerminalText
{
    private static readonly string[] Ascii = Enumerable.Range(0, 128).Select(value => ((char)value).ToString()).ToArray();
    private static readonly ResourceReservation SharedAsciiMemory = ApplicationResources.TerminalState.Reserve(128 * 40);

    internal static string Scalar(Rune value) => value.IsAscii ? Ascii[value.Value] : value.ToString();

    internal static bool IsShared(string value) => value.Length == 1 && value[0] < 128 &&
        ReferenceEquals(value, Ascii[value[0]]);

    internal static string Cluster(ReadOnlySpan<Rune> runes)
    {
        if (runes.Length == 1) return Scalar(runes[0]);
        Span<char> characters = stackalloc char[512];
        int length = 0;
        foreach (Rune rune in runes) length += rune.EncodeToUtf16(characters[length..]);
        return new string(characters[..length]);
    }

    public static int NextCharacterLength(ReadOnlySpan<char> text) =>
        text.IsEmpty ? 0 : Unicode17.NextGraphemeLength(text);
}
