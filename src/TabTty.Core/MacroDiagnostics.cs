using System.Globalization;
using System.Resources;

namespace TabTty.Core;

internal static class MacroDiagnostics
{
    private static readonly ResourceManager Messages = new("TabTty.Core.MacroDiagnostics", typeof(MacroDiagnostics).Assembly);

    internal static string? Find(string code, CultureInfo language) => Messages.GetString(code, language);

    internal static string Describe(MacroSourceException error, CultureInfo language)
    {
        if (error is { Code: "ReservedName", Symbol: { } name })
            return string.Format(language, Find("ReservedNameWithSymbol", language)!, name);
        string code = error.DiagnosticMessageCode ?? error.Code;
        return Find(code, language) ?? Find("InvalidSource", language)!;
    }
}
