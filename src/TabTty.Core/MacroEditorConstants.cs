namespace TabTty.Core;

internal sealed record MacroEditorDependency(string Alias, string? FullPath, MacroPosition AliasPosition, MacroPosition Position);

internal sealed partial class MacroEditorSymbols
{
    internal List<MacroEditorDependency> Dependencies { get; } = [];
    internal List<MacroSourceException> DependencyErrors { get; } = [];
    private readonly Dictionary<string, List<MacroEditorSymbol>> constantSymbols = new(StringComparer.Ordinal);
    private string? completionQualifier;

    internal List<MacroEditorSymbol>? ConstantCompletions
    {
        get
        {
            if (completionQualifier is null || Dependencies.Find(d => d.Alias == completionQualifier) is not { } dependency)
                return null;
            return dependency.FullPath is { } path && constantSymbols.TryGetValue(path, out var members) ? members : [];
        }
    }

    internal void QualifiedMember(MacroExpression receiver, MacroToken dot, MacroToken next)
    {
        if (receiver is not MacroName alias || cursorOffset is not int offset || insideComment) return;
        int start = next.Offset;
        bool hasName = next.Kind == "id";
        if (offset < dot.Offset + 1 || offset > start + (hasName ? next.Text.Length : 0)) return;
        completionQualifier = alias.Name;
    }

    internal IReadOnlyList<MacroConstantsBinding> BindConstants(string macroPath,
        IReadOnlyList<MacroConstantsDeclaration> declarations, IReadOnlyDictionary<string, string> sources)
    {
        var bindings = new List<MacroConstantsBinding>();
        var parsed = new Dictionary<string, (MacroConstantsFile? File, string? Error)>(StringComparer.Ordinal);
        int remaining = MacroConstantsFile.MaxMembers;
        foreach (var declaration in declarations)
        {
            string? path = null;
            try { path = declaration.Resolve(macroPath); }
            catch (MacroSourceException error) { Report(error); }
            Dependencies.Add(new(declaration.Alias, path, declaration.AliasPosition, declaration.Position));
            if (path is null) continue;
            if (!sources.TryGetValue(path, out var text))
            {
                Report(new("ConstantsRead", declaration.Position, macroPath));
                continue;
            }
            if (!parsed.TryGetValue(path, out var result))
            {
                var members = new List<MacroEditorSymbol>();
                try
                {
                    var file = MacroConstantsFile.Parse(path, "", text, remaining, () => { },
                        (token, value) => members.Add(new(token.Text, MacroChecker.ScalarType(value).ToString(), token.Position)));
                    remaining -= file.Values.Count;
                    constantSymbols.Add(path, members);
                    result = (file, null);
                }
                catch (MacroSourceException error)
                {
                    result = (null, error.Code == "ConstantsLimit" ? error.Code : "ConstantsInvalid");
                }
                parsed.Add(path, result);
            }
            if (result.File is { } valid) bindings.Add(new(declaration.Alias, valid));
            else Report(new(result.Error!, declaration.Position, macroPath));
        }
        return bindings;

        void Report(MacroSourceException error)
        {
            DependencyErrors.Add(error);
            Error ??= error;
        }
    }
}
