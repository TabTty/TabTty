using System.Runtime.InteropServices;

namespace TabTty.App;

internal static class SystemLanguage
{
    public static unsafe string Resolve()
    {
        const uint languageNames = 8;
        uint length = 0;
        if (!GetUserPreferredUILanguages(languageNames, out _, null, ref length) || length is 0 or > 32768)
            return "en";
        char[] buffer = new char[length];
        fixed (char* languages = buffer)
        {
            if (!GetUserPreferredUILanguages(languageNames, out _, languages, ref length)) return "en";
        }
        foreach (string tag in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tag.Equals("ja", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("ja-", StringComparison.OrdinalIgnoreCase))
                return "ja";
            if (tag.Equals("en", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                return "en";
        }
        return "en";
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetUserPreferredUILanguages(uint flags, out uint count, char* languages, ref uint length);
}
