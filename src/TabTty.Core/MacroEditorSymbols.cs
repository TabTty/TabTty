namespace TabTty.Core;

internal sealed record MacroEditorParameter(string Name, string Type);
internal sealed record MacroEditorSymbol(string Name, string Type, MacroPosition Position,
    MacroEditorParameter[]? Parameters = null);

// Filled by the shared parser only for editor requests. Positions refer to the current source.
internal sealed class MacroEditorSymbols
{
    private readonly Dictionary<object, (MacroToken Token, int VisibleFrom)> names =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MacroBlock, (int Start, int End)> blocks =
        new(ReferenceEqualityComparer.Instance);
    internal MacroSourceException? Error { get; private set; }
    internal IReadOnlyList<MacroToken> Tokens { get; set; } = [];

    internal bool Recover(MacroSourceException error)
    {
        if (error.Code.EndsWith("Limit", StringComparison.Ordinal)) return false;
        Error ??= error;
        return true;
    }

    internal void Name(object node, MacroToken token, int visibleFrom = 0) =>
        names.Add(node, (token, visibleFrom));

    internal void Block(MacroBlock block, int start, int end) => blocks.Add(block, (start, end));

    internal List<MacroEditorSymbol> At(MacroProgram program, int offset)
    {
        var visible = new List<MacroEditorSymbol>();
        MacroFunction? currentFunction = null;
        foreach (var function in program.Functions.Values)
        {
            visible.Add(new(function.Name, function.Return.ToString(), names[function].Token.Position,
                function.Parameters.Select(p => new MacroEditorParameter(p.Name, p.Type.ToString())).ToArray()));
            if (Inside(function.Body)) currentFunction = function;
        }
        if (currentFunction is not null)
        {
            foreach (var parameter in currentFunction.Parameters) Add(parameter, parameter.Type);
            Visit(currentFunction.Body);
        }
        else
        {
            foreach (var statement in program.Body.Statements) Visit(statement);
        }
        return visible;

        bool Inside(MacroBlock block)
        {
            var span = blocks[block];
            return offset >= span.Start && offset < span.End;
        }

        void Add(object node, MacroType type)
        {
            var (name, from) = names[node];
            if (from <= offset) visible.Add(new(name.Text, type.ToString(), name.Position));
        }

        void Visit(MacroStatement statement)
        {
            switch (statement)
            {
                case MacroDeclaration declaration:
                    Add(declaration, declaration.Declared ?? declaration.Type);
                    break;
                case MacroBlock block when Inside(block):
                    foreach (var child in block.Statements) Visit(child);
                    break;
                case MacroIf branch:
                    Visit(branch.Yes);
                    if (branch.No is not null) Visit(branch.No);
                    break;
                case MacroWhile loop: Visit(loop.Body); break;
                case MacroFor loop:
                    if (loop.Initial is MacroDeclaration initializer && offset < blocks[loop.Body].End)
                        Visit(initializer);
                    Visit(loop.Body);
                    break;
                case MacroForeach loop when Inside(loop.Body):
                    Add(loop, loop.Declared ?? loop.Type); Visit(loop.Body);
                    break;
                case MacroTry attempt:
                    Visit(attempt.Body);
                    if (Inside(attempt.Catch)) { Add(attempt, new("MacroError")); Visit(attempt.Catch); }
                    break;
            }
        }
    }
}
