namespace TabTty.Core;

public readonly record struct TerminalMouseMode(int Tracking, bool SgrCoordinates, int Generation);
public readonly record struct TerminalMouseInput(int Button, int Column, int Row, bool Release, bool Motion,
    TerminalModifiers Modifiers, int Generation);

internal static class TerminalMouse
{
    public static byte[] Encode(TerminalMouseInput input, TerminalMouseMode mode, int columns, int rows) =>
        NativeTerminalCore.EncodeMouse(input, mode, columns, rows);
}
