using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TabTty.App;

internal static class FontCatalog
{
    private static readonly Lazy<string[]> Installed = new(() => Fonts.SystemFontFamilies.Where(IsFixedPitch)
        .Select(family => family.Source).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
    public static IReadOnlyList<string> Names => Installed.Value;

    public static string Resolve(string requested) => Names.FirstOrDefault(name =>
        string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)) ??
        Names.FirstOrDefault(name => string.Equals(name, "Consolas", StringComparison.OrdinalIgnoreCase)) ??
        Names.FirstOrDefault() ?? "Global Monospace";

    public static ComboBox Picker(string selected) => new() { IsEditable = true, ItemsSource = Names, Text = selected };

    public static void Validate(string requested, string? previous)
    {
        if (requested != previous && !Names.Contains(requested, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(App.Text("ChooseFixedPitchFont"));
    }

    private static bool IsFixedPitch(FontFamily family)
    {
        try
        {
            var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!face.TryGetGlyphTypeface(out GlyphTypeface glyph)) return false;
            double? width = null;
            foreach (char character in "iW0 ")
            {
                if (!glyph.CharacterToGlyphMap.TryGetValue(character, out ushort index)) return false;
                double current = glyph.AdvanceWidths[index];
                if (current <= 0 || width is { } first && Math.Abs(current - first) > 0.0001) return false;
                width = current;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
