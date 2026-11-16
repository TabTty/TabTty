using System.Buffers;
using System.Text;

namespace TabTty.Core;

internal static class Unicode17
{
    private static readonly Range[][] Tables = Load();

    public static int ScalarWidth(Rune value, int ambiguousWidth) =>
        Get(0, value) switch { 2 => 2, 1 => ambiguousWidth, _ => 1 };

    public static int GraphemeWidth(ReadOnlySpan<Rune> cluster, int ambiguousWidth)
    {
        if (cluster.Length == 1 && cluster[0].IsAscii) return 1;
        int width = 1;
        bool emoji = false, textPresentation = false, modifierBase = false;
        for (int index = 0; index < cluster.Length; index++)
        {
            Rune value = cluster[index];
            if (Get(3, value) == 0 && (BreakOf(value) is not (Break.Extend or Break.Zwj)))
                width = Math.Max(width, ScalarWidth(value, ambiguousWidth));
            if (Get(5, value) != 0) emoji = true;
            if (value.Value == 0xFE0E) textPresentation = true;
            if (value.Value == 0xFE0F && index != 0 && Get(4, cluster[index - 1]) != 0) emoji = true;
            if (modifierBase && Get(8, value) != 0) emoji = true;
            if (Get(7, value) != 0) modifierBase = true;
            if (value.Value == 0x20E3 && cluster[0].Value is '#' or '*' or >= '0' and <= '9') emoji = true;
        }

        return emoji && !textPresentation ? 2 : width;
    }

    public static bool Joins(ReadOnlySpan<Rune> cluster, Rune next)
    {
        if (cluster[^1].IsAscii && next.IsAscii)
            return cluster[^1].Value == '\r' && next.Value == '\n';
        Break previous = BreakOf(cluster[^1]);
        Break following = BreakOf(next);
        if (previous == Break.Cr && following == Break.Lf) return true;
        if (IsBoundaryControl(previous) || IsBoundaryControl(following)) return false;
        if (previous == Break.L && following is Break.L or Break.V or Break.Lv or Break.Lvt) return true;
        if (previous is Break.Lv or Break.V && following is Break.V or Break.T) return true;
        if (previous is Break.Lvt or Break.T && following == Break.T) return true;
        if (following is Break.Extend or Break.Zwj or Break.SpacingMark || previous == Break.Prepend) return true;

        if (Get(2, next) == 1)
        {
            bool sawLinker = false;
            for (int index = cluster.Length - 1; index >= 0; index--)
            {
                byte property = Get(2, cluster[index]);
                if (property == 3) sawLinker = true;
                else if (property != 2)
                {
                    if (property == 1 && sawLinker) return true;
                    break;
                }
            }
        }

        if (previous == Break.Zwj && Get(6, next) != 0)
        {
            int index = cluster.Length - 2;
            while (index >= 0 && BreakOf(cluster[index]) == Break.Extend) index--;
            if (index >= 0 && Get(6, cluster[index]) != 0) return true;
        }

        if (previous == Break.RegionalIndicator && following == Break.RegionalIndicator)
        {
            int count = 0;
            for (int index = cluster.Length - 1; index >= 0 && BreakOf(cluster[index]) == Break.RegionalIndicator; index--)
                count++;
            return (count & 1) != 0;
        }

        return false;
    }

    public static int NextGraphemeLength(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return 0;
        if (text.Length > 1 && text[0] == '\r' && text[1] == '\n') return 2;
        if (text.Length == 1 || (text[0] < 0x80 && text[1] < 0x80)) return 1;
        Span<Rune> cluster = stackalloc Rune[256];
        int length = 0, count = 0;
        while (length < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[length..], out Rune rune, out int consumed) != OperationStatus.Done)
                throw new ArgumentException("Invalid Unicode text.");
            if (count != 0 && !Joins(cluster[..count], rune)) break;
            if (count == cluster.Length) throw new ArgumentException("A text cluster exceeds 256 Unicode scalars.");
            cluster[count++] = rune;
            length += consumed;
        }

        return length;
    }

    private static Break BreakOf(Rune value) => (Break)Get(1, value);
    private static bool IsBoundaryControl(Break value) => value is Break.Cr or Break.Lf or Break.Control;
    private enum Break : byte
    {
        Other, Cr, Lf, Control, Extend, Zwj, RegionalIndicator, Prepend, SpacingMark, L, V, T, Lv, Lvt,
    }

    private static byte Get(int table, Rune value)
    {
        Range[] ranges = Tables[table];
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            Range range = ranges[mid];
            if (value.Value < range.First) high = mid - 1;
            else if (value.Value > range.Last) low = mid + 1;
            else return range.Value;
        }

        return 0;
    }

    private static Range[][] Load()
    {
        using var reader = new BinaryReader(typeof(Unicode17).Assembly.GetManifestResourceStream("TabTty.Core.Unicode17.bin")
            ?? throw new InvalidOperationException("Unicode 17 data is missing."));
        var tables = new Range[9][];
        for (int index = 0; index < tables.Length; index++)
        {
            var ranges = new Range[reader.ReadInt32()];
            for (int row = 0; row < ranges.Length; row++)
                ranges[row] = new Range(reader.ReadInt32(), reader.ReadInt32(), reader.ReadByte());
            tables[index] = ranges;
        }

        return tables;
    }

    private readonly record struct Range(int First, int Last, byte Value);
}
