using System.Runtime.InteropServices;
using System.Text;

namespace TabTty.Core;

internal sealed class RecordedTextPosition(int ambiguousWidth)
{
    private readonly List<Rune> cluster = [];
    private int column;
    private bool known = true;
    public int? Column => known ? column : null;

    public void Reset() { column = 0; known = true; cluster.Clear(); }

    internal void Import(int serializedColumn, bool isKnown, ReadOnlySpan<uint> scalars)
    {
        column = serializedColumn;
        known = isKnown;
        cluster.Clear();
        foreach (uint scalar in scalars) cluster.Add(new Rune(checked((int)scalar)));
    }

    public void Append(string text)
    {
        foreach (Rune character in text.EnumerateRunes())
        {
            if (character.Value is '\r' or '\n' or '\t') { Reset(); continue; }
            if (!known) continue;
            if (cluster.Count != 0 && !Unicode17.Joins(CollectionsMarshal.AsSpan(cluster), character)) cluster.Clear();
            if (cluster.Count == 256) { known = false; cluster.Clear(); continue; }
            int before = cluster.Count == 0 ? 0 : Unicode17.GraphemeWidth(CollectionsMarshal.AsSpan(cluster), ambiguousWidth);
            cluster.Add(character);
            int after = Unicode17.GraphemeWidth(CollectionsMarshal.AsSpan(cluster), ambiguousWidth);
            column = (column + after - before) & 7;
        }
    }
}
