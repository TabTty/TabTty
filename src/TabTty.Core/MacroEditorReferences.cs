namespace TabTty.Core;

internal sealed record MacroEditorReference(int Start, int End, string Name, string? Type = null,
    MacroEditorParameter[]? Parameters = null, MacroPosition? Definition = null, string? FilePath = null,
    bool BuiltinFunction = false);

internal sealed partial class MacroEditorSymbols
{
    private MacroToken? referenceToken;
    private object? referenceSyntax;
    internal MacroEditorReference? Reference { get; private set; }

    // A call name is known before its closing parenthesis has been typed.
    internal void Identifier(MacroToken token, object? syntax = null)
    {
        if (cursorOffset >= token.Offset && cursorOffset <= token.Offset + token.Text.Length)
        {
            referenceToken = token;
            referenceSyntax = syntax;
        }
    }

    internal void BoundVariable(MacroName use, object declaration, MacroType type)
    {
        if (ReferenceEquals(referenceSyntax, use))
            Reference = MakeReference(use.Name, type.ToString(), definition: names[declaration].Token.Position);
    }

    internal void ConstantDeclaration(MacroToken name, string type)
    {
        Identifier(name);
        if (ReferenceEquals(referenceToken, name)) Reference = MakeReference(name.Text, type);
    }

    private MacroEditorReference MakeReference(string name, string? type = null, MacroEditorParameter[]? parameters = null,
        MacroPosition? definition = null, string? path = null, bool builtin = false) =>
        new(referenceToken!.Offset, referenceToken.Offset + referenceToken.Text.Length, name, type, parameters, definition, path, builtin);

    private MacroEditorSymbol FunctionSymbol(MacroFunction function) =>
        new(function.Name, function.Return.ToString(), names[function].Token.Position,
            function.Parameters.Select(p => new MacroEditorParameter(p.Name, p.Type.ToString())).ToArray());

    private void ResolveReference(MacroProgram program, List<MacroEditorSymbol> visible)
    {
        if (referenceToken is not { } token || Reference is not null) return;
        string name = token.Text;
        switch (referenceSyntax)
        {
            case null:
                if (program.Functions.TryGetValue(name, out var function))
                {
                    var symbol = FunctionSymbol(function);
                    Reference = MakeReference(name, symbol.Type, symbol.Parameters, symbol.Position);
                }
                else if (MacroApi.Signatures.ContainsKey(name))
                    Reference = MakeReference(name, builtin: true);
                break;
            case MacroName:
                if (Dependencies.Find(d => d.Alias == name) is { } alias)
                    Reference = MakeReference(name, "#constants", definition: alias.AliasPosition);
                // Recovery may have discarded the unfinished statement before checking.
                // Only a variable-use token can use a declaration from the cursor's scope.
                else if (visible.FindLast(s => s.Name == name && s.Parameters is null) is { } local)
                    Reference = MakeReference(name, local.Type, definition: local.Position);
                break;
            case MacroMember member:
                if (member.Target is MacroName qualifier && Dependencies.Find(d => d.Alias == qualifier.Name) is { } dependency)
                {
                    if (dependency.FullPath is { } path && constantSymbols.TryGetValue(path, out var members) &&
                        members.Find(s => s.Name == name) is { } constant)
                        Reference = MakeReference(name, constant.Type, definition: constant.Position, path: path);
                }
                else if (member.Type != MacroType.Unknown)
                    Reference = MakeReference(member.Target.Type.ToString().TrimStart('$') + "." + name, member.Type.ToString());
                break;
            case MacroFunction helper:
                var info = FunctionSymbol(helper);
                Reference = MakeReference(name, info.Type, info.Parameters);
                break;
            case MacroConstantsDeclaration:
                Reference = MakeReference(name, "#constants");
                break;
            default:
                MacroType type = referenceSyntax switch
                {
                    MacroDeclaration variable => variable.Declared ?? variable.Type,
                    MacroParameter parameter => parameter.Type,
                    MacroForeach loop => loop.Declared ?? loop.Type,
                    MacroTry => new("MacroError"),
                    _ => throw new InvalidOperationException(),
                };
                Reference = MakeReference(name, type.ToString());
                break;
        }
    }
}
