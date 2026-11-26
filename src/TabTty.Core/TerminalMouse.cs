namespace TabTty.Core;

public readonly record struct TerminalMouseMode(int Tracking, bool SgrCoordinates, int Generation);
public readonly record struct TerminalMouseInput(int Button, int Column, int Row, bool Release, bool Motion,
    TerminalModifiers Modifiers, int Generation);

internal readonly record struct TerminalMouseReport(byte[] Payload, TerminalMouseInput Effective);
