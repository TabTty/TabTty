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
        IReadOnlyList<MacroConstantsDeclaration> declarations, IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, int>? knownBytes)
    {
        var staged = new Dictionary<string, List<MacroEditorSymbol>>(StringComparer.Ordinal);
        return MacroConstantsBinder.BindAsync(macroPath, declarations, Capture,
            dependency: (declaration, path) => Dependencies.Add(new(declaration.Alias, path, declaration.AliasPosition, declaration.Position)),
            report: Report, definition: (path, token, value) =>
            {
                if (!staged.TryGetValue(path, out var members)) staged[path] = members = [];
                members.Add(new(token.Text, MacroChecker.ScalarType(value).ToString(), token.Position));
            }, admitted: file => constantSymbols.Add(file.FullPath, staged.GetValueOrDefault(file.FullPath) ?? []))
            .GetAwaiter().GetResult()!;

        ValueTask<MacroConstantsCapture?> Capture(string path, int remainingBytes)
        {
            if (!sources.TryGetValue(path, out var text))
                throw new MacroSourceException("ConstantsRead", new(1, 1), path);
            int? size = knownBytes?.TryGetValue(path, out int encoded) == true ? encoded : null;
            return ValueTask.FromResult<MacroConstantsCapture?>(new(text, "", size));
        }

        void Report(MacroSourceException error)
        {
            DependencyErrors.Add(error);
            Error ??= error;
        }
    }
}
