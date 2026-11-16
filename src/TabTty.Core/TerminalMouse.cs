using System.Globalization;
using System.Text;

namespace TabTty.Core;

public readonly record struct TerminalMouseMode(int Tracking, bool SgrCoordinates, int Generation);
public readonly record struct TerminalMouseInput(int Button, int Column, int Row, bool Release, bool Motion,
    TerminalModifiers Modifiers, int Generation);

internal static class TerminalMouse
{
    public static byte[] Encode(TerminalMouseInput input, TerminalMouseMode mode, int columns, int rows)
    {
        if (mode.Tracking == 0 || mode.Generation != input.Generation || input.Button is < 0 or > 5 ||
            mode.Tracking == 9 && (input.Release || input.Motion || input.Button >= 4) ||
            input.Motion && mode.Tracking is not (1002 or 1003) ||
            input.Motion && mode.Tracking == 1002 && input.Button == 3)
            return [];
        int button = input.Button >= 4 ? 64 + input.Button - 4 : input.Button;
        button |= (int)input.Modifiers * 4;
        if (input.Motion) button |= 32;
        int x = Math.Clamp(input.Column + 1, 1, columns);
        int y = Math.Clamp(input.Row + 1, 1, rows);
        if (mode.SgrCoordinates)
            return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"\u001b[<{button};{x};{y}{(input.Release ? 'm' : 'M')}"));
        if (input.Release) button = 3 | ((int)input.Modifiers * 4);
        return [0x1B, (byte)'[', (byte)'M', (byte)(32 + button), (byte)(32 + Math.Min(x, 223)),
            (byte)(32 + Math.Min(y, 223))];
    }
}
