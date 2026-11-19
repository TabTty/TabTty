using System.Globalization;
using System.Text;

namespace TabTty.Core;

internal static class PermissionDisplay
{
    private static bool IsDisplayControl(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    internal static string SingleLine(string value) =>
        string.Concat(value.EnumerateRunes().Where(rune => !IsDisplayControl(rune)));

    internal static string Literal(string value) => Render(value, pastePage: false);

    internal static string PastePage(ReadOnlySpan<char> value) => Render(value, pastePage: true);

    private static string Render(ReadOnlySpan<char> value, bool pastePage)
    {
        var display = new StringBuilder(value.Length);
        int offset = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            offset += rune.Utf16SequenceLength;
            // Escape the marker itself. Quotation marks also delimit macro arguments.
            if (IsDisplayControl(rune) || rune.Value == '<' || (!pastePage && rune.Value == '"') ||
                (Rune.IsWhiteSpace(rune) && rune.Value != ' '))
                display.Append($"<U+{rune.Value:X4}>");
            else display.Append(rune);
            if (pastePage && (rune.Value == '\n' ||
                rune.Value == '\r' && (offset == value.Length || value[offset] != '\n')))
                display.AppendLine();
        }
        return display.ToString();
    }
}
