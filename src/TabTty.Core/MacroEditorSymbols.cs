namespace TabTty.Core;

internal sealed record MacroEditorParameter(string Name, string Type);
internal sealed record MacroEditorSymbol(string Name, string Type, MacroPosition Position,
    MacroEditorParameter[]? Parameters = null);
internal sealed record MacroEditorCall(string Name, int Parameter, int[] UsedSlots, int? NamedSlot,
    bool CompleteOption, bool HasOptionColon);

// Filled by the shared parser only for editor requests. Positions refer to the current source.
internal sealed partial class MacroEditorSymbols(int? cursorOffset = null)
{
    internal PostConnectionCursor? PostConnection { get; init; }
    internal bool InsideComment => insideComment;
    private readonly Dictionary<object, (MacroToken Token, int VisibleFrom)> names =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MacroBlock, (int Start, int End)> blocks =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<(MacroEditorSymbol Symbol, string Frame, int Start, int End)> locals = [];
    internal MacroSourceException? Error { get; private set; }
    internal bool UnsupportedVersion { get; private set; }
    private int innermostOpen = -1;
    private bool insideComment;
    private readonly Dictionary<MacroExpression, MacroSourceRange> firstArgumentRanges = new(ReferenceEqualityComparer.Instance);
    internal MacroEditorCall? ActiveCall { get; private set; }
    internal int? VersionInsertion { get; private set; }

    internal void CallArgument(string name, int open, int index, int start, int end,
        List<MacroBoundArgument>? arguments, int? namedSlot,
        int optionNameEnd = -1, bool hasOptionColon = false)
    {
        if (cursorOffset is int offset && open >= innermostOpen && offset >= start && offset <= end)
        {
            innermostOpen = open;
            var used = arguments?.Take(index).Select(a => a.Parameter.Index).ToArray() ?? [];
            ActiveCall = new(name, index, used, namedSlot,
                !insideComment && offset <= optionNameEnd, hasOptionColon);
        }
    }

    internal void RecordFirstArgument(MacroExpression expression, int start, int end) =>
        firstArgumentRanges[expression] = new(start, end);

    internal void CallOptions(int open, List<MacroBoundArgument> arguments)
    {
        if (open == innermostOpen && ActiveCall is { } call)
            ActiveCall = call with { UsedSlots = arguments.Where((_, i) => i != call.Parameter).Select(a => a.Parameter.Index).ToArray() };
    }

    internal MacroSourceRange FirstArgumentRange(MacroExpression expression) => firstArgumentRanges[expression];

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

    internal void Name(object node, MacroToken token, int visibleFrom = 0)
    {
        names.Add(node, (token, visibleFrom));
        Identifier(token, node);
    }

    internal void Block(MacroBlock block, int start, int end) => blocks.Add(block, (start, end));

    internal void AcceptLocal(object declaration, MacroType type, string frame, MacroBlock body, bool includesHeader)
    {
        var (name, from) = names[declaration];
        var (start, end) = blocks.TryGetValue(body, out var extent) ? extent : (0, int.MaxValue);
        locals.Add((new(name.Text, type.ToString(), name.Position), frame,
            includesHeader ? from : Math.Max(start, from), end));
    }

    internal List<MacroEditorSymbol> At(MacroProgram program, int offset)
    {
        var visible = new List<MacroEditorSymbol>();
        string frame = "";
        foreach (var function in program.Functions.Values)
        {
            visible.Add(FunctionSymbol(function));
            var span = blocks[function.Body];
            if (offset >= span.Start && offset < span.End) frame = function.Name;
        }
        foreach (var local in locals)
            if (local.Frame == frame && offset >= local.Start && offset < local.End) visible.Add(local.Symbol);
        ResolveReference(program, visible);
        return visible;
    }
}
