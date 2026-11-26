using System.Text;

namespace TabTty.Core;

// A snapshot owns compact copies, never pointers into the mutable native model.
// Text/search need no per-cell objects; rendering expands only the rows it reads.
internal sealed class CompactTerminalCells
{
    private readonly ResourceReservation memory;
    private readonly long expandedCharge;
    private Cell[]? compact;
    private string? arena;
    private TerminalTextPosition[]? positions;
    private readonly int textLength;
    private TerminalCell[]? expanded;
    private int expandedCount;
    private readonly record struct Cell(uint Offset, ushort Length, short Width, uint Foreground, uint Background, uint Flags);

    private CompactTerminalCells(string displayText, ResourceReservation admittedCopies)
    {
        // For default-style one-column ASCII, the owned row text also encodes the cells.
        arena = displayText;
        expandedCharge = displayText.Length * 64L;
        memory = admittedCopies.Separate(80L + expandedCharge);
        Interlocked.Increment(ref NativeTerminalCoreDiagnostics.CompactSnapshotRows);
    }

    internal static CompactTerminalCells? TryCreatePlainAscii(ReadOnlySpan<NativeTerminalCore.CellView> cells,
        ReadOnlySpan<char> text, string displayText, ResourceReservation admittedCopies)
    {
        if (cells.IsEmpty || displayText.Length != cells.Length || !text.StartsWith(displayText.AsSpan()) ||
            displayText.AsSpan().ContainsAnyExceptInRange('\0', '\u007F'))
            return null;
        for (int column = 0; column < cells.Length; column++)
        {
            ref readonly var cell = ref cells[column];
            if (cell.Offset != (uint)column || cell.Length != 1 || cell.Width != 1 ||
                (cell.Foreground | cell.Background | cell.Flags) != 0)
                return null;
        }
        return new(displayText, admittedCopies);
    }

    internal CompactTerminalCells(ReadOnlySpan<NativeTerminalCore.CellView> cells,
        ReadOnlySpan<char> text, int textLength, TerminalTextPosition[]? positions, ResourceReservation admittedCopies)
    {
        this.positions = positions;
        this.textLength = textLength;
        var packed = new Cell[cells.Length];
        long stringBytes = 0;
        for (int index = 0; index < cells.Length; index++)
        {
            var cell = cells[index];
            packed[index] = new(cell.Offset, cell.Length, cell.Width, cell.Foreground, cell.Background, cell.Flags);
            if (cell.Width < 0 || cell.Length == ushort.MaxValue) continue;
            int offset = checked((int)cell.Offset);
            if (offset > text.Length || cell.Length > text.Length - offset)
                throw new InvalidDataException("The native terminal exported invalid cell text bounds.");
            if (cell.Length != 0 && !(cell.Length == 1 && text[offset] < 128))
                stringBytes += 24L + cell.Length * 2L;
        }
        expandedCharge = cells.Length * 64L + stringBytes;
        // The compact UI cells omit the native history revision. The quota
        // retains a conservative allowance for owned and expanded cell storage.
        // Reserve the eventual expansion here as well, before publishing the snapshot.
        // Rendering only releases quota; it cannot reject a new allocation reservation.
        memory = admittedCopies.Separate(80L + cells.Length * 64L + 24L + text.Length * 2L + expandedCharge);
        try
        {
            compact = packed;
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
            Cell[]? source = compact;
            string text = arena!;
            var result = new TerminalCell[source?.Length ?? text.Length];
            int destination = 0;
            if (source is null)
            {
                for (int column = 0; column < text.Length; column++)
                    result[column] = new TerminalCell(column, 1, column, TerminalText.Scalar(new Rune(text[column])), default);
                destination = text.Length;
            }
            else
            {
                int position = 0;
                for (int column = 0; column < source.Length; column++)
                {
                    Cell cell = source[column];
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
                    int textOffset = Math.Min(column, textLength);
                    if (positions is { } spans)
                    {
                        while (position < spans.Length && (spans[position].Column < column || spans[position].Width == 0)) position++;
                        textOffset = position < spans.Length && spans[position].Column == column ? spans[position].Start : textLength;
                    }
                    result[destination++] = new TerminalCell(column, Math.Max(1, (int)cell.Width), textOffset, value, style, cell.Width == 0);
                }
            }
            expandedCount = destination;
            Volatile.Write(ref expanded, result);
            compact = null;
            arena = null;
            positions = null;
            memory.Resize(expandedCharge);
            Interlocked.Increment(ref NativeTerminalCoreDiagnostics.MaterializedCellRows);
            Interlocked.Add(ref NativeTerminalCoreDiagnostics.MaterializedCells, destination);
            return result.AsMemory(0, destination);
        }
    }
}
