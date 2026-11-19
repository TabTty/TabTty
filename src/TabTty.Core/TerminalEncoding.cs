using System.Buffers;
using System.Text;

namespace TabTty.Core;

public static class TerminalEncodings
{
    public static IReadOnlyList<string> Identifiers { get; } = Array.AsReadOnly(new[]
    {
        "utf-8", "cp932", "euc-jp", "jis", "cp949", "cp936", "cp950",
        "iso-8859-1", "iso-8859-2", "iso-8859-3", "iso-8859-4", "iso-8859-5", "iso-8859-6",
        "iso-8859-7", "iso-8859-8", "iso-8859-9", "iso-8859-10", "iso-8859-11", "iso-8859-13",
        "iso-8859-14", "iso-8859-15", "cp437", "cp737", "cp775", "cp850",
        "cp852", "cp855", "cp857", "cp860", "cp861", "cp862", "cp863", "cp864", "cp865",
        "cp866", "cp869", "cp874", "cp1250", "cp1251", "cp1252", "cp1253", "cp1254", "cp1255",
        "cp1256", "cp1257", "cp1258", "koi8-r",
    });
}

internal sealed class TerminalEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public TerminalEncoding(string identifier)
    {
        if (!TerminalEncodings.Identifiers.Contains(identifier, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported terminal encoding.", nameof(identifier));
        Id = identifier;
        NativePage = identifier switch
        {
            "utf-8" => 0,
            "euc-jp" => 51932,
            "jis" => 50220,
            "koi8-r" => 20866,
            _ when identifier.StartsWith("cp", StringComparison.Ordinal) => uint.Parse(identifier.AsSpan(2)),
            _ => 28590 + uint.Parse(identifier.AsSpan("iso-8859-".Length)),
        };
    }

    public string Id { get; }
    public bool IsUtf8 => NativePage == 0;
    internal uint NativePage { get; }

    internal string DecodeFileName(ReadOnlySpan<byte> bytes)
    {
        if (IsUtf8) return StrictUtf8.GetString(bytes);
        var text = new StringBuilder(bytes.Length);
        using var decoder = new LegacyReceiver(this, sevenBitKana: false);
        decoder.Decode(bytes, flush: false, 0, (rune, _) =>
        {
            if (rune == Rune.ReplacementChar) throw new DecoderFallbackException();
            text.Append(rune.ToString());
        }, () => false);
        if (decoder.HasIncompleteUnit) throw new DecoderFallbackException();
        return text.ToString();
    }

    internal readonly record struct PasteBoundaries(int[] Bytes, int[] Source)
    {
        internal int SourcePrefix(int encoded) =>
            encoded == 0 ? 0 : Source[Array.BinarySearch(Bytes, encoded)];
    }

    internal PasteBoundaries MapPasteBoundaries(string sourceText, int start, int length, byte[] encoded, bool sevenBitKana)
    {
        if (NativePage == 50220) return MapJisBoundaries(sourceText, start, length, encoded, sevenBitKana);
        var text = sourceText.AsSpan(start, length);
        var stops = new int[text.Length];
        var sources = new int[text.Length];
        int count = 0, offset = 0, source = 0;
        while (source < text.Length)
        {
            int units = char.IsHighSurrogate(text[source]) ||
                (text[source] == '\r' && source + 1 < text.Length && text[source + 1] == '\n') ? 2 : 1;
            var unit = text.Slice(source, units);
            offset += IsUtf8 ? StrictUtf8.GetByteCount(unit) :
                NativeTerminalCore.CheckLegacyInput(NativePage, unit, sevenBitKana);
            source += units;
            stops[count] = offset;
            sources[count++] = source;
        }
        if (offset != encoded.Length)
            throw new InvalidDataException("Encoded paste boundaries did not match its prepared bytes.");
        Array.Resize(ref stops, count);
        Array.Resize(ref sources, count);
        return new(stops, sources);
    }

    private PasteBoundaries MapJisBoundaries(string text, int start, int length, byte[] encoded, bool sevenBitKana)
    {
        using var decoder = new LegacyReceiver(this, sevenBitKana);
        int source = 0, position = 0;
        var bytes = new List<int>(length);
        var units = new List<int>(length);
        Action<Rune, int> emit = (rune, _) =>
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(start + source, length - source), out Rune expected, out int used)
                != OperationStatus.Done || rune != expected)
                throw new InvalidDataException("The encoded paste did not preserve the planned source units.");
            source += used;
            if (rune.Value == '\r' && source < length && text[start + source] == '\n') return;
            bytes.Add(position);
            units.Add(source);
        };
        for (int i = 0; i < encoded.Length; i++)
        {
            position = i + 1;
            decoder.Decode(encoded.AsSpan(i, 1), flush: false, 0, emit, static () => false);
        }
        if (source != length || bytes.Count == 0 || decoder.HasIncompleteUnit)
            throw new InvalidDataException("The encoded paste did not finish at a complete character.");
        // The last source scalar and the converter's final reset complete together.
        bytes[^1] = encoded.Length;
        return new([.. bytes], [.. units]);
    }

    internal byte[] PasteClosingBytes(ReadOnlySpan<char> source, ReadOnlySpan<byte> submitted, bool sevenBitKana)
    {
        if (NativePage != 50220) return [];
        byte[] closed = Encode(source, sevenBitKana);
        if (!closed.AsSpan().StartsWith(submitted))
            throw new InvalidDataException("The ISO-2022-JP encoder could not close the submitted prefix.");
        return closed[submitted.Length..];
    }

    internal int ConfirmedEchoLength(string text, ReadOnlySpan<byte> encoded, int confirmed, bool sevenBitKana)
    {
        if (confirmed <= 0) return 0;
        if (confirmed == encoded.Length) return StrictUtf8.GetByteCount(text);
        int source = 0;
        if (NativePage == 50220)
        {
            using var decoder = new LegacyReceiver(this, sevenBitKana);
            int last = 0;
            decoder.Decode(encoded[..confirmed], flush: false, 0, (rune, _) =>
            {
                if (Rune.DecodeFromUtf16(text.AsSpan(source), out Rune expected, out int units) != OperationStatus.Done ||
                    rune != expected)
                    throw new InvalidDataException("The submitted prefix did not preserve the prepared text.");
                last = source;
                source += units;
            }, static () => false);
            if (source == text.Length) source = last; // The final unit includes the encoder reset.
        }
        else
        {
            while (source < text.Length && confirmed > 0)
            {
                int units = char.IsHighSurrogate(text[source]) ? 2 : 1;
                var unit = text.AsSpan(source, units);
                int size = IsUtf8 ? StrictUtf8.GetByteCount(unit) :
                    NativeTerminalCore.CheckLegacyInput(NativePage, unit, sevenBitKana);
                if (size > confirmed) break;
                confirmed -= size;
                source += units;
            }
        }
        return StrictUtf8.GetByteCount(text.AsSpan(0, source));
    }

    public byte[] Encode(ReadOnlySpan<char> text, bool sevenBitKana = false)
    {
        if (IsUtf8)
            return NativeTerminalCore.PrepareUtf8Input(text, NativeTerminalCore.CheckUtf8Input(text));
        int length = NativeTerminalCore.CheckLegacyInput(NativePage, text, sevenBitKana);
        return NativeTerminalCore.PrepareLegacyInput(NativePage, text, sevenBitKana, length);
    }
}
