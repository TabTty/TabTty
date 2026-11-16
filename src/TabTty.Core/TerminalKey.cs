using System.Globalization;
using System.Text;

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
        int modifier = (int)modifiers;
        if ((uint)modifier > 7) throw new ArgumentOutOfRangeException(nameof(modifiers));
        string suffix = (modifier + 1).ToString(CultureInfo.InvariantCulture);
        if (key >= TerminalKey.Keypad0)
        {
            char code = key is >= TerminalKey.Keypad0 and <= TerminalKey.Keypad9
                ? (char)((applicationKeypad ? 'p' : '0') + key - TerminalKey.Keypad0)
                : key switch
                {
                    TerminalKey.KeypadDecimal => applicationKeypad ? 'n' : '.',
                    TerminalKey.KeypadEnter => applicationKeypad ? 'M' : '\r',
                    TerminalKey.KeypadMultiply => applicationKeypad ? 'j' : '*',
                    TerminalKey.KeypadAdd => applicationKeypad ? 'k' : '+',
                    TerminalKey.KeypadSubtract => applicationKeypad ? 'm' : '-',
                    TerminalKey.KeypadDivide => applicationKeypad ? 'o' : '/',
                    _ => throw new ArgumentOutOfRangeException(nameof(key)),
                };
            return Encoding.ASCII.GetBytes(applicationKeypad ? $"\u001bO{code}" : code.ToString());
        }
        char final = key switch
        {
            TerminalKey.Up => 'A', TerminalKey.Down => 'B', TerminalKey.Right => 'C', TerminalKey.Left => 'D',
            TerminalKey.F1 => 'P', TerminalKey.F2 => 'Q', TerminalKey.F3 => 'R', TerminalKey.F4 => 'S',
            _ => '\0',
        };
        if (final != '\0')
        {
            string prefix = modifier != 0 ? $"\u001B[1;{suffix}"
                : applicationCursor || key is >= TerminalKey.F1 and <= TerminalKey.F4 ? "\u001BO" : "\u001B[";
            return Encoding.ASCII.GetBytes(prefix + final);
        }

        if (key == TerminalKey.Escape) return [0x1B];
        if (key == TerminalKey.BackTab) return [0x1B, (byte)'[', (byte)'Z'];
        int parameter = key switch
        {
            TerminalKey.Home => 1, TerminalKey.Insert => 2, TerminalKey.Delete => 3, TerminalKey.End => 4,
            TerminalKey.PageUp => 5, TerminalKey.PageDown => 6,
            TerminalKey.F5 => 15, TerminalKey.F6 => 17, TerminalKey.F7 => 18, TerminalKey.F8 => 19,
            TerminalKey.F9 => 20, TerminalKey.F10 => 21, TerminalKey.F11 => 23, TerminalKey.F12 => 24,
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        string number = parameter.ToString(CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes(modifier == 0 ? $"\u001B[{number}~" : $"\u001B[{number};{suffix}~");
    }
}
