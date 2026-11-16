namespace TabTty.Core;

[Flags]
public enum TerminalStyleFlags
{
    Bold = 1, Faint = 2, Italic = 4, Underline = 8, Inverse = 16, Conceal = 32, Strike = 64,
}

public readonly record struct TerminalStyle
{
    private const uint Assigned = 0x01000000;
    private readonly uint foreground, background;
    public uint? Foreground
    {
        get => (foreground & Assigned) == 0 ? null : foreground & 0xFFFFFF;
        init => foreground = Encode(value);
    }
    public uint? Background
    {
        get => (background & Assigned) == 0 ? null : background & 0xFFFFFF;
        init => background = Encode(value);
    }
    public TerminalStyleFlags Flags { get; init; }

    public TerminalStyle(uint? foreground, uint? background, TerminalStyleFlags flags)
    {
        Foreground = foreground;
        Background = background;
        Flags = flags;
    }

    private static uint Encode(uint? color)
    {
        if (color > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(color));
        return color is { } value ? Assigned | value : 0;
    }
}
public readonly record struct TerminalCell(int Column, int Width, int TextOffset, string Text, TerminalStyle Style,
    bool IsErased = false);
public enum TerminalCursorShape { Block, Underline, Bar }

internal static class TerminalRendition
{
    private static readonly uint[] Palette =
    [
        0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
        0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
    ];
    private static readonly uint[] Levels = [0, 95, 135, 175, 215, 255];

    public static bool Apply(string parameters, ref TerminalStyle style)
    {
        if (parameters.Any(character => !char.IsAsciiDigit(character) && character is not (';' or ':')))
            return false;
        string[] parts = parameters.Split(';');
        bool applied = false;
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            string[] fields = part.Split(':');
            int command = Number(fields[0]);
            if (command is 38 or 48)
            {
                uint? color;
                if (fields.Length > 1)
                {
                    color = Color(fields.AsSpan(1), colon: true);
                }
                else
                {
                    int length = index + 1 < parts.Length ? Number(parts[index + 1]) switch
                    {
                        2 => 4, 5 => 2, _ => 1,
                    } : 0;
                    int available = Math.Min(length, parts.Length - index - 1);
                    color = available == length ? Color(parts.AsSpan(index + 1, available), colon: false) : null;
                    index += available;
                }
                if (color is null) continue;
                style = command == 38 ? style with { Foreground = color } : style with { Background = color };
                applied = true;
                continue;
            }
            if (fields.Length != 1) continue;
            if (command == 0) style = default;
            else if (command is >= 30 and <= 37 or >= 90 and <= 97)
                style = style with { Foreground = Palette[command >= 90 ? command - 82 : command - 30] };
            else if (command is >= 40 and <= 47 or >= 100 and <= 107)
                style = style with { Background = Palette[command >= 100 ? command - 92 : command - 40] };
            else if (command == 39) style = style with { Foreground = null };
            else if (command == 49) style = style with { Background = null };
            else
            {
                TerminalStyleFlags flag = command switch
                {
                    1 => TerminalStyleFlags.Bold,
                    2 => TerminalStyleFlags.Faint,
                    3 or 23 => TerminalStyleFlags.Italic,
                    4 or 24 => TerminalStyleFlags.Underline,
                    7 or 27 => TerminalStyleFlags.Inverse,
                    8 or 28 => TerminalStyleFlags.Conceal,
                    9 or 29 => TerminalStyleFlags.Strike,
                    22 => TerminalStyleFlags.Bold | TerminalStyleFlags.Faint,
                    _ => 0,
                };
                if (flag == 0) continue;
                style = style with { Flags = command < 20 ? style.Flags | flag : style.Flags & ~flag };
            }
            applied = true;
        }
        return applied;
    }

    private static int Number(string part) => part.Length == 0 ? 0 :
        int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out int value) ? value : -1;

    private static uint? Color(ReadOnlySpan<string> fields, bool colon)
    {
        if (fields.Length == 2 && fields[0] == "5" && fields[1].Length > 0 && Number(fields[1]) is >= 0 and <= 255)
        {
            int index = Number(fields[1]);
            if (index < 16) return Palette[index];
            if (index < 232)
            {
                index -= 16;
                return Levels[index / 36] << 16 | Levels[index / 6 % 6] << 8 | Levels[index % 6];
            }
            return (uint)(8 + 10 * (index - 232)) * 0x010101;
        }
        if (fields.Length == 5 && colon && fields[0] == "2" && fields[1] is ("" or "0"))
            fields = fields[1..];
        else if (fields.Length != 4 || fields[0] != "2")
            return null;
        if (fields[1].Length == 0 || fields[2].Length == 0 || fields[3].Length == 0) return null;
        int red = Number(fields[1]), green = Number(fields[2]), blue = Number(fields[3]);
        return (uint)red <= 255 && (uint)green <= 255 && (uint)blue <= 255
            ? (uint)(red << 16 | green << 8 | blue) : null;
    }
}
