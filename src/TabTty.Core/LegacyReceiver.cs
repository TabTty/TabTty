using System.Text;

namespace TabTty.Core;

internal sealed class LegacyReceiver(TerminalEncoding encoding, bool sevenBitKana)
{
    private readonly byte[] unit = new byte[3];
    private readonly byte[] designation = new byte[4];
    private int count;
    private int unitInterval;
    private int designationLength;
    private int designationInterval;
    private JisMode mode;
    private bool kana;
    internal bool HasIncompleteUnit => count != 0 || designationLength != 0;

    public void Decode(ReadOnlySpan<byte> bytes, bool flush, int interval,
        Action<Rune, int> emit, Func<bool> inVtSequence)
    {
        foreach (byte value in bytes)
        {
            if (count != 0)
            {
                if (AcceptsContinuation(unit[0], value))
                {
                    unit[count++] = value;
                    if (encoding.Id == "euc-jp" && unit[0] == 0x8F && count == 2) continue;
                    emit(encoding.Id == "jis"
                        ? mode == JisMode.Kanji ? TerminalEncoding.DecodeJis(unit[0], unit[1]) : Rune.ReplacementChar
                        : encoding.DecodeUnit(unit.AsSpan(0, count)), unitInterval);
                    count = 0;
                    continue;
                }

                count = 0;
                emit(Rune.ReplacementChar, unitInterval);
            }

            if (encoding.Id == "jis" && ConsumeDesignation(value, interval, emit, inVtSequence)) continue;
            if (inVtSequence())
            {
                emit(new Rune(value), interval);
                continue;
            }

            if (sevenBitKana && (encoding.Id is "cp932" or "jis") && value is 0x0E or 0x0F)
            {
                kana = value == 0x0E;
                continue;
            }

            if (kana && value is >= 0x21 and <= 0x5F)
            {
                emit(new Rune(0xFF61 + value - 0x21), interval);
            }
            else if (StartsUnit(value))
            {
                unit[0] = value;
                count = 1;
                unitInterval = interval;
            }
            else if (encoding.Id == "jis")
            {
                emit(value switch
                {
                    0x5C when mode == JisMode.Roman => new Rune(0xA5),
                    0x7E when mode == JisMode.Roman => new Rune(0x203E),
                    < 0x80 => new Rune(value),
                    >= 0xA1 and <= 0xDF => new Rune(0xFF61 + value - 0xA1),
                    _ => Rune.ReplacementChar,
                }, interval);
            }
            else
            {
                unit[0] = value;
                emit(encoding.DecodeUnit(unit.AsSpan(0, 1)), interval);
            }
        }

        if (flush)
        {
            if (count != 0) emit(Rune.ReplacementChar, unitInterval);
            count = 0;
            designationLength = 0;
        }
    }

    private bool StartsUnit(byte value) => encoding.Id switch
    {
        "cp932" => value is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC,
        "cp936" or "cp949" or "cp950" => value is >= 0x81 and <= 0xFE,
        "euc-jp" => value is 0x8E or 0x8F or >= 0xA1 and <= 0xFE,
        "jis" => mode is JisMode.Kanji or JisMode.Unsupported && value is >= 0x21 and <= 0x7E,
        _ => false,
    };

    private bool AcceptsContinuation(byte lead, byte value) => encoding.Id switch
    {
        "cp932" => value is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFC,
        "cp936" => value is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFE,
        "cp949" => value is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0x81 and <= 0xFE,
        "cp950" => value is >= 0x40 and <= 0x7E or >= 0xA1 and <= 0xFE,
        "euc-jp" when lead == 0x8E => value is >= 0xA1 and <= 0xDF,
        "euc-jp" => value is >= 0xA1 and <= 0xFE,
        "jis" => value is >= 0x21 and <= 0x7E,
        _ => false,
    };

    private bool ConsumeDesignation(byte value, int interval, Action<Rune, int> emit, Func<bool> inVtSequence)
    {
        if (designationLength == 0)
        {
            if (value != 0x1B || inVtSequence()) return false;
            designation[designationLength++] = value;
            designationInterval = interval;
            return true;
        }

        if (value < 0x20)
        {
            if (value is 0x18 or 0x1A) designationLength = 0;
            else if (value == 0x1B)
            {
                designationLength = 1;
                designationInterval = interval;
                return true;
            }

            emit(new Rune(value), interval);
            return true;
        }

        designation[designationLength++] = value;
        if (designationLength == 2 && value is (byte)'(' or (byte)'$') return true;
        if (designationLength == 3 && designation[1] == '$' && value == '(') return true;

        JisMode? next = (designationLength, designation[1], value) switch
        {
            (3, (byte)'(', (byte)'B') => JisMode.Ascii,
            (3, (byte)'(', (byte)'J') => JisMode.Roman,
            (3, (byte)'$', (byte)'@' or (byte)'B') => JisMode.Kanji,
            (4, (byte)'$', (byte)'B') => JisMode.Kanji,
            (4, (byte)'$', (byte)'D' or (byte)'O' or (byte)'P' or (byte)'Q') => JisMode.Unsupported,
            _ => null,
        };
        if (next is { } changed)
        {
            mode = changed;
            kana = false;
        }
        else
        {
            for (int index = 0; index < designationLength; index++)
                emit(new Rune(designation[index]), designationInterval);
        }

        designationLength = 0;
        return true;
    }
}
