namespace TabTty.Core;

internal sealed record MacroEditorParameter(string Name, string Type);
internal sealed record MacroEditorSymbol(string Name, string Type, MacroPosition Position,
    MacroEditorParameter[]? Parameters = null);
internal sealed record MacroEditorCall(string Name, int Parameter, int[] UsedSlots, int? NamedSlot,
    bool CompleteOption, bool HasOptionColon);
internal sealed record MacroPatternFix(MacroPosition Call, int Start, int End);

// Filled by the shared parser only for editor requests. Positions refer to the current source.
internal sealed partial class MacroEditorSymbols(int? cursorOffset = null)
{
    private readonly Dictionary<object, (MacroToken Token, int VisibleFrom)> names =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MacroBlock, (int Start, int End)> blocks =
        new(ReferenceEqualityComparer.Instance);
    internal MacroSourceException? Error { get; private set; }
    internal bool UnsupportedVersion { get; private set; }
    private int innermostOpen = -1;
    private bool insideComment;
    private readonly Dictionary<MacroCall, (int Start, int End)> waitPatterns = new(ReferenceEqualityComparer.Instance);
    internal MacroEditorCall? ActiveCall { get; private set; }
    internal MacroPatternFix? PatternFix { get; private set; }
    internal int? VersionInsertion { get; private set; }

    internal void CallArgument(string name, int open, int index, int start, int end, List<MacroArgumentSlot> slots,
        int optionNameEnd = -1, bool hasOptionColon = false)
    {
        if (cursorOffset is int offset && open >= innermostOpen && offset >= start && offset <= end)
        {
            innermostOpen = open;
            ActiveCall = new(name, index, slots.Take(index).Select(s => s.Index).ToArray(), slots.Count > index ? slots[index].Index : null,
                !insideComment && offset <= optionNameEnd, hasOptionColon);
        }
    }

    internal void WaitPatternArgument(MacroCall call, (int Start, int End) range) => waitPatterns[call] = range;

    internal void CallOptions(int open, List<MacroArgumentSlot> slots)
    {
        if (open == innermostOpen && ActiveCall is { } call && slots.Count != 0)
            ActiveCall = call with { UsedSlots = slots.Where((_, i) => i != call.Parameter).Select(s => s.Index).ToArray() };
    }

    internal void WaitPatternMismatch(MacroCall call)
    {
        if (waitPatterns.TryGetValue(call, out var range))
            PatternFix ??= new(call.Position, range.Start, range.End);
    }

    internal void Comment(int start, int end, bool includeEnd)
    {
        if (cursorOffset > start && (cursorOffset < end || includeEnd && cursorOffset == end))
            insideComment = true;
    }

    internal void CheckVersion(List<MacroToken> tokens)
    {
        if (tokens[0].Text == "#" && tokens[1].Text == "tttym" &&
            tokens[2].Kind == "num" && tokens[2].Text != "1")
        {
            UnsupportedVersion = true;
            throw new MacroSourceException("VersionLine", tokens[2].Position);
        }
    }

    internal void VersionHeader(List<MacroToken> tokens, int firstTokenOffset, int length)
    {
        if (insideComment || cursorOffset is not int offset) return;
        int first = firstTokenOffset >= 0 ? firstTokenOffset : length;
        bool replaceHash = tokens[0].Text == "#" && offset == first + 1;
        if (offset > first && !replaceHash) return;
        for (int i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i].Text == "#" && tokens[i + 1].Text == "tttym" &&
                tokens[i + 1].Offset == tokens[i].Offset + 1 && tokens[i + 2].Kind == "num" &&
                tokens[i + 2].Position.Line == tokens[i].Position.Line) return;
        }
        VersionInsertion = replaceHash ? first : offset;
    }

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
