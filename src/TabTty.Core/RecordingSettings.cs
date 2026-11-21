using System.Globalization;
using System.Numerics;
using System.Text;

namespace TabTty.Core;

internal readonly record struct RecordingDefaults(string Directory, string ProfileTemplate, string MacroTemplate)
{
    internal const string ProfileName = "<cpname>";
    internal const string MacroAndProfileName = "<macroname>-<cpname>";
    internal const string DefaultSuffix = "_<datetime:yyyy-MM-dd>_<serial:0,4>";
    internal static RecordingDefaults BuiltIn { get; } = new("", ProfileName + DefaultSuffix, MacroAndProfileName + DefaultSuffix);

    internal void Validate()
    {
        if (!string.IsNullOrWhiteSpace(Directory) && !OrdinaryWindowsPath.IsValid(Directory))
            throw new InvalidDataException("Invalid default log directory.");
        if (!string.IsNullOrWhiteSpace(ProfileTemplate)) RecordingNameTemplate.Parse(ProfileTemplate, macro: false);
        if (!string.IsNullOrWhiteSpace(MacroTemplate)) RecordingNameTemplate.Parse(MacroTemplate, macro: true);
    }
}

internal readonly record struct RecordingOverrides(string? Directory, string? Template, LogCollisionPolicy? Collision,
    string? BaseDirectory = null);

internal sealed record RecordingSettings(RecordingDestination Destination, RecordingNameTemplate Template, LogCollisionPolicy Collision)
{
    internal static RecordingSettings Resolve(string path, ConnectionProfile profile, string? macroName,
        RecordingDefaults? defaults = null, RecordingOverrides overrides = default, RecordingDestination? reviewed = null)
    {
        var fallback = defaults ?? RecordingDefaults.BuiltIn;
        bool hasDirectory = !string.IsNullOrWhiteSpace(overrides.Directory);
        bool hasTemplate = !string.IsNullOrWhiteSpace(overrides.Template);
        bool macro = macroName is not null;
        string template = FirstNonblank(overrides.Template, profile.LogFilenameTemplate,
            macro ? fallback.MacroTemplate : fallback.ProfileTemplate,
            macro ? RecordingDefaults.BuiltIn.MacroTemplate : RecordingDefaults.BuiltIn.ProfileTemplate);
        var destination = reviewed ?? RecordingDestination.Capture(OrdinaryWindowsPath.Resolve(
            FirstNonblank(overrides.Directory, profile.LogDirectory, fallback.Directory, "Logs"),
            (hasDirectory ? overrides.BaseDirectory : null) ?? Path.GetDirectoryName(Path.GetFullPath(path))!));
        return new(destination, RecordingNameTemplate.Parse(template, macro),
            overrides.Collision ?? (hasDirectory || hasTemplate ? LogCollisionPolicy.Error : profile.LogCollision));
    }

    private static string FirstNonblank(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!;
}

internal sealed class RecordingStartException(string message, Exception? inner = null) : IOException(message, inner);

internal sealed class RecordingNameTemplate
{
    internal const int MaximumLength = 1024;
    private static readonly char[] InvalidNameCharacters = "<>:\"/\\|?*".ToCharArray();
    private enum PartKind { Text, Profile, Macro, DateTime, Uuid, Serial }
    private readonly record struct Part(PartKind Kind, string Text = "");
    private readonly Part[] parts;
    private readonly BigInteger start;
    private readonly int width;

    private RecordingNameTemplate(Part[] parts, BigInteger start, int width) =>
        (this.parts, this.start, this.width) = (parts, start, width);

    internal static RecordingNameTemplate Parse(string text, bool macro)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumLength)
            throw new InvalidDataException("Invalid log filename template length.");
        var parts = new List<Part>();
        BigInteger start = BigInteger.Zero;
        int width = 0;
        for (int index = 0; index < text.Length;)
        {
            int opening = text.IndexOf('<', index);
            int end = opening < 0 ? text.Length : opening;
            if (end > index)
            {
                string literal = text[index..end];
                if (InvalidCharacters(literal)) throw InvalidName();
                parts.Add(new(PartKind.Text, literal));
            }
            if (opening < 0) break;
            int closing = text.IndexOf('>', opening + 1);
            if (closing < 0) throw new InvalidDataException("Unclosed log filename placeholder.");
            string placeholder = text[(opening + 1)..closing];
            if (placeholder.StartsWith("datetime:", StringComparison.Ordinal) && placeholder.Length > 9)
            {
                string format = placeholder[9..];
                try { _ = new DateTime(2026, 11, 21, 14, 30, 5).ToString(format, CultureInfo.InvariantCulture); }
                catch (FormatException error) { throw new InvalidDataException("Invalid log date and time format.", error); }
                parts.Add(new(PartKind.DateTime, format));
            }
            else if (placeholder.StartsWith("serial:", StringComparison.Ordinal))
            {
                string[] fields = placeholder[7..].Split(',');
                if (width != 0 || fields.Length != 2 ||
                    !BigInteger.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                    !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out width) || width is < 1 or > 255)
                    throw new InvalidDataException("Use one log serial placeholder with a nonnegative start and positive width.");
                parts.Add(new(PartKind.Serial));
            }
            else parts.Add(placeholder switch
            {
                "cpname" => new(PartKind.Profile),
                "macroname" when macro => new(PartKind.Macro),
                "macroname" => throw new InvalidDataException("The macroname placeholder requires a macro connection."),
                "uuid" => new(PartKind.Uuid),
                _ => throw new InvalidDataException("Unknown log filename placeholder."),
            });
            index = closing + 1;
        }
        return new(parts.ToArray(), start, width);
    }

    internal ExpandedRecordingName Expand(string profileName, string? macroName, DateTime localTime)
    {
        string uuid = Guid.NewGuid().ToString("D");
        var name = new StringBuilder();
        string prefix = "";
        foreach (Part part in parts)
        {
            if (part.Kind == PartKind.Serial)
            {
                prefix = name.ToString();
                name.Clear();
                continue;
            }
            name.Append(part.Kind switch
            {
                PartKind.Profile => Sanitize(profileName),
                PartKind.Macro => Sanitize(macroName!),
                PartKind.DateTime => localTime.ToString(part.Text, CultureInfo.InvariantCulture),
                PartKind.Uuid => uuid,
                _ => part.Text,
            });
        }
        var expanded = new ExpandedRecordingName(prefix, name.ToString(), start, width);
        expanded.FileBase(start);
        return expanded;
    }

    internal static void ValidateBase(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            InvalidCharacters(name) || OrdinaryWindowsPath.IsReservedFileName(name)) throw InvalidName();
    }

    private static bool InvalidCharacters(string value) => value.IndexOfAny(InvalidNameCharacters) >= 0 || value.Any(c => c < ' ');
    private static InvalidDataException InvalidName() => new("The log template produces an invalid Windows filename.");

    private static string Sanitize(string value)
    {
        var name = new StringBuilder();
        foreach (Rune rune in value.EnumerateRunes().Take(64))
            name.Append(rune.IsAscii && (rune.Value < ' ' || InvalidNameCharacters.Contains((char)rune.Value)) ? "_" : rune.ToString());
        string label = name.ToString().Trim(' ', '.');
        if (label.Length == 0) return "TabTty";
        return OrdinaryWindowsPath.IsReservedFileName(label) && label.Contains('.') ? "_" + label : label;
    }
}

internal readonly record struct ExpandedRecordingName(string Prefix, string Suffix, BigInteger Start, int Width)
{
    internal string Digits(BigInteger number) => number.ToString("D" + Width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    internal string FileBase(BigInteger number)
    {
        string name = Width == 0 ? Suffix : Prefix + Digits(number) + Suffix;
        RecordingNameTemplate.ValidateBase(name);
        return name;
    }
}
