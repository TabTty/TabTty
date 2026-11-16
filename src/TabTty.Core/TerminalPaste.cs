using System.Text;

namespace TabTty.Core;

public sealed class TerminalPaste
{
    internal static UTF8Encoding Encoding { get; } = new(false, true);
    public string Text { get; }
    public int CharacterCount { get; }
    public int LineCount { get; }
    public bool HasNewline => LineCount > 1;
    public bool EndsWithNewline => Text.EndsWith('\r') || Text.EndsWith('\n');

    private TerminalPaste(string text, int characterCount, int lineCount)
    {
        Text = text;
        CharacterCount = characterCount;
        LineCount = lineCount;
    }

    public static TerminalPaste Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1024 * 1024)
            throw new ArgumentException("Paste exceeds its input size limit.", nameof(text));
        Encoding.GetByteCount(text);
        foreach (Rune character in text.EnumerateRunes())
        {
            if (Rune.IsControl(character) && character.Value is not ('\t' or '\r' or '\n'))
                throw new ArgumentException("Paste contains a disallowed control character.", nameof(text));
        }

        int characters = 0;
        int lines = 1;
        for (int index = 0; index < text.Length;)
        {
            characters++;
            if (text[index] is '\r' or '\n') lines++;
            index += Unicode17.NextGraphemeLength(text.AsSpan(index));
        }

        return new TerminalPaste(text, characters, lines);
    }
}
