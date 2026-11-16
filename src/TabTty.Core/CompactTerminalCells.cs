using System.Text;

namespace TabTty.Core;

// A snapshot owns compact copies, never pointers into the mutable native model.
// Text/search need no per-cell objects; rendering expands only the rows it reads.
internal sealed class CompactTerminalCells
{
    private readonly ResourceReservation memory;
    private readonly long expandedCharge;
    private NativeTerminalCore.CellView[]? compact;
    private string? arena;
    private TerminalCell[]? expanded;
    private int expandedCount;

    internal CompactTerminalCells(ReadOnlySpan<NativeTerminalCore.CellView> cells,
        ReadOnlySpan<char> text, ResourceQuota quota)
    {
        long stringBytes = 0;
        foreach (NativeTerminalCore.CellView cell in cells)
        {
            if (cell.Width < 0 || cell.Length == ushort.MaxValue) continue;
            int offset = checked((int)cell.Offset);
            if (offset > text.Length || cell.Length > text.Length - offset)
                throw new InvalidDataException("The native terminal exported invalid cell text bounds.");
            if (cell.Length != 0 && !(cell.Length == 1 && text[offset] < 128))
                stringBytes += 24L + cell.Length * 2L;
        }
        expandedCharge = cells.Length * 64L + stringBytes;
        // The quota uses the same conservative 64-byte cell-slot contract as the
        // managed reducer; the owned compact array itself occupies 20 bytes/cell.
        // Reserve the eventual expansion here as well, before publishing the snapshot.
        // Rendering only releases quota; it cannot reject a new allocation reservation.
        memory = quota.Reserve(80L + cells.Length * 64L + 24L + text.Length * 2L + expandedCharge);
        try
        {
            compact = cells.ToArray();
            arena = new string(text);
        }
        catch { memory.Dispose(); throw; }
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.CompactSnapshotRows);
    }

    internal ReadOnlyMemory<TerminalCell> GetCells()
    {
        TerminalCell[]? ready = Volatile.Read(ref expanded);
        if (ready is not null)
        {
            ReadOnlyMemory<TerminalCell> result = ready.AsMemory(0, expandedCount);
            GC.KeepAlive(this);
            return result;
        }
        lock (this)
        {
            if (expanded is not null) return expanded.AsMemory(0, expandedCount);
            NativeTerminalCore.CellView[] source = compact!;
            string text = arena!;
            var result = new TerminalCell[source.Length];
            int destination = 0, textOffset = 0;
            for (int column = 0; column < source.Length; column++)
            {
                NativeTerminalCore.CellView cell = source[column];
                if (cell.Width < 0) continue;
                string value;
                if (cell.Length == ushort.MaxValue) value = " ";
                else
                {
                    ReadOnlySpan<char> characters = text.AsSpan(checked((int)cell.Offset), cell.Length);
                    value = characters.Length == 1 && characters[0] < 128
                        ? TerminalText.Scalar(new Rune(characters[0])) : new string(characters);
                }
                var style = new TerminalStyle(NativeTerminalCore.Color(cell.Foreground),
                    NativeTerminalCore.Color(cell.Background), (TerminalStyleFlags)cell.Flags);
                result[destination++] = new TerminalCell(column, Math.Max(1, (int)cell.Width),
                    textOffset, value, style, cell.Width == 0);
                textOffset += value.Length;
            }
            expandedCount = destination;
            Volatile.Write(ref expanded, result);
            compact = null;
            arena = null;
            memory.Resize(expandedCharge);
            Interlocked.Increment(ref NativeTerminalCoreDiagnostics.MaterializedCellRows);
            Interlocked.Add(ref NativeTerminalCoreDiagnostics.MaterializedCells, destination);
            return result.AsMemory(0, destination);
        }
    }
}
