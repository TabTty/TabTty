using System.Text;

namespace TabTty.Core;

internal sealed class LegacyReceiver(TerminalEncoding encoding, bool sevenBitKana) : IDisposable
{
    private readonly byte[] unit = new byte[3];
    private int count;
    private int unitInterval;
    private bool kana;

    private NativeTerminalCore.JisReceiver? jis;
    private readonly byte[] designation = new byte[4];
    private int designationLength;
    private int designationInterval;
    private bool jisPartial;
    internal bool HasIncompleteUnit => count != 0 || designationLength != 0 || jisPartial;

    public void Decode(ReadOnlySpan<byte> bytes, bool flush, int interval,
        Action<Rune, int> emit, Func<bool> inVtSequence)
    {
        if (encoding.Id == "jis")
        {
            DecodeJis(bytes, flush, interval, emit, inVtSequence);
            return;
        }

        foreach (byte value in bytes)
        {
            if (count != 0)
            {
                if (AcceptsContinuation(unit[0], value))
                {
                    unit[count++] = value;
                    if (encoding.Id == "euc-jp" && unit[0] == 0x8F && count == 2) continue;
                    emit(encoding.DecodeUnit(unit.AsSpan(0, count)), unitInterval);
                    count = 0;
                    continue;
                }
                count = 0;
                emit(Rune.ReplacementChar, unitInterval);
            }
            if (inVtSequence())
            {
                emit(new Rune(value), interval);
                continue;
            }
            if (sevenBitKana && encoding.Id == "cp932" && value is 0x0E or 0x0F)
            {
                kana = value == 0x0E;
                continue;
            }
            if (StartsUnit(value))
            {
                unit[0] = value;
                count = 1;
                unitInterval = interval;
            }
            else
            {
                unit[0] = kana && value is >= 0x21 and <= 0x5F ? (byte)(value + 0x80) : value;
                emit(encoding.DecodeUnit(unit.AsSpan(0, 1)), interval);
            }
        }
        if (flush && count != 0)
        {
            count = 0;
            emit(Rune.ReplacementChar, unitInterval);
        }
    }

    private void DecodeJis(ReadOnlySpan<byte> bytes, bool flush, int interval,
        Action<Rune, int> emit, Func<bool> inVtSequence)
    {
        foreach (byte value in bytes)
        {
            if (designationLength == 0)
            {
                if (inVtSequence())
                    emit(new Rune(value), interval);
                else if (value == 0x1B)
                {
                    if (jisPartial) EmitJis([0], false, interval, emit, suppressNul: true);
                    designation[0] = value;
                    designationLength = 1;
                    designationInterval = interval;
                }
                else EmitJis([value], false, interval, emit);
                continue;
            }
            if (value < 0x20)
            {
                if (value is 0x18 or 0x1A) designationLength = 0;
                if (value == 0x1B)
                {
                    designationLength = 1;
                    designationInterval = interval;
                }
                else emit(new Rune(value), interval);
                continue;
            }
            designation[designationLength++] = value;
            if (designationLength == 2 && value is (byte)'$' or (byte)'(' or (byte)'&') continue;
            if (designationLength == 3 && designation[1] == '$' && value == '(') continue;
            // VT and latching character-set escapes share ESC. Forward the
            // designation spelling verbatim so ICU applies its own modes.
            bool codecEscape = designation[1] is (byte)'$' or (byte)'(' or (byte)'&' &&
                designationLength > 2 && !(designation[1] == '(' && value == '0');
            if (codecEscape) EmitJis(designation.AsSpan(0, designationLength), false, designationInterval, emit);
            else
                for (int index = 0; index < designationLength; index++)
                    emit(new Rune(designation[index]), designationInterval);
            designationLength = 0;
        }
        if (flush)
        {
            designationLength = 0;
            if (jis is not null) EmitJis([], true, interval, emit);
        }
    }

    private void EmitJis(ReadOnlySpan<byte> bytes, bool flush, int interval, Action<Rune, int> emit, bool suppressNul = false)
    {
        jis ??= new NativeTerminalCore.JisReceiver(sevenBitKana);
        Span<char> output = stackalloc char[16];
        bool previouslyPartial = jisPartial;
        var (written, pending) = jis.Decode(bytes, output, flush);
        int sourceInterval = previouslyPartial ? unitInterval : interval;
        foreach (Rune character in output[..written].EnumerateRunes())
        {
            if (!suppressNul || character.Value != 0) emit(character, sourceInterval);
            sourceInterval = interval;
        }
        if (pending && (!previouslyPartial || written != 0)) unitInterval = interval;
        jisPartial = pending;
    }

    private bool StartsUnit(byte value) => encoding.Id switch
    {
        "cp932" => value is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC,
        "cp936" or "cp949" or "cp950" => value is >= 0x81 and <= 0xFE,
        "euc-jp" => value is 0x8E or 0x8F or >= 0xA1 and <= 0xFE,
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
        _ => false,
    };

    public void Dispose() => jis?.Dispose();
}
