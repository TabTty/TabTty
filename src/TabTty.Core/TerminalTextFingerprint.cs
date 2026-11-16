using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TabTty.Core;

// A composable content fingerprint lets history ranges survive physical reflow.
// It is an internal change detector, not a permission token.
internal readonly record struct TerminalTextFingerprint(ulong First, ulong Second, ulong FirstPower, ulong SecondPower, long Length)
{
    private static readonly ulong firstBase = NewBase();
    private static readonly ulong secondBase = NewBase();
    internal static readonly TerminalTextFingerprint Empty = new(0, 0, 1, 1, 0);
    internal static readonly TerminalTextFingerprint LineBreak = FromText("\n");

    private static ulong NewBase() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) | 0x100000001UL;

    internal static TerminalTextFingerprint FromText(ReadOnlySpan<char> text)
    {
        ulong a = 0, b = 0, ap = 1, bp = 1;
        foreach (char character in text)
        {
            a = unchecked(a * firstBase + character + 1UL);
            b = unchecked(b * secondBase + character + 1UL);
            ap = unchecked(ap * firstBase);
            bp = unchecked(bp * secondBase);
        }
        return new(a, b, ap, bp, text.Length);
    }

    internal TerminalTextFingerprint Append(TerminalTextFingerprint suffix) => new(
        unchecked(First * suffix.FirstPower + suffix.First),
        unchecked(Second * suffix.SecondPower + suffix.Second),
        unchecked(FirstPower * suffix.FirstPower), unchecked(SecondPower * suffix.SecondPower), Length + suffix.Length);
}
