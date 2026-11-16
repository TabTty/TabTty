namespace TabTty.Core;

public enum TerminalKey
{
    Up, Down, Right, Left, Home, End, Insert, Delete, PageUp, PageDown, BackTab, Escape,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
    KeypadDecimal, KeypadEnter, KeypadMultiply, KeypadAdd, KeypadSubtract, KeypadDivide,
}

[Flags]
public enum TerminalModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }

internal static class TerminalKeys
{
    public static byte[] Encode(TerminalKey key, TerminalModifiers modifiers, bool applicationCursor, bool applicationKeypad)
    {
        if ((uint)modifiers > 7) throw new ArgumentOutOfRangeException(nameof(modifiers));
        if ((uint)key > (uint)TerminalKey.KeypadDivide) throw new ArgumentOutOfRangeException(nameof(key));
        return NativeTerminalCore.EncodeKey(key, modifiers, applicationCursor, applicationKeypad);
    }
}
