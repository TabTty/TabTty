namespace TabTty.Core;

internal static class OrdinaryWindowsPath
{
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    internal static bool IsReservedFileName(string name)
    {
        int dot = name.IndexOf('.');
        return Devices.Contains((dot < 0 ? name : name[..dot]).TrimEnd(' '));
    }

    internal static bool IsValid(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32760) return false;
        string normalized = path.Replace('/', '\\');
        if (Path.IsPathRooted(normalized) && !Path.IsPathFullyQualified(normalized)) return false;
        bool unc = normalized.StartsWith(@"\\", StringComparison.Ordinal);
        bool drive = normalized.Length > 1 && normalized[1] == ':' && char.IsAsciiLetter(normalized[0]);
        int offset = unc || drive ? 2 : 0;
        var parts = normalized[offset..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (unc)
        {
            if (parts.Length < 2 || normalized[2] == '\\' || parts[0].TrimEnd(' ', '.').Length == 0) return false;
            string share = parts[1].TrimEnd(' ', '.');
            if (share.Length == 0 || share.Equals("pipe", StringComparison.OrdinalIgnoreCase) ||
                share.Equals("mailslot", StringComparison.OrdinalIgnoreCase) ||
                share.Equals("IPC$", StringComparison.OrdinalIgnoreCase)) return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].IndexOfAny(InvalidNameCharacters) >= 0) return false;
            if ((!unc || i >= 2) && IsReservedFileName(parts[i])) return false;
        }
        return true;
    }

    internal static string Resolve(string path, string? baseDirectory = null)
    {
        if (!IsValid(path)) throw new InvalidDataException("Use an ordinary relative, DOS or UNC file path.");
        string full = baseDirectory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
        if (!IsValid(full)) throw new InvalidDataException("The resolved file path is not an ordinary Windows path.");
        return full;
    }
}
