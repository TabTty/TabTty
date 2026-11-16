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
        "iso-8859-14", "iso-8859-15", "iso-8859-16", "cp437", "cp737", "cp775", "cp850",
        "cp852", "cp855", "cp857", "cp860", "cp861", "cp862", "cp863", "cp864", "cp865",
        "cp866", "cp869", "cp874", "cp1250", "cp1251", "cp1252", "cp1253", "cp1254", "cp1255",
        "cp1256", "cp1257", "cp1258", "koi8-r",
    });
}

internal enum JisMode { Ascii, Roman, Kanji, Unsupported }
internal readonly record struct JisState(JisMode Mode, bool Kana = false);
internal readonly record struct EncodedText(byte[] Bytes, JisState Next);

internal sealed class TerminalEncoding
{
    private const int PayloadLimit = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Lazy<JapaneseMapping> Japanese = new(() => new JapaneseMapping());
    private readonly Encoding? codePage;
    private readonly string? singleByteTable;

    static TerminalEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public TerminalEncoding(string identifier)
    {
        if (!TerminalEncodings.Identifiers.Contains(identifier, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported terminal encoding.", nameof(identifier));
        Id = identifier;
        singleByteTable = identifier switch
        {
            "iso-8859-10" => LegacyIsoTables.Iso10,
            "iso-8859-11" => LegacyIsoTables.Iso11,
            "iso-8859-14" => LegacyIsoTables.Iso14,
            "iso-8859-16" => LegacyIsoTables.Iso16,
            _ => null,
        };
        if (singleByteTable is not null || identifier is "jis" or "euc-jp") return;
        codePage = identifier switch
        {
            "utf-8" => StrictUtf8,
            "koi8-r" => StrictCodePage(20866),
            _ when identifier.StartsWith("cp", StringComparison.Ordinal) => StrictCodePage(int.Parse(identifier.AsSpan(2))),
            _ => StrictCodePage(28590 + int.Parse(identifier.AsSpan("iso-8859-".Length))),
        };
    }

    public string Id { get; }
    public bool IsUtf8 => Id == "utf-8";

    internal string DecodeFileName(ReadOnlySpan<byte> bytes)
    {
        if (IsUtf8) return StrictUtf8.GetString(bytes);
        var text = new StringBuilder(bytes.Length);
        var decoder = new LegacyReceiver(this, sevenBitKana: false);
        decoder.Decode(bytes, flush: false, 0, (rune, _) =>
        {
            if (rune == Rune.ReplacementChar) throw new DecoderFallbackException();
            text.Append(rune.ToString());
        }, () => false);
        if (decoder.HasIncompleteUnit) throw new DecoderFallbackException();
        return text.ToString();
    }

    public Rune DecodeUnit(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 1 && (bytes[0] < 0x20 || bytes[0] == 0x7F)) return new Rune(bytes[0]);
        if (singleByteTable is not null)
        {
            int value = bytes[0];
            char character = value < 0xA0 ? (char)value : singleByteTable[value - 0xA0];
            return character == '\uFFFF' ? Rune.ReplacementChar : new Rune(character);
        }

        if (Id == "euc-jp")
        {
            if (bytes[0] < 0x80 && bytes.Length == 1) return new Rune(bytes[0]);
            return bytes[0] switch
            {
                0x8E when bytes.Length == 2 => new Rune(0xFF61 + bytes[1] - 0xA1),
                >= 0xA1 and <= 0xFE when bytes.Length == 2 => DecodeJis(bytes[0] - 0x80, bytes[1] - 0x80),
                _ => Rune.ReplacementChar,
            };
        }

        return codePage is null ? Rune.ReplacementChar
            : Id == "cp932" && bytes.Length == 2 ? DecodeCp932Pair(codePage, bytes)
            : DecodeMapped(codePage, bytes);
    }

    public static Rune DecodeJis(int row, int cell)
    {
        int value = Japanese.Value.Cells[(row - 0x21) * 94 + cell - 0x21];
        return value == 0 ? Rune.ReplacementChar : new Rune(value);
    }

    public EncodedText Encode(ReadOnlySpan<char> text, JisState initial = default, bool finalize = true,
        bool oldKanjiIn = false, bool romanKanjiOut = false, bool sevenBitKana = false)
    {
        if (codePage is not null)
        {
            if (codePage.GetByteCount(text) > PayloadLimit) throw PayloadTooLarge();
            byte[] bytes = new byte[codePage.GetByteCount(text)];
            try
            {
                codePage.GetBytes(text, bytes);
                if (!IsUtf8)
                {
                    char[] decoded = codePage.GetChars(bytes);
                    try { if (!text.SequenceEqual(decoded)) throw Unmappable(); }
                    finally { Array.Clear(decoded); }
                }
            }
            catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
            return new EncodedText(bytes, initial);
        }

        // Validate UTF-16 before Rune enumeration so unmatched surrogates cannot become replacement text.
        StrictUtf8.GetByteCount(text);
        var output = new List<byte>();
        JisState state = initial;
        JisMode outMode = romanKanjiOut ? JisMode.Roman : JisMode.Ascii;
        try
        {
        foreach (Rune character in text.EnumerateRunes())
        {
            int value = character.Value;
            if (singleByteTable is not null)
            {
                int index = value;
                if (value >= 0xA0)
                {
                    int slot = value < 0xFFFF ? singleByteTable.IndexOf((char)value) : -1;
                    if (slot < 0) throw Unmappable();
                    index = slot + 0xA0;
                }
                output.Add((byte)index);
            }
            else if (Id == "euc-jp")
            {
                if (value < 0x80) output.Add((byte)value);
                else if (value is >= 0xFF61 and <= 0xFF9F) output.AddRange([0x8E, (byte)(value - 0xFF61 + 0xA1)]);
                else if (Japanese.Value.Reverse.TryGetValue(value, out int position))
                    output.AddRange([(byte)((position >> 8) + 0x80), (byte)((position & 255) + 0x80)]);
                else throw Unmappable();
            }
            else
            {
                if (value < 0x20 || value == 0x7F)
                {
                    output.Add((byte)value);
                }
                else if (value is >= 0xFF61 and <= 0xFF9F)
                {
                    if (state.Mode == JisMode.Kanji) SwitchTo(outMode);
                    if (sevenBitKana && !state.Kana)
                    {
                        output.Add(0x0E);
                        state = state with { Kana = true };
                    }
                    output.Add((byte)(value - 0xFF61 + (sevenBitKana ? 0x21 : 0xA1)));
                }
                else if (value < 0x80 || value is 0xA5 or 0x203E)
                {
                    EndKana();
                    if (state.Mode == JisMode.Kanji) SwitchTo(outMode);
                    if (value is 0xA5 or 0x203E) SwitchTo(JisMode.Roman);
                    else if (value is 0x5C or 0x7E) SwitchTo(JisMode.Ascii);
                    output.Add((byte)(value switch { 0xA5 => 0x5C, 0x203E => 0x7E, _ => value }));
                }
                else if (Japanese.Value.Reverse.TryGetValue(value, out int position))
                {
                    EndKana();
                    SwitchTo(JisMode.Kanji);
                    output.AddRange([(byte)(position >> 8), (byte)position]);
                }
                else throw Unmappable();
            }

            if (output.Count > PayloadLimit) throw PayloadTooLarge();
        }

        if (finalize)
        {
            EndKana();
            if (state.Mode == JisMode.Kanji) SwitchTo(outMode);
            if (output.Count > PayloadLimit) throw PayloadTooLarge();
        }

        return new EncodedText(output.ToArray(), state);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output)); }

        void EndKana()
        {
            if (!state.Kana) return;
            output.Add(0x0F);
            state = state with { Kana = false };
        }

        void SwitchTo(JisMode mode)
        {
            if (state.Mode == mode) return;
            output.AddRange(mode switch
            {
                JisMode.Kanji => [0x1B, 0x24, oldKanjiIn ? (byte)0x40 : (byte)0x42],
                JisMode.Roman => [0x1B, 0x28, 0x4A],
                _ => [0x1B, 0x28, 0x42],
            });
            state = new JisState(mode);
        }
    }

    private static EncoderFallbackException Unmappable() => new("The selected encoding cannot represent this text exactly.");
    private static ArgumentException PayloadTooLarge() => new("Encoded terminal input exceeds 1 MiB.");
    private static Encoding StrictCodePage(int number) =>
        Encoding.GetEncoding(number, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static Rune DecodeMapped(Encoding mapping, ReadOnlySpan<byte> bytes)
    {
        Span<char> characters = stackalloc char[4];
        try
        {
            int length = mapping.GetChars(bytes, characters);
            return Rune.DecodeFromUtf16(characters[..length], out Rune character, out int used) == OperationStatus.Done
                && used == length ? character : Rune.ReplacementChar;
        }
        catch (DecoderFallbackException) { return Rune.ReplacementChar; }
    }

    private static Rune DecodeCp932Pair(Encoding mapping, ReadOnlySpan<byte> pair)
    {
        string? duplicates = pair[0] switch
        {
            0x87 => Cp932Duplicates.Lead87,
            0xED => Cp932Duplicates.LeadED,
            0xEE => Cp932Duplicates.LeadEE,
            0xFA => Cp932Duplicates.LeadFA,
            _ => null,
        };
        if (duplicates is not null)
        {
            int index = pair[1] < 0x7F ? pair[1] - 0x40 : pair[1] - 0x41;
            char character = duplicates[index];
            if (character != '\uFFFF') return new Rune(character);
        }

        return DecodeMapped(mapping, pair);
    }

    private sealed class JapaneseMapping
    {
        public int[] Cells { get; } = new int[94 * 94];
        public Dictionary<int, int> Reverse { get; } = new();

        public JapaneseMapping()
        {
            Encoding cp932 = StrictCodePage(932);
            Span<byte> pair = stackalloc byte[2];
            for (int row = 0x21; row <= 0x7E; row++)
            {
                pair[0] = (byte)(((row + 1) >> 1) + (row < 0x5F ? 0x70 : 0xB0));
                for (int cell = 0x21; cell <= 0x7E; cell++)
                {
                    pair[1] = (byte)(cell + ((row & 1) == 0 ? 0x7E : cell < 0x60 ? 0x1F : 0x20));
                    Rune character = DecodeCp932Pair(cp932, pair);
                    if (character == Rune.ReplacementChar) continue;
                    Cells[(row - 0x21) * 94 + cell - 0x21] = character.Value;
                    Reverse.TryAdd(character.Value, (row << 8) | cell);
                }
            }
        }
    }
}
