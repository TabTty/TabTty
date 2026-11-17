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
